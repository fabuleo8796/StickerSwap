using System;
using System.Collections.Generic;

namespace StickerStudio;

/// A picture as plain RGBA bytes, rows bottom-up (the order Unity textures use).
internal sealed class Picture
{
    public readonly int Width, Height;
    public readonly byte[] Rgba;
    readonly Dictionary<int, Picture> smaller = new();
    (int x, int y, int w, int h)? bounds;

    public Picture(int width, int height, byte[] rgba)
    {
        Width = width; Height = height; Rgba = rgba;
    }

    /// The part of the picture that isn't transparent (the whole picture for photos).
    public (int x, int y, int w, int h) Bounds => bounds ??= FindBounds();

    (int, int, int, int) FindBounds()
    {
        int x0 = Width, y0 = Height, x1 = -1, y1 = -1;
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            if (Rgba[(y * Width + x) * 4 + 3] <= 8) continue;
            if (x < x0) x0 = x; if (x > x1) x1 = x;
            if (y < y0) y0 = y; if (y > y1) y1 = y;
        }
        return x1 < 0 ? (0, 0, Width, Height) : (x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    /// A box-filtered copy `factor` times smaller, so shrinking a big photo onto a small sticker doesn't sparkle.
    public Picture Shrunk(int factor)
    {
        if (factor <= 1) return this;
        if (smaller.TryGetValue(factor, out var cached)) return cached;
        int w = Math.Max(1, Width / factor), h = Math.Max(1, Height / factor);
        var outp = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            float r = 0, g = 0, b = 0, a = 0;
            for (var dy = 0; dy < factor; dy++)
            for (var dx = 0; dx < factor; dx++)
            {
                var i = ((y * factor + dy) * Width + x * factor + dx) * 4;
                var al = Rgba[i + 3];
                r += Rgba[i] * al; g += Rgba[i + 1] * al; b += Rgba[i + 2] * al; a += al;
            }
            var o = (y * w + x) * 4;
            if (a > 0) { outp[o] = (byte)(r / a); outp[o + 1] = (byte)(g / a); outp[o + 2] = (byte)(b / a); }
            outp[o + 3] = (byte)(a / (factor * factor));
        }
        return smaller[factor] = new Picture(w, h, outp);
    }
}

/// Turns a picture + the editor's settings into the sticker texture. Pure C# (no Unity), so it can be tested anywhere.
/// Order: place the picture (zoom / rotate / move / flip), turn everything by the slot's tilt so it sits straight on
/// the phone, then add the white die-cut border around its shape — the same look as the game's own stickers.
internal static class StickerBaker
{
    public const float Margin = 0.06f;
    static readonly byte[] RimColor = { 196, 200, 206 };

    public static byte[] Bake(Picture picture, int size, SlotSettings s, float tilt)
    {
        var border = s.Border ? Math.Max(0f, s.BorderPx) * size / 512f : 0f;
        var inner = size * Margin + border;
        var (bx, by, bw, bh) = picture.Bounds;

        float total = Rad(s.Rotation + tilt), user = Rad(s.Rotation), t = Rad(tilt);
        float clipHalf = (size / 2f - inner) / (MathF.Abs(MathF.Cos(t)) + MathF.Abs(MathF.Sin(t)));
        float scale;
        if (s.Fill)
        {
            // Fill: the picture covers the whole sticker (photos become a full square sticker).
            scale = 2f * clipHalf / Math.Max(1f, Math.Min(bw, bh));
        }
        else
        {
            // Fit: the whole picture is visible.
            var (rw, rh) = Rotated(bw, bh, total);
            scale = (size - 2f * inner) / Math.Max(1f, Math.Max(rw, rh));
        }
        scale *= Math.Clamp(s.Zoom, 0.05f, 8f);

        // Shrink big pictures first so the sampling below stays clean.
        var factor = Math.Max(1, (int)MathF.Floor(1f / Math.Max(scale, 1e-4f)));
        var src = picture.Shrunk(factor);
        var sScale = scale * factor;
        float cx = (bx + bw / 2f) / factor, cy = (by + bh / 2f) / factor;

        var art = new float[size * size * 4]; // premultiplied
        float cosT = MathF.Cos(-t), sinT = MathF.Sin(-t), cosU = MathF.Cos(-user), sinU = MathF.Sin(-user);
        float offX = s.OffsetX * size, offY = s.OffsetY * size, half = size / 2f;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            float px = x + 0.5f - half, py = y + 0.5f - half;
            // back to "straight on the phone" space
            float qx = px * cosT - py * sinT, qy = px * sinT + py * cosT;
            if (s.Fill && (MathF.Abs(qx) > clipHalf || MathF.Abs(qy) > clipHalf)) continue;
            qx -= offX; qy -= offY;
            float ax = (qx * cosU - qy * sinU) / sScale, ay = (qx * sinU + qy * cosU) / sScale;
            if (s.Flip) ax = -ax;
            Sample(src, ax + cx - 0.5f, ay + cy - 0.5f, art, (y * size + x) * 4);
        }

