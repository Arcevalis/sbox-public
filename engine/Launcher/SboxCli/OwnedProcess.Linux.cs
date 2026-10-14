using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Sandbox.Cli;

/// <summary>
/// Linux half of <see cref="OwnedProcess"/>. Where Windows uses a job object, the child is
/// started in its own session (setsid) so the whole tree dies with a single killpg.
/// </summary>
internal sealed partial class OwnedProcess
{
	const string SetsidPath = "/usr/bin/setsid";
	const int SIGTERM = 15;
	const int SIGKILL = 9;

	bool ownsSession;
	int sessionId;

	Process StartLinux( string executable, string directory, IReadOnlyList<string> arguments )
	{
		var start = new ProcessStartInfo
		{
			WorkingDirectory = directory,
			UseShellExecute = false,
			CreateNoWindow = true,
			// The CLI's stdin carries the MCP stream; the child must never inherit it.
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

		if ( File.Exists( SetsidPath ) )
		{
			// A freshly spawned child is never a group leader, so setsid doesn't fork:
			// the child leads its own session and PGID == its PID.
			start.FileName = SetsidPath;
			start.ArgumentList.Add( executable );
			ownsSession = true;
		}
		else
		{
			start.FileName = executable;
		}

		foreach ( var argument in arguments )
			start.ArgumentList.Add( argument );

		ApplyLinuxEngineEnvironment( start, directory );

		var process = new Process { StartInfo = start };

		// Engine output must never reach the MCP stream on stdout: drain and drop it,
		// while diagnostics go to the CLI's stderr where agents can see them.
		process.OutputDataReceived += ( _, _ ) => { };
		process.ErrorDataReceived += ( _, e ) =>
		{
			if ( e.Data is not null )
				Console.Error.WriteLine( e.Data );
		};

		try
		{
			if ( !process.Start() )
				throw new IOException( $"Failed to start {executable}." );
		}
		catch ( Win32Exception e )
		{
			throw new IOException( $"Failed to start {executable}: {e.Message} (check the file exists and has the executable bit).", e );
		}

		// Never write: the child reads EOF instead of blocking on (or consuming) our stream.
		process.StandardInput.Close();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		if ( ownsSession )
			sessionId = process.Id;

		return process;
	}

	/// <summary>
	/// What ampersand/apps/_common.sh does for hand launches, applied to the child only:
	/// the engine ships its own HarfBuzz (SkiaSharp) but Qt's xcb plugin pulls the system
	/// one through fontconfig, and glibc aborts freeing across the two - so the engine
	/// copy preloads first. The loader also needs the native dir, and only the xcb Qt
	/// plugin ships, so Qt and SDL pin to X11. The script stays source of truth.
	/// </summary>
	static void ApplyLinuxEngineEnvironment( ProcessStartInfo start, string engineDirectory )
	{
		if ( !OperatingSystem.IsLinux() )
			return;

		var native = Path.Combine( engineDirectory, "bin", "linuxsteamrt64" );
		var harfbuzz = Path.Combine( native, "libHarfBuzzSharp.so" );

		if ( File.Exists( harfbuzz ) )
			Prepend( start, "LD_PRELOAD", harfbuzz );

		if ( Directory.Exists( native ) )
			Prepend( start, "LD_LIBRARY_PATH", native );

		start.Environment["QT_QPA_PLATFORM"] = "xcb";
		start.Environment["SDL_VIDEO_DRIVER"] = "x11";
	}

	static void Prepend( ProcessStartInfo start, string name, string value )
	{
		var existing = Environment.GetEnvironmentVariable( name );
		start.Environment[name] = string.IsNullOrEmpty( existing ) ? value : $"{value}:{existing}";
	}

	void KillLinuxTree()
	{
		bool exited;
		try { exited = Process.HasExited; }
		catch ( InvalidOperationException ) { return; }
		if ( exited )
			return;

		// Connect already sent MCP quit and waited; this is the unconditional backstop,
		// mirroring the job-object cleanup on Windows.
		SignalTree( SIGTERM );

		try
		{
			if ( !Process.WaitForExit( 2000 ) )
				SignalTree( SIGKILL );

			Process.WaitForExit( 5000 );
		}
		catch ( InvalidOperationException ) { }
	}

	void SignalTree( int signal )
	{
		// One syscall for the whole session when setsid contained it.
		if ( ownsSession && NativeKillGroup( sessionId, signal ) )
			return;

		// Otherwise the child plus whatever it spawned, found through /proc.
		int pid;
		try { pid = Process.Id; }
		catch ( InvalidOperationException ) { return; }

		NativeKillOne( pid, signal );

		List<int> descendants;
		try { descendants = DescendantsOf( pid ); }
		catch ( IOException ) { return; }
		catch ( UnauthorizedAccessException ) { return; }

		foreach ( var descendant in descendants )
			NativeKillOne( descendant, signal );
	}

	/// <summary>
	/// Every descendant of a root PID, breadth-first, mapped from /proc parent links.
	/// </summary>
	static List<int> DescendantsOf( int root )
	{
		var byParent = new Dictionary<int, List<int>>();

		foreach ( var directory in Directory.EnumerateDirectories( "/proc" ) )
		{
			if ( !int.TryParse( Path.GetFileName( directory ), out var pid ) )
				continue;

			if ( ParentOf( pid ) is not { } ppid )
				continue;

			if ( !byParent.TryGetValue( ppid, out var list ) )
				byParent[ppid] = list = new List<int>();

			list.Add( pid );
		}

		var result = new List<int>();
		var queue = new Queue<int>();
		queue.Enqueue( root );

		while ( queue.Count > 0 )
		{
			if ( !byParent.TryGetValue( queue.Dequeue(), out var kids ) )
				continue;

			foreach ( var kid in kids )
			{
				result.Add( kid );
				queue.Enqueue( kid );
			}
		}

		return result;
	}

	static int? ParentOf( int pid )
	{
		string stat;
		try { stat = File.ReadAllText( $"/proc/{pid}/stat" ); }
		catch ( IOException ) { return null; }
		catch ( UnauthorizedAccessException ) { return null; }

		// comm may itself contain spaces or parens, so cut at the last one before splitting.
		var fields = stat[(stat.LastIndexOf( ')' ) + 1)..].Split( ' ', StringSplitOptions.RemoveEmptyEntries );

		// state, ppid, pgrp, ...
		if ( fields.Length >= 2 && int.TryParse( fields[1], out var ppid ) )
			return ppid;

		return null;
	}

	static bool NativeKillGroup( int group, int signal )
	{
		try { return KillProcessGroup( group, signal ) == 0; }
		catch ( DllNotFoundException ) { return false; }
		catch ( EntryPointNotFoundException ) { return false; }
	}

	static void NativeKillOne( int pid, int signal )
	{
		try { KillProcess( pid, signal ); }
		catch ( DllNotFoundException ) { }
		catch ( EntryPointNotFoundException ) { }
	}

	/// <summary>
	/// SIGTERM has no managed event like CancelKeyPress, so register one that runs the same
	/// graceful shutdown. No-op on Windows. The caller disposes it with the application lifetime.
	/// </summary>
	internal static IDisposable WatchTermination( CancellationTokenSource lifetime )
	{
		if ( OperatingSystem.IsWindows() )
			return NullDisposable.Instance;

		return PosixSignalRegistration.Create( PosixSignal.SIGTERM, context =>
		{
			context.Cancel = true;

			try { lifetime.Cancel(); }
			catch ( ObjectDisposedException ) { }
		} );
	}

	sealed class NullDisposable : IDisposable
	{
		internal static readonly NullDisposable Instance = new();

		public void Dispose() { }
	}

	[DllImport( "libc", EntryPoint = "killpg", SetLastError = true )]
	static extern int KillProcessGroup( int group, int signal );

	[DllImport( "libc", EntryPoint = "kill", SetLastError = true )]
	static extern int KillProcess( int pid, int signal );
}
