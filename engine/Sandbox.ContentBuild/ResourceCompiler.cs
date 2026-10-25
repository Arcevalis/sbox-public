using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Sandbox.ContentBuild;

/// <summary>
/// resourcecompiler.exe for a build: a few long-lived worker processes, each taking one job at a time. A job's build
/// report makes the compiler skip associate compiles and report what it read.
/// </summary>
public sealed class ResourceCompiler( string executable, AssetTypeTable types ) : IAssetCompiler
{
	public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes( 15 );

	/// <summary>
	/// Worker processes at most, over all roots; at least as many as the build runs jobs at once.
	/// </summary>
	public int MaxWorkers { get; init; } = Environment.ProcessorCount;

	/// <summary>
	/// Worker processes started this build.
	/// </summary>
	public int Launches => launches;

	/// <summary>
	/// Why each worker that went before the build's end went: it crashed, timed out, was cancelled, or couldn't
	/// guarantee its next job starts clean.
	/// </summary>
	public IReadOnlyCollection<string> Ended => ended;

	/// <summary>
	/// Idle workers stopped to make room for a worker with other mounts or arguments.
	/// </summary>
	public int Evicted => evicted;

	readonly object gate = new();
	readonly List<CompilerWorker> idle = [];
	readonly ConcurrentQueue<string> ended = new();
	SemaphoreSlim slots;
	int launches, evicted;

	/// <summary>
	/// The processes of the workers waiting for a job (for tests).
	/// </summary>
	internal IReadOnlyList<int> IdleProcessIds { get { lock ( gate ) return idle.Select( x => x.ProcessId ).ToList(); } }

	public AssetKind Classify( string sourceExtension, out string outputExtension ) => types.Classify( sourceExtension, out outputExtension );

	/// <summary>
	/// Runs the job on a worker for its mounts. A worker that dies before answering is replaced and the job runs once
	/// more on the new one, from empty staging; a second loss, a timeout or cancellation fails the job.
	/// </summary>
	public CompileResult Compile( CompileJob job, CancellationToken token )
	{
		var report = Path.Combine( job.Staging, "report.txt" );
		var mode = job.Mode switch { JobMode.Describe => "describe", JobMode.Adopt => "adopt", _ => "compile" };

		for ( var attempt = 0; ; attempt++ )
		{
			CompilerWorker.Answer answer;
			var worker = Take( job, token );
			if ( worker is null ) return Failed( "cancelled" );
			try
			{
				answer = worker.Run( mode, job.Source, job.Staging, report, Timeout, token );
			}
			catch
			{
				End( worker, "failed" );
				throw;
			}

			if ( answer.Answered && answer.TakesAnother ) Return( worker );
			else End( worker, answer.Reason );

			if ( answer.Answered )
			{
				var lines = File.Exists( report ) ? File.ReadAllLines( report ) : [];
				File.Delete( report );
				return BuildReportParser.Parse( lines, answer.Result, answer.Log );
			}

			if ( token.IsCancellationRequested ) return Failed( "cancelled" );
			if ( attempt > 0 || answer.Reason.StartsWith( "timed out" ) ) return Failed( $"resourcecompiler {answer.Reason}\n{answer.Log}" );

			Directory.Delete( job.Staging, true );
			Directory.CreateDirectory( job.Staging );
		}
	}

	static CompileResult Failed( string log ) => new( false, log, [], [], [], [], [] );

