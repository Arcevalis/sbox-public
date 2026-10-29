using Editor;
using Sandbox;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

[TestClass]
public class FileReferenceCaseIdentityTests
{
	[DataTestMethod]
	[DataRow( "Maps/Example/NavMesh/Baked.NAVDATA", "maps/example/navmesh/baked.navdata" )]
	[DataRow( "/Maps\\Example.SCENE_C", "maps/example.scene_c" )]
	[DataRow( "Maps/Example.SCENE_D", "maps/example.scene_d" )]
	[DataRow( "mount://GAME/Maps/Example.NAVDATA_C", "mount://game/maps/example.navdata_c" )]
	[DataRow( "", "" )]
	[DataRow( null, null )]
	public void ConstructionUsesLogicalIdentityWithoutStrippingSuffixes( string path, string expected )
	{
		var reference = FileReference.FromPath( path );

		Assert.AreEqual( expected, reference.Path );
		Assert.AreEqual( expected, reference.ToString() );
		Assert.AreEqual( expected, (string)reference );
		Assert.AreEqual( FileReference.ReferenceTypeName, reference.ReferenceType );
	}

	[DataTestMethod]
	[DataRow( "/Maps\\Example.NAVDATA_C", "maps/example.navdata_c" )]
	[DataRow( "Maps/Example.NAVDATA_D", "maps/example.navdata_d" )]
	[DataRow( "mount://GAME/Maps/Example.NAVDATA", "mount://game/maps/example.navdata" )]
	[DataRow( "", "" )]
	[DataRow( null, null )]
	public void AuthoredJsonIsCanonicalAfterRoundTrip( string path, string expected )
	{
		var authored = new JsonObject
		{
			["$reference_type"] = FileReference.ReferenceTypeName,
			["path"] = path
		};

		var reference = JsonSerializer.Deserialize<FileReference>( authored.ToJsonString() );
		var serialized = JsonSerializer.Serialize( reference );
		var emitted = JsonNode.Parse( serialized );
		var roundTripped = JsonSerializer.Deserialize<FileReference>( serialized );

		Assert.AreEqual( expected, reference.Path );
		Assert.AreEqual( FileReference.ReferenceTypeName, emitted["$reference_type"].GetValue<string>() );
		Assert.AreEqual( expected, emitted["path"]?.GetValue<string>() );
		Assert.AreEqual( expected, roundTripped.Path );
	}

	[DataTestMethod]
	[DataRow( "/Maps\\Example.NAVDATA_C", "maps/example.navdata_c" )]
	[DataRow( "Maps/Example.NAVDATA_D", "maps/example.navdata_d" )]
	[DataRow( "mount://GAME/Maps/Example.NAVDATA", "mount://game/maps/example.navdata" )]
	[DataRow( "/", "" )]
	[DataRow( "", "" )]
	[DataRow( null, null )]
	public void CompiledJsonUsesTheAdoptedFileReferenceIdentity( string path, string expected )
	{
		var authored = new JsonObject
		{
			["$reference_type"] = FileReference.ReferenceTypeName,
			["path"] = path
		};

		var reference = ResourceCompileContextImp.NormalizeFileReference( authored );

		Assert.AreEqual( expected, reference.Path );
		Assert.AreEqual( expected, authored["path"]?.GetValue<string>() );
		Assert.AreEqual( FileReference.ReferenceTypeName, authored["$reference_type"].GetValue<string>() );
	}

	[TestMethod]
	public void MissingJsonPathRemainsNull()
	{
		var reference = JsonSerializer.Deserialize<FileReference>( "{\"$reference_type\":\"filereference\"}" );

		Assert.IsNull( reference.Path );
		Assert.IsNull( JsonNode.Parse( JsonSerializer.Serialize( reference ) )["path"] );
	}

	[DataTestMethod]
	[DataRow( "Maps/Example.NAVDATA_C", "maps/example.navdata_c" )]
	[DataRow( "Maps\\Example.NAVDATA_D", "maps/example.navdata_d" )]
	[DataRow( "mount://GAME/Maps/Example.NAVDATA", "mount://game/maps/example.navdata" )]
	[DataRow( "", "" )]
	[DataRow( null, null )]
	public void NativeGameFileRegistrationCanonicalizesLogicalPaths( string path, string expected )
	{
		Assert.AreEqual( expected, ResourceCompileContextImp.GetGameFileReferencePath( path ) );
	}

	[DataTestMethod]
	[DataRow( "/Project/Assets/Maps/Example.NAVDATA_C" )]
	[DataRow( "C:\\Project\\Assets\\Maps\\Example.NAVDATA_D" )]
	[DataRow( "C:/Project/Assets/Maps/Example.NAVDATA_C" )]
	[DataRow( "\\\\Server\\Project\\Maps\\Example.NAVDATA" )]
	public void NativeGameFileRegistrationDoesNotRewritePhysicalPaths( string path )
	{
		Assert.AreEqual( path, ResourceCompileContextImp.GetGameFileReferencePath( path ) );
	}

	[DataTestMethod]
	[DataRow( "_c" )]
	[DataRow( "_d" )]
	public void LogicalReferenceResolvesLegacyDiskCaseWithSuffixIntact( string suffix )
	{
		var root = Path.Combine( Path.GetTempPath(), $"sbox-file-reference-{Guid.NewGuid():N}" );
		var source = Path.Combine( root, "Maps", "Example", $"Baked.NAVDATA{suffix}" );
		Directory.CreateDirectory( Path.GetDirectoryName( source ) );
		File.WriteAllText( source, "baked data" );

		var fs = new LocalFileSystem( root );
		try
		{
			var reference = JsonSerializer.Deserialize<FileReference>( JsonSerializer.Serialize( new
			{
				@path = $"/MAPS\\Example\\Baked.NAVDATA{suffix.ToUpperInvariant()}"
			} ) );

			Assert.AreEqual( $"maps/example/baked.navdata{suffix}", reference.Path );
			Assert.IsTrue( fs.FileExists( reference.Path ) );
			Assert.AreEqual( "baked data", fs.ReadAllText( reference.Path ) );

			var absolutePath = fs.GetFullPath( reference.Path );
			Assert.AreEqual( "baked data", File.ReadAllText( absolutePath ) );
			if ( OperatingSystem.IsLinux() )
				Assert.AreEqual( source, absolutePath );
		}
		finally
		{
			fs.Dispose();
			Directory.Delete( root, true );
		}
	}
}
