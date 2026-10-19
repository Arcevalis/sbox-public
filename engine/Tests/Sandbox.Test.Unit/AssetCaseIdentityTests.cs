using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

[TestClass]
public class AssetCaseIdentityTests
{
	sealed class StubAsset : Asset
	{
		public string SourceFile { get; set; }
		public string CompiledFile { get; set; }
		public void SetPath( string path ) => AbsolutePath = path;
		public void SetSourcePath( string path ) => AbsoluteSourcePath = path;
		public void SetResourcePath( string path ) => Path = path;
		public string PendingPath { get; set; }

		internal override void UpdateInternals()
		{
			if ( PendingPath is null ) return;
			RelativePath = PendingPath;
			Path = PendingPath.NormalizeFilename( false );
			AbsolutePath = System.IO.Path.GetFullPath( PendingPath );
			AbsoluteCompiledPath = AbsolutePath.LowercaseFileName() + "_c";
		}
		public override bool CanRecompile => false;
		public override string GetCompiledFile( bool absolute ) => CompiledFile;
		public override string GetSourceFile( bool absolute ) => absolute ? SourceFile : SourceFile?.Replace( '\\', '/' );
		internal override int FindIntEditInfo( string name ) => 0;
		public override string FindStringEditInfo( string name ) => null;
		public override void OpenInEditor( string nativeEditor ) { }
		public override List<Asset> GetReferences( bool deep ) => new();
		public override List<Asset> GetDependants( bool deep ) => new();
		public override List<Asset> GetParents( bool deep ) => new();
		public override List<string> GetAdditionalContentFiles() => new();
		public override List<string> GetAdditionalGameFiles() => new();
		public override List<string> GetInputDependencies() => new();
		public override List<string> GetUnrecognizedReferencePaths() => new();
		public override bool Compile( bool full ) => false;
		public override Model GetPreviewModel() => null;
		public override void RecordOpened() { }
		public override bool IsCompiled => false;
		public override bool IsCompiledAndUpToDate => false;
		public override bool IsCompileFailed => false;
		public override ValueTask<bool> CompileIfNeededAsync( float timeout = 30.0f ) => new( false );
		public override bool HasSourceFile => false;
		public override bool HasCompiledFile => false;
		public override bool SetInMemoryReplacement( string sourceData ) => false;
		public override void ClearInMemoryReplacement() { }
	}

	[DataTestMethod, DoNotParallelize]
	[DataRow( false )]
	[DataRow( true )]
	public void RenameDirectory_DoesNotFlattenRegisteredNestedAssets( bool merge )
	{
		WithTemporaryDirectory( root =>
		{
			var sourceDirectory = System.IO.Path.Combine( root, "SourceAssets" );
			var targetDirectory = System.IO.Path.Combine( root, "TargetAssets" );
			var nested = System.IO.Path.Combine( sourceDirectory, "NestedAssets" );
			System.IO.Directory.CreateDirectory( nested );
			if ( merge ) System.IO.Directory.CreateDirectory( targetDirectory );
			var source = System.IO.Path.Combine( nested, "Devstead.SCENE" );
			System.IO.File.WriteAllText( source, "source" );
			System.IO.File.WriteAllText( Asset.GetRelatedFilePath( source, "_c" ), "compiled" );
			var asset = new StubAsset { SourceFile = source };
			asset.SetPath( source );
			var field = typeof( AssetSystem ).GetField( "allAssets", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic );
			var original = field.GetValue( null );
			try
			{
				field.SetValue( null, new Dictionary<ulong, Asset> { { 1, asset } } );
				EditorUtility.RenameDirectory( sourceDirectory, targetDirectory );

				var target = System.IO.Path.Combine( targetDirectory, "NestedAssets", "Devstead.SCENE" );
				Assert.AreEqual( "source", System.IO.File.ReadAllText( target ) );
				Assert.AreEqual( "compiled", System.IO.File.ReadAllText( Asset.GetRelatedFilePath( target, "_c" ) ) );
				Assert.IsFalse( System.IO.File.Exists( System.IO.Path.Combine( targetDirectory, "Devstead.SCENE" ) ) );
			}
			finally
			{
				field.SetValue( null, original );
			}
		} );
	}

	[TestMethod]
	public void AssetPathCache_MoveRemovesEveryOldAlias()
	{
		var folder = $"case-cache-{Guid.NewGuid():N}";
		var asset = new StubAsset { PendingPath = $"{folder}/Old/Devstead.scene" };
		AssetSystem.UpdateAsset( asset );
		var oldPaths = new[] { asset.Path, asset.RelativePath, asset.AbsolutePath, asset.AbsoluteCompiledPath };

		try
		{
			asset.PendingPath = $"{folder}/New/Renamed.scene";
			AssetSystem.UpdateAsset( asset );

			foreach ( var path in oldPaths )
				Assert.IsNull( AssetSystem.FindByPath( path ), path );

			foreach ( var path in new[] { asset.Path, asset.RelativePath, asset.AbsolutePath, asset.AbsoluteCompiledPath } )
				Assert.AreSame( asset, AssetSystem.FindByPath( path ), path );
		}
		finally
		{
			AssetSystem.RemoveAssetPaths( asset );
		}
	}

	[TestMethod]
	public void AssetPathCache_StaleRemovalPreservesReplacement()
	{
		var path = $"case-cache-{Guid.NewGuid():N}/Devstead.scene";
		var original = new StubAsset { PendingPath = path };
		var replacement = new StubAsset { PendingPath = path };
		AssetSystem.UpdateAsset( original );
		AssetSystem.UpdateAsset( replacement );

		try
		{
			AssetSystem.RemoveAssetPaths( original );
			Assert.AreSame( replacement, AssetSystem.FindByPath( path ) );
			Assert.AreSame( replacement, AssetSystem.FindByPath( replacement.AbsoluteCompiledPath ) );
		}
		finally
		{
			AssetSystem.RemoveAssetPaths( original );
			AssetSystem.RemoveAssetPaths( replacement );
		}
	}