        var outp = new byte[size * size * 4];
        if (border <= 0f)
        {
            for (var i = 0; i < size * size; i++) WritePremultiplied(art, i * 4, outp, 0, 0, 0, 0f);
            return outp;
        }

        var dist = DistanceToOpaque(art, size);
        for (var i = 0; i < size * size; i++)
        {
            var d = dist[i];
            var white = Math.Clamp(border - d + 0.5f, 0f, 1f);
            var rim = Math.Clamp(1f - MathF.Abs(d - (border - 1.2f)) / 1.5f, 0f, 1f) * 0.55f * white;
            float br = 255 + (RimColor[0] - 255) * rim, bg = 255 + (RimColor[1] - 255) * rim, bb = 255 + (RimColor[2] - 255) * rim;
            WritePremultiplied(art, i * 4, outp, br, bg, bb, white);
        }
        return outp;
    }

    /// Art (premultiplied) over a backing colour with alpha `backA`, written as straight RGBA bytes.
    static void WritePremultiplied(float[] art, int i, byte[] outp, float br, float bg, float bb, float backA)
    {
        var a = art[i + 3];
        var outA = a + backA * (1 - a);
        if (outA <= 1e-5f) return;
        outp[i] = (byte)Math.Clamp((art[i] + br * backA * (1 - a)) / outA, 0, 255);
        outp[i + 1] = (byte)Math.Clamp((art[i + 1] + bg * backA * (1 - a)) / outA, 0, 255);
        outp[i + 2] = (byte)Math.Clamp((art[i + 2] + bb * backA * (1 - a)) / outA, 0, 255);
        outp[i + 3] = (byte)Math.Clamp(outA * 255f + 0.5f, 0, 255);
    }

    /// Bilinear, premultiplied (no dark fringes around transparent edges); outside the picture is transparent.
    static void Sample(Picture p, float sx, float sy, float[] dst, int o)
    {
        int x0 = (int)MathF.Floor(sx), y0 = (int)MathF.Floor(sy);
        float fx = sx - x0, fy = sy - y0;
        float r = 0, g = 0, b = 0, a = 0;
        for (var j = 0; j < 2; j++)
        for (var i = 0; i < 2; i++)
        {
            int x = x0 + i, y = y0 + j;
            if (x < 0 || y < 0 || x >= p.Width || y >= p.Height) continue;
            var w = (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy);
            var k = (y * p.Width + x) * 4;
            var al = p.Rgba[k + 3] / 255f * w;
            r += p.Rgba[k] * al; g += p.Rgba[k + 1] * al; b += p.Rgba[k + 2] * al; a += al;
        }
        dst[o] = r; dst[o + 1] = g; dst[o + 2] = b; dst[o + 3] = a;
    }

    /// Distance (px) from every pixel to the nearest solid pixel of the art — Felzenszwalb's exact transform.
    static float[] DistanceToOpaque(float[] art, int n)
    {
        const float Inf = 1e20f;
        var f = new float[n * n];
        for (var i = 0; i < n * n; i++) f[i] = art[i * 4 + 3] >= 0.5f ? 0f : Inf;
        var line = new float[n]; var d = new float[n]; var v = new int[n]; var z = new float[n + 1];
        for (var x = 0; x < n; x++)
        {
            for (var y = 0; y < n; y++) line[y] = f[y * n + x];
            Transform1D(line, d, v, z, n);
            for (var y = 0; y < n; y++) f[y * n + x] = d[y];
        }
        for (var y = 0; y < n; y++)
        {
            Array.Copy(f, y * n, line, 0, n);
            Transform1D(line, d, v, z, n);
            Array.Copy(d, 0, f, y * n, n);
        }
        for (var i = 0; i < n * n; i++) f[i] = MathF.Sqrt(f[i]);
        return f;
    }

    static void Transform1D(float[] f, float[] d, int[] v, float[] z, int n)
    {
        var k = 0;
        v[0] = 0; z[0] = float.NegativeInfinity; z[1] = float.PositiveInfinity;
        for (var q = 1; q < n; q++)
        {
            var s = ((f[q] + q * q) - (f[v[k]] + v[k] * v[k])) / (2f * q - 2f * v[k]);
            while (s <= z[k])
            {
                k--;
                s = ((f[q] + q * q) - (f[v[k]] + v[k] * v[k])) / (2f * q - 2f * v[k]);
            }
            k++; v[k] = q; z[k] = s; z[k + 1] = float.PositiveInfinity;
        }
        k = 0;
        for (var q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            d[q] = (q - v[k]) * (q - v[k]) + f[v[k]];
        }
    }

    static (float, float) Rotated(float w, float h, float rad)
    {
        float c = MathF.Abs(MathF.Cos(rad)), s = MathF.Abs(MathF.Sin(rad));
        return (w * c + h * s, w * s + h * c);
    }

    static float Rad(float deg) => deg * MathF.PI / 180f;
}
