using System;
using System.IO;

namespace Editor;

/// <summary>
/// A filesystem that can be accessed by the game.
/// </summary>
public static class FileSystem
{
	/// <summary>
	/// Paths from tool addons which are mounted.
	/// </summary>
	public static BaseFileSystem Mounted => Sandbox.FileSystem.Mounted;

	/// <summary>
	/// Root of the game's folder.
	/// </summary>
	public static BaseFileSystem Root => EngineFileSystem.Root;

	/// <summary>
	/// The engine /game/.source2/ folder for temporary files and caches.
	/// </summary>
	public static BaseFileSystem Temporary { get; internal set; }

	/// <summary>
	/// The engine /game/config/ folder
	/// </summary>
	public static BaseFileSystem Config => EngineFileSystem.Config;

	/// <summary>
	/// The engine /game/.source2/http/ folder.
	/// </summary>
	public static BaseFileSystem WebCache { get; internal set; }

	/// <summary>
	/// The current project's .sbox/ folder for temporary files and caches.
	/// </summary>
	public static BaseFileSystem ProjectTemporary { get; internal set; }

	/// <summary>
	/// The current project's .sbox/cloud/ folder. We download files from sbox.game right into this filesystem.
	/// </summary>
	public static BaseFileSystem Cloud { get; internal set; }

	/// <summary>
	/// The current project's .sbox/transient/ folder. This is where assets are created at runtime. These are assets
	/// that are created by another asset,that don't need to be stored in source control or anything - because they
	/// can get re-created at will.
	/// </summary>
	public static BaseFileSystem Transient { get; internal set; }

	/// <summary>
	/// Content from active addons (and content paths)
	/// </summary>
	public static BaseFileSystem Content { get; internal set; }

	/// <summary>
	/// The current project's ProjectSettings folder
	/// </summary>
	public static BaseFileSystem ProjectSettings { get; internal set; }

	/// <summary>
	/// The current project's Libraries folder
	/// </summary>
	public static BaseFileSystem Libraries { get; internal set; }

	/// <summary>
	/// The current project's Localization folder
	/// </summary>
	public static BaseFileSystem Localization { get; internal set; }

	/// <summary>
	/// Should be called on startup and whenever the mounted local addons have changed
	/// </summary>
	internal static void RebuildContentPath()
	{
		Content?.Dispose();
		Content = null;

		Content = new AggregateFileSystem();
		Content.CreateAndMount( EngineFileSystem.Root, "/core/" );
		Content.CreateAndMount( EngineFileSystem.Root, "/addons/citizen/Assets/" );
		Content.Mount( Cloud );

		foreach ( var addon in Project.All.Where( x => x.Active ) )
		{
			Content.Mount( addon.AssetsFileSystem );
		}

		var watch = Content.Watch();
		watch.OnChangedFile += OnContentFileChanged;

		// Mount assets from tool addons for Qt, example being hammer outliner
		foreach ( var addon in Project.All.Where( x => x.Active && x.Config.Type == "tool" ) )
		{
			var contentPath = addon.GetAssetsPath();
			if ( string.IsNullOrWhiteSpace( contentPath ) ) continue;
			if ( !System.IO.Directory.Exists( contentPath ) ) continue;

			QDir.addSearchPath( "toolimages", contentPath );
		}
	}

	/// <summary>
	/// Called when a file has changed on the <see cref="Content"/> path.
	/// </summary>
	private static void OnContentFileChanged( string filename )
	{
		ThreadSafe.AssertIsMainThread();

		// Retry files that couldn't be registered when they first appeared
		// (usually because they were still being written to).
		if ( pendingNewFiles.Count > 0 )
		{
			foreach ( var pending in pendingNewFiles.ToArray() )
			{
				if ( !string.Equals( pending, filename, StringComparison.OrdinalIgnoreCase ) )
					TryRegisterNewContentFile( pending );
			}
		}

		// Check to see if this asset was deleted from explorer - and mark it as deleted in the asset system.
		if ( AssetSystem.FindByPath( filename ) is Asset asset )
		{
			var fileExists = System.IO.File.Exists( asset.AbsolutePath );
			if ( !asset.IsDeleted && !fileExists )
			{
				asset.IsDeleted = true;
			}

			// Source file for a gameresource was edited, let's compile it (for detecting changes from version control)
			if ( asset.TryLoadResource<GameResource>( out var gameResource ) )
			{
				asset.Compile( false );
			}
		}
		else
		{
			// A file the asset system hasn't seen before. The native asset system only
			// discovers it on a full UpdateMods scan (i.e. an editor restart), so register
			// it now - otherwise newly added files stay invisible until then.
			TryRegisterNewContentFile( filename );
		}

		EditorEvent.Run( "content.changed", filename );
	}

