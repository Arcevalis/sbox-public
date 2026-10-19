using Editor;
using System;
using System.IO;

[TestClass]
public class SceneDataFolderCaseIdentityTests
{
	static void WithDirectory( Action<string> action )
	{
		var root = Path.Combine( Path.GetTempPath(), $"scene-data-case-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( root );
		try
		{
			action( root );
		}
		finally
		{
			Directory.Delete( root, true );
		}
	}

	[DataTestMethod]
	[DataRow( "DEVSTEAD_SCENE_DATA" )]
	[DataRow( "DeVsTeAd_ScEnE_DaTa" )]
	[DataRow( "Devstead_scene_data" )]
	public void Prepare_MigratesSoleLegacyTreeAfterSourceCaseChanges( string legacyName )
	{
		if ( !OperatingSystem.IsLinux() ) return;
		WithDirectory( root =>
		{
			var parent = Path.Combine( root, "MiXeD", "Assets" );
			var source = Path.Combine( parent, "DEVSTEAD.SCENE" );
			var legacy = Path.Combine( parent, legacyName );
			Directory.CreateDirectory( Path.Combine( legacy, "NavMesh" ) );
			File.WriteAllText( source, "source" );
			File.WriteAllText( Path.Combine( legacy, "NavMesh", "baked.navdata" ), "baked data" );

			var canonical = SceneDataFolder.Prepare( source );

			Assert.AreEqual( Path.Combine( parent, "devstead_scene_data" ), canonical );
			Assert.AreEqual( "baked data", File.ReadAllText( Path.Combine( canonical, "NavMesh", "baked.navdata" ) ) );
			Assert.AreEqual( "source", File.ReadAllText( source ) );
			CollectionAssert.AreEqual( new[] { canonical }, Directory.GetDirectories( parent ) );
		} );
	}

	[DataTestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void Prepare_ConflictingCaseVariantsAreNeverMergedOrOverwritten( bool includeCanonical )
	{
		if ( !OperatingSystem.IsLinux() ) return;
		WithDirectory( root =>
		{
			var source = Path.Combine( root, "DEVSTEAD.SCENE" );
			var first = Path.Combine( root, "Devstead_scene_data" );
			var second = Path.Combine( root, includeCanonical ? "devstead_scene_data" : "deVStead_SCENE_DATA" );
			Directory.CreateDirectory( first );
			Directory.CreateDirectory( second );
			File.WriteAllText( Path.Combine( first, "first.bin" ), "first" );
			File.WriteAllText( Path.Combine( second, "second.bin" ), "second" );

			Assert.ThrowsException<IOException>( () => SceneDataFolder.Prepare( source ) );

			Assert.AreEqual( "first", File.ReadAllText( Path.Combine( first, "first.bin" ) ) );
			Assert.AreEqual( "second", File.ReadAllText( Path.Combine( second, "second.bin" ) ) );
			Assert.AreEqual( 2, Directory.GetDirectories( root ).Length );
			if ( !includeCanonical ) Assert.IsFalse( Directory.Exists( SceneDataFolder.GetPath( source ) ) );
		} );
	}

	[DataTestMethod]
	[DataRow( "devstead_scene_data" )]
	[DataRow( "DEVSTEAD_SCENE_DATA" )]
	public void Prepare_RejectsCaseCollidingFilesWithoutCreatingATree( string filename )
	{
		if ( !OperatingSystem.IsLinux() ) return;
		WithDirectory( root =>
		{
			var source = Path.Combine( root, "Devstead.scene" );
			var file = Path.Combine( root, filename );
			File.WriteAllText( file, "not a data directory" );

			Assert.ThrowsException<IOException>( () => SceneDataFolder.Prepare( source ) );

			Assert.AreEqual( "not a data directory", File.ReadAllText( file ) );
			Assert.AreEqual( 0, Directory.GetDirectories( root ).Length );
		} );
	}

	[TestMethod]
	public void Prepare_RelativeCanonicalPathIsIdempotent()
	{
		WithDirectory( root =>
		{
			var source = Path.GetRelativePath( Environment.CurrentDirectory, Path.Combine( root, "Devstead.scene" ) );
			var canonical = SceneDataFolder.GetPath( source );
			Directory.CreateDirectory( canonical );
			File.WriteAllText( Path.Combine( canonical, "marker.bin" ), "existing" );

			Assert.AreEqual( canonical, SceneDataFolder.Prepare( source ) );
			Assert.AreEqual( canonical, SceneDataFolder.Prepare( source ) );

			Assert.AreEqual( "existing", File.ReadAllText( Path.Combine( canonical, "marker.bin" ) ) );
			Assert.AreEqual( 1, Directory.GetDirectories( root ).Length );
		} );
	}

	[TestMethod]
	public void Prepare_DoesNotTreatLongerFolderPrefixesAsTheSameIdentity()
	{
		WithDirectory( root =>
		{
			var source = Path.Combine( root, "Devstead.scene" );
			var other = Path.Combine( root, "devstead_scene_database" );
			Directory.CreateDirectory( other );
			File.WriteAllText( Path.Combine( other, "marker.bin" ), "unrelated" );

			var canonical = SceneDataFolder.Prepare( source );

			Assert.AreEqual( Path.Combine( root, "devstead_scene_data" ), canonical );
			Assert.IsTrue( Directory.Exists( canonical ) );
			Assert.AreEqual( "unrelated", File.ReadAllText( Path.Combine( other, "marker.bin" ) ) );
		} );
	}

	[TestMethod]
	public void Relocate_MovesTreeToNewIdentity()
	{
		WithDirectory( root =>
		{
			var oldSource = Path.Combine( root, "DEVSTEAD.SCENE" );
			var newSource = Path.Combine( root, "Other", "Newstead.scene" );
			Directory.CreateDirectory( Path.Combine( root, "Other" ) );
			var oldDirectory = SceneDataFolder.GetPath( oldSource );
			Directory.CreateDirectory( Path.Combine( oldDirectory, "NavMesh" ) );
			File.WriteAllText( Path.Combine( oldDirectory, "NavMesh", "baked.navdata" ), "baked data" );

			Assert.IsTrue( SceneDataFolder.Relocate( oldSource, newSource ) );

			var newDirectory = SceneDataFolder.GetPath( newSource );
			Assert.AreEqual( "baked data", File.ReadAllText( Path.Combine( newDirectory, "NavMesh", "baked.navdata" ) ) );
			Assert.IsFalse( Directory.Exists( oldDirectory ) );
		} );
	}

	[TestMethod]
	public void Relocate_CaseOnlyRenameKeepsTheSameTree()
	{
		WithDirectory( root =>
		{
			var oldSource = Path.Combine( root, "DEVSTEAD.SCENE" );
			var newSource = Path.Combine( root, "devstead.scene" );
			var directory = SceneDataFolder.GetPath( oldSource );
			Directory.CreateDirectory( directory );
			File.WriteAllText( Path.Combine( directory, "marker.bin" ), "existing" );

			Assert.IsFalse( SceneDataFolder.Relocate( oldSource, newSource ) );

			Assert.AreEqual( "existing", File.ReadAllText( Path.Combine( directory, "marker.bin" ) ) );
			Assert.AreEqual( 1, Directory.GetDirectories( root ).Length );
		} );
	}

	[TestMethod]
	public void Relocate_MissingTreeCreatesNothing()
	{
		WithDirectory( root =>
		{
			var oldSource = Path.Combine( root, "Devstead.scene" );
			var newSource = Path.Combine( root, "Newstead.scene" );

			Assert.IsFalse( SceneDataFolder.Relocate( oldSource, newSource ) );

			Assert.AreEqual( 0, Directory.GetDirectories( root ).Length );
		} );
	}

	[TestMethod]
	public void Relocate_ExistingDestinationIsNeverMergedOrOverwritten()
	{
		WithDirectory( root =>
		{
			var oldSource = Path.Combine( root, "Devstead.scene" );
			var newSource = Path.Combine( root, "Newstead.scene" );
			var oldDirectory = SceneDataFolder.GetPath( oldSource );
			var newDirectory = SceneDataFolder.GetPath( newSource );
			Directory.CreateDirectory( oldDirectory );
			Directory.CreateDirectory( newDirectory );
			File.WriteAllText( Path.Combine( oldDirectory, "old.bin" ), "old" );
			File.WriteAllText( Path.Combine( newDirectory, "new.bin" ), "new" );

			Assert.ThrowsException<IOException>( () => SceneDataFolder.Relocate( oldSource, newSource ) );

			Assert.AreEqual( "old", File.ReadAllText( Path.Combine( oldDirectory, "old.bin" ) ) );
			Assert.AreEqual( "new", File.ReadAllText( Path.Combine( newDirectory, "new.bin" ) ) );
		} );
	}

	[TestMethod]
	public void Relocate_LegacyVariantAtDestinationIsNeverMerged()
	{
		if ( !OperatingSystem.IsLinux() ) return;
		WithDirectory( root =>
		{
			var oldSource = Path.Combine( root, "Devstead.scene" );
			var newSource = Path.Combine( root, "Newstead.scene" );
			var oldDirectory = SceneDataFolder.GetPath( oldSource );
			var legacy = Path.Combine( root, "NEWSTEAD_SCENE_DATA" );
			Directory.CreateDirectory( oldDirectory );
			Directory.CreateDirectory( legacy );
			File.WriteAllText( Path.Combine( oldDirectory, "old.bin" ), "old" );
			File.WriteAllText( Path.Combine( legacy, "legacy.bin" ), "legacy" );

			Assert.ThrowsException<IOException>( () => SceneDataFolder.Relocate( oldSource, newSource ) );

			Assert.AreEqual( "old", File.ReadAllText( Path.Combine( oldDirectory, "old.bin" ) ) );
			Assert.AreEqual( "legacy", File.ReadAllText( Path.Combine( legacy, "legacy.bin" ) ) );
			Assert.IsFalse( Directory.Exists( SceneDataFolder.GetPath( newSource ) ) );
		} );
	}

	[TestMethod]
	public void Delete_MissingTreeIsANoOp()
	{
		WithDirectory( root =>
		{
			var source = Path.Combine( root, "Devstead.scene" );

			// The recycle path needs native; covered by in-editor acceptance instead.
			SceneDataFolder.Delete( source );

			Assert.AreEqual( 0, Directory.GetDirectories( root ).Length );
		} );
	}
}
