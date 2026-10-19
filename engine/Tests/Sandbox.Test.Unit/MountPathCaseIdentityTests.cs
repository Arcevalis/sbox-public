using Sandbox.Mounting;
using System.Reflection;
using System.Threading.Tasks;
using ResourceLoader = Sandbox.Mounting.ResourceLoader;

[TestClass]
public class MountPathCaseIdentityTests
{
	sealed class TestMount : BaseGameMount
	{
		public override string Ident => "testgame";
		public override string Title => "Test Game";
		protected override void Initialize( InitializeContext context ) { }
		protected override Task Mount( MountContext context ) => Task.CompletedTask;
	}

	sealed class TestLoader : ResourceLoader { }

	static TestLoader CreateLoader( string path, out TestMount mount )
	{
		mount = new TestMount();
		var loader = new TestLoader();
		typeof( ResourceLoader ).GetMethod( "InitializeInternal", BindingFlags.Instance | BindingFlags.NonPublic )
			.Invoke( loader, new object[] { ResourceType.Scene, path, mount } );
		return loader;
	}

	[DataTestMethod]
	[DataRow( "Maps/Example.SCENE", "Maps/Example.SCENE" )]
	[DataRow( "Maps/Example.scene", "Maps/Example.scene" )]
	[DataRow( "/Maps\\Example/", "Maps/Example.scene" )]
	public void LoaderKeepsRelativeSpellingButCanonicalizesIdentity( string input, string relativePath )
	{
		var loader = CreateLoader( input, out var mount );

		Assert.AreEqual( relativePath, loader.RelativePath );
		Assert.AreEqual( "Example", loader.Name );
		Assert.AreEqual( "mount://testgame/maps/example.scene", loader.Path );
		Assert.AreSame( loader, mount.GetByPath( "mount://testgame/MAPS/EXAMPLE.SCENE" ) );
	}

	[DataTestMethod]
	[DataRow( "Maps/Example.SCENE" )]
	[DataRow( "maps/example.scene" )]
	[DataRow( "Maps\\Example" )]
	public void ThumbnailWriterAndReaderUseCanonicalRelativeIdentity( string input )
	{
		var loader = CreateLoader( input, out _ );

		Assert.AreEqual( "thumbs/testgame/maps/example.png", MountUtility.GetThumbnailPath( loader ) );
	}
}