	[TestMethod]
	public void AssetPathCache_DeletedAssetIsNotReinserted()
	{
		var asset = new StubAsset { PendingPath = $"case-cache-{Guid.NewGuid():N}/Devstead.scene" };
		AssetSystem.UpdateAsset( asset );

		try
		{
			asset.IsDeleted = true;
			AssetSystem.UpdateAsset( asset );
			asset.IsDeleted = false;
			Assert.IsNull( AssetSystem.FindByPath( asset.Path ) );
		}
		finally
		{
			AssetSystem.RemoveAssetPaths( asset );
		}
	}

	[TestMethod]
	public void LowercaseIdentity_LowersFileNameKeepsDirectories()
	{
		Assert.AreEqual(
			System.IO.Path.Combine( "scenes", "devstead.scene" ),
			AssetSystem.LowercaseAssetFileName( "scenes/Devstead.scene" ) );

		Assert.AreEqual(
			System.IO.Path.Combine( "Assets", "Scenes", "brutalistkfc.scene" ),
			AssetSystem.LowercaseAssetFileName( "Assets/Scenes/BRUTALISTKFC.SCENE" ) );
	}

	[TestMethod]
	public void LowercaseIdentity_PassesThroughLowercase()
	{
		Assert.AreEqual(
			System.IO.Path.Combine( "scenes", "devstead.scene" ),
			AssetSystem.LowercaseAssetFileName( "scenes/devstead.scene" ) );

		Assert.AreEqual(
			"devstead.scene",
			AssetSystem.LowercaseAssetFileName( "devstead.scene" ) );
	}

	[TestMethod]
	public void LowercaseIdentity_BareFileName()
	{
		Assert.AreEqual(
			"devstead.scene",
			AssetSystem.LowercaseAssetFileName( "Devstead.scene" ) );
	}

	// Metadata is engine-owned, so it lives beside the lowercase identity
	// no matter how the source file is cased on disk.
	[TestMethod]
	public void MetadataFile_DerivesFromLowercaseIdentity()
	{
		var dir = System.IO.Path.Combine( "Assets", "scenes" );
		var asset = new StubAsset();
		var source = System.IO.Path.GetFullPath( System.IO.Path.Combine( dir, "Devstead.scene" ) );
		asset.SourceFile = source;
		asset.SetPath( source );
		asset.SetSourcePath( source );

		Assert.AreEqual(
			System.IO.Path.GetFullPath( System.IO.Path.Combine( dir, "devstead.scene.meta" ) ),
			asset.GetMetadataFile() );
	}

	[TestMethod]
	public void MetadataFile_RelativeSourceDerivesFromLowercaseIdentity()
	{
		var asset = new StubAsset();
		var source = System.IO.Path.GetFullPath( System.IO.Path.Combine( "scenes", "Devstead.SCENE" ) );
		asset.SourceFile = source;
		asset.SetPath( source );
		asset.SetSourcePath( source );

		Assert.AreEqual( System.IO.Path.GetFullPath( System.IO.Path.Combine( "scenes", "devstead.scene.meta" ) ), asset.GetMetadataFile() );
	}

	[TestMethod]
	public void MetadataFile_NullSourceReturnsNull()
	{
		var asset = new StubAsset();

		Assert.IsNull( asset.GetMetadataFile() );
	}

	[TestMethod]
	public void MetadataFile_CloudSourceReturnsNull()
	{
		var asset = new StubAsset();
		var source = "/home/user/.sbox/cloud/pkg/Devstead.scene";
		asset.SourceFile = source;
		asset.SetPath( source );
		asset.SetSourcePath( source );

		Assert.IsNull( asset.GetMetadataFile() );
	}

	[TestMethod]
	public void LowercaseFileName_LowersFileNameKeepsDirectories()
	{
		Assert.AreEqual(
			System.IO.Path.Combine( "scenes", "devstead.scene" ),
			"scenes/Devstead.scene".LowercaseFileName() );

		Assert.AreEqual(
			"devstead.scene",
			"Devstead.scene".LowercaseFileName() );
	}

	[TestMethod]
	public void LowercaseFileName_PassesThroughLowercase()
	{
		Assert.AreEqual(
			System.IO.Path.Combine( "scenes", "devstead.scene" ),
			"scenes/devstead.scene".LowercaseFileName() );
	}

	[TestMethod]
	public void LowercaseFileName_LowersExtensionToo()
	{
		Assert.AreEqual(
			System.IO.Path.Combine( "models", "hero.fbx" ),
			"models/Hero.FBX".LowercaseFileName() );
	}

	[TestMethod]
	public void RelatedFilePath_SourceKeepsDiskSpelling()
	{
		var source = System.IO.Path.Combine( "Assets", "Scenes", "Devstead.scene" );

		Assert.AreEqual( source, Asset.GetRelatedFilePath( source, "" ) );
	}

	[TestMethod]
	public void RelatedFilePath_SidecarsDeriveFromLowercaseIdentity()
	{
		var dir = System.IO.Path.Combine( "Assets", "Scenes" );
		var source = System.IO.Path.Combine( dir, "Devstead.scene" );

		Assert.AreEqual( System.IO.Path.Combine( dir, "devstead.scene_c" ), Asset.GetRelatedFilePath( source, "_c" ) );
		Assert.AreEqual( System.IO.Path.Combine( dir, "devstead.scene_d" ), Asset.GetRelatedFilePath( source, "_d" ) );
		Assert.AreEqual( System.IO.Path.Combine( dir, "devstead.scene.meta" ), Asset.GetRelatedFilePath( source, ".meta" ) );
	}

	[TestMethod]
	public void MoveCheck_MixedCaseArrivalMatchesLowercaseRecord()
	{
		var stored = Sandbox.Resource.FixPath( "scenes/devstead.scene_c" );
		var arrival = Sandbox.Resource.FixPath( "Scenes/Devstead.scene_c" );

		Assert.AreEqual( "scenes/devstead.scene", stored );
		Assert.AreEqual( stored, arrival );
		Assert.IsTrue( string.Equals( stored, arrival, System.StringComparison.Ordinal ) );
	}

