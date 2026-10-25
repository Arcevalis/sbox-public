using Sandbox.ContentBuild;
using static Facepunch.Constants;

namespace Facepunch.Steps;

/// <summary>
/// build-content: builds the engine content roots as one graph with Sandbox.ContentBuild. Records live where the
/// editor's startup build looks for them, so it starts from what this built.
/// </summary>
/// <param name="onlyRoot">A root under game/ to build, with whatever it reads from the others.</param>
internal class BuildContent( string onlyRoot = null )
{
	internal ExitCode Run() => BuildDisplay.Run( "Build content", () =>
	{
		var gameDir = Path.Combine( Directory.GetCurrentDirectory(), "game" );
		var binDir = Path.Combine( gameDir, "bin", NativePlatform.Current.DirectoryName );
		var executable = Path.Combine( binDir, OperatingSystem.IsWindows() ? "resourcecompiler.exe" : "resourcecompiler" );

		if ( !OperatingSystem.IsWindows() )
		{
			// Restore execute permission for downloaded binaries, including cached artifacts.
			var mode = File.GetUnixFileMode( executable );
			if ( (mode & UnixFileMode.UserExecute) == 0 )
				File.SetUnixFileMode( executable, mode | UnixFileMode.UserExecute );
		}

		var compiler = new ResourceCompiler( executable, new AssetTypeTable( Path.Combine( gameDir, "bin", "assettypes.txt" ) ) );
		var roots = ContentRoots.Engine( gameDir );
		var done = 0;
		BuildDisplay.Status( "Compile" );

		var report = ContentBuilder.Build( new BuildRequest
		{
			Roots = roots,
			Select = onlyRoot is null ? null : [Path.Combine( gameDir, onlyRoot )],
			JobFinished = _ => BuildDisplay.Status( $"Compile: {Interlocked.Increment( ref done )} done" ),
		}, compiler );

		if ( !BuildDisplay.IsActive || !report.Success ) Log.Info( report.Error ?? report.Log.ToString() );
		foreach ( var root in roots )
		{
			int Count( List<string> list ) => list.Count( x => x.StartsWith( $"{root.Mod}:", StringComparison.OrdinalIgnoreCase ) );
			Log.Info( $"{root.Mod}: {Count( report.Jobs )} compiled, {Count( report.Failed )} failed, {Count( report.Blocked )} blocked, {Count( report.Unsupported )} unsupported, {Count( report.Removed )} removed" );
		}
		Log.Info( $"{report.DescribeMisses} describe misses" );

		return report.Success ? ExitCode.Success : ExitCode.Failure;
	} );
}
