using Sandbox;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

[TestClass]
public class ResourceFolderCaseIdentityTests
{
	sealed class TestResource : Resource
	{
		public TestResource( string path )
		{
			ResourcePath = Resource.FixPath( path );
#pragma warning disable CS0618
			ResourceId = ResourcePath.FastHash();
#pragma warning restore CS0618
			ResourceIdLong = ResourcePath.FastHash64();
		}

		public override bool IsValid => true;
	}

	[TestMethod]
	public void FolderQuery_NormalizesCaseSeparatorsAndRoot()
	{
		var resources = new ResourceSystem();
		var direct = new TestResource( "Scenes/Devstead.scene" );
		var child = new TestResource( "Scenes/Child/Another.scene" );
		var sibling = new TestResource( "ScenesElsewhere/Other.scene" );
		var compiledSuffixFolder = new TestResource( "Scenes_c/Other.scene" );
		var index = (Dictionary<int, Resource>)typeof( ResourceSystem )
			.GetProperty( "ResourceIndex", BindingFlags.Instance | BindingFlags.NonPublic ).GetValue( resources );
		index.Add( 1, direct );
		index.Add( 2, child );
		index.Add( 3, sibling );
		index.Add( 4, compiledSuffixFolder );

		CollectionAssert.AreEquivalent( new[] { direct, child }, resources.GetAll<TestResource>( "/SCENES\\" ).ToArray() );
		CollectionAssert.AreEquivalent( new[] { direct }, resources.GetAll<TestResource>( "ScEnEs", false ).ToArray() );
		CollectionAssert.AreEquivalent( new[] { child }, resources.GetAll<TestResource>( "SCENES\\CHILD" ).ToArray() );
		CollectionAssert.AreEquivalent( new[] { compiledSuffixFolder }, resources.GetAll<TestResource>( "SCENES_C" ).ToArray() );
		CollectionAssert.AreEquivalent( new[] { compiledSuffixFolder }, resources.GetAll<TestResource>( "SCENES_C/" ).ToArray() );
	}
}