	[TestMethod]
	public void ResolveNativeCasing_ExactPathPassesThrough()
	{
		var dir = System.IO.Path.Combine( System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName() );
		System.IO.Directory.CreateDirectory( dir );

		try
		{
			var file = System.IO.Path.Combine( dir, "exact.txt" );
			System.IO.File.WriteAllText( file, "x" );

			Assert.AreEqual( file, Sandbox.CaseInsensitivePhysicalFileSystem.ResolveNativeCasing( file ) );
		}
		finally
		{
			System.IO.Directory.Delete( dir, true );
		}
	}

	[TestMethod]
	public void ResolveNativeCasing_LowercaseIdentityFindsMixedCaseDiskFile()
	{
		if ( !System.OperatingSystem.IsLinux() )
			return;

		var dir = System.IO.Path.Combine( System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName() );
		System.IO.Directory.CreateDirectory( dir );

		try
		{
			var real = System.IO.Path.Combine( dir, "MiXeD.SoUrCe" );
			System.IO.File.WriteAllText( real, "x" );

			var resolved = Sandbox.CaseInsensitivePhysicalFileSystem.ResolveNativeCasing( System.IO.Path.Combine( dir, "mixed.source" ) );

			Assert.AreEqual( real, resolved );
		}
		finally
		{
			System.IO.Directory.Delete( dir, true );
		}
	}

	[TestMethod]
	public void ResolveRelatedFilePath_PrefersIdentityWhenPresent()
	{
		var dir = System.IO.Path.Combine( System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName() );
		System.IO.Directory.CreateDirectory( dir );

		try
		{
			var source = System.IO.Path.Combine( dir, "Devstead.scene" );
			System.IO.File.WriteAllText( source, "x" );
			var identity = System.IO.Path.Combine( dir, "devstead.scene_d" );
			System.IO.File.WriteAllText( identity, "x" );

			Assert.AreEqual( identity, Asset.ResolveRelatedFilePath( source, "_d" ) );
			Assert.IsTrue( Asset.RelatedFileExists( source, "_d" ) );
		}
		finally
		{
			System.IO.Directory.Delete( dir, true );
		}
	}

	[TestMethod]
	public void ResolveRelatedFilePath_FindsLegacyTwinWhenIdentityMissing()
	{
		if ( !System.OperatingSystem.IsLinux() )
			return;

		var dir = System.IO.Path.Combine( System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName() );
		System.IO.Directory.CreateDirectory( dir );

		try
		{
			var source = System.IO.Path.Combine( dir, "Devstead.scene" );
			System.IO.File.WriteAllText( source, "x" );
			var legacy = System.IO.Path.Combine( dir, "Devstead.scene_d" );
			System.IO.File.WriteAllText( legacy, "x" );

			Assert.AreEqual( legacy, Asset.ResolveRelatedFilePath( source, "_d" ) );
			Assert.IsTrue( Asset.RelatedFileExists( source, "_d" ) );
		}
		finally
		{
			System.IO.Directory.Delete( dir, true );
		}
	}

	[TestMethod]
	public void RelatedFileExists_FalseWhenNothingOnDisk()
	{
		var dir = System.IO.Path.Combine( System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName() );
		System.IO.Directory.CreateDirectory( dir );

		try
		{
			var source = System.IO.Path.Combine( dir, "Devstead.scene" );
			System.IO.File.WriteAllText( source, "x" );

			Assert.IsFalse( Asset.RelatedFileExists( source, "_d" ) );
			Assert.AreEqual(
				System.IO.Path.Combine( dir, "devstead.scene_d" ),
				Asset.ResolveRelatedFilePath( source, "_d" ) );
		}
		finally
		{
			System.IO.Directory.Delete( dir, true );
		}
	}

	[TestMethod]
	public void ResolveNativeCasing_FindsExternallyCreatedDirectoryAfterCache()
	{
		if ( !System.OperatingSystem.IsLinux() )
			return;

		var root = System.IO.Path.Combine( System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName() );
		System.IO.Directory.CreateDirectory( root );

		try
		{
			var probe = System.IO.Path.Combine( root, "Probe.txt" );
			System.IO.File.WriteAllText( probe, "x" );
			Assert.AreEqual( probe, Sandbox.CaseInsensitivePhysicalFileSystem.ResolveNativeCasing( System.IO.Path.Combine( root, "probe.txt" ) ) );

			var fresh = System.IO.Path.Combine( root, "FreshDir" );
			System.IO.Directory.CreateDirectory( fresh );
			var real = System.IO.Path.Combine( fresh, "MiXeD.txt" );
			System.IO.File.WriteAllText( real, "x" );

			Assert.AreEqual( real, Sandbox.CaseInsensitivePhysicalFileSystem.ResolveNativeCasing( System.IO.Path.Combine( root, "freshdir", "mixed.txt" ) ) );
		}
		finally
		{
			System.IO.Directory.Delete( root, true );
		}
	}

	[DataTestMethod]
	[DataRow( false, false )]
	[DataRow( false, true )]
	[DataRow( true, false )]
	[DataRow( true, true )]
	public void TransferAsset_DifferentCompiledExtensionAndDirectory( bool move, bool legacyOnly )
	{
		WithTemporaryDirectory( root =>
		{
			var sourceDirectory = System.IO.Path.Combine( root, "SourceAssets" );
			var compiledDirectory = System.IO.Path.Combine( root, "GameAssets" );
			var targetDirectory = System.IO.Path.Combine( root, "TargetAssets" );
			foreach ( var directory in new[] { sourceDirectory, compiledDirectory, targetDirectory } )
				System.IO.Directory.CreateDirectory( directory );

			var source = System.IO.Path.Combine( sourceDirectory, "Voice.WAV" );
			var compiledBase = System.IO.Path.Combine( compiledDirectory, "Voice.VSND" );
			var compiled = compiledBase + "_C";
			var target = System.IO.Path.Combine( targetDirectory, "Next.WAV" );
			var targetBase = System.IO.Path.Combine( targetDirectory, "Next.vsnd" );
			System.IO.File.WriteAllText( source, "source" );
			System.IO.File.WriteAllText( source + "_c", "unrelated" );
			System.IO.File.WriteAllText( Asset.GetRelatedFilePath( source, ".meta" ), "metadata" );
			System.IO.File.WriteAllText( compiled, "legacy" );
			if ( !legacyOnly ) System.IO.File.WriteAllText( Asset.GetRelatedFilePath( compiledBase, "_c" ), "canonical" );
			System.IO.File.WriteAllText( targetBase + "_C", "stale" );
			var asset = new StubAsset { SourceFile = source, CompiledFile = compiled };
			asset.SetPath( compiled );

			Assert.IsTrue( move ? EditorUtility.MoveAsset( asset, target, true ) : EditorUtility.CopyAsset( asset, target, true ) );
			Assert.AreEqual( "source", System.IO.File.ReadAllText( target ) );
			Assert.AreEqual( legacyOnly ? "legacy" : "canonical", System.IO.File.ReadAllText( Asset.GetRelatedFilePath( targetBase, "_c" ) ) );
			Assert.AreEqual( "metadata", System.IO.File.ReadAllText( Asset.GetRelatedFilePath( target, ".meta" ) ) );
			Assert.AreEqual( "unrelated", System.IO.File.ReadAllText( source + "_c" ) );
			Assert.AreEqual( 1, Asset.GetRelatedFilePaths( targetBase, "_c" ).Length );
			Assert.AreEqual( !move, System.IO.File.Exists( source ) );
			var remainingCompiled = move ? 0 : (legacyOnly || !OperatingSystem.IsLinux() ? 1 : 2);
			Assert.AreEqual( remainingCompiled, Asset.GetRelatedFilePaths( compiledBase, "_c" ).Length );
			Assert.IsFalse( System.IO.File.Exists( Asset.GetRelatedFilePath( target, "_c" ) ) );
		} );
	}

