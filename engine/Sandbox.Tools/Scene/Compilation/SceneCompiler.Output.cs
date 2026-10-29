using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Editor;

internal static partial class SceneCompiler
{
	readonly record struct SourceRevision( long Length, DateTime LastWriteTimeUtc, byte[] Hash );

	static SourceRevision CaptureSourceRevision( string sourcePath )
	{
		var content = File.ReadAllBytes( sourcePath );
		var info = new FileInfo( sourcePath );
		return new SourceRevision( content.LongLength, info.LastWriteTimeUtc, System.Security.Cryptography.SHA256.HashData( content ) );
	}

	static bool HasSourceRevision( string sourcePath, SourceRevision revision )
	{
		var before = new FileInfo( sourcePath );
		if ( !before.Exists || before.Length != revision.Length || before.LastWriteTimeUtc != revision.LastWriteTimeUtc )
			return false;

		var content = File.ReadAllBytes( sourcePath );
		var after = new FileInfo( sourcePath );
		return after.Exists && after.Length == revision.Length && after.LastWriteTimeUtc == revision.LastWriteTimeUtc
			&& System.Security.Cryptography.CryptographicOperations.FixedTimeEquals( revision.Hash, System.Security.Cryptography.SHA256.HashData( content ) );
	}

	static string GetGenerationPath( Asset asset, string generation )
	{
		// Same lowercase-identity derivation as RunGeneration's resourceFolder.
		var sourcePath = AssetSystem.LowercaseAssetFileName( asset.GetSourceFile( false ) );
		var extension = Path.GetExtension( sourcePath ).Replace( ".", "_" );
		var dataFolder = $"{Path.ChangeExtension( sourcePath, null )}{extension}_data";
		return $"{dataFolder}/compiled/{generation}".NormalizeFilename( false );
	}

	internal static void CleanupGeneration( string path )
	{
		try
		{
			if ( FileSystem.Mounted.DirectoryExists( path ) )
				DeleteStagedDirectory( path );
		}
		catch ( Exception e )
		{
			Log.Warning( $"Could not clean up staged scene resources at '{path}': {e.Message}" );
		}
	}

	// FileSystem.Mounted is a read-only aggregate view, so mutating it always throws:
	// resolve the physical path and delete through System.IO instead.
	static void DeleteStagedDirectory( string path )
	{
		var physical = FileSystem.Mounted.GetFullPath( path );
		if ( string.IsNullOrEmpty( physical ) || !Directory.Exists( physical ) )
			return;

		Directory.Delete( physical, true );
	}

	static void DeleteStagedFile( string path )
	{
		var physical = FileSystem.Mounted.GetFullPath( path );
		if ( string.IsNullOrEmpty( physical ) || !File.Exists( physical ) )
			return;

		File.Delete( physical );
	}

	internal static void CleanupPreviousGenerations( string currentPath, object generationGate, HashSet<string> activeGenerations )
	{
		var compiledFolder = currentPath[..currentPath.LastIndexOf( '/' )];
		HashSet<string> active;

		lock ( generationGate )
			active = new HashSet<string>( activeGenerations, StringComparer.OrdinalIgnoreCase );

		// The aggregate merges every mounted layer, so only delete victims sitting beside
		// the generation just written - never lower-layer shadows of this folder.
		var scope = Path.GetDirectoryName( FileSystem.Mounted.GetFullPath( currentPath ) );

		try
		{
			foreach ( var directory in FileSystem.Mounted.FindDirectory( compiledFolder ).ToArray() )
			{
				var path = $"{compiledFolder}/{directory}".NormalizeFilename( false );
				if ( path.Equals( currentPath, StringComparison.OrdinalIgnoreCase ) || active.Contains( path ) )
					continue;

				if ( !IsStagedInScope( path, scope ) )
					continue;

				CleanupGeneration( path );
			}

			foreach ( var file in FileSystem.Mounted.FindFile( compiledFolder ).ToArray() )
			{
				var path = $"{compiledFolder}/{file}".NormalizeFilename( false );
				try
				{
					if ( IsStagedInScope( path, scope ) )
						DeleteStagedFile( path );
				}
				catch ( Exception e )
				{
					Log.Warning( $"Could not clean up old scene resource '{path}': {e.Message}" );
				}
			}
		}
		catch ( Exception e )
		{
			Log.Warning( $"Could not clean up old compiled scene resources in '{compiledFolder}': {e.Message}" );
		}
	}

