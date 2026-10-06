// A captured screen on an arbitrary planar quad.
//
// The mesh carries (u*q, v*q, q) in TEXCOORD0 (see QuadMath.CornerUvq): the projective map from the quad to the picture. The division by q is done per
// pixel, so a quad that is not a parallelogram (a rectangle seen in perspective) shows the picture without affine shear. _Crop selects a rectangle of the
// source (x, y, width, height; normalised, origin top-left, y down) so one captured source can feed several panels. The texture's row 0 is the top of the
// captured image (frames are uploaded top row first), so the picture's y maps straight onto v.
Shader "XrSpatial/Panel"
{
    Properties
    {
        _MainTex ("Source", 2D) = "black" {}
        _Crop ("Crop (x y w h, top-left origin)", Vector) = (0, 0, 1, 1)
        _Opacity ("Opacity", Range(0, 1)) = 1
        _Brightness ("Brightness", Range(0.2, 2)) = 1
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        [Toggle] _ShowEdge ("Edge highlight", Float) = 0
        _EdgeColor ("Edge colour", Color) = (0.2, 0.8, 1, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite On

        Pass
        {
            Name "Panel"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Crop;
                half _Opacity;
                half _Brightness;
                half4 _Tint;
                half _ShowEdge;
                half4 _EdgeColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 uvq : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 uvq : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uvq = v.uvq;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 p = i.uvq.xy / i.uvq.z;                                  // picture coordinates: u right, v up
                float2 src = float2(_Crop.x + p.x * _Crop.z, _Crop.y + (1.0 - p.y) * _Crop.w);
                half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, src);
                half3 rgb = c.rgb * _Brightness * _Tint.rgb;
                if (_ShowEdge > 0.5)
                {
                    float2 e = min(p, 1.0 - p);
                    float w = max(fwidth(p.x), fwidth(p.y)) * 2.5;
                    float edge = 1.0 - smoothstep(0.0, w, min(e.x, e.y));
                    rgb = lerp(rgb, _EdgeColor.rgb, edge * _EdgeColor.a);
                }
                return half4(rgb, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