	[DataTestMethod]
	[DataRow( "Voice.WAV", false )]
	[DataRow( "Voice.WAV", true )]
	[DataRow( "VOICE.wav", false )]
	[DataRow( "VOICE.wav", true )]
	[DataRow( "Next.WAV", false )]
	[DataRow( "Next.WAV", true )]
	public void RenameAsset_IndependentCompiledDirectoryIsPreserved( string name, bool useRenameWrapper )
	{
		WithTemporaryDirectory( root =>
		{
			var sourceDirectory = System.IO.Path.Combine( root, "SourceAssets" );
			var compiledDirectory = System.IO.Path.Combine( root, "GameAssets" );
			System.IO.Directory.CreateDirectory( sourceDirectory );
			System.IO.Directory.CreateDirectory( compiledDirectory );
			var source = System.IO.Path.Combine( sourceDirectory, "Voice.WAV" );
			var compiled = System.IO.Path.Combine( compiledDirectory, "Voice.VSND_C" );
			System.IO.File.WriteAllText( source, "source" );
			System.IO.File.WriteAllText( compiled, "compiled" );
			System.IO.File.WriteAllText( Asset.GetRelatedFilePath( source, ".meta" ), "metadata" );
			var asset = new StubAsset { SourceFile = source, CompiledFile = compiled };
			asset.SetPath( compiled );
			var target = System.IO.Path.Combine( sourceDirectory, name );

			Assert.IsTrue( useRenameWrapper ? EditorUtility.RenameAsset( asset, name ) : EditorUtility.MoveAsset( asset, target ) );

			Assert.AreEqual( "source", System.IO.File.ReadAllText( target ) );
			Assert.AreEqual( "metadata", System.IO.File.ReadAllText( Asset.GetRelatedFilePath( target, ".meta" ) ) );
			var compiledTarget = System.IO.Path.ChangeExtension( System.IO.Path.Combine( compiledDirectory, name ), ".vsnd" );
			Assert.AreEqual( "compiled", System.IO.File.ReadAllText( Asset.GetRelatedFilePath( compiledTarget, "_c" ) ) );
			Assert.AreEqual( 1, System.IO.Directory.GetFiles( compiledDirectory ).Length );
			Assert.IsFalse( System.IO.File.Exists( System.IO.Path.ChangeExtension( target, ".vsnd" ).LowercaseFileName() + "_c" ) );
		} );
	}

	[TestMethod]
	public void RelatedFiles_DeletionSnapshotUsesIndependentCompiledRole()
	{
		WithTemporaryDirectory( root =>
		{
			var source = System.IO.Path.Combine( root, "Voice.WAV" );
			var compiledBase = System.IO.Path.Combine( root, "Voice.VSND" );
			var compiled = compiledBase + "_C";
			System.IO.File.WriteAllText( source, "source" );
			System.IO.File.WriteAllText( source + "_c", "unrelated" );
			System.IO.File.WriteAllText( compiled, "compiled" );
			System.IO.File.WriteAllText( Asset.GetRelatedFilePath( source, ".meta" ), "metadata" );
			var asset = new StubAsset { SourceFile = source, CompiledFile = compiled };
			asset.SetPath( source );

			var files = asset.GetRelatedFiles();
			var selectedCompiled = OperatingSystem.IsLinux() ? compiled : Asset.GetRelatedFilePath( compiledBase, "_c" );
			CollectionAssert.AreEquivalent( new[] { source, selectedCompiled, Asset.GetRelatedFilePath( source, ".meta" ) }, files );
			foreach ( var file in files ) System.IO.File.Delete( file );
			Assert.AreEqual( 0, asset.GetRelatedFiles().Length );
			Assert.AreEqual( "unrelated", System.IO.File.ReadAllText( source + "_c" ) );
		} );
	}

	[TestMethod]
	public void CopyAsset_MissingCompiledRoleRemovesStaleResourceExtension()
	{
		WithTemporaryDirectory( root =>
		{
			var source = System.IO.Path.Combine( root, "Voice.WAV" );
			var target = System.IO.Path.Combine( root, "Next.WAV" );
			var stale = System.IO.Path.Combine( root, "Next.VSND_C" );
			System.IO.File.WriteAllText( source, "source" );
			System.IO.File.WriteAllText( stale, "stale" );
			System.IO.File.WriteAllText( target + "_c", "unrelated" );
			var asset = new StubAsset { SourceFile = source };
			asset.SetPath( source );
			asset.SetResourcePath( "sounds/voice.vsnd" );

			Assert.IsTrue( EditorUtility.CopyAsset( asset, target, true ) );
			Assert.IsFalse( System.IO.File.Exists( stale ) );
			Assert.AreEqual( "unrelated", System.IO.File.ReadAllText( target + "_c" ) );
			Assert.AreEqual( "source", System.IO.File.ReadAllText( target ) );
		} );
	}

