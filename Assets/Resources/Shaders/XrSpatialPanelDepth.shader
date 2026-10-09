// Depth Lab: the captured screen on a subdivided grid whose vertices are pushed along the panel's front normal by a depth map (a shallow 2.5D diorama).
//
// Same picture code as XrSpatial/Panel (projective map, _Crop, tint, edge highlight); only the vertex stage differs. The colour is sampled at the UNdisplaced
// position, so the picture stays glued to the surface and the displacement shows up as true per-eye parallax. Depth is 0 = far, 1 = near (bright = near, the
// convention of depth-estimation models). _DepthParams: x = relief in metres (how far depth 1 vs depth 0 differ), y = focus (the depth value that stays on the
// panel plane), z = maximum absolute displacement in metres, w unused. _PanelNormal = the panel's front normal (towards the viewer), in object space.
Shader "XrSpatial/PanelDepth"
{
    Properties
    {
        _MainTex ("Source", 2D) = "black" {}
        _DepthTex ("Depth (0 far, 1 near)", 2D) = "gray" {}
        _Crop ("Crop (x y w h, top-left origin)", Vector) = (0, 0, 1, 1)
        _Opacity ("Opacity", Range(0, 1)) = 1
        _Brightness ("Brightness", Range(0.2, 2)) = 1
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        [Toggle] _ShowEdge ("Edge highlight", Float) = 0
        _EdgeColor ("Edge colour", Color) = (0.2, 0.8, 1, 1)
        _DepthParams ("Relief, focus, max, -", Vector) = (0, 0.5, 0.35, 0)
        _PanelNormal ("Panel front normal", Vector) = (0, 0, -1, 0)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite On

        Pass
        {
            Name "PanelDepth"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            TEXTURE2D(_DepthTex); SAMPLER(sampler_DepthTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _DepthTex_ST;
                float4 _Crop;
                half _Opacity;
                half _Brightness;
                half4 _Tint;
                half _ShowEdge;
                half4 _EdgeColor;
                float4 _DepthParams;
                float4 _PanelNormal;
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
                float2 p = v.uvq.xy / v.uvq.z;
                float2 src = float2(_Crop.x + p.x * _Crop.z, _Crop.y + (1.0 - p.y) * _Crop.w);
                // a slightly blurred read (mip 1) keeps one noisy depth pixel from becoming a spike
                float d = SAMPLE_TEXTURE2D_LOD(_DepthTex, sampler_DepthTex, src, 1.0).r;
                d = saturate(lerp(d, d * d * (3.0 - 2.0 * d), _DepthParams.w));   // Pop: an S-curve that pulls mid depths apart, so objects separate from the background
                float disp = clamp((d - _DepthParams.y) * _DepthParams.x, -_DepthParams.z, _DepthParams.z);
                float3 pos = v.positionOS.xyz + _PanelNormal.xyz * disp;
                o.positionCS = TransformObjectToHClip(pos);
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
