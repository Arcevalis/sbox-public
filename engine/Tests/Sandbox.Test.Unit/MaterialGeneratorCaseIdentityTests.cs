using Editor;
using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

[TestClass]
public class MaterialGeneratorCaseIdentityTests
{
	sealed class TextureAsset : Asset
	{
		public TextureAsset() => RelativePath = "Textures/Brick_Color.PNG";
		internal override void UpdateInternals( bool compileImmediately = true ) { }
		public override bool CanRecompile => false;
		public override string GetCompiledFile( bool absolute = false ) => null;
		public override string GetSourceFile( bool absolute = false ) => RelativePath;
		internal override int FindIntEditInfo( string name ) => 0;
		public override string FindStringEditInfo( string name ) => null;
		public override void OpenInEditor( string nativeEditor = null ) { }
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
		public override bool HasSourceFile => true;
		public override bool HasCompiledFile => false;
		public override bool SetInMemoryReplacement( string sourceData ) => false;
		public override void ClearInMemoryReplacement() { }
	}

	static MaterialGenerator.TextureSet CreateSet( string directory ) => new()
	{
		Directory = directory,
		BaseName = "BrIcK",
		BySuffix = new Dictionary<string, Asset>( StringComparer.OrdinalIgnoreCase ) { ["color"] = new TextureAsset() }
	};

	static Dictionary<string, string> SuffixMap() => new() { ["color"] = "TextureColor" };

	[TestMethod]
	public void NewMaterialUsesLowercaseFilenameInPhysicalDirectory()
	{
		var root = Path.Combine( Path.GetTempPath(), $"sbox-material-case-{Guid.NewGuid():N}" );
		var directory = Path.Combine( root, "MiXeD", "Materials" );
		Directory.CreateDirectory( directory );
		try
		{
			var target = MaterialGenerator.GetOrCreateMaterialSource( CreateSet( directory ), "Shaders\\MiXeD.SHADER", SuffixMap() );
			Assert.AreEqual( Path.Combine( directory, "brick.vmat" ), target );
			Assert.IsTrue( File.Exists( target ) );
			var contents = File.ReadAllText( target );
			StringAssert.Contains( contents, "\"textures/brick_color.png\"" );
			StringAssert.Contains( contents, "shader \"shaders/mixed.shader\"" );
			Assert.AreEqual( 1, Directory.GetFiles( directory ).Length );
		}
		finally
		{
			Directory.Delete( root, true );
		}
	}

	[DataTestMethod]
	[DataRow( "brick.vmat" )]
	[DataRow( "Brick.VMAT" )]
	public void ExistingMaterialKeepsDiskSpellingAndContents( string filename )
	{
		var root = Path.Combine( Path.GetTempPath(), $"sbox-material-existing-{Guid.NewGuid():N}" );
		var directory = Path.Combine( root, "MiXeD", "Materials" );
		Directory.CreateDirectory( directory );
		try
		{
			var existing = Path.Combine( directory, filename );
			File.WriteAllText( existing, "existing artist material" );
			var timestamp = new DateTime( 2020, 1, 1, 0, 0, 0, DateTimeKind.Utc );
			File.SetLastWriteTimeUtc( existing, timestamp );

			var target = MaterialGenerator.GetOrCreateMaterialSource( CreateSet( directory ), MaterialGenerator.DefaultShader, SuffixMap() );

			if ( OperatingSystem.IsLinux() )
				Assert.AreEqual( existing, target );
			Assert.AreEqual( "existing artist material", File.ReadAllText( target ) );
			Assert.AreEqual( timestamp, File.GetLastWriteTimeUtc( existing ) );
			Assert.AreEqual( 1, Directory.GetFiles( directory ).Length );
		}
		finally
		{
			Directory.Delete( root, true );
		}
	}

	[TestMethod]
	public void ExactCanonicalMaterialWinsOverCaseDistinctLegacySibling()
	{
		if ( !OperatingSystem.IsLinux() ) return;
		var root = Path.Combine( Path.GetTempPath(), $"sbox-material-sibling-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( root );
		try
		{
			var canonical = Path.Combine( root, "brick.vmat" );
			var legacy = Path.Combine( root, "BrIcK.VMAT" );
			File.WriteAllText( canonical, "canonical" );
			File.WriteAllText( legacy, "legacy" );

			var target = MaterialGenerator.GetOrCreateMaterialSource( CreateSet( root ), MaterialGenerator.DefaultShader, SuffixMap() );

			Assert.AreEqual( canonical, target );
			Assert.AreEqual( "canonical", File.ReadAllText( canonical ) );
			Assert.AreEqual( "legacy", File.ReadAllText( legacy ) );
			Assert.AreEqual( 2, Directory.GetFiles( root ).Length );
		}
		finally
		{
			Directory.Delete( root, true );
		}
	}
}
