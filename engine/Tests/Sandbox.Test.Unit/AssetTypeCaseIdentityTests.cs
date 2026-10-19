using Editor;
using System.Collections.Generic;
using System.Reflection;

[TestClass, DoNotParallelize]
public class AssetTypeCaseIdentityTests
{
	static readonly FieldInfo ExtensionCacheField = typeof( AssetType ).GetField( "_extensionCache", BindingFlags.Static | BindingFlags.NonPublic );

	Dictionary<int, AssetType> savedAssetTypes;
	Dictionary<string, AssetType> savedExtensionCache;
	AssetType assetType;

	[TestInitialize]
	public void Initialize()
	{
		savedAssetTypes = AssetType.AssetTypeCache;
		savedExtensionCache = (Dictionary<string, AssetType>)ExtensionCacheField.GetValue( null );
		AssetType.AssetTypeCache = new();
		ExtensionCacheField.SetValue( null, new Dictionary<string, AssetType>( savedExtensionCache.Comparer ) );

		assetType = new AssetType
		{
			FileExtension = "scene",
			AllFileExtensions = new() { "scene", "scn" }
		};
		AssetType.AssetTypeCache.Add( 1, assetType );
	}

	[TestCleanup]
	public void Cleanup()
	{
		AssetType.AssetTypeCache = savedAssetTypes;
		ExtensionCacheField.SetValue( null, savedExtensionCache );
	}

	[DataTestMethod]
	[DataRow( "scene" )]
	[DataRow( ".SCENE" )]
	[DataRow( "scene_c" )]
	[DataRow( ".ScEnE_C" )]
	[DataRow( "scn" )]
	[DataRow( ".SCN" )]
	[DataRow( "scn_c" )]
	[DataRow( ".ScN_C" )]
	public void FromExtension_SourceAndCompiledSpellingsResolveOnColdCache( string extension )
	{
		Assert.AreSame( assetType, AssetType.FromExtension( extension ) );
	}

	[DataTestMethod]
	[DataRow( "Scenes/Example.scene" )]
	[DataRow( "Scenes/Example.SCENE_C" )]
	[DataRow( "Scenes/Example.ScEnE_c" )]
	[DataRow( "Scenes/Example.scn" )]
	[DataRow( "Scenes/Example.SCN_C" )]
	[DataRow( "Scenes/Example.ScN_c" )]
	public void ResolveFromPath_SourceAndCompiledSpellingsResolveOnColdCache( string path )
	{
		Assert.AreSame( assetType, AssetType.ResolveFromPath( path ) );
	}

	[DataTestMethod]
	[DataRow( "scene_C", "scene_c" )]
	[DataRow( "scene_c", "scene_C" )]
	[DataRow( ".ScEnE_C", ".scene_c" )]
	[DataRow( ".scene_c", ".ScEnE_C" )]
	[DataRow( "scn_C", "scn_c" )]
	[DataRow( "scn_c", "scn_C" )]
	public void FromExtension_CompiledSuffixCannotPoisonCaseInsensitiveCache( string first, string second )
	{
		// Without case-insensitive stripping, uppercase _C matches this literal
		// extension and caches the wrong type for subsequent lowercase _c lookups.
		var compiledExtensionType = new AssetType
		{
			FileExtension = "scene_c",
			AllFileExtensions = new() { "scene_c", "scn_c" }
		};
		AssetType.AssetTypeCache.Add( 2, compiledExtensionType );

		Assert.AreSame( assetType, AssetType.FromExtension( first ) );
		Assert.AreSame( assetType, AssetType.FromExtension( second ) );
	}

	[TestMethod]
	public void FromExtension_MissingTypeIsNotCached()
	{
		Assert.IsNull( AssetType.FromExtension( ".LATER_C" ) );
		var laterType = new AssetType { FileExtension = "later" };
		AssetType.AssetTypeCache.Add( 2, laterType );

		Assert.AreSame( laterType, AssetType.FromExtension( ".later_c" ) );
		Assert.AreSame( laterType, AssetType.FromExtension( ".LATER_C" ) );
	}
}