	/// <summary>
	/// New files that couldn't be registered yet (still being written to, ...).
	/// Retried on the next content change event. Key is the content-relative path.
	/// </summary>
	static HashSet<string> pendingNewFiles = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>
	/// Register a file that appeared under a content path but isn't tracked by the
	/// asset system yet - e.g. dropped in from the OS while the editor is running.
	/// </summary>
	private static void TryRegisterNewContentFile( string filename )
	{
		var path = filename;

		// Compiled outputs are owned by their source asset (and by ResourceLoader's *_c
		// watchers) - never register them directly. If the source sibling exists but
		// isn't tracked either, fall through and register that instead; compiling it
		// produces the _c side.
		if ( path.EndsWith( "_c", StringComparison.OrdinalIgnoreCase ) )
		{
			path = path.Substring( 0, path.Length - 2 );
			if ( AssetSystem.FindByPath( path ) is not null )
				return; // source is tracked - its own change event drives the compile
		}

		// Re-check: the file may have registered itself since the event fired
		// (files created by the editor self-register on creation).
		if ( AssetSystem.FindByPath( path ) is not null )
		{
			pendingNewFiles.Remove( path );
			return;
		}

		// Only files with a known asset type are worth registering - this also
		// filters out junk like .meta sidecars, temp files and stray text.
		if ( AssetType.ResolveFromPath( path ) is null )
			return;

		var absolutePath = Content.GetFullPath( path );
		if ( string.IsNullOrWhiteSpace( absolutePath ) || !System.IO.File.Exists( absolutePath ) )
			return;

		// Anything under .sbox/ (cloud downloads, transient output, caches) is managed
		// by its own systems - leave it alone.
		if ( absolutePath.Contains( "/.sbox/", StringComparison.OrdinalIgnoreCase ) )
			return;

		// Tool addon files (toolimages, styles, ...) are loaded by path, never tracked
		// as assets - the native side always refuses them, so don't try.
		if ( IsUnderToolProject( absolutePath ) )
			return;

		try
		{
			var registered = AssetSystem.RegisterFile( absolutePath );
			if ( registered is null )
			{
				// Warn once - retries stay quiet to avoid spamming the console
				// on every subsequent content change.
				if ( pendingNewFiles.Add( path ) )
					Log.Warning( $"Something went wrong when registering {absolutePath}" );

				return;
			}

			pendingNewFiles.Remove( path );
			Log.Info( $"Registered new asset {registered.Path} from {filename}" );
		}
		catch ( System.Exception e )
		{
			// Most likely the file is still being written (locked / half copied).
			// Park it and retry when the next content change arrives - the follow-up
			// write events, or any other file activity, will trigger another attempt.
			pendingNewFiles.Add( path );
			Log.Trace( $"Couldn't register {absolutePath} yet, will retry ({e.Message})" );
		}
	}

	/// <summary>
	/// Whether an absolute path sits under an active tool project's folder.
	/// </summary>
	private static bool IsUnderToolProject( string absolutePath )
	{
		return Project.All.Any( x => x.Active && x.Config.Type == "tool" &&
			absolutePath.StartsWith( x.GetRootPath(), StringComparison.OrdinalIgnoreCase ) );
	}

	/// <summary>
	/// Stop the game from triggering a hotload for this file - because presumably you have
	/// already reloaded it.
	/// </summary>
	public static void SuppressNextHotload()
	{
		FileWatch.SuppressWatchers = RealTime.Now + 0.5f;
	}

	/// <summary>
	/// Initialize the editor filesytems from this project, which is assumably the main game project.
	/// </summary>
	internal static void InitializeFromProject( Project project )
	{
		var root = project.GetRootPath();

		var projectsFolder = Path.Combine( root, "ProjectSettings" );
		Directory.CreateDirectory( projectsFolder );
		ProjectSettings = new LocalFileSystem( projectsFolder );

		var librariesFolder = Path.Combine( root, "Libraries" );
		Directory.CreateDirectory( librariesFolder );
		Libraries = new LocalFileSystem( librariesFolder );

		var localizationFolder = Path.Combine( root, "Localization" );
		Directory.CreateDirectory( localizationFolder );
		Localization = new LocalFileSystem( localizationFolder );

		var sboxFolder = Path.Combine( root, ".sbox" );
		Directory.CreateDirectory( sboxFolder );
		ProjectTemporary = new LocalFileSystem( sboxFolder );

		// Set folder as hidden. Will hide it from explorer (by default) and from the asset browser
		var di = new DirectoryInfo( sboxFolder );
		di.Attributes |= FileAttributes.Hidden;

		var cloudFolder = Path.Combine( sboxFolder, "cloud" );
		Directory.CreateDirectory( cloudFolder );
		Cloud = new LocalFileSystem( cloudFolder );
		Mounted.Mount( Cloud );

		var transientFolder = Path.Combine( sboxFolder, "transient" );
		Directory.CreateDirectory( transientFolder );
		Transient = new LocalFileSystem( transientFolder );
		Mounted.Mount( Transient );
	}
}
