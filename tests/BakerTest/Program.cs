using System;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using StickerStudio;

namespace StickerStudio
{
    // Minimal stand-in for the plugin's settings type (the real one lives in StickerStore.cs, which needs BepInEx).
    internal class SlotSettings
    {
        public float Zoom { get; set; } = 1f; public float Rotation { get; set; }
        public float OffsetX { get; set; } public float OffsetY { get; set; }
        public bool Flip { get; set; } public bool Fill { get; set; }
        public bool Border { get; set; } = true; public float BorderPx { get; set; } = 14f;
    }
}

static class Program
{
    // BakerTest <in.rgba> <w> <h> <out.rgba> <size> <tilt> [rot] [zoom] [fill 0/1] [flip 0/1] [offx] [offy] [border px]
    static void Main(string[] a)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var pic = new Picture(int.Parse(a[1]), int.Parse(a[2]), File.ReadAllBytes(a[0]));
        var s = new SlotSettings();
        if (a.Length > 6) s.Rotation = float.Parse(a[6]);
        if (a.Length > 7) s.Zoom = float.Parse(a[7]);
        if (a.Length > 8) s.Fill = a[8] == "1";
        if (a.Length > 9) s.Flip = a[9] == "1";
        if (a.Length > 10) s.OffsetX = float.Parse(a[10]);
        if (a.Length > 11) s.OffsetY = float.Parse(a[11]);
        if (a.Length > 12) { s.BorderPx = float.Parse(a[12]); s.Border = s.BorderPx > 0; }
        int size = int.Parse(a[4]);
        var sw = Stopwatch.StartNew();
        var outp = StickerBaker.Bake(pic, size, s, float.Parse(a[5]));
        var t1 = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        StickerBaker.Bake(pic, 160, s, float.Parse(a[5]));
        Console.WriteLine($"baked {size}px in {t1:F1} ms, live-preview 160px in {sw.Elapsed.TotalMilliseconds:F1} ms");
        File.WriteAllBytes(a[3], outp);
    }
}
