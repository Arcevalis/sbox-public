using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Sandbox.ContentBuild;

/// <summary>
/// resourcecompiler.exe, one process per asset. -buildreport makes it skip associate compiles and report
/// what it read.
/// </summary>
public sealed class ResourceCompiler( string executable, AssetTypeTable types ) : IAssetCompiler
{
	public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes( 15 );

	public AssetKind Classify( string sourceExtension, out string outputExtension ) => types.Classify( sourceExtension, out outputExtension );

	public CompileResult Compile( CompileJob job, CancellationToken token )
	{
		var report = Path.Combine( job.Staging, "report.txt" );
		var args = new List<string>
		{
			// A few threads each: a lone heavy compile (a skybox prefilter) finishes ~2.5x sooner, and a machine
			// already full of compiles loses nothing to the extra threads
			"-skiprendersystem", "-testmode", "-ignorecontentasserts", "-unbufferedio", "-threads", "4",
			// Each mount is added in front of the last, so the order matches the editor's
			"-searchpaths", string.Join( ";", job.Mounts.Select( x => $"{x.Mod}|{x.Path}" ) ), "-buildreport", report, "-outroot", job.Staging,
		};

		// Root and children always compile; associates never do in a managed build
		args.Add( job.Mode switch { JobMode.Describe => "-describe", JobMode.Adopt => "-adopt", _ => "-fshallow2" } );
		if ( job.Mode == JobMode.Describe ) args.Add( "-fshallow2" );
		args.AddRange( job.CompilerArgs );
		args.AddRange( ["-i", job.Source] );

		var exitCode = Run( args, job.Root.Path, token, out var log );
		var lines = File.Exists( report ) ? File.ReadAllLines( report ) : [];
		File.Delete( report );

		return BuildReportParser.Parse( lines, exitCode == 0, log );
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