	[DataTestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void DirectoryTransfer_PreservesPhysicalSpellingAndCanonicalRoles( bool move )
	{
		WithTemporaryDirectory( root =>
		{
			var sourceDirectory = System.IO.Path.Combine( root, "SourceAssets" );
			var targetDirectory = System.IO.Path.Combine( root, "TargetAssets" );
			System.IO.Directory.CreateDirectory( sourceDirectory );
			System.IO.Directory.CreateDirectory( targetDirectory );
			var source = System.IO.Path.Combine( sourceDirectory, "Voice.WAV" );
			var compiled = System.IO.Path.Combine( sourceDirectory, "Voice.VSND_C" );
			System.IO.File.WriteAllText( source, "source" );
			System.IO.File.WriteAllText( compiled, "compiled" );
			foreach ( var ext in new[] { "_d", ".meta" } )
				System.IO.File.WriteAllText( Asset.GetRelatedFilePath( source, ext ), ext );
			var asset = new StubAsset { SourceFile = source, CompiledFile = compiled };
			asset.SetPath( compiled );

			if ( move ) EditorUtility.MoveAssetToDirectory( asset, targetDirectory );
			else EditorUtility.CopyAssetToDirectory( asset, targetDirectory );

			var target = System.IO.Path.Combine( targetDirectory, "Voice.WAV" );
			Assert.AreEqual( "source", System.IO.File.ReadAllText( target ) );
			Assert.AreEqual( "compiled", System.IO.File.ReadAllText( System.IO.Path.Combine( targetDirectory, "voice.vsnd_c" ) ) );
			foreach ( var ext in new[] { "_d", ".meta" } )
				Assert.AreEqual( ext, System.IO.File.ReadAllText( Asset.GetRelatedFilePath( target, ext ) ) );
			Assert.AreEqual( !move, System.IO.File.Exists( source ) );
		} );
	}

	[DataTestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void MoveAssetToDirectory_HonorsOverwrite( bool overwrite )
	{
		WithTemporaryDirectory( root =>
		{
			var targetDirectory = System.IO.Path.Combine( root, "TargetAssets" );
			System.IO.Directory.CreateDirectory( targetDirectory );
			var source = System.IO.Path.Combine( root, "Devstead.scene" );
			var target = System.IO.Path.Combine( targetDirectory, "Devstead.scene" );
			System.IO.File.WriteAllText( source, "new" );
			System.IO.File.WriteAllText( target, "existing" );
			var asset = new StubAsset { SourceFile = source };
			asset.SetPath( source );

			EditorUtility.MoveAssetToDirectory( asset, targetDirectory, overwrite );

			Assert.AreEqual( overwrite ? "new" : "existing", System.IO.File.ReadAllText( target ) );
			Assert.AreEqual( !overwrite, System.IO.File.Exists( source ) );
		} );
	}

	[TestMethod]
	public void MoveAssetToDirectory_DistinguishesCaseSensitiveParentDirectories()
	{
		if ( !OperatingSystem.IsLinux() ) return;
		WithTemporaryDirectory( root =>
		{
			var sourceDirectory = System.IO.Path.Combine( root, "Sounds" );
			var targetDirectory = System.IO.Path.Combine( root, "sounds" );
			System.IO.Directory.CreateDirectory( sourceDirectory );
			System.IO.Directory.CreateDirectory( targetDirectory );
			var source = System.IO.Path.Combine( sourceDirectory, "Voice.WAV" );
			System.IO.File.WriteAllText( source, "source" );
			var asset = new StubAsset { SourceFile = source };
			asset.SetPath( source );

			EditorUtility.MoveAssetToDirectory( asset, targetDirectory );

			Assert.AreEqual( "source", System.IO.File.ReadAllText( System.IO.Path.Combine( targetDirectory, "Voice.WAV" ) ) );
			Assert.IsFalse( System.IO.File.Exists( source ) );
		} );
	}

	[TestMethod]
	public void MoveAsset_PreservesSourceCasingAndMovesCanonicalSidecars()
	{
		WithTemporaryDirectory( root =>
		{
			var sourceDirectory = System.IO.Path.Combine( root, "SourceAssets" );
			var targetDirectory = System.IO.Path.Combine( root, "TargetAssets" );
			System.IO.Directory.CreateDirectory( sourceDirectory );
			System.IO.Directory.CreateDirectory( targetDirectory );
			var source = System.IO.Path.Combine( sourceDirectory, "Devstead.SCENE" );
			var target = System.IO.Path.Combine( targetDirectory, "Next.SCENE" );
			System.IO.File.WriteAllText( source, "source" );
			foreach ( var ext in new[] { "_c", "_d", ".meta" } )
			{
				System.IO.File.WriteAllText( Asset.GetRelatedFilePath( source, ext ), "canonical" );
				if ( System.OperatingSystem.IsLinux() ) System.IO.File.WriteAllText( source + ext, "legacy" );
			}
			var asset = new StubAsset();
			asset.SetPath( source );

			Assert.IsTrue( EditorUtility.MoveAsset( asset, target ) );
			Assert.AreEqual( "source", System.IO.File.ReadAllText( target ) );
			foreach ( var ext in new[] { "_c", "_d", ".meta" } )
			{
				Assert.AreEqual( "canonical", System.IO.File.ReadAllText( Asset.GetRelatedFilePath( target, ext ) ) );
				Assert.AreEqual( 0, Asset.GetRelatedFilePaths( source, ext ).Length );
			}
			Assert.IsFalse( System.IO.File.Exists( source ) );
		} );
	}

	[TestMethod]
	public void CopyAsset_OverwriteRetiresDestinationLegacyTwins()
	{
		WithTemporaryDirectory( root =>
		{
			var source = System.IO.Path.Combine( root, "Devstead.SCENE" );
			var target = System.IO.Path.Combine( root, "Next.SCENE" );
			System.IO.File.WriteAllText( source, "new source" );
			System.IO.File.WriteAllText( target, "old source" );
			foreach ( var ext in new[] { "_c", "_d", ".meta" } )
			{
				System.IO.File.WriteAllText( Asset.GetRelatedFilePath( source, ext ), "canonical" );
				System.IO.File.WriteAllText( Asset.GetRelatedFilePath( target, ext ), "old canonical" );
				if ( System.OperatingSystem.IsLinux() )
				{
					System.IO.File.WriteAllText( source + ext, "legacy" );
					System.IO.File.WriteAllText( target + ext, "old legacy" );
				}
			}
			var asset = new StubAsset();
			asset.SetPath( source );

			Assert.IsTrue( EditorUtility.CopyAsset( asset, target, overwrite: true ) );
			Assert.AreEqual( "new source", System.IO.File.ReadAllText( target ) );
			Assert.AreEqual( "new source", System.IO.File.ReadAllText( source ) );
			foreach ( var ext in new[] { "_c", "_d", ".meta" } )
			{
				Assert.AreEqual( "canonical", System.IO.File.ReadAllText( Asset.ResolveRelatedFilePath( target, ext ) ) );
				Assert.AreEqual( 1, Asset.GetRelatedFilePaths( target, ext ).Length );
				Assert.AreEqual( System.OperatingSystem.IsLinux() ? 2 : 1, Asset.GetRelatedFilePaths( source, ext ).Length );
			}
		} );
	}

	[TestMethod]
	public void MoveAsset_CaseOnlyRenamePreservesRequestedSourceSpelling()
	{
		WithTemporaryDirectory( root =>
		{
			var source = System.IO.Path.Combine( root, "Devstead.scene" );
			var target = System.IO.Path.Combine( root, "DEVSTEAD.SCENE" );
			System.IO.File.WriteAllText( source, "source" );
			System.IO.File.WriteAllText( Asset.GetRelatedFilePath( source, "_d" ), "blob" );
			var asset = new StubAsset();
			asset.SetPath( source );

			Assert.IsTrue( EditorUtility.MoveAsset( asset, target ) );
			Assert.AreEqual( "source", System.IO.File.ReadAllText( target ) );
			Assert.AreEqual( "blob", System.IO.File.ReadAllText( Asset.GetRelatedFilePath( target, "_d" ) ) );
			CollectionAssert.Contains( System.IO.Directory.GetFiles( root ), target );
		} );
	}

	[TestMethod]
	public void MoveAsset_DistinguishesCaseSensitiveParentDirectories()
	{
		if ( !System.OperatingSystem.IsLinux() ) return;

		WithTemporaryDirectory( root =>
		{
			var sourceDirectory = System.IO.Path.Combine( root, "Assets" );
			var targetDirectory = System.IO.Path.Combine( root, "assets" );
			System.IO.Directory.CreateDirectory( sourceDirectory );
			System.IO.Directory.CreateDirectory( targetDirectory );
			var source = System.IO.Path.Combine( sourceDirectory, "Devstead.scene" );
			var target = System.IO.Path.Combine( targetDirectory, "Devstead.scene" );
			System.IO.File.WriteAllText( source, "source" );
			System.IO.File.WriteAllText( target, "existing" );
			var asset = new StubAsset();
			asset.SetPath( source );

			Assert.IsFalse( EditorUtility.MoveAsset( asset, target ) );
			Assert.AreEqual( "source", System.IO.File.ReadAllText( source ) );
			Assert.AreEqual( "existing", System.IO.File.ReadAllText( target ) );
		} );
	}

	[TestMethod]
	public void RelatedFilePaths_SnapshotIncludesAllTwinsButOnlyOneSource()
	{
		if ( !System.OperatingSystem.IsLinux() ) return;

		WithTemporaryDirectory( root =>
		{
			var source = System.IO.Path.Combine( root, "Devstead.scene" );
			var otherSource = System.IO.Path.Combine( root, "devstead.scene" );
			System.IO.File.WriteAllText( source, "source" );
			System.IO.File.WriteAllText( otherSource, "other source" );
			var identity = Asset.GetRelatedFilePath( source, "_d" );
			System.IO.File.WriteAllText( identity, "new" );
			System.IO.File.WriteAllText( source + "_d", "old" );
			CaseInsensitivePhysicalFileSystem.ResolveNativeCasing( identity );

			var files = Asset.GetRelatedFilePaths( source, "_d" );
			CollectionAssert.AreEqual( new[] { identity, source + "_d" }, files );
			foreach ( var file in files ) System.IO.File.Delete( file );
			Assert.AreEqual( 0, Asset.GetRelatedFilePaths( source, "_d" ).Length );
			CollectionAssert.AreEqual( new[] { source }, Asset.GetRelatedFilePaths( source, "" ) );
			Assert.AreEqual( "other source", System.IO.File.ReadAllText( otherSource ) );
		} );
	}

	[DataTestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void TransferAsset_OverwriteRemovesSidecarsMissingFromIncomingAsset( bool move )
	{
		WithTemporaryDirectory( root =>
		{
			var source = System.IO.Path.Combine( root, "Source.scene" );
			var target = System.IO.Path.Combine( root, "Target.scene" );
			System.IO.File.WriteAllText( source, "new source" );
			System.IO.File.WriteAllText( target, "old source" );
			System.IO.File.WriteAllText( Asset.GetRelatedFilePath( target, "_d" ), "stale blob" );
			var asset = new StubAsset();
			asset.SetPath( source );

			Assert.IsTrue( move ? EditorUtility.MoveAsset( asset, target, true ) : EditorUtility.CopyAsset( asset, target, true ) );
			Assert.AreEqual( "new source", System.IO.File.ReadAllText( target ) );
			Assert.AreEqual( 0, Asset.GetRelatedFilePaths( target, "_d" ).Length );
		} );
	}

	[TestMethod]
	public void CopyAsset_MissingSourceDoesNotEraseDestination()
	{
		WithTemporaryDirectory( root =>
		{
			var source = System.IO.Path.Combine( root, "Missing.scene" );
			var target = System.IO.Path.Combine( root, "Target.scene" );
			System.IO.File.WriteAllText( target, "existing" );
			System.IO.File.WriteAllText( Asset.GetRelatedFilePath( target, "_d" ), "existing blob" );
			var asset = new StubAsset();
			asset.SetPath( source );

			Assert.IsFalse( EditorUtility.CopyAsset( asset, target, true ) );
			Assert.AreEqual( "existing", System.IO.File.ReadAllText( target ) );
			Assert.AreEqual( "existing blob", System.IO.File.ReadAllText( Asset.GetRelatedFilePath( target, "_d" ) ) );
		} );
	}

	[TestMethod]
	public void MoveAsset_CompiledOnlyUppercaseSuffixProducesCanonicalOutput()
	{
		WithTemporaryDirectory( root =>
		{
			var source = System.IO.Path.Combine( root, "Devstead.scene_C" );
			var target = System.IO.Path.Combine( root, "Next.SCENE" );
			System.IO.File.WriteAllText( source, "compiled" );
			var asset = new StubAsset();
			asset.SetPath( source );

			Assert.IsTrue( EditorUtility.MoveAsset( asset, target ) );
			Assert.AreEqual( "compiled", System.IO.File.ReadAllText( Asset.GetRelatedFilePath( target, "_c" ) ) );
			Assert.IsFalse( System.IO.File.Exists( source ) );
			Assert.IsFalse( System.IO.File.Exists( target ) );
		} );
	}

	static void WithTemporaryDirectory( System.Action<string> test )
	{
		var root = System.IO.Path.Combine( System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName() );
		System.IO.Directory.CreateDirectory( root );
		try
		{
			test( root );
		}
		finally
		{
			System.IO.Directory.Delete( root, true );
		}
	}

	[TestMethod]
	public void ResolveNativeCasing_PrefersNewExactFileAfterCachedAlias()
	{
		if ( !System.OperatingSystem.IsLinux() ) return;

		WithTemporaryDirectory( root =>
		{
			var legacy = System.IO.Path.Combine( root, "Devstead.scene_d" );
			var identity = System.IO.Path.Combine( root, "devstead.scene_d" );
			System.IO.File.WriteAllText( legacy, "old" );
			Assert.AreEqual( legacy, CaseInsensitivePhysicalFileSystem.ResolveNativeCasing( identity ) );

			System.IO.File.WriteAllText( identity, "new" );
			var resolved = CaseInsensitivePhysicalFileSystem.ResolveNativeCasing( identity );
			Assert.AreEqual( identity, resolved );
			Assert.AreEqual( "new", System.IO.File.ReadAllText( resolved ) );
		} );
	}

	[TestMethod]
	public void ResolveNativeCasing_RefreshesRenamedCachedAlias()
	{
		if ( !System.OperatingSystem.IsLinux() ) return;

		WithTemporaryDirectory( root =>
		{
			var original = System.IO.Path.Combine( root, "Devstead.scene" );
			var renamed = System.IO.Path.Combine( root, "DEVSTEAD.SCENE" );
			var identity = System.IO.Path.Combine( root, "devstead.scene" );
			System.IO.File.WriteAllText( original, "source" );
			Assert.AreEqual( original, CaseInsensitivePhysicalFileSystem.ResolveNativeCasing( identity ) );

			System.IO.File.Move( original, renamed );
			Assert.AreEqual( renamed, CaseInsensitivePhysicalFileSystem.ResolveNativeCasing( identity ) );
		} );
	}

	[TestMethod]
	public void ResolveNativeCasing_FindsLegacyTwinAfterExactFileDeleted()
	{
		if ( !System.OperatingSystem.IsLinux() ) return;

		WithTemporaryDirectory( root =>
		{
			var identity = System.IO.Path.Combine( root, "devstead.scene_d" );
			var legacy = System.IO.Path.Combine( root, "Devstead.scene_d" );
			System.IO.File.WriteAllText( identity, "new" );
			System.IO.File.WriteAllText( legacy, "old" );
			Assert.AreEqual( identity, CaseInsensitivePhysicalFileSystem.ResolveNativeCasing( identity ) );

			System.IO.File.Delete( identity );
			Assert.AreEqual( legacy, CaseInsensitivePhysicalFileSystem.ResolveNativeCasing( identity ) );
		} );
	}

	[TestMethod]
	public void ResolveNativeCasing_NewExactDirectorySupersedesCachedAlias()
	{
		if ( !System.OperatingSystem.IsLinux() ) return;

		WithTemporaryDirectory( root =>
		{
			var legacy = System.IO.Path.Combine( root, "Scenes" );
			var identity = System.IO.Path.Combine( root, "scenes" );
			System.IO.Directory.CreateDirectory( legacy );
			var original = System.IO.Path.Combine( legacy, "source.scene" );
			var requested = System.IO.Path.Combine( identity, "source.scene" );
			System.IO.File.WriteAllText( original, "source" );
			Assert.AreEqual( original, CaseInsensitivePhysicalFileSystem.ResolveNativeCasing( requested ) );

			System.IO.Directory.CreateDirectory( identity );
			Assert.AreEqual( requested, CaseInsensitivePhysicalFileSystem.ResolveNativeCasing( requested ) );
			Assert.IsFalse( System.IO.File.Exists( requested ) );
		} );
	}

	[TestMethod]
	public void SaveToDisk_WritesCanonicalBlobAfterLegacyRead()
	{
		WithTemporaryDirectory( root =>
		{
			var source = System.IO.Path.Combine( root, "Devstead.scene" );
			var identity = System.IO.Path.Combine( root, "devstead.scene_d" );
			System.IO.File.WriteAllText( source + "_d", "old" );
			CaseInsensitivePhysicalFileSystem.ResolveNativeCasing( identity );
			var resource = new SceneFile { BinaryData = [1, 2, 3] };
			resource.SaveToDisk( source, "{}" );

			Assert.AreEqual( "{}", System.IO.File.ReadAllText( source ) );
			CollectionAssert.AreEqual( new byte[] { 1, 2, 3 }, System.IO.File.ReadAllBytes( Asset.ResolveRelatedFilePath( source, "_d" ) ) );
			Assert.IsTrue( System.IO.File.Exists( identity ) );
		} );
	}

	[TestMethod]
	public void SceneDataFolder_DerivesIdentityAndCreatesCanonicalDirectory()
	{
		WithTemporaryDirectory( root =>
		{
			var source = System.IO.Path.Combine( root, "Devstead.SCENE" );
			var expected = System.IO.Path.Combine( root, "devstead_scene_data" );
			Assert.AreEqual( expected, SceneDataFolder.GetPath( source ) );
			Assert.AreEqual( expected, SceneDataFolder.Prepare( source ) );
			Assert.IsTrue( System.IO.Directory.Exists( expected ) );
		} );
	}

	[TestMethod]
	public void SceneDataFolder_MigratesExistingLegacyTree()
	{
		if ( !System.OperatingSystem.IsLinux() ) return;

		WithTemporaryDirectory( root =>
		{
			var source = System.IO.Path.Combine( root, "Devstead.SCENE" );
			var legacy = System.IO.Path.Combine( root, "Devstead_SCENE_data" );
			System.IO.Directory.CreateDirectory( System.IO.Path.Combine( legacy, "compiled" ) );
			System.IO.File.WriteAllText( System.IO.Path.Combine( legacy, "compiled", "mesh.vmdl_c" ), "fragment" );

			var canonical = SceneDataFolder.Prepare( source );
			Assert.AreEqual( "fragment", System.IO.File.ReadAllText( System.IO.Path.Combine( canonical, "compiled", "mesh.vmdl_c" ) ) );
			Assert.IsFalse( System.IO.Directory.Exists( legacy ) );
		} );
	}

	[TestMethod]
	public void SceneDataFolder_ConflictingTreesArePreserved()
	{
		if ( !System.OperatingSystem.IsLinux() ) return;

		WithTemporaryDirectory( root =>
		{
			var source = System.IO.Path.Combine( root, "Devstead.scene" );
			var legacy = System.IO.Path.Combine( root, "Devstead_scene_data" );
			var canonical = SceneDataFolder.GetPath( source );
			System.IO.Directory.CreateDirectory( legacy );
			System.IO.Directory.CreateDirectory( canonical );
			System.IO.File.WriteAllText( System.IO.Path.Combine( legacy, "legacy.bin" ), "old" );
			System.IO.File.WriteAllText( System.IO.Path.Combine( canonical, "canonical.bin" ), "new" );

			Assert.ThrowsException<System.IO.IOException>( () => SceneDataFolder.Prepare( source ) );
			Assert.AreEqual( "old", System.IO.File.ReadAllText( System.IO.Path.Combine( legacy, "legacy.bin" ) ) );
			Assert.AreEqual( "new", System.IO.File.ReadAllText( System.IO.Path.Combine( canonical, "canonical.bin" ) ) );
		} );
	}

	[TestMethod]
	public void MoveAsset_TreeOwningSourceRelocatesTreeAndDropsStaleOutput()
	{
		WithTemporaryDirectory( root =>
		{
			var source = System.IO.Path.Combine( root, "Devstead.scene" );
			var target = System.IO.Path.Combine( root, "Newstead.scene" );
			System.IO.File.WriteAllText( source, "source" );
			System.IO.File.WriteAllText( Asset.GetRelatedFilePath( source, "_c" ), "compiled" );
			var oldDirectory = SceneDataFolder.GetPath( source );
			System.IO.Directory.CreateDirectory( oldDirectory );
			System.IO.File.WriteAllText( System.IO.Path.Combine( oldDirectory, "marker.bin" ), "tree" );
			var asset = new StubAsset { SourceFile = source };
			asset.SetPath( source );

			Assert.IsTrue( EditorUtility.MoveAsset( asset, target, true ) );

			var newDirectory = SceneDataFolder.GetPath( target );
			Assert.AreEqual( "tree", System.IO.File.ReadAllText( System.IO.Path.Combine( newDirectory, "marker.bin" ) ) );
			Assert.IsFalse( System.IO.Directory.Exists( oldDirectory ) );
			Assert.IsFalse( System.IO.File.Exists( Asset.GetRelatedFilePath( target, "_c" ) ) );
			Assert.IsFalse( System.IO.File.Exists( source ) );
		} );
	}

	[TestMethod]
	public void CopyAsset_TreeOwningSourceKeepsTreeAndDropsCopiedOutput()
	{
		WithTemporaryDirectory( root =>
		{
			var source = System.IO.Path.Combine( root, "Devstead.scene" );
			var target = System.IO.Path.Combine( root, "Newstead.scene" );
			System.IO.File.WriteAllText( source, "source" );
			System.IO.File.WriteAllText( Asset.GetRelatedFilePath( source, "_c" ), "compiled" );
			var oldDirectory = SceneDataFolder.GetPath( source );
			System.IO.Directory.CreateDirectory( oldDirectory );
			System.IO.File.WriteAllText( System.IO.Path.Combine( oldDirectory, "marker.bin" ), "tree" );
			var asset = new StubAsset { SourceFile = source };
			asset.SetPath( source );

			Assert.IsTrue( EditorUtility.CopyAsset( asset, target, true ) );

			Assert.AreEqual( "tree", System.IO.File.ReadAllText( System.IO.Path.Combine( oldDirectory, "marker.bin" ) ) );
			Assert.IsFalse( System.IO.Directory.Exists( SceneDataFolder.GetPath( target ) ) );
			Assert.IsFalse( System.IO.File.Exists( Asset.GetRelatedFilePath( target, "_c" ) ) );
			Assert.IsTrue( System.IO.File.Exists( source ) );
		} );
	}

	[TestMethod]
	public void CopyAsset_TreelessSourceKeepsWorkingOutput()
	{
		WithTemporaryDirectory( root =>
		{
			var source = System.IO.Path.Combine( root, "Voice.WAV" );
			var target = System.IO.Path.Combine( root, "Next.WAV" );
			System.IO.File.WriteAllText( source, "source" );
			System.IO.File.WriteAllText( Asset.GetRelatedFilePath( source, "_c" ), "compiled" );
			var asset = new StubAsset { SourceFile = source };
			asset.SetPath( source );

			Assert.IsTrue( EditorUtility.CopyAsset( asset, target, true ) );

			Assert.AreEqual( "compiled", System.IO.File.ReadAllText( Asset.GetRelatedFilePath( target, "_c" ) ) );
			Assert.IsFalse( System.IO.Directory.Exists( SceneDataFolder.GetPath( target ) ) );
		} );
	}
}
