using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Sandbox.ContentBuild;

namespace ContentBuild;

/// <summary>
/// Sandbox.ContentBuild with resourcecompiler.exe, its records and staging under the fixture's parent.
/// Several jobs at once by default, so ordering mistakes show up as races; one job compiles on the
/// calling thread, as the editor does.
/// </summary>
public sealed class ManagedBuilder : IContentBuilder
{
	public string Name => MaxJobs == 1 ? "managed-inline" : "managed";
	public int MaxJobs { get; init; } = 4;

	internal Action<string> BeforeCommit { get; init; }

	static readonly AssetTypeTable Types = new( Path.Combine( ContentFixture.Engine, "bin", "assettypes.txt" ) );

	public static ResourceCompiler Compiler() => new( Path.Combine( ContentFixture.Engine, "bin", "win64", "resourcecompiler.exe" ), Types );

	static string State( ContentFixture fixture ) => Path.Combine( fixture.Parent, "state" );

	public BuildResult Build( ContentFixture fixture, BuildOptions options, CancellationToken token )
	{
		var request = new BuildRequest
		{
			Roots = [new ContentRoot( fixture.Mod, fixture.Root, State( fixture ) )],
			Select = options.Select?.Select( fixture.Path ).ToList(),
			Force = options.Force,
			CompilerArgs = options.CompilerArgs,
			MaxJobs = MaxJobs,
			JobFinished = options.JobFinished,
			BeforeCommit = BeforeCommit,
		};

		var compiler = Compiler();
		var report = ContentBuilder.Build( request, compiler, token );

		var failed = new List<string>( report.Failed );
		failed.AddRange( report.Blocked );
		failed.AddRange( report.Unsupported );
		return new BuildResult( report.Success, failed, report.Jobs, report.Log.ToString(), report.DescribeMisses );
	}

	public void Clean( ContentFixture fixture )
	{
		if ( Directory.Exists( State( fixture ) ) ) Directory.Delete( State( fixture ), true );
	}
}
