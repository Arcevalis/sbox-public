using System;
using System.Text.Json.Nodes;

namespace SceneTests.Prefab;

public partial class InstancesTest
{
	[DataTestMethod]
	[DataRow( "__Adoption_Missing/MiXeD.PREFAB", false )]
	[DataRow( "__Adoption_Missing/MiXeD.PREFAB", true )]
	[DataRow( "/__Adoption_Missing\\MiXeD.PREFAB", false )]
	[DataRow( "/__Adoption_Missing\\MiXeD.PREFAB", true )]
	[DataRow( "/__Adoption_Missing\\MiXeD.PREFAB_C", false )]
	[DataRow( "/__Adoption_Missing\\MiXeD.PREFAB_C", true )]
	[DataRow( "/__Adoption_Missing\\MiXeD.OBJECT_C", false )]
	[DataRow( "/__Adoption_Missing\\MiXeD.OBJECT_C", true )]
	public void MissingPrefabSourceIsCanonicalBeforeLoadAndAcrossSerializationFormats( string alias, bool withGuid )
	{
		const string expected = "__adoption_missing/mixed.prefab";
		Guid? guid = withGuid ? Guid.NewGuid() : null;
		var source = new ResourceId { Guid = guid, Path = alias };
		Assert.IsNull( ResourceLibrary.Get<PrefabFile>( source ) );
		var scene = new Scene();
		using var scope = scene.Push();
		var instance = new GameObject( "Missing Prefab" );

		instance.InitPrefabInstance( source, false );

		Assert.AreEqual( expected, instance.PrefabInstanceSource );
		Assert.AreEqual( guid, instance.PrefabInstance.PrefabSource.Guid );
		Assert.AreEqual( alias, source.Path );
		AssertPrefabSourceReference( instance.Serialize()[GameObject.JsonKeys.PrefabInstanceSource], expected, guid );

		instance.NetworkMode = NetworkMode.Object;
		var network = instance.Serialize( new GameObject.SerializeOptions { SingleNetworkObject = true } );
		Assert.AreEqual( expected, network[GameObject.JsonKeys.NetworkedPrefabInstance].GetValue<string>() );

		var parent = new GameObject( "Outer Prefab" );
		parent.InitPrefabInstance( "__adoption_missing/parent.prefab", false );
		instance.InitPrefabInstance( source, true );
		instance.Parent = parent;
		Assert.IsTrue( instance.IsNestedPrefabInstanceRoot );
		Assert.IsFalse( instance.IsOutermostPrefabInstanceRoot );
		var nested = instance.Serialize();
		AssertPrefabSourceReference( nested[GameObject.JsonKeys.EditorPrefabInstanceNestedSource], expected, guid );
		var undo = instance.Serialize( new GameObject.SerializeOptions { SerializeForUndo = true } );
		AssertPrefabSourceReference( undo[GameObject.JsonKeys.EditorPrefabInstanceNestedSource], expected, guid );

		var shallowUndo = instance.Serialize( new GameObject.SerializeOptions { SerializeForUndo = true, IgnoreChildren = true } );
		Assert.IsTrue( shallowUndo[GameObject.JsonKeys.EditorSkipPrefabBreakOnRefresh].GetValue<bool>() );
		var ownership = instance.PrefabInstance.CaptureOwnership();
		AssertPrefabSourceReference( Json.ToNode( ownership.Source ), expected, guid );
		Assert.IsNull( ResourceLibrary.Get<PrefabFile>( source ) );
	}

	[DataTestMethod]
	[DataRow( null )]
	[DataRow( "" )]
	public void GuidOnlyPrefabSourcePreservesGuidAndAbsentPath( string path )
	{
		var scene = new Scene();
		using var scope = scene.Push();
		var instance = new GameObject( "Guid Only Prefab" );
		var guid = Guid.NewGuid();

		instance.InitPrefabInstance( new ResourceId { Guid = guid, Path = path }, false );

		Assert.IsTrue( instance.IsPrefabInstanceRoot );
		Assert.AreEqual( guid, instance.PrefabInstance.PrefabSource.Guid );
		Assert.AreEqual( path, instance.PrefabInstanceSource );
		AssertPrefabSourceReference( instance.Serialize()[GameObject.JsonKeys.PrefabInstanceSource], path, guid );
	}

	[DataTestMethod]
	[DataRow( null, false )]
	[DataRow( "", false )]
	[DataRow( null, true )]
	[DataRow( "", true )]
	public void EmptyPrefabIdClearsExistingInstance( string path, bool emptyGuid )
	{
		var scene = new Scene();
		using var scope = scene.Push();
		var instance = new GameObject( "Previously A Prefab" );
		instance.InitPrefabInstance( "__adoption_missing/previous.prefab", false );
		Assert.IsTrue( instance.IsPrefabInstanceRoot );

		instance.InitPrefabInstance( new ResourceId { Guid = emptyGuid ? Guid.Empty : null, Path = path }, false );

		Assert.IsFalse( instance.IsPrefabInstanceRoot );
		Assert.IsNull( instance.PrefabInstance );
		Assert.IsNull( instance.PrefabInstanceSource );
		Assert.IsNull( instance.Serialize()[GameObject.JsonKeys.PrefabInstanceSource] );
	}

	static void AssertPrefabSourceReference( JsonNode node, string path, Guid? guid )
	{
		Assert.IsNotNull( node );
		var source = Json.FromNode<ResourceId>( node );
		Assert.AreEqual( path, source.Path );
		Assert.AreEqual( guid, source.Guid );
	}
}
