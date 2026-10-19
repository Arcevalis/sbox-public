using System;
using System.IO;
using System.Text.Json;

namespace Editor;

internal static class PrefabCompiler
{
	/// Whether a prefab recompile must bypass native <c>RecompileAsset</c>, which opens
	/// sources by lowercase identity and cannot open mixed-case sources on Linux.
	internal static bool ShouldBypassNativePrefabCompile( Asset asset )
	{
		if ( asset is null || asset.IsDeleted ) return false;
		if ( !string.Equals( Path.GetExtension( asset.Path ), ".prefab", StringComparison.OrdinalIgnoreCase ) ) return false;
		return AssetSystem.SourceSpellingDiffersFromIdentity( asset );
	}

	/// Recompile a mixed-case prefab from memory through the lowercase identity, then
	/// reload. Blobs travel via the identity <c>_d</c> sidecar.
	internal static bool CompileCaseMismatchedPrefab( Asset asset )
	{
		if ( !ShouldBypassNativePrefabCompile( asset ) ) return false;

		var sourcePath = asset.GetSourceFile( true );

		string json;
		try
		{
			json = SceneSource.ReadJson( sourcePath );
		}
		catch ( Exception e ) when ( e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException )
		{
			Log.Error( e, $"Could not load prefab source '{asset.Path}'." );
			return false;
		}

		var previousOutput = asset.GetCompiledFile( true );
		var previousBytes = !string.IsNullOrEmpty( previousOutput ) && File.Exists( previousOutput ) ? File.ReadAllBytes( previousOutput ) : null;

		try
		{
			// Compile through the lowercase identity: same asset, lowercase _c.
			var compilePath = AssetSystem.LowercaseAssetFileName( sourcePath );

			if ( !IResourceCompilerSystem.GenerateResourceFileForced( compilePath, json ) )
				throw new InvalidOperationException( $"Could not write compiled prefab '{asset.Path}'." );

			var outputPath = asset.GetCompiledFile( true );
			if ( string.IsNullOrEmpty( outputPath ) || !File.Exists( outputPath ) )
				throw new InvalidOperationException( $"Could not find the compiled output path for '{asset.Path}'." );

			NativeEngine.g_pResourceSystem.ReloadResource( asset.Path );
			return true;
		}
		catch ( Exception e )
		{
			Log.Error( e, $"Could not compile prefab '{asset.Path}'." );

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
				Log.Warning( $"Could not restore the last compiled prefab '{asset.Path}': {restoreException.Message}" );
			}

			return false;
		}
	}
}