	/// <summary>
	/// An idle worker for the job's mounts and arguments, or a new one. When the cap is reached by idle workers of
	/// other mounts, one of them goes; the cap holds because a slot is taken for every worker in use.
	/// </summary>
	CompilerWorker Take( CompileJob job, CancellationToken token )
	{
		lock ( gate ) slots ??= new SemaphoreSlim( MaxWorkers );
		try { slots.Wait( token ); }
		catch ( OperationCanceledException ) { return null; }

		// Any root's job: a compile takes its mod from the source's path, and every root of a build has the same mounts
		var key = string.Join( '\n', [.. job.Mounts.Select( x => $"{x.Mod}|{x.Path}" ), .. job.CompilerArgs] );
		CompilerWorker evict = null;
		lock ( gate )
		{
			var found = idle.FirstOrDefault( x => x.Key == key );
			if ( found is not null )
			{
				idle.Remove( found );
				return found;
			}

			// Workers in use (this one included) plus idle ones stay within the cap
			if ( idle.Count > slots.CurrentCount )
			{
				evict = idle[0];
				idle.RemoveAt( 0 );
			}
		}
		Interlocked.Increment( ref launches );
		var args = new List<string>
		{
			// A few threads each: a lone heavy compile (a skybox prefilter) finishes ~2.5x sooner, and a machine
			// already full of compiles loses nothing to the extra threads
			"-skiprendersystem", "-testmode", "-ignorecontentasserts", "-unbufferedio", "-threads", "4",
			// Each mount is added in front of the last, so the order matches the editor's
			"-searchpaths", string.Join( ";", job.Mounts.Select( x => $"{x.Mod}|{x.Path}" ) ),
			// Jobs come in one at a time, each with its own report; -i is only there because the compiler won't start without input
			"-serve",
		};
		args.AddRange( job.CompilerArgs );
		args.AddRange( ["-i", job.Source] );

		try
		{
			if ( evict is not null )
			{
				Interlocked.Increment( ref evicted );
				evict.Stop();
			}
			return new CompilerWorker( executable, key, args, Path.GetDirectoryName( executable ) );
		}
		catch
		{
			slots.Release();
			throw;
		}
	}

	void Return( CompilerWorker worker )
	{
		lock ( gate ) idle.Add( worker );
		slots.Release();
	}

	/// <summary>
	/// A worker leaves before the build ends; its slot is free again however stopping it goes.
	/// </summary>
	void End( CompilerWorker worker, string reason )
	{
		ended.Enqueue( reason ?? "ended" );
		try { worker.Stop(); }
		finally { slots.Release(); }
	}

	/// <summary>
	/// The build is over: every idle worker goes (a build has none busy once it returns). Another build can use the
	/// compiler afterwards and starts new ones.
	/// </summary>
	public void Finished()
	{
		List<CompilerWorker> all;
		lock ( gate )
		{
			all = [.. idle];
			idle.Clear();
		}
		foreach ( var worker in all ) worker.Stop();
	}

	public IReadOnlyList<uint?> Fingerprints( IReadOnlyList<SpecialDependency> dependencies )
	{
		var dir = Directory.CreateTempSubdirectory( "sbox-fingerprints" ).FullName;
		try
		{
			var input = Path.Combine( dir, "in.txt" );
			var output = Path.Combine( dir, "out.txt" );
			File.WriteAllLines( input, dependencies.Select( x => $"{x.Compiler}\t{x.Data}\t{x.Name}" ) );

			Run( ["-skiprendersystem", "-testmode", "-fingerprints", input, output], dir, default, out var log );
			var lines = File.Exists( output ) ? File.ReadAllLines( output ) : [];
			if ( lines.Length != dependencies.Count )
				throw new InvalidOperationException( $"resourcecompiler -fingerprints returned {lines.Length} of {dependencies.Count}:\n{log}" );

			return lines.Select( x => uint.TryParse( x, NumberStyles.HexNumber, null, out var v ) ? v : (uint?)null ).ToArray();
		}
		finally
		{
			Directory.Delete( dir, true );
		}
	}

	int Run( IEnumerable<string> args, string workingDirectory, CancellationToken token, out string log )
	{
		var info = new ProcessStartInfo( executable )
		{
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		foreach ( var arg in args ) info.ArgumentList.Add( arg );

		using var process = new Process { StartInfo = info };
		var text = new StringBuilder();
		DataReceivedEventHandler collect = ( _, e ) =>
		{
			// Shutdown spews a line per leaked convar
			if ( e.Data is null || e.Data.StartsWith( "***" ) ) return;
			lock ( text ) text.AppendLine( e.Data );
		};
		process.OutputDataReceived += collect;
		process.ErrorDataReceived += collect;
		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		using ( token.Register( () => Kill( process ) ) )
		{
			if ( !process.WaitForExit( Timeout ) )
			{
				Kill( process );
				lock ( text ) text.AppendLine( $"Timed out after {Timeout}" );
			}
			process.WaitForExit();
		}

		lock ( text ) log = text.ToString().Trim();
		return token.IsCancellationRequested ? -1 : process.ExitCode;
	}

	static void Kill( Process process )
	{
		try { process.Kill( true ); } catch ( InvalidOperationException ) { }
	}
}
