using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace Editor;

internal static partial class SceneCompileCache
{
	internal sealed record FileValidation( bool HasCompilation, bool IsCurrent, string Error, string[] Paths );

	internal static FileValidation ValidateFiles( string source, string compiled, CancellationToken cancel = default )
	{
		var paths = new HashSet<string>( StringComparer.OrdinalIgnoreCase )
		{
			source, source + "_d", MetadataPath( source ), ManifestPath( source ), compiled
		};
		try
		{
			if ( !File.Exists( ManifestPath( source ) ) )
			{
				var required = ReadMetadata( ReadMetadataBytes( source ) )[RequiredProperty]?.GetValue<bool>() == true;
				if ( !required && File.Exists( compiled ) )
				{
					try
					{
						var json = ReadRuntimeJson( File.ReadAllBytes( compiled ) );
						required = JsonNode.Parse( json, default, new() { MaxDepth = 512 } )?["__scene_compiled"]?.GetValue<bool>() == true;
					}
					catch ( Exception e ) when ( e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or FormatException )
					{
						Log.Warning( $"Cannot read ordinary compiled scene '{source}': {e.Message}. Recompile it from source." );
					}
				}
				return new( required, !required, required ? Error( source, "is missing its generated cache" ) : null, [.. paths] );
			}

			var compilation = JsonSerializer.Deserialize<Compilation>( File.ReadAllText( ManifestPath( source ) ), JsonOptions );
			ValidateCompilationFiles( source, compilation, cancel, paths );
			ValidateRuntime( File.ReadAllBytes( compiled ), compilation );
			return new( true, true, null, [.. paths] );
		}
		catch ( Exception e ) when ( e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or FormatException )
		{
			return new( true, false, Error( source, e.Message.TrimEnd( '.' ) ), [.. paths] );
		}
	}

	static void ValidateCompilationFiles( string source, Compilation compilation, CancellationToken cancel, HashSet<string> paths )
	{
		if ( compilation is null || compilation.Version != Version || !Guid.TryParseExact( compilation.Generation, "N", out _ )
			|| compilation.Source?.Inputs is null || compilation.Source.Inputs.Count == 0 || compilation.Outputs is null
			|| !compilation.Outputs.ContainsKey( SceneJson ) || !compilation.Outputs.ContainsKey( SceneBlob ) )
			throw new InvalidDataException( "Invalid scene compilation manifest." );

		foreach ( var name in compilation.Source.Inputs.Keys )
			paths.Add( InputPath( source, name ) );

		foreach ( var (name, hash) in compilation.Source.Inputs )
		{
			cancel.ThrowIfCancellationRequested();
			if ( InputFingerprint( InputPath( source, name ) ) != hash )
				throw new InvalidDataException( Error( source, "is stale because a saved input changed" ) );
		}

		foreach ( var (name, hash) in compilation.Outputs )
		{
			cancel.ThrowIfCancellationRequested();
			var path = OutputPath( source, compilation, name );
			paths.Add( path );
			if ( Path.GetFileName( name ) != name || hash == Missing || Fingerprint( path ) != hash )
				throw new InvalidDataException( $"Generated data '{name}' changed or is missing." );
		}
	}

	static void ValidateRuntime( byte[] data, Compilation compilation )
	{
		var json = ReadRuntimeJson( data );
		var runtime = JsonNode.Parse( json, default, new() { MaxDepth = 512 } );
		if ( runtime?[GenerationProperty]?.GetValue<string>() != compilation.Generation
			|| runtime["__guid"]?.GetValue<Guid>() != compilation.SceneId
			|| runtime["__scene_compiled"]?.GetValue<bool>() != true )
			throw new InvalidDataException( $"The runtime scene does not match its compilation (expected {compilation.Generation}, got {runtime?[GenerationProperty]})." );

		var blob = Game.Resources.ReadCompiledResourceBlock( BlobDataSerializer.CompiledBlobName, data ) ?? [];
		if ( Convert.ToHexString( SHA256.HashData( blob ) ) != compilation.Outputs[SceneBlob] )
			throw new InvalidDataException( "The runtime scene has mismatched binary data." );
	}

	static string ReadRuntimeJson( byte[] data )
	{
		if ( data.Length < 16 || BinaryPrimitives.ReadUInt16LittleEndian( data.AsSpan( 6 ) ) > 1 )
			throw new InvalidDataException( "Invalid compiled scene resource version." );
		var json = Game.Resources.ReadCompiledResourceBlock( "DATA", data )
			?? throw new InvalidDataException( "Compiled scene has no DATA block." );
		return Encoding.UTF8.GetString( json ).TrimEnd( '\0' );
	}
}
