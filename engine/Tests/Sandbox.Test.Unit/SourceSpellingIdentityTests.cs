using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

[TestClass]
public class SourceSpellingIdentityTests
{
	sealed class StubAsset : Asset
	{
		public string SourceFile { get; set; }

		internal override void UpdateInternals( bool compileImmediately = true ) { }
		public override bool CanRecompile => false;
		public override string GetCompiledFile( bool absolute ) => null;
		public override string GetSourceFile( bool absolute ) => SourceFile;
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

	[DataTestMethod]
	[DataRow( "/tmp/project/Assets/Scenes/MiXeDcAsE.scene", true )]
	[DataRow( "/tmp/project/Assets/Scenes/MIXEDCASE.SCENE", true )]
	[DataRow( "/tmp/project/Assets/Scenes/mixedcase.scene", false )]
	[DataRow( "/tmp/project/Assets/MiXeD/mixedcase.scene", false )]
	[DataRow( null, false )]
	public void SourceSpellingDiffersFromIdentity_PinsFilenameRule( string source, bool expected )
	{
		var asset = new StubAsset { SourceFile = source };
		Assert.AreEqual( expected, AssetSystem.SourceSpellingDiffersFromIdentity( asset ) );
	}

	[TestMethod]
	public void SourceSpellingDiffersFromIdentity_NullAsset_ReturnsFalse()
	{
		Assert.IsFalse( AssetSystem.SourceSpellingDiffersFromIdentity( null ) );
	}
}
