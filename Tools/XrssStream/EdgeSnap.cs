using System;

/// <summary>
/// Depth Lab option: edge-aware smoothing. A joint bilateral filter that averages each depth value only with neighbours whose SCREEN colour is similar, so depth
/// stops bleeding across the outlines of the picture (halos and wobble around objects and window edges). Guided by the frame's brightness at the depth resolution.
/// Strength 0 returns the input untouched (the original behaviour); the filter never runs unless the headset asks for it.
/// </summary>
public static class EdgeSnap
{
    /// <summary>Filtered copy of <paramref name="depth"/> (w x h), guided by <paramref name="guide"/> (brightness 0..1, same size). Strength 0..1.</summary>
    public static float[] Apply(float[] depth, float[] guide, int w, int h, float strength)
    {
        if (strength <= 0.001f || guide == null || guide.Length < w * h || depth.Length < w * h) return depth;
        strength = Math.Min(1f, strength);
        int radius = 2 + (int)Math.Round(strength * 3f);                              // 2 to 5 pixels
        float sigmaS = radius / 1.5f, sigmaR = 0.20f + (0.06f - 0.20f) * strength;    // stricter about colour differences as strength rises
        float mix = Math.Min(1f, strength * 2f);                                      // low strengths blend with the unfiltered depth
        int passes = strength > 0.6f ? 2 : 1;
        var spatial = new float[(2 * radius + 1) * (2 * radius + 1)];
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
                spatial[(dy + radius) * (2 * radius + 1) + dx + radius] = (float)Math.Exp(-(dx * dx + dy * dy) / (2f * sigmaS * sigmaS));
        float inv2r = 1f / (2f * sigmaR * sigmaR);
        var rangeW = new float[256];                                                  // colour-difference weight per 8-bit brightness step
        for (int k = 0; k < 256; k++) { float dg = k / 255f; rangeW[k] = (float)Math.Exp(-dg * dg * inv2r); }
        var src = depth;
        for (int pass = 0; pass < passes; pass++)
        {
            var dst = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x; float g0 = guide[i], sum = 0f, wsum = 0f;
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        int yy = y + dy; if (yy < 0 || yy >= h) continue;
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int xx = x + dx; if (xx < 0 || xx >= w) continue;
                            int j = yy * w + xx; float dg = guide[j] - g0; if (dg < 0f) dg = -dg;
                            float wt = spatial[(dy + radius) * (2 * radius + 1) + dx + radius] * rangeW[Math.Min(255, (int)(dg * 255f + 0.5f))];
                            sum += src[j] * wt; wsum += wt;
                        }
                    }
                    dst[i] = wsum > 1e-6f ? sum / wsum : src[i];
                }
            src = dst;
        }
        var res = new float[w * h];
        for (int i = 0; i < res.Length; i++) res[i] = depth[i] + (src[i] - depth[i]) * mix;
        return res;
    }
}
