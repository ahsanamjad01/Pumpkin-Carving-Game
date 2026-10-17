using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Tool ids stored in MeshEdit.tool. Add new tools here.</summary>
public static class EditTool
{
    public const int LassoCut = 0;
    public const int Scrape = 1;
    // public const int Extrude = 1;
    // public const int Smooth  = 2;
}

/// <summary>Layout of MeshEdit.values for EditTool.LassoCut (the settings that were active for that cut).</summary>
public static class LassoValues
{
    public const int Smoothing = 0;
    public const int FrontOnly = 1;     // 0 / 1
    public const int Depth = 2;
    public const int EdgePx = 3;
    public const int Curvature = 4;
    public const int RemoveLoose = 5;   // 0 / 1
    public const int MaxPasses = 6;
    public const int Count = 7;
}

/// <summary>
/// Everything needed to re-project the mouse shape onto the mesh again. Stored in OBJECT-LOCAL space,
/// so replaying still works even if the object was rotated / moved between edits.
/// </summary>
[Serializable]
public struct CameraSnapshot
{
    public Matrix4x4 viewFromLocal;
    public Matrix4x4 projection;
    public Rect pixelRect;
    public bool orthographic;
}

/// <summary>One recorded change to the mesh.</summary>
[Serializable]
public class MeshEdit
{
    public int tool;               // EditTool.*

    // ---- input (what the player did)
    public Vector2[] points2D;     // lasso: raw mouse path in screen pixels
    public float[] values;         // tool settings, layout depends on the tool (see LassoValues)
    public CameraSnapshot camera;

    // ---- result (what changed)
    public Vector3[] points3D;     // lasso: 3D outline of the hole on the surface (object-local space)
    public float areaRemoved;      // surface area removed by this edit (world units^2)
    public int trianglesBefore;
    public int trianglesAfter;
    public float time;             // Time.realtimeSinceStartup when it was made
}

/// <summary>What gets saved to disk / JSON.</summary>
[Serializable]
public class MeshEditLog
{
    public int version = 1;
    public string meshName;
    public int sourceVertexCount;      // sanity check: the log only makes sense for the mesh it was recorded on
    public int sourceTriangleCount;
    public List<MeshEdit> edits = new List<MeshEdit>();
}

// ====================================================================== comparison

public struct ShapeScore
{
    public float iou;        // 0..1, overlap of the two filled shapes (1 = identical)
    public float meanDist;   // average distance between the two outlines (pixels)
    public float maxDist;    // worst distance between the two outlines (pixels)
}

public static class EditCompare
{
    /// <summary>
    /// Compares two drawn outlines (screen space). With alignShapes the second shape is moved so both
    /// centroids match, i.e. only the SHAPE is compared, not where it was drawn.
    /// </summary>
    public static ShapeScore Compare2D(Vector2[] reference, Vector2[] attempt, bool alignShapes = false, int grid = 128)
    {
        var s = new ShapeScore();
        if (reference == null || attempt == null || reference.Length < 3 || attempt.Length < 3) return s;

        Vector2[] b = attempt;
        if (alignShapes)
        {
            Vector2 shift = Centroid(reference) - Centroid(attempt);
            b = new Vector2[attempt.Length];
            for (int i = 0; i < b.Length; i++) b[i] = attempt[i] + shift;
        }

        // overlap by sampling a grid over the union bounds
        Vector2 min = Vector2.Min(MinOf(reference), MinOf(b));
        Vector2 max = Vector2.Max(MaxOf(reference), MaxOf(b));
        int inter = 0, union = 0;
        for (int y = 0; y < grid; y++)
        {
            for (int x = 0; x < grid; x++)
            {
                var p = new Vector2(Mathf.Lerp(min.x, max.x, (x + 0.5f) / grid),
                                    Mathf.Lerp(min.y, max.y, (y + 0.5f) / grid));
                bool ia = Inside(reference, p), ib = Inside(b, p);
                if (ia && ib) inter++;
                if (ia || ib) union++;
            }
        }
        s.iou = union > 0 ? (float)inter / union : 0f;

        // outline distance in both directions
        Vector2[] ra = Resample(reference, 128), rb = Resample(b, 128);
        float sum = 0f, worst = 0f;
        for (int i = 0; i < ra.Length; i++)
        {
            float d = DistToPolygon(ra[i], b);
            sum += d; worst = Mathf.Max(worst, d);
        }
        for (int i = 0; i < rb.Length; i++)
        {
            float d = DistToPolygon(rb[i], reference);
            sum += d; worst = Mathf.Max(worst, d);
        }
        s.meanDist = sum / (ra.Length + rb.Length);
        s.maxDist = worst;
        return s;
    }

