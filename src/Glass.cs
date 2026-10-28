using System;
using System.Collections.Generic;
using UnityEngine;

namespace StickerStudio;

/// The "baked once" liquid glass. When the panel opens: one screenshot of the game, blurred with a few bilinear
/// blits (no shaders). Every glass shape is a mesh built once: the panel's mesh samples that blurred screenshot, with
/// the UVs near its edge pushed outward so the edge bends what's behind it like thick glass. Rim light, gloss and
/// shadows are vertex colours on the same kind of mesh. Nothing glassy runs per frame.
internal static class Glass
{
    public static RenderTexture Backdrop { get; private set; }
    static bool flipY;

    /// Call at the end of a frame, before the panel shows (so it doesn't blur itself).
    public static void Snapshot()
    {
        int w = Math.Max(16, Screen.width), h = Math.Max(16, Screen.height);
        var temps = new List<RenderTexture>();
        try
        {
            var full = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            temps.Add(full);
            ScreenCapture.CaptureScreenshotIntoRenderTexture(full);
            RenderTexture src = full;
            var chain = new List<RenderTexture>();
            for (var i = 0; i < 4; i++) // 1/2 .. 1/16
            {
                w = Math.Max(8, w / 2); h = Math.Max(8, h / 2);
                var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                rt.filterMode = FilterMode.Bilinear;
                temps.Add(rt); chain.Add(rt);
                Graphics.Blit(src, rt);
                src = rt;
            }
            for (var i = chain.Count - 2; i >= 1; i--) // back up to 1/4: the bilinear upsample smooths the blocks
            {
                Graphics.Blit(src, chain[i]);
                src = chain[i];
            }
            if (Backdrop == null || Backdrop.width != src.width || Backdrop.height != src.height)
            {
                if (Backdrop != null) Backdrop.Release();
                Backdrop = new RenderTexture(src.width, src.height, 0, RenderTextureFormat.ARGB32)
                {
                    filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave,
                };
            }
            Graphics.Blit(src, Backdrop);
            flipY = Plugin.FlipBackground.Value switch
            {
                "Yes" => true,
                "No" => false,
                _ => SystemInfo.graphicsUVStartsAtTop,
            };
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Couldn't grab the screen for the glass, using plain tint: {e.Message}");
        }
        finally
        {
            foreach (var rt in temps) RenderTexture.ReleaseTemporary(rt);
        }
    }

    /// Screen pixel -> UV in the backdrop.
    public static Vector2 BackdropUV(Vector2 screen)
    {
        var u = screen.x / Math.Max(1, Screen.width);
        var v = screen.y / Math.Max(1, Screen.height);
        return new Vector2(u, flipY ? 1 - v : v);
    }
}

/// One rounded rectangle as rings of vertices (from the outside in) and a fan to the centre. Depth is the distance in
/// from the edge (negative = outside, for soft shadows and anti-aliasing). Callbacks pick each vertex's colour / UV.
internal sealed class MeshBuilder
{
    const int CornerSegments = 10;
    readonly List<Vector3> positions = new();
    readonly List<Color32> colors = new();
    readonly List<Vector2> uvs = new();
    readonly List<int> triangles = new();

    public delegate Color32 ColorFn(Vector2 position, Vector2 normal, float depth);
    public delegate Vector2 UvFn(Vector2 position, Vector2 normal, float depth);

