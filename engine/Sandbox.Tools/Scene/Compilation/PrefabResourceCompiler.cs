using NativeEngine;
using Sandbox.Resources;
using System;
using System.IO;
using System.Text;

namespace Editor;

[Expose]
[ResourceIdentity( "prefab" )]
internal class PrefabResourceCompiler : ResourceCompiler
{
	protected override Task<bool> Compile()
	{
		if ( Context is not ResourceCompileContextImp context )
			throw new InvalidOperationException( "Prefab compilation requires an editor resource context." );

		if ( !context.TryReadOverrideInput( out var input ) )
		{
			AssetSystem.CompileGameResource( context );
			return Task.FromResult( true );
		}

		// Same as CompileGameResource, but fed from the in-memory override text -
		// native cannot open a mixed-case source by its lowercase identity.
		var jsonString = Encoding.UTF8.GetString( input );

		if ( jsonString.StartsWith( '<' ) )
		{
			var kv = EngineGlue.LoadKeyValues3( jsonString );
			try
			{
				jsonString = EngineGlue.KeyValues3ToJson( kv.FindOrCreateMember( "data" ) );
			}
			finally
			{
				kv.DeleteThis();
			}
		}

		jsonString = context.ScanJson( jsonString );

		context.Data.Write( jsonString );

		var blobPath = Asset.ResolveRelatedFilePath( context.AbsolutePath, "_d" );
		if ( File.Exists( blobPath ) )
		{
			context.AddCompileReference( blobPath );

			var blobData = File.ReadAllBytes( blobPath );
			unsafe
			{
				fixed ( byte* ptr = blobData )
				{
					context.WriteBlock( BlobDataSerializer.CompiledBlobName, (IntPtr)ptr, blobData.Length );
				}
			}
		}

		return Task.FromResult( true );
	}
}