	static bool IsStagedInScope( string path, string scope )
	{
		if ( string.IsNullOrEmpty( scope ) )
			return true;

		var physical = FileSystem.Mounted.GetFullPath( path );
		if ( string.IsNullOrEmpty( physical ) )
			return false;

		var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
		return string.Equals( Path.GetDirectoryName( physical ), scope, comparison );
	}

	static void WriteScene( Asset asset, string sourcePath, SceneEditorSession editor, SourceRevision sourceRevision, SceneFile file, SceneCompilerSettings settings )
	{
		if ( asset.IsDeleted || !File.Exists( sourcePath ) || sourcePath != asset.GetSourceFile( true ) )
			throw new InvalidOperationException( "The source scene was moved or deleted during compilation." );

		if ( editor.HasUnsavedChanges || !HasSourceRevision( sourcePath, sourceRevision ) )
			throw new InvalidOperationException( "The source scene changed during compilation. Save it and compile again." );

		var json = file.Serialize();
		json["__blobdata"] = System.Convert.ToBase64String( file.BinaryData ?? [] );
		var previousOutput = asset.GetCompiledFile( true );
		var previousBytes = !string.IsNullOrEmpty( previousOutput ) && File.Exists( previousOutput ) ? File.ReadAllBytes( previousOutput ) : null;

		try
		{
			// Outputs derive from this path string, so compile through the lowercase
			// identity: same asset (lookups are case-insensitive), lowercase _c/_d.
			var compilePath = AssetSystem.LowercaseAssetFileName( sourcePath );

			if ( !IResourceCompilerSystem.GenerateResourceFileForced( compilePath, json.ToJsonString( new JsonSerializerOptions( JsonSerializerOptions.Default ) { MaxDepth = 512 } ) ) )
				throw new InvalidOperationException( $"Could not write compiled scene '{asset.Path}'." );

			var outputPath = asset.GetCompiledFile( true );
			if ( string.IsNullOrEmpty( outputPath ) )
				throw new InvalidOperationException( $"Could not find the compiled output path for '{asset.Path}'." );

			var bytes = File.ReadAllBytes( outputPath );
			using var compiledJson = JsonDocument.Parse( Game.Resources.ReadCompiledResourceJson( bytes ), new JsonDocumentOptions { MaxDepth = 512 } );
			if ( compiledJson.RootElement.TryGetProperty( "__blobdata", out _ ) )
				throw new InvalidOperationException( $"The scene resource compiler did not process '{asset.Path}'." );

			// Native reopens the source by its lowercase identity, which cannot open a
			// mixed-case source on a case-sensitive filesystem. The forced write above
			// already produced and verified the output, and ReloadResource below picks it
			// up - so only refresh through native when it can actually open the source.
			if ( !AssetSystem.SourceSpellingDiffersFromIdentity( asset ) && !asset.Compile( false ) )
				throw new InvalidOperationException( $"Could not refresh compiled scene '{asset.Path}'." );

			if ( editor.HasUnsavedChanges || !HasSourceRevision( sourcePath, sourceRevision ) )
				throw new InvalidOperationException( "The source scene changed during compilation. Save it and compile again." );

			NativeEngine.g_pResourceSystem.ReloadResource( asset.Path );
			asset.MetaData.Set( SceneCompilerSettings.MetadataProperty, settings );
		}
		catch
		{
			try
			{
				var outputPath = asset.GetCompiledFile( true );
				if ( string.IsNullOrEmpty( outputPath ) )
					outputPath = previousOutput;
				if ( !string.IsNullOrEmpty( outputPath ) )
				{
					if ( previousBytes is not null )
						File.WriteAllBytes( outputPath, previousBytes );
					else if ( File.Exists( outputPath ) )
						File.Delete( outputPath );

					NativeEngine.g_pResourceSystem.ReloadResource( asset.Path );
				}
			}
			catch ( Exception e )
			{
				Log.Warning( $"Could not restore the last compiled scene '{asset.Path}': {e.Message}" );
			}

			throw;
		}
	}

