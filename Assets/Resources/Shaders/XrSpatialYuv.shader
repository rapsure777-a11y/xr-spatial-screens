// Turns a decoded video picture (three 8-bit planes: Y full size, U and V half size; BT.709, limited range, as the PC encoder tags it) into the RGB source texture the
// panels sample. Used with Graphics.Blit into the source's render texture, so panels cannot tell video from JPEG.
Shader "XrSpatial/Yuv"
{
    Properties
    {
        _TexY ("Y", 2D) = "black" {}
        _TexU ("U", 2D) = "gray" {}
        _TexV ("V", 2D) = "gray" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _TexY, _TexU, _TexV;

            fixed4 frag(v2f_img i) : SV_Target
            {
                float y = (tex2D(_TexY, i.uv).r - 16.0 / 255.0) * (255.0 / 219.0);
                float cb = (tex2D(_TexU, i.uv).r - 128.0 / 255.0) * (255.0 / 224.0);
                float cr = (tex2D(_TexV, i.uv).r - 128.0 / 255.0) * (255.0 / 224.0);
                float3 rgb = saturate(float3(y + 1.5748 * cr, y - 0.18732 * cb - 0.46812 * cr, y + 1.8556 * cb));
            #if !defined(UNITY_COLORSPACE_GAMMA)
                rgb = GammaToLinearSpace(rgb);                       // the source texture is an sRGB render texture: store what the decoder produced
            #endif
                return fixed4(rgb, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