    public MeshBuilder Rounded(Vector2 center, Vector2 size, float radius, float[] depths, ColorFn color, UvFn uv = null, bool fill = true)
    {
        var half = size / 2f;
        radius = Mathf.Min(radius, Mathf.Min(half.x, half.y));
        // rings deeper than the shape is thick would turn inside out: keep the ones that fit
        var limit = Mathf.Min(half.x, half.y) - 0.5f;
        var kept = new List<float>();
        foreach (var depth in depths) if (depth < limit) kept.Add(depth);
        if (kept.Count == 0) return this;
        depths = kept.ToArray();
        var ring = 4 * (CornerSegments + 1);
        var first = positions.Count;
        foreach (var depth in depths)
        {
            var effective = Mathf.Max(radius - depth, 0f);
            for (var corner = 0; corner < 4; corner++)
            {
                var sx = corner == 0 || corner == 3 ? 1f : -1f; // TR, TL, BL, BR (counter-clockwise from the right)
                var sy = corner < 2 ? 1f : -1f;
                var c = new Vector2(sx * (half.x - depth - effective), sy * (half.y - depth - effective));
                for (var s = 0; s <= CornerSegments; s++)
                {
                    var angle = (corner * 90f + 90f * s / CornerSegments) * Mathf.Deg2Rad;
                    var n = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                    var p = c + n * effective;
                    Add(center + p, n, depth, color, uv);
                }
            }
        }
        for (var r = 0; r + 1 < depths.Length; r++)
        for (var i = 0; i < ring; i++)
        {
            int a = first + r * ring + i, b = first + r * ring + (i + 1) % ring;
            int c = a + ring, d = b + ring;
            triangles.Add(a); triangles.Add(b); triangles.Add(c);
            triangles.Add(b); triangles.Add(d); triangles.Add(c);
        }
        if (fill)
        {
            var centerIndex = positions.Count;
            Add(center, Vector2.zero, 1e6f, color, uv);
            var inner = first + (depths.Length - 1) * ring;
            for (var i = 0; i < ring; i++)
            {
                triangles.Add(inner + i); triangles.Add(inner + (i + 1) % ring); triangles.Add(centerIndex);
            }
        }
        return this;
    }

    void Add(Vector2 p, Vector2 n, float depth, ColorFn color, UvFn uv)
    {
        positions.Add(p);
        colors.Add(color(p, n, depth));
        uvs.Add(uv != null ? uv(p, n, depth) : new Vector2(0.5f, 0.5f));
    }

    public Mesh Into(Mesh mesh)
    {
        if (mesh == null) mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
        mesh.Clear();
        mesh.vertices = positions.ToArray();
        mesh.colors32 = colors.ToArray();
        mesh.uv = uvs.ToArray();
        mesh.triangles = triangles.ToArray();
        return mesh;
    }
}

/// A UI object drawn straight from a mesh (no Graphic): a RectTransform + CanvasRenderer. The panel re-sends the
/// mesh whenever it becomes visible, because a CanvasRenderer forgets it when its object is switched off.
internal sealed class GlassShape
{
    public readonly RectTransform Rect;
    readonly CanvasRenderer renderer;
    readonly Func<GlassShape, MeshBuilder> build;
    readonly Func<Texture> texture;
    Mesh mesh;

    public GlassShape(Transform parent, string name, Func<GlassShape, MeshBuilder> build, Func<Texture> texture = null)
    {
        var go = new GameObject(name);
        Rect = go.AddComponent<RectTransform>();
        Rect.SetParent(parent, false);
        Rect.anchorMin = Rect.anchorMax = Rect.pivot = new Vector2(0.5f, 0.5f);
        renderer = go.AddComponent<CanvasRenderer>();
        this.build = build;
        this.texture = texture ?? (() => Texture2D.whiteTexture);
    }

    public void Rebuild()
    {
        try
        {
            mesh = build(this).Into(mesh);
            renderer.materialCount = 1;
            renderer.SetMaterial(Canvas.GetDefaultCanvasMaterial(), 0);
            renderer.SetTexture(texture() ?? Texture2D.whiteTexture);
            renderer.SetMesh(mesh);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"glass {Rect.name}: {e.Message}");
        }
    }

    /// Where a point of this shape (in its own units) is on screen, in pixels.
    public Vector2 ToScreen(Vector2 local)
    {
        var world = Rect.TransformPoint(local);
        return new Vector2(world.x, world.y);
    }
}
