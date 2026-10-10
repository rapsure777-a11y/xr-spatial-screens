// The PC mouse cursor drawn on a screen: a small flat sprite on the screen's own plane. The sprite texture holds three masks: R the cursor's fill, G its dark outline, B a wider halo
// (used only by High Visibility). Drawn over the screen (alpha blended); _ZTest is LessEqual normally and Always while the screen is shown in 2.5D, so the cursor stays visible even
// where displaced picture geometry comes out in front of the flat plane: it marks where Windows really has the mouse. Single-pass-instanced safe.
Shader "XrSpatial/Cursor"
{
    Properties
    {
        _MainTex ("Cursor masks", 2D) = "black" {}
        _Cell ("Atlas cell (x y w h)", Vector) = (0, 0, 0.25, 0.25)
        _High ("High visibility", Float) = 0
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+10" "RenderPipeline" = "UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest [_ZTest]
        Cull Off

        Pass
        {
            Name "Cursor"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _Cell;
                half _High;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = _Cell.xy + v.uv * _Cell.zw;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                half3 m = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).rgb;
                half fill = m.r, outline = max(m.g, m.r), halo = m.b;
                half3 col = lerp(half3(0.02, 0.02, 0.03), half3(1, 1, 1), saturate(fill));
                half a = saturate(outline);
                if (_High > 0.5h)
                {
                    // a soft warm halo outside the dark outline keeps the cursor findable against bright and dark pictures alike
                    col = lerp(half3(1.0, 0.82, 0.15), col, saturate(a * 1.5h));
                    a = max(a, halo * 0.85h);
                }
                return half4(col, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
