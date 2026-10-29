using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

[TestClass]
public class PrefabCompileBypassTests
{
	sealed class StubAsset : Asset
	{
		public string SourceFile { get; set; }
		public void SetResourcePath( string path ) => Path = path;

		internal override void UpdateInternals() { }
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

	static StubAsset Prefab( string source, string path )
	{
		var asset = new StubAsset { SourceFile = source };
		asset.SetResourcePath( path );
		return asset;
	}

	[DataTestMethod]
	[DataRow( "/tmp/project/Assets/Prefabs/MiXeD.prefab", "prefabs/mixed.prefab", true )]
	[DataRow( "/tmp/project/Assets/Prefabs/MIXED.PREFAB", "prefabs/mixed.prefab", true )]
	[DataRow( "/tmp/project/Assets/Prefabs/mixed.prefab", "prefabs/mixed.prefab", false )]
	[DataRow( "/tmp/project/Assets/MiXeD/mixed.prefab", "prefabs/mixed.prefab", false )]
	[DataRow( "/tmp/project/Assets/Prefabs/MiXeD.prefab", "prefabs/mixed.PREFAB", true )]
	[DataRow( "/tmp/project/Assets/Prefabs/MiXeD.prefab", "prefabs/mixed.scene", false )]
	[DataRow( "/tmp/project/Assets/Scenes/MiXeD.scene", "scenes/mixed.scene", false )]
	[DataRow( null, "prefabs/mixed.prefab", false )]
	public void ShouldBypassNativePrefabCompile_PinsPrefabRouting( string source, string path, bool expected )
	{
		Assert.AreEqual( expected, PrefabCompiler.ShouldBypassNativePrefabCompile( Prefab( source, path ) ) );
	}

	[TestMethod]
	public void ShouldBypassNativePrefabCompile_NullAsset_ReturnsFalse()
	{
		Assert.IsFalse( PrefabCompiler.ShouldBypassNativePrefabCompile( null ) );
	}

	[TestMethod]
	public void ShouldBypassNativePrefabCompile_NullPath_ReturnsFalse()
	{
		Assert.IsFalse( PrefabCompiler.ShouldBypassNativePrefabCompile( Prefab( "/tmp/project/Assets/Prefabs/MiXeD.prefab", null ) ) );
	}
}
