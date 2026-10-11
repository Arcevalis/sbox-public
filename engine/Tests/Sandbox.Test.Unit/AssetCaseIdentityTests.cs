using Editor;
using Sandbox;
using System.Collections.Generic;
using System.Threading.Tasks;

[TestClass]
public class AssetCaseIdentityTests
{
	sealed class StubAsset : Asset
	{
		public string SourceFile { get; set; }
		public void SetPath( string path ) => AbsolutePath = path;

		internal override void UpdateInternals( bool compileImmediately = true ) { }
		public override bool CanRecompile => false;
		public override string GetCompiledFile( bool absolute ) => null;
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

	// Sources keep their disk case, but the engine identity is lowercase:
	// Devstead.scene in, devstead.scene_c out.
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

		Assert.AreEqual(
			System.IO.Path.GetFullPath( System.IO.Path.Combine( dir, "devstead.scene.meta" ) ),
			asset.GetMetadataFile( true ) );
	}

	[TestMethod]
	public void MetadataFile_RelativeSourceUnchanged()
	{
		var asset = new StubAsset();
		asset.SourceFile = "scenes/devstead.scene";
		asset.SetPath( System.IO.Path.GetFullPath( "scenes/devstead.scene" ) );

		Assert.AreEqual( "scenes/devstead.scene.meta", asset.GetMetadataFile( false ) );
	}

	[TestMethod]
	public void MetadataFile_NullSourceReturnsNull()
	{
		var asset = new StubAsset();

		Assert.IsNull( asset.GetMetadataFile( true ) );
		Assert.IsNull( asset.GetMetadataFile( false ) );
	}

	[TestMethod]
	public void MetadataFile_CloudSourceReturnsNull()
	{
		var asset = new StubAsset();
		var source = "/home/user/.sbox/cloud/pkg/Devstead.scene";
		asset.SourceFile = source;
		asset.SetPath( source );

		Assert.IsNull( asset.GetMetadataFile( true ) );
	}
}
