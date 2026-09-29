using System.IO;

namespace Editor;

internal static class SceneSource
{
	internal static Asset FindAsset( SceneFile file )
	{
		if ( file is null )
			return null;

		var asset = AssetSystem.FindByPath( file.ResourcePath );
		if ( file.Guid != System.Guid.Empty && (asset is null || asset.Guid != file.Guid) )
			asset = AssetSystem.All.FirstOrDefault( x => x.Guid == file.Guid );

		if ( asset is not null && file.IsSourceSnapshot && file.ResourcePath != asset.Path )
			file.InitializeSource( asset.Path, asset.Guid );

		return asset;
	}

	internal static SceneFile LoadForEditing( Asset asset )
	{
		var path = asset.GetSourceFile( true );
		var json = File.ReadAllText( path );
		if ( json.StartsWith( '<' ) )
		{
			var kv = NativeEngine.EngineGlue.LoadKeyValues3( json );
			json = NativeEngine.EngineGlue.KeyValues3ToJson( kv.FindOrCreateMember( "data" ) );
			kv.DeleteThis();
		}

		var blobPath = path + "_d";
		var blobs = File.Exists( blobPath ) ? File.ReadAllBytes( blobPath ) : [];
		return SceneFile.FromSource( asset.Path, asset.Guid, json, blobs );
	}

	internal static SceneFile ResolveRuntime( SceneFile file )
	{
		if ( file.IsSourcePreview || string.IsNullOrEmpty( file.ResourcePath ) )
			return file;

		var asset = FindAsset( file );
		if ( asset is null || !SceneCompileCache.HasCompilation( asset ) )
			return file;

		var session = SceneEditorSession.All.FirstOrDefault( x => x.Scene is { IsEditor: true, Source: SceneFile source } && FindAsset( source ) == asset );
		if ( session?.HasUnsavedChanges == true )
			return PreviewSource( asset, session, "The scene has unsaved changes. Save and use Scene > Compile Scene to update its compiled data." );

		if ( !SceneCompileCache.Validate( asset, out var error ) )
			return PreviewSource( asset, session, error );

		var compiledPath = asset.GetCompiledFile( true );
		if ( string.IsNullOrEmpty( compiledPath ) )
			compiledPath = asset.GetSourceFile( true ) + "_c";

		return SceneFile.FromCompiled( asset.Path, asset.Guid, File.ReadAllBytes( compiledPath ) );
	}

	static SceneFile PreviewSource( Asset asset, SceneEditorSession session, string reason )
	{
		if ( session is null && !File.Exists( asset.GetSourceFile( true ) ) )
		{
			Log.Error( $"Cannot preview '{asset.Path}': its editable source is missing. {reason}" );
			return null;
		}

		var file = session?.Scene.CreateSceneFile() ?? LoadForEditing( asset );
		file.IsSourcePreview = true;
		Log.Warning( $"Playing '{asset.Path}' uncompiled. {reason}" );
		return file;
	}

	internal static bool PreparePlay( SceneEditorSession session, out SceneLoadOptions options )
	{
		options = null;
		var asset = FindAsset( session.Scene.Source as SceneFile );
		if ( asset is null || !SceneCompileCache.HasCompilation( asset ) )
			return true;

		options = new SceneLoadOptions();
		options.SetScene( session.Scene.Source as SceneFile );
		return options.PrepareRuntime();
	}
}