	/// <summary>
	/// Whether a scene recompile must bypass native <c>RecompileAsset</c>. Native file IO
	/// uses the lowercase identity spelling, which cannot open a source that keeps a
	/// mixed-case disk spelling on a case-sensitive filesystem. The native scene compiler
	/// stays the compiler - it is fed from memory instead (see <see cref="CompileCaseMismatchedScene"/>).
	/// </summary>
	internal static bool ShouldBypassNativeSceneCompile( Asset asset )
	{
		if ( asset is null || asset.IsDeleted ) return false;
		if ( !string.Equals( asset.AssetType?.FileExtension, "scene", StringComparison.OrdinalIgnoreCase ) ) return false;
		return AssetSystem.SourceSpellingDiffersFromIdentity( asset );
	}

	/// <summary>
	/// Recompile a mixed-case scene source without native source file IO. Mirrors the
	/// <see cref="WriteScene"/> feed: load from the physical spelling (source + _d blobs),
	/// hand the native compiler its input text addressed to the lowercase identity, verify
	/// it processed the output, then reload the resource.
	/// </summary>
	internal static bool CompileCaseMismatchedScene( Asset asset )
	{
		if ( !ShouldBypassNativeSceneCompile( asset ) ) return false;

		var sourcePath = asset.GetSourceFile( true );

		SceneFile file;
		try
		{
			file = SceneSource.LoadForEditing( asset );
		}
		catch ( Exception e ) when ( e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException )
		{
			Log.Error( e, $"Could not load scene source '{asset.Path}'." );
			return false;
		}

		if ( file is null )
		{
			Log.Error( $"Could not load scene source '{asset.Path}'." );
			return false;
		}

		var json = file.Serialize();
		json["__blobdata"] = System.Convert.ToBase64String( file.BinaryData ?? [] );
		var previousOutput = asset.GetCompiledFile( true );
		var previousBytes = !string.IsNullOrEmpty( previousOutput ) && File.Exists( previousOutput ) ? File.ReadAllBytes( previousOutput ) : null;

		try
		{
			// Outputs derive from this path string, so compile through the lowercase
			// identity: same asset (lookups are case-insensitive), lowercase _c.
			var compilePath = AssetSystem.LowercaseAssetFileName( sourcePath );

			if ( !IResourceCompilerSystem.GenerateResourceFileForced( compilePath, json.ToJsonString( new JsonSerializerOptions( JsonSerializerOptions.Default ) { MaxDepth = 512 } ) ) )
				throw new InvalidOperationException( $"Could not write compiled scene '{asset.Path}'." );

			var outputPath = asset.GetCompiledFile( true );
			if ( string.IsNullOrEmpty( outputPath ) )
				throw new InvalidOperationException( $"Could not find the compiled output path for '{asset.Path}'." );

			var bytes = File.ReadAllBytes( outputPath );
			using var compiledJson = JsonDocument.Parse( Game.Resources.ReadCompiledResourceJson( bytes ), new JsonDocumentOptions { MaxDepth = 512 } );
			if ( compiledJson.RootElement.TryGetProperty( "__blobdata", out _ ) )
				throw new InvalidOperationException( $"The scene resource compiler did not process '{asset.Path}'." );

			NativeEngine.g_pResourceSystem.ReloadResource( asset.Path );
			return true;
		}
		catch ( Exception e )
		{
			Log.Error( e, $"Could not compile scene '{asset.Path}'." );

			try
			{
				var outputPath = asset.GetCompiledFile( true );
				if ( string.IsNullOrEmpty( outputPath ) )
					outputPath = previousOutput;
				if ( !string.IsNullOrEmpty( outputPath ) )
				{
					if ( previousBytes is not null )
						File.WriteAllBytes( outputPath, previousBytes );
					else if ( File.Exists( outputPath ) )
						File.Delete( outputPath );

					NativeEngine.g_pResourceSystem.ReloadResource( asset.Path );
				}
			}
			catch ( Exception restoreException )
			{
				Log.Warning( $"Could not restore the last compiled scene '{asset.Path}': {restoreException.Message}" );
			}

			return false;
		}
	}
}
