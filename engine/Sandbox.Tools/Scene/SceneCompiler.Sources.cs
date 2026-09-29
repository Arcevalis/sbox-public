using System;
using System.Collections.Generic;
using Sandbox;

namespace Editor;

partial class SceneCompiler
{
	readonly record struct Source( Component Component, string Label, string SkipReason, bool NeedsConversion = false )
	{
		public bool NeedsCompilation => SkipReason is null || NeedsConversion;
	}

	internal static bool HasAnythingToCompile( Scene scene ) => DiscoverSources( scene ).Any( x => x.NeedsCompilation );

	static IEnumerable<Source> DiscoverSources( Scene scene )
	{
		foreach ( var mesh in scene.Components.GetAll<MeshComponent>( FindMode.EverythingInSelfAndDescendants ) )
			yield return new Source( mesh, "meshes", SkipReason( mesh ), NeedsConversion: Runtime( mesh.GameObject ) );

		foreach ( var renderer in scene.GetAllComponents<ModelRenderer>() )
			yield return new Source( renderer, "props", SkipReason( renderer ) );
	}

	/// <summary>
	/// Why a mesh can't be welded into the world, or null when it can. Anything that triggers or
	/// collides with its own physics settings has to keep doing its own thing, and so does anything
	/// drawing with shadows turned off - an aggregate draws what it holds, with shadows.
	/// </summary>
	static string SkipReason( MeshComponent mesh )
	{
		if ( !mesh.Active )
			return "not active";

		if ( mesh.Mesh is null )
			return "no mesh";

		if ( !Runtime( mesh.GameObject ) )
			return "object doesn't load in game";

		if ( !mesh.GameObject.IsStatic )
			return "object isn't static";

		if ( mesh.Rigidbody.IsValid() )
			return "driven by a rigidbody";

		if ( Visible( mesh ) && mesh.RenderType != ModelRenderer.ShadowRenderType.On )
			return "shadows aren't on";

		if ( mesh.Collision == MeshComponent.CollisionType.None )
			return null;

		if ( mesh.IsTrigger )
			return "is a trigger";

		if ( mesh.ColliderFlags != default )
			return "has collider flags";

		if ( !mesh.SurfaceVelocity.IsNearZeroLength )
			return "has surface velocity";

		if ( mesh.Friction.HasValue || mesh.Elasticity.HasValue || mesh.RollingResistance.HasValue )
			return "has physics overrides";

		return null;
	}

	/// <summary>
	/// Whether an object reaches the compiled scene at all. Compilation lifts geometry out of the
	/// hierarchy it sat in and parents it under the world root, so an object the scene loader would
	/// have dropped - or one sat under any object it would have dropped - has to be left alone.
	/// </summary>
	static bool Runtime( GameObject go )
	{
		const GameObjectFlags excluded = GameObjectFlags.NotSaved | GameObjectFlags.EditorOnly;

		for ( var current = go; current.IsValid(); current = current.Parent )
		{
			if ( (current.Flags & excluded) != 0 )
				return false;
		}

		return true;
	}

	static bool PropOrigin( Material material ) => material.IsValid() && material.Flags.GetBool( "VertexNeedsPropOrigin" );

	/// <summary>
	/// Whether a mesh draws in game, and so has render geometry worth compiling. One that doesn't is
	/// compiled for its collision alone, which is how you'd build something like a player clip.
	/// </summary>
	static bool Visible( MeshComponent mesh ) => !mesh.HideInGame;

	/// <summary>
	/// Why a model renderer's geometry can't be welded into the world, or null when it can. This
	/// mirrors the map compiler's prop_static test - anything that picks its meshes or materials at
	/// runtime, or draws somewhere other than the world, has to keep drawing itself.
	/// </summary>
	static string SkipReason( ModelRenderer renderer )
	{
		if ( renderer is SkinnedModelRenderer )
			return "animated";

		if ( !renderer.Active )
			return "not active";

		if ( !Runtime( renderer.GameObject ) )
			return "object doesn't load in game";

		if ( !renderer.GameObject.IsStatic )
			return "object isn't static";

		if ( renderer.Components.GetAll<ModelDeformer>( FindMode.EverythingInSelfAndDescendants )
			.Any( x => x.Target == renderer && Runtime( x.GameObject ) ) )
			return "has model deformers";

		if ( renderer.RenderType != ModelRenderer.ShadowRenderType.On )
			return "shadows aren't on";

		var options = renderer.RenderOptions;

		if ( !options.Game || options.Overlay || options.Bloom || options.AfterUI )
			return "doesn't draw in the world";

		if ( !renderer.Model.IsValid() )
			return "no model";

		if ( renderer.Model.IsProcedural )
			return "model is procedural";

		if ( !renderer.Model.HasRenderMeshes() )
			return "model has no render meshes";

		var materials = renderer.Materials;

		for ( int i = 0; i < materials.Count; i++ )
		{
			if ( materials.HasOverride( i ) )
				return "material overrides";
		}

		if ( renderer.GameObject.Components.Get<ModelCollider>( FindMode.EverythingInSelf ) is { } collider && SkipReason( collider ) is { } reason )
			return $"collision {reason}";

		return null;
	}

	/// <summary>
	/// Why a collider's shapes can't be welded into the world, or null when they can. A renderer we
	/// compile takes its collider with it, so anything the collider does for itself keeps them both.
	/// </summary>
	static string SkipReason( ModelCollider collider )
	{
		if ( !collider.Active )
			return "isn't active";

		if ( !collider.Model.IsValid() || collider.Model.Physics is null )
			return "has no shapes";

		if ( collider.Rigidbody.IsValid() )
			return "is driven by a rigidbody";

		if ( !collider.Static )
			return "isn't static";

		if ( collider.IsTrigger )
			return "is a trigger";

		if ( collider.ColliderFlags != default )
			return "has collider flags";

		if ( !collider.SurfaceVelocity.IsNearZeroLength )
			return "has surface velocity";

		if ( collider.Friction.HasValue || collider.Elasticity.HasValue || collider.RollingResistance.HasValue )
			return "has physics overrides";

		return null;
	}

	/// <summary>
	/// The collider whose shapes get welded in alongside a compiled renderer. Null means there's
	/// nothing to weld - <see cref="SkipReason(ModelRenderer)"/> has already turned away anything
	/// with collision we can't take.
	/// </summary>
	static ModelCollider Collider( GameObject go )
	{
		var collider = go.Components.Get<ModelCollider>( FindMode.EverythingInSelf );

		return collider is not null && SkipReason( collider ) is null ? collider : null;
	}

	/// <summary>
	/// The prop driving this renderer, if there is one. A prop builds its own renderer whenever it
	/// loads, so compiling one without taking the prop with it just draws the geometry twice.
	/// </summary>
	static Prop Owner( ModelRenderer renderer ) => renderer.GameObject.Components.Get<Prop>( FindMode.EverythingInSelf );

	/// <summary>
	/// An object's effective tags, ancestors included, as one comparable key. Compiled geometry moves
	/// out of the hierarchy it inherited these from, so anything sharing an aggregate or a collision
	/// shape has to share its tags.
	/// </summary>
	static string TagKey( GameObject go )
	{
		var tags = new List<string>();

		foreach ( var tag in go.Tags.TryGetAll() )
		{
			tags.Add( tag );
		}

		if ( tags.Count == 0 )
			return string.Empty;

		tags.Sort( StringComparer.Ordinal );

		return string.Join( ',', tags );
	}
}
