using System;
using Xunit;

public class EdgeSnapTests
{
    const int W = 48, H = 8;

    // depth: a soft ramp around x=20; guide: a hard picture edge at x=24 (the depth edge sits 4 pixels away from the picture's edge)
    static (float[] depth, float[] guide) Scene()
    {
        var d = new float[W * H]; var g = new float[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                d[y * W + x] = Math.Clamp((x - 18) / 6f, 0f, 1f);
                g[y * W + x] = x < 24 ? 0.1f : 0.9f;
            }
        return (d, g);
    }

    [Fact]
    public void ZeroStrengthReturnsTheInputUnchanged()
    {
        var (d, g) = Scene();
        Assert.Same(d, EdgeSnap.Apply(d, g, W, H, 0f));
    }

    [Fact]
    public void MissingGuideReturnsTheInputUnchanged()
    {
        var (d, _) = Scene();
        Assert.Same(d, EdgeSnap.Apply(d, null, W, H, 0.8f));
    }

    [Fact]
    public void DepthDoesNotBleedAcrossAPictureEdge()
    {
        var (d, g) = Scene();
        var f = EdgeSnap.Apply(d, g, W, H, 1f);
        int y = H / 2;
        float before = Math.Abs(d[y * W + 25] - d[y * W + 22]), after = Math.Abs(f[y * W + 25] - f[y * W + 22]);
        Assert.True(after > before + 0.1f, $"step across the picture edge should grow: {before} -> {after}");
        // each side moves toward its own plateau: left of the edge stays low, right of the edge stays high
        Assert.True(f[y * W + 22] <= d[y * W + 22] + 1e-4f);
        Assert.True(f[y * W + 25] >= d[y * W + 25] - 1e-4f);
    }

    [Fact]
    public void FlatAreasStayFlatAndValuesStayInRange()
    {
        var d = new float[W * H]; var g = new float[W * H];
        for (int i = 0; i < d.Length; i++) { d[i] = 0.4f; g[i] = (i * 7 % 13) / 13f; }
        var f = EdgeSnap.Apply(d, g, W, H, 0.7f);
        foreach (float v in f) Assert.InRange(v, 0.4f - 1e-4f, 0.4f + 1e-4f);
        var (d2, g2) = Scene();
        foreach (float v in EdgeSnap.Apply(d2, g2, W, H, 0.5f)) Assert.InRange(v, -1e-4f, 1f + 1e-4f);
    }

    [Fact]
    public void LowStrengthChangesLessThanHighStrength()
    {
        var (d, g) = Scene();
        float Diff(float s) { var f = EdgeSnap.Apply(d, g, W, H, s); float m = 0; for (int i = 0; i < f.Length; i++) m = Math.Max(m, Math.Abs(f[i] - d[i])); return m; }
        Assert.True(Diff(0.15f) < Diff(1f));
    }

    [Fact]
    public void StrongestSettingIsFastEnoughForLiveDepth()
    {
        const int w = 256, h = 144; var d = new float[w * h]; var g = new float[w * h];
        for (int i = 0; i < d.Length; i++) { d[i] = (i % w) / (float)w; g[i] = ((i * 31) % 97) / 97f; }
        EdgeSnap.Apply(d, g, w, h, 1f);                                                   // warm up
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int n = 0; n < 5; n++) EdgeSnap.Apply(d, g, w, h, 1f);
        double ms = sw.Elapsed.TotalMilliseconds / 5;
        Console.WriteLine("edge snap at 256x144, strength 1: " + ms.ToString("F1") + " ms");
        Assert.True(ms < 100, "took " + ms + " ms");
    }
}
