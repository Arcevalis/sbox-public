using Editor;
using System;
using System.Collections.Generic;

namespace SceneTests.Prefab;

public partial class InstancesTest
{
	[DataTestMethod]
	[DataRow( "___Prefab_Writeback/Source.PREFAB" )]
	[DataRow( "/___Prefab_Writeback\\Source.PREFAB" )]
	[DataRow( "___Prefab_Writeback/Source.PREFAB_C" )]
	public void WriteBackInstanceWithPathAliasPreservesPrefabAndInstanceIds( string alias )
	{
		const string path = "___prefab_writeback/source.prefab";
		var file = new PrefabFile();
		file.LoadFromJson( "{\"RootObject\":" + _basicPrefabSource + "}" );
		var resourceGuid = Guid.NewGuid();
		file.RegisterWeakResourceId( path, resourceGuid );
		file.Register( path );
		try
		{
			Assert.AreEqual( resourceGuid, file.Guid );
			var prefabScene = SceneUtility.GetPrefabScene( file );
			var prefabRootId = prefabScene.Id;
			var prefabComponentId = prefabScene.Components.Get<ModelRenderer>().Id;
			var prefabPosition = prefabScene.LocalPosition;
			var scene = new Scene();
			using var scope = scene.Push();
			var instance = prefabScene.Clone( new Vector3( 100, 200, 300 ) );
			var instancePosition = instance.LocalPosition;
			var mappings = new Dictionary<Guid, Guid>( instance.PrefabInstance.PrefabToInstanceLookup );
			instance.InitPrefabInstance( new ResourceId { Guid = resourceGuid, Path = alias }, false );
			Assert.AreEqual( path, instance.PrefabInstanceSource );
			instance.PrefabInstance.InitLookups( mappings );
			// Keep exercising legacy instances adopted before source paths were canonicalized.
			typeof( PrefabInstanceData ).GetProperty( nameof( PrefabInstanceData.PrefabSource ) )
				.SetValue( instance.PrefabInstance, new ResourceId { Guid = resourceGuid, Path = alias } );
			Assert.AreEqual( alias, instance.PrefabInstanceSource );
			Assert.IsTrue( instance.IsOutermostPrefabInstanceRoot );
			var instanceId = instance.Id;
			var renderer = instance.Components.Get<ModelRenderer>();
			var componentId = renderer.Id;
			renderer.Tint = Color.Blue;
			instance.PrefabInstance.RefreshPatch();
			Assert.IsTrue( instance.PrefabInstance.IsModified() );

			EditorUtility.Prefabs.WriteInstanceToPrefab( instance, skipDiskWrite: true );

			Assert.AreEqual( prefabRootId, prefabScene.Id );
			Assert.AreEqual( prefabComponentId, prefabScene.Components.Get<ModelRenderer>().Id );
			Assert.AreEqual( prefabPosition, prefabScene.LocalPosition );
			Assert.AreEqual( Color.Blue, prefabScene.Components.Get<ModelRenderer>().Tint );
			Assert.AreEqual( instanceId, instance.Id );
			Assert.AreEqual( componentId, instance.Components.Get<ModelRenderer>().Id );
			Assert.AreEqual( instancePosition, instance.LocalPosition );
			Assert.IsFalse( instance.PrefabInstance.IsModified() );
			Assert.AreEqual( mappings.Count, instance.PrefabInstance.PrefabToInstanceLookup.Count );
			foreach ( var (prefabId, mappedInstanceId) in mappings )
				Assert.AreEqual( mappedInstanceId, instance.PrefabInstance.PrefabToInstanceLookup[prefabId] );
		}
		finally
		{
			Game.Resources.Unregister( file );
		}
	}
}
