#ifndef LIGHT_MODULATORS_HLSL
#define LIGHT_MODULATORS_HLSL

#include "common/Bindless.hlsl"
#include "common_samplers.fxc"
#include "common.fxc"

// Scalar-layout mirror of LightModulatorData. Rectangles belong to the scene, not the camera.
struct LightModulatorRectangle
{
    float3 Origin;
    int TextureIndex;
    float3 U;
    float HalfWidth;
    float3 V;
    float HalfHeight;
    float3 Normal;
    int TwoSided;
    float3 Tint;
    float MipLevel;
    float DistanceFeather;
    float DistanceMipBias;
    float2 Tiling;
    float2 Offset;
    float2 ScrollSpeed;
    float2 Rotation; // cosine and sine, computed only when packing component settings
    float Contrast;
    float Brightness;
    int BlendMode;
    float Strength;
    float EdgeFeather;
};

StructuredBuffer<LightModulatorRectangle> LightModulatorRectangles < Attribute( "LightModulatorRectangles" ); >;
StructuredBuffer<uint> LightModulatorCandidates < Attribute( "LightModulatorCandidates" ); >;
uint LightModulatorRectangleCount < Attribute( "LightModulatorRectangleCount" ); Default( 0 ); >;
uint LightModulatorCandidateCount < Attribute( "LightModulatorCandidateCount" ); Default( 0 ); >;
bool LightModulatorsDisabled < Attribute( "LightModulatorsDisabled" ); Default( 0 ); >;

// Scene-wide light transmission queries shared by surfaces and volumetric fog.
struct LightModulators
{
    // Explicit LOD keeps samples valid inside divergent light and rectangle loops.
    static float4 SampleTexture( LightModulatorRectangle rect, float2 uv, float mip )
    {
        // The native sRGB view decodes sRGB formats and aliases the linear view for linear/HDR formats.
        return Bindless::GetTexture2DSrgb( rect.TextureIndex ).SampleLevel( g_sTrilinearWrap, uv, mip );
    }

    // Intersect one rectangle with the receiver-to-source segment and sample its transmission.
    static float3 Sample( uint index, float3 position, float3 toLight, float distanceToLight )
    {
        LightModulatorRectangle rect = LightModulatorRectangles[index];
        if ( rect.Strength <= 0.0f )
            return 1.0f;

        float denominator = dot( rect.Normal, toLight );

        // Trace toward the source. A one-sided filter accepts light traveling along +normal.
        if ( abs( denominator ) < 0.00001f || (rect.TwoSided == 0 && denominator >= 0.0f) )
            return 1.0f;

        float distance = dot( rect.Origin - position, rect.Normal ) / denominator;
        if ( distance <= 0.01f || distance >= distanceToLight )
            return 1.0f;

        float3 relative = position + toLight * distance - rect.Origin;
        float2 uv = float2( dot( relative, rect.U ), dot( relative, rect.V ) ) * float2( 1.0f, -1.0f ) + 0.5f;

        // Clip the physical aperture before transforming UVs: tiling never enlarges the rectangle.
        if ( any( uv < 0.0f ) || any( uv > 1.0f ) )
            return 1.0f;

        float edgeDistance = min( min( uv.x, 1.0f - uv.x ), min( uv.y, 1.0f - uv.y ) );
        float coverage = rect.Strength;
        if ( rect.EdgeFeather > 0.0f )
            coverage *= smoothstep( 0.0f, rect.EdgeFeather, edgeDistance );

        // World-space distance normal to the plane keeps the fade width independent of the light angle.
        if ( rect.DistanceFeather > 0.0f )
            coverage *= smoothstep( 0.0f, rect.DistanceFeather, abs( dot( position - rect.Origin, rect.Normal ) ) );

        float2 centered = uv - 0.5f;
        uv = float2( rect.Rotation.x * centered.x + rect.Rotation.y * centered.y,
            -rect.Rotation.y * centered.x + rect.Rotation.x * centered.y );
        uv = uv * rect.Tiling + 0.5f + rect.Offset - frac( g_flTime * rect.ScrollSpeed );

        // Explicit LOD is safe inside divergent light loops. Softening uses pane-to-receiver distance.
        float mip = min( 12.0f, rect.MipLevel + rect.DistanceMipBias * log2( 1.0f + distance / 128.0f ) );
        float4 sampleValue = SampleTexture( rect, uv, mip );

        float3 transmission = max( (sampleValue.rgb - 0.5f) * rect.Contrast + 0.5f, 0.0f );
        transmission *= rect.Brightness * rect.Tint;
        transmission = max( transmission, 0.0f );
        if ( rect.BlendMode == 1 )
            transmission = max( transmission, 1.0f );
        else if ( rect.BlendMode == 2 )
            transmission = min( transmission, 1.0f );

        return lerp( 1.0f, transmission, saturate( sampleValue.a ) * coverage );
    }

    // Evaluate only the candidates belonging to this local light.
    static float3 Evaluate( float3 position, float3 toLight, float distanceToLight, uint first, uint count )
    {
        if ( LightModulatorsDisabled || LightModulatorRectangleCount == 0 || first >= LightModulatorCandidateCount )
            return 1.0f;

        float3 transmission = 1.0f;
        count = min( count, LightModulatorCandidateCount - first );
        for ( uint i = 0; i < count; i++ )
        {
            uint index = LightModulatorCandidates[first + i];
            if ( index < LightModulatorRectangleCount )
                transmission *= Sample( index, position, toLight, distanceToLight );
        }

        return transmission;
    }

    // A directional light has unlimited reach, so every scene rectangle is a candidate.
    static float3 EvaluateSun( float3 position, float3 toLight )
    {
        if ( LightModulatorsDisabled )
            return 1.0f;

        float3 transmission = 1.0f;
        for ( uint i = 0; i < LightModulatorRectangleCount; i++ )
        {
            transmission *= Sample( i, position, toLight, 1.0e30f );
        }

        return transmission;
    }
};

#endif
