using System.Runtime.InteropServices;

namespace Sandbox;

/// <summary>
/// Filters direct light passing through a textured rectangle in the local Y/Z plane.
/// This changes lighting on receivers; add a separate renderer to display the glass itself.
/// </summary>
[Expose, Title( "Light Modulator" ), Category( "Light" ), Icon( "filter" )]
public sealed class LightModulator : Component, Component.ExecuteInEditor
{
	/// <summary>
	/// Limits how the processed texture can change each colour channel of direct light.
	/// </summary>
	public enum ModulationBlendMode
	{
		/// <summary>
		/// Multiply light by the processed texture, allowing both attenuation and amplification.
		/// </summary>
		Multiply,

		/// <summary>
		/// Keep multipliers at or above one. Raise brightness above one to produce highlights.
		/// </summary>
		Lighten,

		/// <summary>
		/// Keep multipliers at or below one, allowing shadows without boosting light.
		/// </summary>
		Darken
	}

	/// <summary>
	/// Multiply applies the texture directly; Lighten only boosts light and Darken only reduces it.
	/// Applied per RGB channel after contrast, brightness and tint, before strength and alpha.
	/// </summary>
	[Property, Title( "Mode" ), Order( -100 ), EnumButtonGroup]
	public ModulationBlendMode BlendMode
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;
			InvalidateData();
		}
	}

	/// <summary>
	/// Blend between original light at zero and full texture modulation at one.
	/// Multiplies texture alpha and edge feathering rather than dimming the light itself.
	/// </summary>
	[Property, Range( 0, 1 )]
	public float Strength
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;
			InvalidateData();
		}
	} = 1;

	/// <summary>
	/// Fade inward from each rectangle edge, as a fraction of its width or height.
	/// Zero gives a hard boundary; 0.1 fades across the outer ten percent on each axis.
	/// Uses the physical rectangle, independent of texture tiling or scrolling.
	/// </summary>
	[Property, Range( 0, 0.5f )]
	public float EdgeFeather
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;
			InvalidateData();
		}
	}

	/// <summary>
	/// Fade in modulation over this many world units from the rectangle's plane.
	/// Uses perpendicular distance for both surfaces and fog, independent of light angle or object scale.
	/// Zero disables distance feathering; full strength is reached at this distance and beyond.
	/// </summary>
	[Property, Range( 0, 1024 )]
	public float DistanceFeather
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;
			InvalidateData();
		}
	}

	/// <summary>
	/// RGB transmission through the rectangle. Alpha blends between unfiltered and filtered light.
	/// Sampling uses the texture's own colour space.
	/// </summary>
	[Property]
	public Texture Texture
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;
			InvalidateData();
		}
	}

	/// <summary>
	/// Texture repeats across the rectangle on each axis. One fits once; negative values mirror it.
	/// Scaling and rotation are centered on the rectangle.
	/// </summary>
	[Property, Group( "Texture Mapping" )]
	public Vector2 Tiling
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;
			InvalidateData();
		}
	} = Vector2.One;

	/// <summary>
	/// Texture-coordinate offset in repeats, applied after tiling and rotation.
	/// </summary>
	[Property, Group( "Texture Mapping" )]
	public Vector2 Offset
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;
			InvalidateData();
		}
	}

	/// <summary>
	/// Texture movement in repeats per second along U and V. Zero leaves the projection still.
	/// Runs in the shader without rebuilding textures or light candidate lists each frame.
	/// </summary>
	[Property, Group( "Texture Mapping" )]
	public Vector2 ScrollSpeed
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;
			InvalidateData();
		}
	}

	/// <summary>
	/// Clockwise rotation of the projected texture in degrees, around its center.
	/// </summary>
	[Property, Group( "Texture Mapping" )]
	public float TextureRotation
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;
			InvalidateData();
		}
	}

	/// <summary>
	/// RGB contrast around linear 0.5, before brightness and tint. One preserves the texture.
	/// Alpha is unaffected; negative RGB results are clamped to zero.
	/// </summary>
	[Property, Group( "Texture Adjustments" ), Range( 0, 8 )]
	public float Contrast
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;
			InvalidateData();
		}
	} = 1;

	/// <summary>
	/// Linear brightness multiplier after contrast. One preserves brightness; values above one boost light.
	/// Alpha is unaffected. This is an artistic light multiplier, not physical light focusing.
	/// </summary>
	[Property, Group( "Texture Adjustments" ), Range( 0, 16 )]
	public float Brightness
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;
			InvalidateData();
		}
	} = 1;

	/// <summary>
	/// Rectangle width along local Y and height along local Z, before object scaling.
	/// </summary>
	[Property]
	public Vector2 Size
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;
			InvalidateData();
		}
	} = new( 64, 96 );

	/// <summary>
	/// Linear RGB multiplier, including values above one for artistic caustics.
	/// </summary>
	[Property, ColorUsage( false, true )]
	public Color Tint
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;
			InvalidateData();
		}
	} = Color.White;

	/// <summary>
	/// Applies the filter in both directions. Otherwise light travels along local positive X only.
	/// </summary>
	[Property]
	public bool TwoSided
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;
			InvalidateData();
		}
	} = true;

	/// <summary>
	/// Explicit texture mip used by divergent light queries; larger values soften the projection.
	/// </summary>
	[Property, Range( 0, 12 )]
	public float MipLevel
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;
			InvalidateData();
		}
	}

	/// <summary>
	/// Softens transmission as light travels from the glass to the receiving surface or fog sample.
	/// Adds this many mip levels at 128 world units, twice as many at 384 units. Zero disables distance softening.
	/// </summary>
	[Property, Range( 0, 4 )]
	public float DistanceMipBias
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;
			InvalidateData();
		}
	}

	/// <summary>
	/// Build orthogonal world-space axes and reciprocal extents for intersection and UV mapping.
	/// </summary>
	internal bool BuildData( out LightModulatorData data )
	{
		var texture = Texture;
		texture?.MarkUsed();
		var textureIndex = texture?.Index ?? 0;
		if ( dataDirty || textureIndex != cachedTextureIndex )
		{
			cachedDataValid = PackData( out cachedData );
			cachedTextureIndex = textureIndex;
			dataDirty = false;
			DataBuildCount++;
		}

		data = cachedData;
		return cachedDataValid;
	}

	bool dataDirty = true;
	bool cachedDataValid;
	int cachedTextureIndex;
	LightModulatorData cachedData;

	/// <summary>
	/// Number of packed rectangle snapshots, for diagnosing redundant render preparation.
	/// </summary>
	internal int DataBuildCount { get; private set; }

	void InvalidateData()
	{
		dataDirty = true;
	}

	/// <summary>
	/// Track transform changes, including movement inherited from a parent.
	/// </summary>
	protected override void OnEnabled()
	{
		Transform.OnTransformChanged += InvalidateData;
		InvalidateData();
	}

	/// <summary>
	/// Stop tracking transforms while inactive and discard the cached snapshot.
	/// </summary>
	protected override void OnDisabled()
	{
		Transform.OnTransformChanged -= InvalidateData;
		InvalidateData();
	}

	bool PackData( out LightModulatorData data )
	{
		data = default;
		if ( !Active )
			return false;

		var texture = Texture;
		if ( texture is null )
			return false;

		if ( Size.x <= 0 || Size.y <= 0 )
			return false;

		var width = MathF.Abs( Size.x * WorldScale.y );
		var height = MathF.Abs( Size.y * WorldScale.z );
		if ( !float.IsFinite( width ) || !float.IsFinite( height ) || width < 0.001f || height < 0.001f )
			return false;

		data.Origin = WorldPosition;
		data.TextureIndex = texture.Index;
		data.U = WorldRotation.Left * MathF.Sign( WorldScale.y ) / width;
		data.V = WorldRotation.Up * MathF.Sign( WorldScale.z ) / height;
		data.Normal = WorldRotation.Forward;
		data.TwoSided = TwoSided ? 1 : 0;
		data.HalfWidth = width * 0.5f;
		data.HalfHeight = height * 0.5f;
		data.Tint = new Vector3( Tint.r, Tint.g, Tint.b );
		data.MipLevel = FiniteClamp( MipLevel, 0, 12 );
		data.DistanceMipBias = FiniteClamp( DistanceMipBias, 0, 4 );
		data.Tiling = new Vector2( FiniteClamp( Tiling.x, -1024, 1024, 1 ), FiniteClamp( Tiling.y, -1024, 1024, 1 ) );
		data.Offset = new Vector2( FiniteClamp( Offset.x, -1000000, 1000000 ), FiniteClamp( Offset.y, -1000000, 1000000 ) );
		data.ScrollSpeed = new Vector2( FiniteClamp( ScrollSpeed.x, -1024, 1024 ), FiniteClamp( ScrollSpeed.y, -1024, 1024 ) );
		var angle = float.IsFinite( TextureRotation ) ? TextureRotation % 360 * (MathF.PI / 180) : 0;
		data.Rotation = new Vector2( MathF.Cos( angle ), MathF.Sin( angle ) );
		data.Contrast = FiniteClamp( Contrast, 0, 8, 1 );
		data.Brightness = FiniteClamp( Brightness, 0, 16, 1 );
		data.BlendMode = Enum.IsDefined( BlendMode ) ? (int)BlendMode : 0;
		data.Strength = FiniteClamp( Strength, 0, 1, 1 );
		data.EdgeFeather = FiniteClamp( EdgeFeather, 0, 0.5f );
		data.DistanceFeather = FiniteClamp( DistanceFeather, 0, 1000000 );
		return data.TextureIndex > 0;
	}

	/// <summary>
	/// Keep shader controls finite even when assigned directly by scripts.
	/// </summary>
	static float FiniteClamp( float value, float minimum, float maximum, float fallback = 0 )
	{
		return float.IsFinite( value ) ? Math.Clamp( value, minimum, maximum ) : fallback;
	}

	/// <summary>
	/// Shows the physical rectangle rather than a view-dependent projection volume.
	/// </summary>
	protected override void DrawGizmos()
	{
		Gizmo.Draw.Color = Color.Cyan;
		Gizmo.Draw.LineBBox( new BBox( new Vector3( -0.1f, -Size.x * 0.5f, -Size.y * 0.5f ), new Vector3( 0.1f, Size.x * 0.5f, Size.y * 0.5f ) ) );
	}
}

