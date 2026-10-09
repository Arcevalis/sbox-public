using Sandbox.Rendering;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace SceneTests;

[TestClass]
public class DecalTests
{
	[TestMethod]
	public void DefinitionsDefaultToEqualWeightAlphaBlending()
	{
		var definition = new DecalDefinition();
		Assert.AreEqual( DecalBlendMode.Alpha, definition.BlendMode );
		Assert.AreEqual( 1f, definition.SelectionWeight );
		Assert.IsTrue( typeof( DecalBlendMode ).GetCustomAttributesData().Any( attribute => attribute.AttributeType.Name == "ExposeAttribute" ) );
	}

	[TestMethod]
	public void WeightedSelectionPreservesIntervalsAndSkipsZeroWeights()
	{
		var first = new DecalDefinition { SelectionWeight = 1 };
		var zero = new DecalDefinition { SelectionWeight = 0 };
		var last = new DecalDefinition { SelectionWeight = 3 };
		DecalDefinition[] definitions = [first, zero, last];
		Assert.AreSame( first, Decal.SelectDefinition( definitions, 0 ) );
		Assert.AreSame( first, Decal.SelectDefinition( definitions, 0.249f ) );
		Assert.AreSame( last, Decal.SelectDefinition( definitions, 0.25f ) );
		Assert.AreSame( last, Decal.SelectDefinition( definitions, 1 ) );
	}

	[TestMethod]
	public void EqualWeightsReachEveryDefinitionBeyond64()
	{
		var definitions = Enumerable.Range( 0, 100 ).Select( _ => new DecalDefinition() ).ToArray();
		for ( var i = 0; i < definitions.Length; i++ )
			Assert.AreSame( definitions[i], Decal.SelectDefinition( definitions, (i + 0.5f) / definitions.Length ) );
	}

	[TestMethod]
	public void NullDefinitionsRetainExistingEmptySelectionBehavior()
	{
		var definition = new DecalDefinition();
		Assert.IsNull( Decal.SelectDefinition( [null, definition], 0 ) );
		Assert.AreSame( definition, Decal.SelectDefinition( [null, definition], 0.75f ) );
	}

	[TestMethod]
	public void InvalidWeightsAndRandomValuesAreRejected()
	{
		foreach ( var weight in new[] { -1f, float.NaN, float.PositiveInfinity, 0 } )
			Assert.ThrowsException<InvalidOperationException>( () => Decal.SelectDefinition( [new DecalDefinition { SelectionWeight = weight }], 0 ) );
		Assert.ThrowsException<ArgumentOutOfRangeException>( () => Decal.SelectDefinition( [new DecalDefinition()], float.NaN ) );
	}

	[TestMethod]
	public void SceneObjectPublishesBlendModeWithoutChangingNativeLayout()
	{
		var scene = new Scene();
		using var scope = scene.Push();
		var decal = new DecalSceneObject( scene.SceneWorld );
		Assert.AreEqual( DecalBlendMode.Alpha, decal.BlendMode );
		decal.BlendMode = DecalBlendMode.Multiply;
		Assert.AreEqual( DecalBlendMode.Multiply, decal.BlendMode );
		decal.BlendMode = DecalBlendMode.Alpha;
		Assert.AreEqual( DecalBlendMode.Alpha, decal.BlendMode );
		decal.Delete();
		scene.Destroy();
	}

	[TestMethod]
	public void ManagedRendererMirrorsBlendModeChanges()
	{
		var scene = new Scene();
		using var scope = scene.Push();
		try
		{
			var source = new DecalSceneObject( scene.SceneWorld );
			var decal = new Sandbox.SceneRenderer.DecalObject( Transform.Zero );
			var mirror = typeof( Sandbox.SceneRenderer.DecalObject ).Assembly.GetType( "Sandbox.SceneRenderer.Bridge.SceneMirror", true );
			var apply = mirror.GetMethod( "ApplyDecal", BindingFlags.Static | BindingFlags.NonPublic );
			foreach ( var mode in new[] { DecalBlendMode.Multiply, DecalBlendMode.Additive, DecalBlendMode.Alpha } )
			{
				source.BlendMode = mode;
				apply.Invoke( null, [source, decal, SceneObjectChange.Settings] );
				Assert.AreEqual( mode, decal.BlendMode );
			}
			source.Delete();
		}
		finally
		{
			scene.Destroy();
		}
	}
}
