using NativeEngine;
using Sandbox.ContentBuild;
using Sandbox.Engine;
using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Editor;

/// <summary>
/// The editor's content build, the same pipeline as SboxBuild build-content: one build over every root the editor
/// mounts, so a compile that reads another root's output waits for it. It builds everything at startup and again
/// after files change (the asset system counts changes, the frame tick builds them), which rebuilds whatever a
/// change affects, in order. The asset system compiles single assets itself (a load of a changed one, a tool's
/// compile); their outputs record what they were built from, so the build takes them as they are rather than
/// compiling them again. Compiles run one at a time on the main thread, where game resources and managed compilers
/// live.
/// </summary>
internal static class EditorContentBuild
{
	static Project project;
	static bool building;
	static bool pruneOrphans;
	static int builtEpoch = -1;
	static int seenEpoch;
	static RealTimeSince sinceChange;
	static RealTimeUntil retryAfter;
	static HashSet<string> current = new( StringComparer.OrdinalIgnoreCase );
	static HashSet<string> sharedOutputs = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>
	/// Changes usually come in bursts (a save writes several files, a checkout many)
	/// </summary>
	const float Debounce = 0.25f;

	/// <summary>
	/// A build that couldn't run (another build holds a root) tries again after this, not every frame
	/// </summary>
	const float RetryDelay = 2.0f;

	public static bool IsBuilding => building;

	/// <summary>
	/// Builds everything before the editor opens, waiting for any other build of the same roots.
	/// </summary>
	public static void Startup( Project project )
	{
		EditorContentBuild.project = project;
		Update( true );
	}

	/// <summary>
	/// A file in another output root that a record still lists, which the orphan collector has to leave.
	/// </summary>
	public static bool IsOwned( string path ) => sharedOutputs.Contains( Path.GetFullPath( path ) );

	[EditorEvent.Frame]
	static void Frame()
	{
		if ( project is null || building ) return;

		if ( pruneOrphans )
		{
			pruneOrphans = false;
			AssetSystem.Tick();
			AssetSystem.DeleteOrphans();
		}

		var epoch = IAssetSystem.GetContentChangeEpoch();
		if ( epoch != seenEpoch )
		{
			seenEpoch = epoch;
			sinceChange = 0;
		}

		if ( epoch != builtEpoch && sinceChange > Debounce && retryAfter ) Update( false );
	}

	/// <summary>
	/// One build over every mounted root, then everything the asset system needs to know about it: each source's
	/// verdict and the files it published, which reloads what's loaded. Nothing compiles itself on load meanwhile.
	/// </summary>
	static void Update( bool startup )
	{
		building = true;
		IAssetSystem.SuspendOnDemandCompiles( true );

		try
		{
			// Changes made before this build (the .meta files asset registration writes) are ones it sees, so count
			// them now rather than when the watcher next delivers them
			IAssetSystem.RunFrame();
			var epoch = IAssetSystem.GetContentChangeEpoch();

			var roots = Roots( project );

			// A single asset compiled by the asset system holds the roots' locks, as this build does
			IAssetSystem.SetContentRootLocks( string.Join( "\n", roots.Select( ContentBuilder.LockFile ) ) );

			var compiler = new EditorAssetCompiler();
			var report = ContentBuilder.Build( new BuildRequest
			{
				Roots = roots,

				// Game resources anywhere generate their textures into the open project's transient folder
				OutputRoots = new Dictionary<string, string> { ["transient"] = Path.Combine( project.GetRootPath(), ".sbox", "transient" ) },

				// The engine's shipped transients, which every project but the menu mounts after its own
				OutputFallbacks = project.Config.Ident == "menu" ? new Dictionary<string, string>() : new Dictionary<string, string> { ["transient"] = EngineFileSystem.Root.GetFullPath( "addons/menu/transients" ) },

				// What each job publishes is known before the next frame runs, so its file events aren't changes
				Published = paths => IAssetSystem.RegisterBuildOutputs( string.Join( "\n", paths ) ),
				MaxJobs = 1,

				// Only startup waits for another build, with the splash screen up; the editor never freezes on one
				LockTimeout = startup ? TimeSpan.FromMinutes( 30 ) : TimeSpan.Zero,
				Waiting = startup ? root =>
				{
					EditorSplashScreen.SetMessage( $"Waiting for another content build of {root}" );
					IToolsDll.Current?.Spin();
				} : null,
				JobFinished = startup ? source =>
				{
					EditorSplashScreen.SetMessage( $"Compiled {source}" );
					IToolsDll.Current?.Spin();
				} : null,
			}, compiler );

			Report( report, roots, startup );

			if ( report.Error is not null || report.Cancelled || compiler.ReloadPending )
			{
				retryAfter = RetryDelay;
				return;
			}

			Publish( report, epoch );
			builtEpoch = epoch;
			pruneOrphans |= report.Changed.Count > 0;
		}
		catch ( Exception e )
		{
			Log.Error( e, "Content build failed" );
			retryAfter = RetryDelay;
		}
		finally
		{
			IAssetSystem.SuspendOnDemandCompiles( false );
			building = false;
		}
	}

