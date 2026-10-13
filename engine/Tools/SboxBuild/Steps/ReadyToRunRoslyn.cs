using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Facepunch.Steps;

/// <summary>
/// Swaps the IL-only Roslyn assemblies NuGet puts in the build output for ReadyToRun (precompiled)
/// copies of the same version. Otherwise every process JITs the compiler the first time it compiles
/// code - around 5 seconds in the editor, when joining a game and in each test project.
///
/// Optional: if anything goes wrong it warns and leaves the IL copies, which work the same, just slower.
/// </summary>
internal static partial class ReadyToRunRoslyn
{
	static readonly string[] Assemblies =
	[
		"Microsoft.CodeAnalysis.dll",
		"Microsoft.CodeAnalysis.CSharp.dll",
		"Microsoft.CodeAnalysis.Workspaces.dll",
		"Microsoft.CodeAnalysis.CSharp.Workspaces.dll",
	];

	internal static void Run( string rootDir )
	{
		try
		{
			var engineDir = Path.Combine( rootDir, "engine" );
			var precompiled = GetPrecompiled( engineDir );
			if ( precompiled is null )
				return;

			// The game, and the test projects so CI's test runs get the same benefit
			var targets = new List<string> { Path.Combine( rootDir, "game", "bin", "managed" ) };
			var testsDir = Path.Combine( engineDir, "Tests" );
			if ( Directory.Exists( testsDir ) )
				targets.AddRange( Directory.GetDirectories( testsDir ).Select( x => Path.Combine( x, "bin", "Release", "net10.0" ) ) );

			var replaced = 0;
			foreach ( var target in targets.Where( Directory.Exists ) )
			{
				foreach ( var name in Assemblies )
				{
					var destination = Path.Combine( target, name );
					if ( !File.Exists( destination ) )
						continue;

					var source = Path.Combine( precompiled, name );

					// Never swap in a different build of Roslyn than the one the engine was compiled against
					var expected = FileVersionInfo.GetVersionInfo( destination ).FileVersion;
					var actual = FileVersionInfo.GetVersionInfo( source ).FileVersion;
					if ( expected != actual )
					{
						Log.Warning( $"Not precompiling {destination}: it's version {expected}, ReadyToRun copy is {actual}" );
						continue;
					}

					File.Copy( source, destination, true );
					replaced++;
				}
			}

			Log.Info( $"Replaced {replaced} Roslyn assemblies with ReadyToRun copies" );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"Couldn't precompile Roslyn, keeping the IL copies: {ex.Message}" );
		}
	}

	/// <summary>
	/// Returns a folder holding the precompiled assemblies, publishing them if this machine doesn't
	/// have them cached yet. The cache lives outside the checkout so clean CI builds reuse it.
	/// </summary>
	static string GetPrecompiled( string engineDir )
	{
		var csproj = File.ReadAllText( Path.Combine( engineDir, "Sandbox.Compiling", "Sandbox.Compiling.csproj" ) );
		var match = RoslynReference().Match( csproj );
		if ( !match.Success )
		{
			Log.Warning( "Couldn't find the Roslyn version Sandbox.Compiling uses, skipping ReadyToRun" );
			return null;
		}

		var version = match.Groups["version"].Value;
		var rid = RuntimeInformation.RuntimeIdentifier;
		var cache = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "sbox-build", "roslyn-r2r", $"{version}-{rid}" );

		if ( Assemblies.All( name => File.Exists( Path.Combine( cache, name ) ) ) )
			return cache;

		Log.Info( $"Publishing ReadyToRun Roslyn {version} for {rid}" );
		var output = Path.Combine( Path.GetTempPath(), $"sbox-roslyn-r2r-{Guid.NewGuid():N}" );
		try
		{
			var project = Path.Combine( "Tools", "RoslynReadyToRun", "RoslynReadyToRun.csproj" );
			if ( !Utility.RunDotnetCommand( engineDir, $"publish {project} -c Release -r {rid} -p:RoslynVersion={version} -o \"{output}\"" ) )
			{
				Log.Warning( "Publishing ReadyToRun Roslyn failed, keeping the IL copies" );
				return null;
			}

			Directory.CreateDirectory( cache );
			foreach ( var name in Assemblies )
				File.Copy( Path.Combine( output, name ), Path.Combine( cache, name ), true );

			return cache;
		}
		finally
		{
			if ( Directory.Exists( output ) )
				Directory.Delete( output, true );
		}
	}

	[GeneratedRegex( @"Include=""Microsoft\.CodeAnalysis\.CSharp""\s+Version=""(?<version>[^""]+)""" )]
	private static partial Regex RoslynReference();
}
