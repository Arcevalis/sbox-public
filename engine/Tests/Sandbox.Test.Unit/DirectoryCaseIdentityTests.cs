using Editor;
using System;
using System.IO;

[TestClass]
public class DirectoryCaseIdentityTests
{
	static void WithDirectory( Action<string> action )
	{
		var root = Path.Combine( Path.GetTempPath(), $"case-directory-{Guid.NewGuid():N}" );
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
	[DataRow( "_C", "_c" )]
	[DataRow( "_D", "_d" )]
	[DataRow( ".META", ".meta" )]
	public void RenameDirectory_NormalizesOwnedOutputsButPreservesSources( string legacySuffix, string canonicalSuffix )
	{
		WithDirectory( root =>
		{
			var source = Path.Combine( root, "SourceAssets" );
			var nested = Path.Combine( source, "NestedAssets" );
			var target = Path.Combine( root, "TargetAssets" );
			Directory.CreateDirectory( nested );
			File.WriteAllText( Path.Combine( nested, "Devstead.SCENE" ), "source" );
			File.WriteAllText( Path.Combine( nested, "AlBeDo.PNG" ), "image source" );
			File.WriteAllText( Path.Combine( nested, "Devstead.SCENE" + legacySuffix ), "owned output" );

			EditorUtility.RenameDirectory( source, target );

			var destination = Path.Combine( target, "NestedAssets" );
			Assert.AreEqual( "owned output", File.ReadAllText( Path.Combine( destination, "devstead.scene" + canonicalSuffix ) ) );
			CollectionAssert.AreEquivalent( new[]
			{
				Path.Combine( destination, "Devstead.SCENE" ),
				Path.Combine( destination, "AlBeDo.PNG" ),
				Path.Combine( destination, "devstead.scene" + canonicalSuffix )
			}, Directory.GetFiles( destination ) );
			Assert.AreEqual( "source", File.ReadAllText( Path.Combine( destination, "Devstead.SCENE" ) ) );
			Assert.AreEqual( "image source", File.ReadAllText( Path.Combine( destination, "AlBeDo.PNG" ) ) );
		} );
	}

	[DataTestMethod]
	[DataRow( "_c", false )]
	[DataRow( "_c", true )]
	[DataRow( "_d", false )]
	[DataRow( "_d", true )]
	[DataRow( ".meta", false )]
	[DataRow( ".meta", true )]
	public void RenameDirectory_CanonicalOutputWinsAndLegacyTwinsAreRetired( string suffix, bool recursive )
	{
		if ( !OperatingSystem.IsLinux() ) return;
		WithDirectory( root =>
		{
			var source = Path.Combine( root, "SourceAssets" );
			var target = Path.Combine( root, "TargetAssets" );
			Directory.CreateDirectory( source );
			File.WriteAllText( Path.Combine( source, "Devstead.SCENE" ), "source" );
			File.WriteAllText( Path.Combine( source, "devstead.scene" + suffix ), "canonical" );
			File.WriteAllText( Path.Combine( source, "Devstead.SCENE" + suffix.ToUpperInvariant() ), "legacy" );

			EditorUtility.RenameDirectory( source, target, recursive );

			Assert.AreEqual( "canonical", File.ReadAllText( Path.Combine( target, "devstead.scene" + suffix ) ) );
			Assert.AreEqual( "source", File.ReadAllText( Path.Combine( target, "Devstead.SCENE" ) ) );
			Assert.AreEqual( 2, Directory.GetFiles( target ).Length );
			Assert.AreEqual( recursive, Directory.Exists( source ) );
			if ( recursive ) Assert.AreEqual( 0, Directory.GetFiles( source ).Length );
		} );
	}

	[TestMethod]
	public void RenameDirectory_LegacyOnlyOutputWinnerIsDeterministic()
	{
		if ( !OperatingSystem.IsLinux() ) return;
		WithDirectory( root =>
		{
			var source = Path.Combine( root, "SourceAssets" );
			var target = Path.Combine( root, "TargetAssets" );
			Directory.CreateDirectory( source );
			File.WriteAllText( Path.Combine( source, "Devstead.SCENE_C" ), "later" );
			File.WriteAllText( Path.Combine( source, "DEVSTEAD.scene_C" ), "first" );

			EditorUtility.RenameDirectory( source, target );

			Assert.AreEqual( "first", File.ReadAllText( Path.Combine( target, "devstead.scene_c" ) ) );
			Assert.AreEqual( 1, Directory.GetFiles( target ).Length );
		} );
	}

	[DataTestMethod]
	[DataRow( "_C" )]
	[DataRow( "" )]
	public void RenameDirectory_ExistingCaseAliasFailsBeforeAnyTransfer( string suffix )
	{
		WithDirectory( root =>
		{
			var source = Path.Combine( root, "SourceAssets" );
			var target = Path.Combine( root, "TargetAssets" );
			Directory.CreateDirectory( source );
			Directory.CreateDirectory( target );
			var incoming = Path.Combine( source, "Devstead.SCENE" + suffix );
			var existing = Path.Combine( target, "dEvStEaD.scene" + suffix.ToLowerInvariant() );
			File.WriteAllText( Path.Combine( source, "First.txt" ), "first" );
			File.WriteAllText( incoming, "incoming" );
			File.WriteAllText( existing, "existing" );

			Assert.ThrowsException<IOException>( () => EditorUtility.RenameDirectory( source, target ) );

			Assert.AreEqual( "incoming", File.ReadAllText( incoming ) );
			Assert.AreEqual( "existing", File.ReadAllText( existing ) );
			Assert.AreEqual( "first", File.ReadAllText( Path.Combine( source, "First.txt" ) ) );
			Assert.AreEqual( 1, Directory.GetFiles( target ).Length );
		} );
	}

	[DataTestMethod]
	[DataRow( "devstead.scene_c" )]
	[DataRow( "Devstead.SCENE_C" )]
	public void RenameDirectory_PlannedOutputCannotShadowADirectory( string directoryName )
	{
		if ( !OperatingSystem.IsLinux() ) return;
		WithDirectory( root =>
		{
			var source = Path.Combine( root, "SourceAssets" );
			var target = Path.Combine( root, "TargetAssets" );
			Directory.CreateDirectory( Path.Combine( source, directoryName ) );
			var output = Path.Combine( source, "DEVSTEAD.SCENE_C" );
			File.WriteAllText( output, "compiled" );
			File.WriteAllText( Path.Combine( source, "First.txt" ), "first" );

			Assert.ThrowsException<IOException>( () => EditorUtility.RenameDirectory( source, target ) );

			Assert.AreEqual( "compiled", File.ReadAllText( output ) );
			Assert.AreEqual( "first", File.ReadAllText( Path.Combine( source, "First.txt" ) ) );
			Assert.IsTrue( Directory.Exists( Path.Combine( source, directoryName ) ) );
			Assert.IsFalse( Directory.Exists( target ) );
		} );
	}

	[TestMethod]
	public void RenameDirectory_PreservesBothCaseDistinctSourceFiles()
	{
		if ( !OperatingSystem.IsLinux() ) return;
		WithDirectory( root =>
		{
			var source = Path.Combine( root, "SourceAssets" );
			var target = Path.Combine( root, "TargetAssets" );
			Directory.CreateDirectory( source );
			File.WriteAllText( Path.Combine( source, "Devstead.scene" ), "mixed" );
			File.WriteAllText( Path.Combine( source, "devstead.scene" ), "lower" );

			EditorUtility.RenameDirectory( source, target );

			Assert.AreEqual( "mixed", File.ReadAllText( Path.Combine( target, "Devstead.scene" ) ) );
			Assert.AreEqual( "lower", File.ReadAllText( Path.Combine( target, "devstead.scene" ) ) );
			Assert.AreEqual( 2, Directory.GetFiles( target ).Length );
		} );
	}

	[TestMethod]
	public void RenameDirectory_DoesNotCreateShadowDirectoriesDuringMerge()
	{
		if ( !OperatingSystem.IsLinux() ) return;
		WithDirectory( root =>
		{
			var source = Path.Combine( root, "SourceAssets" );
			var target = Path.Combine( root, "TargetAssets" );
			Directory.CreateDirectory( Path.Combine( source, "NestedAssets" ) );
			Directory.CreateDirectory( Path.Combine( target, "nestedassets" ) );
			File.WriteAllText( Path.Combine( source, "First.txt" ), "first" );
			var incoming = Path.Combine( source, "NestedAssets", "Devstead.scene" );
			var existing = Path.Combine( target, "nestedassets", "Existing.scene" );
			File.WriteAllText( incoming, "incoming" );
			File.WriteAllText( existing, "existing" );

			Assert.ThrowsException<IOException>( () => EditorUtility.RenameDirectory( source, target ) );

			Assert.AreEqual( "incoming", File.ReadAllText( incoming ) );
			Assert.AreEqual( "existing", File.ReadAllText( existing ) );
			Assert.AreEqual( "first", File.ReadAllText( Path.Combine( source, "First.txt" ) ) );
			Assert.IsFalse( Directory.Exists( Path.Combine( target, "NestedAssets" ) ) );
		} );
	}

	[TestMethod]
	public void RenameDirectory_AllowsSiblingWithCommonPrefix()
	{
		WithDirectory( root =>
		{
			var source = Path.Combine( root, "Assets" );
			var target = Path.Combine( root, "AssetsRenamed" );
			Directory.CreateDirectory( source );
			File.WriteAllText( Path.Combine( source, "Devstead.scene" ), "source" );

			EditorUtility.RenameDirectory( source, target );

			Assert.AreEqual( "source", File.ReadAllText( Path.Combine( target, "Devstead.scene" ) ) );
			Assert.IsFalse( Directory.Exists( source ) );
		} );
	}

	[TestMethod]
	public void RenameDirectory_PreservesNestedPhysicalSpellingAndResolvesSourceAlias()
	{
		WithDirectory( root =>
		{
			var source = Path.Combine( root, "SourceAssets" );
			var nested = Path.Combine( source, "NestedAssets" );
			var target = Path.Combine( root, "TargetAssets", "Renamed" );
			Directory.CreateDirectory( nested );
			File.WriteAllText( Path.Combine( nested, "Devstead.SCENE" ), "source" );
			File.WriteAllText( Path.Combine( nested, "devstead.scene_c" ), "compiled" );

			EditorUtility.RenameDirectory( Path.Combine( root, "sourceassets" ), target );

			Assert.AreEqual( "source", File.ReadAllText( Path.Combine( target, "NestedAssets", "Devstead.SCENE" ) ) );
			Assert.AreEqual( "compiled", File.ReadAllText( Path.Combine( target, "NestedAssets", "devstead.scene_c" ) ) );
			Assert.IsFalse( File.Exists( Path.Combine( target, "Devstead.SCENE" ) ) );
			Assert.IsFalse( Directory.Exists( source ) );
		} );
	}

	[TestMethod]
	public void RenameDirectory_CaseOnlyRenameKeepsRequestedDirectorySpelling()
	{
		WithDirectory( root =>
		{
			var source = Path.Combine( root, "SourceAssets" );
			var target = Path.Combine( root, "SOURCEASSETS" );
			Directory.CreateDirectory( source );
			File.WriteAllText( Path.Combine( source, "Devstead.scene" ), "source" );

			EditorUtility.RenameDirectory( source, target );

			CollectionAssert.AreEqual( new[] { target }, Directory.GetDirectories( root ) );
			Assert.AreEqual( "source", File.ReadAllText( Path.Combine( target, "Devstead.scene" ) ) );
		} );
	}

	[TestMethod]
	public void RenameDirectory_DistinguishesCaseSensitiveSiblingDirectories()
	{
		if ( !OperatingSystem.IsLinux() ) return;
		WithDirectory( root =>
		{
			var source = Path.Combine( root, "Assets" );
			var target = Path.Combine( root, "assets" );
			Directory.CreateDirectory( source );
			Directory.CreateDirectory( target );
			File.WriteAllText( Path.Combine( source, "Devstead.scene" ), "source" );
			File.WriteAllText( Path.Combine( target, "Existing.txt" ), "existing" );

			EditorUtility.RenameDirectory( source, target );

			Assert.AreEqual( "source", File.ReadAllText( Path.Combine( target, "Devstead.scene" ) ) );
			Assert.AreEqual( "existing", File.ReadAllText( Path.Combine( target, "Existing.txt" ) ) );
			Assert.IsFalse( Directory.Exists( source ) );
		} );
	}

	[TestMethod]
	public void RenameDirectory_RejectsDescendantWithoutChangingSource()
	{
		WithDirectory( root =>
		{
			var source = Path.Combine( root, "SourceAssets" );
			var target = Path.Combine( source, "Nested" );
			Directory.CreateDirectory( source );
			File.WriteAllText( Path.Combine( source, "Devstead.scene" ), "source" );

			EditorUtility.RenameDirectory( source, target );

			Assert.IsFalse( Directory.Exists( target ) );
			Assert.AreEqual( "source", File.ReadAllText( Path.Combine( source, "Devstead.scene" ) ) );
		} );
	}

	[TestMethod]
	public void RenameDirectory_MergeConflictIsDetectedBeforeMovingAnyFiles()
	{
		WithDirectory( root =>
		{
			var source = Path.Combine( root, "SourceAssets" );
			var target = Path.Combine( root, "TargetAssets" );
			Directory.CreateDirectory( Path.Combine( source, "Nested" ) );
			Directory.CreateDirectory( Path.Combine( target, "Nested" ) );
			File.WriteAllText( Path.Combine( source, "First.txt" ), "first" );
			File.WriteAllText( Path.Combine( source, "Nested", "Conflict.txt" ), "source" );
			File.WriteAllText( Path.Combine( target, "Nested", "Conflict.txt" ), "target" );

			Assert.ThrowsException<IOException>( () => EditorUtility.RenameDirectory( source, target ) );

			Assert.AreEqual( "first", File.ReadAllText( Path.Combine( source, "First.txt" ) ) );
			Assert.AreEqual( "source", File.ReadAllText( Path.Combine( source, "Nested", "Conflict.txt" ) ) );
			Assert.AreEqual( "target", File.ReadAllText( Path.Combine( target, "Nested", "Conflict.txt" ) ) );
			Assert.IsFalse( File.Exists( Path.Combine( target, "First.txt" ) ) );
		} );
	}

	[TestMethod]
	public void RenameDirectory_OverlappingAncestorMergeFailsBeforeMutation()
	{
		WithDirectory( root =>
		{
			var source = Path.Combine( root, "Source" );
			Directory.CreateDirectory( Path.Combine( source, "Source" ) );
			File.WriteAllText( Path.Combine( source, "First.txt" ), "first" );
			File.WriteAllText( Path.Combine( source, "Source", "Nested.txt" ), "nested" );

			Assert.ThrowsException<IOException>( () => EditorUtility.RenameDirectory( source, root ) );

			Assert.AreEqual( "first", File.ReadAllText( Path.Combine( source, "First.txt" ) ) );
			Assert.AreEqual( "nested", File.ReadAllText( Path.Combine( source, "Source", "Nested.txt" ) ) );
			Assert.IsFalse( File.Exists( Path.Combine( root, "First.txt" ) ) );
		} );
	}

	[DataTestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void RenameDirectory_AncestorLinkCannotHideSourceContainment( bool recursive )
	{
		if ( !OperatingSystem.IsLinux() ) return;
		WithDirectory( root =>
		{
			var source = Path.Combine( root, "Source" );
			var alias = Path.Combine( root, "Alias" );
			Directory.CreateDirectory( Path.Combine( source, "Nested" ) );
			Directory.CreateSymbolicLink( alias, source );
			File.WriteAllText( Path.Combine( source, "First.txt" ), "source" );

			Assert.ThrowsException<IOException>( () => EditorUtility.RenameDirectory( source, Path.Combine( alias, "Nested" ), recursive ) );

			Assert.AreEqual( "source", File.ReadAllText( Path.Combine( source, "First.txt" ) ) );
			Assert.AreEqual( 0, Directory.GetFileSystemEntries( Path.Combine( source, "Nested" ) ).Length );
		} );
	}

	[TestMethod]
	public void RenameDirectory_CrossDeviceMovePreservesSourceSpelling()
	{
		if ( !OperatingSystem.IsLinux() || !Directory.Exists( "/dev/shm" ) ) return;
		var target = Path.Combine( "/dev/shm", $"case-directory-{Guid.NewGuid():N}" );
		try
		{
			WithDirectory( root =>
			{
				var source = Path.Combine( root, "SourceAssets" );
				Directory.CreateDirectory( Path.Combine( source, "NestedAssets" ) );
				File.WriteAllText( Path.Combine( source, "NestedAssets", "Devstead.SCENE" ), "source" );

				EditorUtility.RenameDirectory( source, target );

				Assert.AreEqual( "source", File.ReadAllText( Path.Combine( target, "NestedAssets", "Devstead.SCENE" ) ) );
				Assert.IsFalse( Directory.Exists( source ) );
			} );
		}
		finally
		{
			if ( Directory.Exists( target ) ) Directory.Delete( target, true );
		}
	}

	[TestMethod]
	public void RenameDirectory_MissingSourceIsANoOp()
	{
		EditorUtility.RenameDirectory( null, null );
		EditorUtility.RenameDirectory( "", null );
		WithDirectory( root => EditorUtility.RenameDirectory( Path.Combine( root, "Missing" ), null ) );
	}

	[TestMethod]
	public void RenameDirectory_MergesNestedFilesWithoutFlattening()
	{
		WithDirectory( root =>
		{
			var source = Path.Combine( root, "SourceAssets" );
			var target = Path.Combine( root, "TargetAssets" );
			Directory.CreateDirectory( Path.Combine( source, "NestedAssets", "Empty" ) );
			Directory.CreateDirectory( target );
			File.WriteAllText( Path.Combine( source, "NestedAssets", "Devstead.scene" ), "source" );
			File.WriteAllText( Path.Combine( target, "Existing.txt" ), "existing" );

			EditorUtility.RenameDirectory( source, target );

			Assert.AreEqual( "source", File.ReadAllText( Path.Combine( target, "NestedAssets", "Devstead.scene" ) ) );
			Assert.IsTrue( Directory.Exists( Path.Combine( target, "NestedAssets", "Empty" ) ) );
			Assert.AreEqual( "existing", File.ReadAllText( Path.Combine( target, "Existing.txt" ) ) );
			Assert.IsFalse( Directory.Exists( source ) );
		} );
	}

	[TestMethod]
	public void RenameDirectory_RecursiveModePreservesEmptySourceDirectories()
	{
		WithDirectory( root =>
		{
			var source = Path.Combine( root, "SourceAssets" );
			var target = Path.Combine( root, "TargetAssets" );
			Directory.CreateDirectory( Path.Combine( source, "Nested" ) );
			File.WriteAllText( Path.Combine( source, "Nested", "Devstead.scene" ), "source" );

			EditorUtility.RenameDirectory( source, target, recursive: true );

			Assert.IsTrue( Directory.Exists( Path.Combine( source, "Nested" ) ) );
			Assert.AreEqual( 0, Directory.GetFiles( source, "*", SearchOption.AllDirectories ).Length );
			Assert.AreEqual( "source", File.ReadAllText( Path.Combine( target, "Nested", "Devstead.scene" ) ) );
		} );
	}

	[TestMethod]
	public void RenameDirectory_DoesNotMergeThroughSymbolicLinks()
	{
		if ( !OperatingSystem.IsLinux() ) return;
		WithDirectory( root =>
		{
			var source = Path.Combine( root, "SourceAssets" );
			var target = Path.Combine( root, "TargetAssets" );
			var external = Path.Combine( root, "External" );
			Directory.CreateDirectory( source );
			Directory.CreateDirectory( target );
			Directory.CreateDirectory( external );
			File.WriteAllText( Path.Combine( source, "First.txt" ), "source" );
			File.WriteAllText( Path.Combine( external, "External.txt" ), "external" );
			Directory.CreateSymbolicLink( Path.Combine( source, "Link" ), external );

			Assert.ThrowsException<IOException>( () => EditorUtility.RenameDirectory( source, target ) );

			Assert.AreEqual( "source", File.ReadAllText( Path.Combine( source, "First.txt" ) ) );
			Assert.AreEqual( "external", File.ReadAllText( Path.Combine( external, "External.txt" ) ) );
			Assert.AreEqual( 0, Directory.GetFiles( target ).Length );
		} );
	}
}