    /// <summary>
    /// Camera independent comparison of two 3D hole outlines (MeshEdit.points3D).
    /// Returns the mean nearest-point distance in both directions (Chamfer distance); max is the worst point.
    /// </summary>
    public static float Chamfer3D(Vector3[] a, Vector3[] b, out float mean, out float max)
    {
        mean = max = float.NaN;
        if (a == null || b == null || a.Length == 0 || b.Length == 0) return float.NaN;

        float sum = 0f, worst = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            float d = NearestDist(a[i], b);
            sum += d; worst = Mathf.Max(worst, d);
        }
        for (int i = 0; i < b.Length; i++)
        {
            float d = NearestDist(b[i], a);
            sum += d; worst = Mathf.Max(worst, d);
        }
        mean = sum / (a.Length + b.Length);
        max = worst;
        return mean;
    }

    // ---------------------------------------------------------------- helpers
    static float NearestDist(Vector3 p, Vector3[] cloud)
    {
        float best = float.MaxValue;
        for (int i = 0; i < cloud.Length; i++)
        {
            float d = (cloud[i] - p).sqrMagnitude;
            if (d < best) best = d;
        }
        return Mathf.Sqrt(best);
    }

    static Vector2 MinOf(Vector2[] p)
    {
        Vector2 m = p[0];
        for (int i = 1; i < p.Length; i++) m = Vector2.Min(m, p[i]);
        return m;
    }

    static Vector2 MaxOf(Vector2[] p)
    {
        Vector2 m = p[0];
        for (int i = 1; i < p.Length; i++) m = Vector2.Max(m, p[i]);
        return m;
    }

    static Vector2 Centroid(Vector2[] p)
    {
        float a = 0f, cx = 0f, cy = 0f;
        for (int i = 0; i < p.Length; i++)
        {
            Vector2 u = p[i], v = p[(i + 1) % p.Length];
            float cr = u.x * v.y - v.x * u.y;
            a += cr; cx += (u.x + v.x) * cr; cy += (u.y + v.y) * cr;
        }
        if (Mathf.Abs(a) < 1e-6f)
        {
            Vector2 mean = Vector2.zero;
            for (int i = 0; i < p.Length; i++) mean += p[i];
            return mean / p.Length;
        }
        a *= 0.5f;
        return new Vector2(cx, cy) / (6f * a);
    }

    static bool Inside(Vector2[] poly, Vector2 p)
    {
        bool inside = false;
        int n = poly.Length;
        Vector2 b = poly[n - 1];
        for (int i = 0; i < n; i++)
        {
            Vector2 a = b; b = poly[i];
            if ((a.y > p.y) != (b.y > p.y))
            {
                float xi = a.x + (p.y - a.y) * (b.x - a.x) / (b.y - a.y);
                if (p.x < xi) inside = !inside;
            }
        }
        return inside;
    }

    static float DistToPolygon(Vector2 p, Vector2[] poly)
    {
        float best = float.MaxValue;
        int n = poly.Length;
        for (int i = 0; i < n; i++)
        {
            Vector2 a = poly[i], b = poly[(i + 1) % n];
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 > 1e-12f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2) : 0f;
            float d = (a + ab * t - p).sqrMagnitude;
            if (d < best) best = d;
        }
        return Mathf.Sqrt(best);
    }

    // evenly spaced points along a closed outline
    static Vector2[] Resample(Vector2[] p, int count)
    {
        int n = p.Length;
        float total = 0f;
        for (int i = 0; i < n; i++) total += Vector2.Distance(p[i], p[(i + 1) % n]);

        var r = new Vector2[count];
        if (total < 1e-6f)
        {
            for (int i = 0; i < count; i++) r[i] = p[0];
            return r;
        }

        float step = total / count, acc = 0f;
        int seg = 0;
        float segLen = Vector2.Distance(p[0], p[1 % n]);
        for (int k = 0; k < count; k++)
        {
            float target = k * step;
            while (acc + segLen < target && seg < n - 1)
            {
                acc += segLen;
                seg++;
                segLen = Vector2.Distance(p[seg], p[(seg + 1) % n]);
            }
            float t = segLen > 1e-9f ? (target - acc) / segLen : 0f;
            r[k] = Vector2.Lerp(p[seg], p[(seg + 1) % n], Mathf.Clamp01(t));
        }
        return r;
    }
}