	static void Publish( BuildReport report, int epoch )
	{
		// Only what's current is the build's to say; why anything else isn't is for a compile of it to find out
		foreach ( var source in report.Current )
			IAssetSystem.SetAssetBuiltAt( source, epoch );

		// No longer current: failed now, gone, or not built here any more (a root was unmounted)
		foreach ( var source in current.Where( x => !report.Current.Contains( x ) ) )
			IAssetSystem.SetAssetBuiltAt( source, -1 );

		current = report.Current;
		sharedOutputs = report.SharedOutputs;

	}

	static void Report( BuildReport report, List<ContentRoot> roots, bool startup )
	{
		if ( startup )
		{
			foreach ( var root in roots )
			{
				int Count( List<string> list ) => list.Count( x => x.StartsWith( $"{root.Mod}:", StringComparison.OrdinalIgnoreCase ) );
				Log.Info( $"Content {root.Mod}: {Count( report.Jobs )} compiles, {Count( report.Failed )} failed, {Count( report.Blocked )} blocked, {Count( report.Adopted )} adopted, {Count( report.Described )} described, {Count( report.Removed )} removed, {Count( report.Unsupported )} unsupported" );
			}
			Log.Info( $"Content build: {report.DescribeMisses} describe misses" );
		}
		else if ( report.Jobs.Count > 0 || report.Removed.Count > 0 || report.Adopted.Count > 0 )
		{
			Log.Info( $"Content build: {report.Jobs.Count} compiles, {report.Adopted.Count} adopted, {report.Failed.Count} failed, {report.Blocked.Count} blocked, {report.Removed.Count} removed" );
		}

		if ( report.Error is not null ) Log.Warning( $"Content wasn't built: {report.Error}" );
		else if ( report.Failed.Count > 0 || report.Blocked.Count > 0 ) Log.Warning( report.Log.ToString() );
	}

	/// <summary>
	/// Everything the editor mounts and can write to, in mount order: engine roots, libraries, the project.
	/// </summary>
	static List<ContentRoot> Roots( Project project ) => ContentRoots.WithProjects(
		ContentRoots.Engine( EngineFileSystem.Root.GetFullPath( "/" ) ),
		LibrarySystem.All.Select( x => x.Project ).Append( project ).Where( x => x.HasAssetsPath() )
			.Select( x => (x.Config.FullIdent, x.GetAssetsPath(), x.GetRootPath()) ) );
}

/// <summary>
/// Compiles in the editor through the resource compiler, which hands game resources and managed
/// compilers over to managed code as usual.
/// </summary>
internal sealed class EditorAssetCompiler : IAssetCompiler
{
	readonly AssetTypeTable types = new( EngineFileSystem.Root.GetFullPath( "bin/assettypes.txt" ) );

	/// <summary>
	/// A compile didn't run because something it may read hadn't reloaded yet.
	/// </summary>
	public bool ReloadPending { get; private set; }

	/// <summary>
	/// The engine's types as SboxBuild sees them, plus game resources, which only the editor compiles.
	/// </summary>
	public AssetKind Classify( string sourceExtension, out string outputExtension )
	{
		var kind = types.Classify( sourceExtension, out outputExtension );

		// A texture can be a managed generator that copies another texture's compiled pixels; describing an
		// ordinary one costs nothing
		if ( kind == AssetKind.Compiled && string.Equals( sourceExtension, ".vtex", StringComparison.OrdinalIgnoreCase ) ) return AssetKind.Described;
		if ( kind != AssetKind.None ) return kind;

		var type = AssetType.FromExtension( sourceExtension.TrimStart( '.' ) );
		if ( type is null || !type.IsGameResource ) return AssetKind.None;

		// Its compiler declares the compiled outputs it reads (an embedded texture copying another), so a
		// describe reports them without generating anything
		outputExtension = $".{type.FileExtension}_c";
		return AssetKind.Described;
	}

	public CompileResult Compile( CompileJob job, CancellationToken token )
	{
		// A compile here can read loaded resources (a texture another copies), so what was just published reloads first.
		// One still waiting (its inputs are being written) fails the job, and the whole build is tried again shortly.
		if ( job.Mode == JobMode.Compile && !IAssetSystem.ServiceReloadsNow() )
		{
			ReloadPending = true;
			return new CompileResult( false, "Something it may read is still waiting to reload", [], [], [], [], [] );
		}

		using var report = IResourceCompilerSystem.GenerateResourceFileForBuild( job.Source, job.Staging, (int)job.Mode );
		var text = Encoding.UTF8.GetString( report.ToArray() );
		return BuildReportParser.Parse( text.Split( '\n' ), true, "" );
	}

	public IReadOnlyList<uint?> Fingerprints( IReadOnlyList<SpecialDependency> dependencies ) => dependencies
		.Select( x => IResourceCompilerSystem.CalculateSpecialDependencyFingerprint( x.Compiler, x.Name, x.Data ) )
		.Select( x => x < 0 ? (uint?)null : (uint)x )
		.ToArray();
}
