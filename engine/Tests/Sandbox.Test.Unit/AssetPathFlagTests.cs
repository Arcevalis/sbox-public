using Editor;
using Sandbox;
using System.Collections.Generic;
using System.Threading.Tasks;

[TestClass]
public class AssetPathFlagTests
{
	sealed class StubAsset : Asset
	{
		public void SetPath( string path ) => AbsolutePath = path;

		internal override void UpdateInternals( bool compileImmediately = true ) { }
		public override bool CanRecompile => false;
		public override string GetCompiledFile( bool absolute ) => null;
		public override string GetSourceFile( bool absolute ) => null;
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

	// Fresh and deleted assets have a null AbsolutePath (IsDeleted covers it).
	// The flags must answer false, not throw: editor load scans every asset
	// through GetMetadataFile -> IsCloud on first update, when the managed
	// path field has not been assigned yet.
	[TestMethod]
	public void CloudAndTransientFlagsTolerateNullPath()
	{
		var asset = new StubAsset();
		Assert.IsFalse( asset.IsCloud );
		Assert.IsFalse( asset.IsTransient );
	}

	[TestMethod]
	public void CloudFlagMatchesCloudFolder()
	{
		var asset = new StubAsset();
		asset.SetPath( "/home/user/.sbox/cloud/mypackage/models/foo.vmdl" );
		Assert.IsTrue( asset.IsCloud );
		Assert.IsFalse( asset.IsTransient );

		asset.SetPath( "/home/user/game/core/models/foo.vmdl" );
		Assert.IsFalse( asset.IsCloud );
	}

	[TestMethod]
	public void TransientFlagMatchesTransientFolders()
	{
		var asset = new StubAsset();
		asset.SetPath( "/home/user/.sbox/transient/foo.vmdl" );
		Assert.IsTrue( asset.IsTransient );

		asset.SetPath( "/home/user/game/addons/menu/transients/foo.vmdl" );
		Assert.IsTrue( asset.IsTransient );

		asset.SetPath( "/home/user/game/core/models/foo.vmdl" );
		Assert.IsFalse( asset.IsTransient );
	}
}