/// <summary>
/// Scalar-layout GPU rectangle. Keep synchronized with common/classes/LightModulators.hlsl.
/// </summary>
[StructLayout( LayoutKind.Sequential, Pack = 4 )]
internal struct LightModulatorData
{
	internal Vector3 Origin;
	internal int TextureIndex;
	internal Vector3 U;
	internal float HalfWidth;
	internal Vector3 V;
	internal float HalfHeight;
	internal Vector3 Normal;
	internal int TwoSided;
	internal Vector3 Tint;
	internal float MipLevel;
	internal float DistanceFeather;
	internal float DistanceMipBias;
	internal Vector2 Tiling;
	internal Vector2 Offset;
	internal Vector2 ScrollSpeed;
	internal Vector2 Rotation;
	internal float Contrast;
	internal float Brightness;
	internal int BlendMode;
	internal float Strength;
	internal float EdgeFeather;

	/// <summary>
	/// Conservative point/spot broad phase: closest point on the rectangle versus light range.
	/// A spot's enclosing range sphere deliberately permits false positives, never false negatives.
	/// </summary>
	internal readonly bool IntersectsLight( Vector3 position, float radius )
	{
		var delta = position - Origin;
		var u = U * (2 * HalfWidth);
		var v = V * (2 * HalfHeight);
		var closest = Origin
			+ u * Math.Clamp( Vector3.Dot( delta, u ), -HalfWidth, HalfWidth )
			+ v * Math.Clamp( Vector3.Dot( delta, v ), -HalfHeight, HalfHeight );

		return (closest - position).LengthSquared <= radius * radius;
	}
}
