using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Result of comparing a player's carving with a reference carving.</summary>
public struct CarveScore
{
    public float accuracy;   // 0..1  overlap score (Dice). 0.9 = "90% accurate"
    public float iou;        // 0..1  stricter version: overlap / union
    public float coverage;   // 0..1  how much of the target shape the player carved out
    public float overcut;    // 0..1  how much of the player's cut is OUTSIDE the target shape
    public int targetSamples, playerSamples;
}

/// <summary>Evenly spread points on the ORIGINAL mesh surface (area weighted, deterministic).</summary>
public class SurfaceSamples
{
    public readonly Vector3[] verts;
    public readonly int[] tris;
    public readonly Vector3[] pos;
    public readonly Vector3[] nrm;

    public SurfaceSamples(Mesh mesh, int count, int seed = 12345)
    {
        verts = mesh.vertices;
        tris = mesh.triangles;
        pos = new Vector3[count];
        nrm = new Vector3[count];

        int triCount = tris.Length / 3;
        if (triCount == 0) return;

        var cdf = new float[triCount];
        float total = 0f;
        for (int i = 0; i < triCount; i++)
        {
            Vector3 a = verts[tris[i * 3]], b = verts[tris[i * 3 + 1]], c = verts[tris[i * 3 + 2]];
            total += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            cdf[i] = total;
        }

        var rnd = new System.Random(seed);
        for (int k = 0; k < count; k++)
        {
            float r = (float)(rnd.NextDouble() * total);
            int idx = Array.BinarySearch(cdf, r);
            if (idx < 0) idx = ~idx;
            if (idx >= triCount) idx = triCount - 1;

            Vector3 a = verts[tris[idx * 3]], b = verts[tris[idx * 3 + 1]], c = verts[tris[idx * 3 + 2]];
            float u = (float)rnd.NextDouble(), w = (float)rnd.NextDouble();
            if (u + w > 1f) { u = 1f - u; w = 1f - w; }
            pos[k] = a + (b - a) * u + (c - a) * w;
            nrm[k] = Vector3.Cross(b - a, c - a).normalized;
        }
    }
}

public static class CarveMetrics
{
    /// <summary>Which surface samples were carved away by the first editCount edits.</summary>
    public static bool[] RemovedMask(SurfaceSamples s, IReadOnlyList<MeshEdit> edits, int editCount)
    {
        var mask = new bool[s.pos.Length];
        for (int i = 0; i < editCount && i < edits.Count; i++)
        {
            if (edits[i].tool == EditTool.LassoCut) AddLasso(s, edits[i], mask);
            // add other tools here
        }
        return mask;
    }

    public static CarveScore Score(bool[] target, bool[] player)
    {
        int t = 0, p = 0, both = 0;
        for (int i = 0; i < target.Length; i++)
        {
            if (target[i]) t++;
            if (player[i]) p++;
            if (target[i] && player[i]) both++;
        }

        var s = new CarveScore { targetSamples = t, playerSamples = p };
        s.accuracy = (t + p) > 0 ? 2f * both / (t + p) : 0f;
        s.iou = (t + p - both) > 0 ? (float)both / (t + p - both) : 0f;
        s.coverage = t > 0 ? (float)both / t : 0f;
        s.overcut = p > 0 ? 1f - (float)both / p : 0f;
        return s;
    }

    /// <summary>Compares two carvings (any two edit lists recorded on the same mesh).</summary>
    public static CarveScore Compare(SurfaceSamples s, IReadOnlyList<MeshEdit> reference, IReadOnlyList<MeshEdit> attempt)
    {
        return Score(RemovedMask(s, reference, reference.Count), RemovedMask(s, attempt, attempt.Count));
    }

    // Marks the samples a lasso edit would have removed (same rules as the real cut: inside the shape,
    // facing the camera, within the depth window of the nearest visible surface).
    static void AddLasso(SurfaceSamples s, MeshEdit ed, bool[] mask)
    {
        float[] v = ed.values;
        if (ed.points2D == null || ed.points2D.Length < 3 || v == null || v.Length < LassoValues.Count) return;

        List<Vector2> pts = new List<Vector2>(ed.points2D);
        int smooth = Mathf.RoundToInt(v[LassoValues.Smoothing]);
        for (int i = 0; i < smooth; i++) pts = Chaikin(pts);
        Vector2[] poly = pts.ToArray();

        Vector2 min = poly[0], max = poly[0];
        for (int i = 1; i < poly.Length; i++) { min = Vector2.Min(min, poly[i]); max = Vector2.Max(max, poly[i]); }

        bool frontOnly = v[LassoValues.FrontOnly] > 0.5f;
        float depthWindow = v[LassoValues.Depth];
        Matrix4x4 view = ed.camera.viewFromLocal, proj = ed.camera.projection;
        Rect pix = ed.camera.pixelRect;
        bool ortho = ed.camera.orthographic;
        Matrix4x4 inv = view.inverse;
        Vector3 camPos = inv.MultiplyPoint3x4(Vector3.zero);
        Vector3 camFwd = inv.MultiplyVector(new Vector3(0f, 0f, -1f)).normalized;

        var idx = new List<int>();
        var dep = new List<float>();
        float zNear = float.MaxValue;

        for (int i = 0; i < s.pos.Length; i++)
        {
            if (!ProjectPoint(view, proj, pix, s.pos[i], out Vector2 sp, out float depth)) continue;
            if (sp.x < min.x || sp.x > max.x || sp.y < min.y || sp.y > max.y) continue;
            if (frontOnly)
            {
                bool facing = ortho ? Vector3.Dot(s.nrm[i], camFwd) < 0f
                                    : Vector3.Dot(s.nrm[i], camPos - s.pos[i]) > 0f;
                if (!facing) continue;
            }
            if (!Inside(poly, sp)) continue;
            idx.Add(i); dep.Add(depth);
            if (depth < zNear) zNear = depth;
        }

        float limit = depthWindow > 0f ? zNear + depthWindow : float.MaxValue;
        for (int k = 0; k < idx.Count; k++)
            if (dep[k] <= limit) mask[idx[k]] = true;
    }

    static bool ProjectPoint(Matrix4x4 view, Matrix4x4 proj, Rect pix, Vector3 p, out Vector2 sp, out float depth)
    {
        sp = default(Vector2);
        Vector3 v = view.MultiplyPoint3x4(p);
        depth = -v.z;
        if (depth <= 1e-5f) return false;
        Vector4 clip = proj * new Vector4(v.x, v.y, v.z, 1f);
        if (clip.w <= 1e-5f) return false;
        sp = new Vector2(pix.x + (clip.x / clip.w * 0.5f + 0.5f) * pix.width,
                         pix.y + (clip.y / clip.w * 0.5f + 0.5f) * pix.height);
        return true;
    }

    public static bool Inside(Vector2[] poly, Vector2 p)
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

    public static List<Vector2> Chaikin(List<Vector2> p)
    {
        var r = new List<Vector2>(p.Count * 2);
        for (int i = 0; i < p.Count; i++)
        {
            Vector2 a = p[i], b = p[(i + 1) % p.Count];
            r.Add(Vector2.Lerp(a, b, 0.25f));
            r.Add(Vector2.Lerp(a, b, 0.75f));
        }
        return r;
    }
}

/// <summary>
/// Pumpkin carving judge.
///
/// DEVELOPER: tick Dev Mode, carve a shape, press "Save current carving as reference".
///            The file lands in Assets/Resources/Carvings/ (drag it into Templates afterwards).
/// PLAYER:    untick Dev Mode, assign Templates. The reference outline is shown on the pumpkin as a
///            stencil and the accuracy updates after every cut / undo.
///
/// Scoring compares WHICH PARTS OF THE PUMPKIN SURFACE were carved away, in object space.
/// It does not matter how the player rotated the pumpkin or where the camera was.
/// </summary>
public class CarveScorer : MonoBehaviour
{
    [Header("Setup")]
    public MeshHoleCutter cutter;
    [Tooltip("ON: you are authoring reference shapes. OFF: the game, player is scored against Templates.")]
    public bool devMode = false;
    public TextAsset[] templates;
    public int templateIndex = 0;

    [Header("Developer: saving references")]
    public string newReferenceName = "pumpkin_shape";

    [Header("Scoring")]
    [Tooltip("More samples = finer accuracy on small shapes")]
    public int sampleCount = 40000;

    [Header("Stencil guide (shows the target on the pumpkin)")]
    public bool showGuide = true;
    public Color guideColor = new Color(0.2f, 1f, 1f, 0.9f);
    public float guideWidth = 0.012f;
    public float guideOffset = 0.01f;

    [Header("UI")]
    public bool showScoreUI = true;

    public CarveScore Current { get; private set; }
    public event Action<CarveScore> Scored;     // raised by Submit()

    SurfaceSamples samples;
    List<MeshEdit> targetEdits;
    bool[] targetMask;
    int lastVersion = -1;
    readonly List<GameObject> guides = new List<GameObject>();

    [SerializeField] private MeshHoleCutter _cutter;
    [SerializeField] private Stacker _stacker;

    // ------------------------------------------------------------------ Unity
    public void Initialize()
    {
        if (!cutter) cutter = GetComponent<MeshHoleCutter>();
        if (!cutter) { Debug.LogError("CarveScorer: assign the MeshHoleCutter.", this); enabled = false; return; }

        samples = new SurfaceSamples(cutter.SourceMesh, Mathf.Max(1000, sampleCount));
        if (!devMode && templates != null && templates.Length > 0) SetTemplate(templateIndex);
    }

    void OnDestroy() { ClearGuide(); }

    void Update()
    {
        if (!cutter || samples == null) return;
        if (cutter.HistoryVersion == lastVersion) return;
        lastVersion = cutter.HistoryVersion;
        Recompute();
    }

    void OnGUI()
    {
        if (!showScoreUI || !cutter) return;

        var r = new Rect(10, Screen.height - 150, 340, 140);
        cutter.extraBlockedRect = r;
        GUILayout.BeginArea(r, GUI.skin.box);

        if (devMode)
        {
            GUILayout.Label("DEV MODE: carve the shape, then save it as a reference");
            GUILayout.Label("File name: " + newReferenceName + ".json  (change it in the inspector)");
            if (GUILayout.Button("Save current carving as reference")) SaveAsReference(newReferenceName);
        }
        else if (targetEdits != null)
        {
            GUILayout.Label("Target: " + (templates != null && templateIndex < templates.Length ? templates[templateIndex].name : "-"));
            GUILayout.Label("Accuracy: " + Mathf.RoundToInt(Current.accuracy * 100f) + "%");
            GUILayout.Label("Shape carved: " + Mathf.RoundToInt(Current.coverage * 100f) + "%     Extra cut: " + Mathf.RoundToInt(Current.overcut * 100f) + "%");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Submit")) Submit();
            if (templates != null && templates.Length > 1 && GUILayout.Button("Next shape (new pumpkin)")) NextShape();
            GUILayout.EndHorizontal();
        }
        else GUILayout.Label("No target shape assigned (see Templates)");

        GUILayout.EndArea();
    }

    // ------------------------------------------------------------------ Public API
    /// <summary>Loads a reference carving saved by the developer.</summary>
    public bool SetTemplate(int index)
    {
        if (templates == null || index < 0 || index >= templates.Length) return false;
        templateIndex = index;
        return SetTemplate(templates[index]);
    }

    public bool SetTemplate(TextAsset asset)
    {
        if (!cutter || samples == null || !asset) return false;

        MeshEditLog log = JsonUtility.FromJson<MeshEditLog>(asset.text);
        if (log == null || log.edits == null || log.edits.Count == 0)
        {
            Debug.LogWarning("CarveScorer: " + asset.name + " has no edits.");
            return false;
        }
        Mesh src = cutter.SourceMesh;
        if (log.sourceVertexCount != src.vertexCount || log.sourceTriangleCount != src.triangles.Length / 3)
        {
            Debug.LogWarning("CarveScorer: " + asset.name + " was recorded on a different mesh.");
            return false;
        }

        targetEdits = log.edits;
        targetMask = CarveMetrics.RemovedMask(samples, targetEdits, targetEdits.Count);
        BuildGuide();
        lastVersion = -1;      // force a score refresh
        return true;
    }

    /// <summary>Current accuracy 0..1 (also raises the Scored event).</summary>
    public CarveScore Submit()
    {
        Recompute();
        Destroy(_cutter.targetPumpkin);
        _stacker.CanSelectMore = true;
        if (Scored != null) Scored(Current);
        return Current;
    }

    /// <summary>Fresh pumpkin + next template.</summary>
    public void NextShape()
    {
        if (templates == null || templates.Length == 0) return;
        cutter.ResetMesh();
        SetTemplate((templateIndex + 1) % templates.Length);
    }

    /// <summary>Compare any two recorded carvings, e.g. the player's last attempt against the current one.</summary>
    public CarveScore Compare(IReadOnlyList<MeshEdit> a, IReadOnlyList<MeshEdit> b)
    {
        return CarveMetrics.Compare(samples, a, b);
    }

    /// <summary>Saves everything carved so far as a reference shape (developer workflow).</summary>
    public string SaveAsReference(string fileName)
    {
        string json = cutter.ExportLog(true);
        string dir;
#if UNITY_EDITOR
        dir = System.IO.Path.Combine(Application.dataPath, "Resources/Carvings");
#else
        dir = Application.persistentDataPath;
#endif
        System.IO.Directory.CreateDirectory(dir);

        string path = System.IO.Path.Combine(dir, fileName + ".json");
        int n = 2;
        while (System.IO.File.Exists(path))
            path = System.IO.Path.Combine(dir, fileName + "_" + (n++) + ".json");   // never overwrite

        System.IO.File.WriteAllText(path, json);
#if UNITY_EDITOR
        UnityEditor.AssetDatabase.Refresh();
#endif
        Debug.Log("CarveScorer: saved reference to " + path);
        return path;
    }

    // ------------------------------------------------------------------ Internals
    void Recompute()
    {
        if (targetMask == null) { Current = default(CarveScore); return; }
        bool[] player = CarveMetrics.RemovedMask(samples, cutter.History, cutter.AppliedEditCount);
        Current = CarveMetrics.Score(targetMask, player);
    }

    // ---- stencil: the reference outline drawn on the pumpkin surface
    void BuildGuide()
    {
        ClearGuide();
        if (!showGuide || targetEdits == null) return;

        Transform guideParent = cutter.targetPumpkin ? cutter.targetPumpkin.transform : cutter.transform;
        float s = Mathf.Max(1e-5f, (Mathf.Abs(guideParent.lossyScale.x) + Mathf.Abs(guideParent.lossyScale.y) + Mathf.Abs(guideParent.lossyScale.z)) / 3f);

        foreach (MeshEdit ed in targetEdits)
        {
            if (ed.tool != EditTool.LassoCut || ed.points2D == null || ed.points2D.Length < 3 || ed.values == null) continue;

            List<Vector2> pts = new List<Vector2>(ed.points2D);
            int smooth = Mathf.RoundToInt(ed.values[LassoValues.Smoothing]);
            for (int i = 0; i < smooth; i++) pts = CarveMetrics.Chaikin(pts);

            Matrix4x4 invVP = (ed.camera.projection * ed.camera.viewFromLocal).inverse;
            Rect pix = ed.camera.pixelRect;
            int stride = Mathf.Max(1, pts.Count / 96);

            var line = new List<Vector3>();
            for (int i = 0; i < pts.Count; i += stride)
            {
                float nx = (pts[i].x - pix.x) / pix.width * 2f - 1f;
                float ny = (pts[i].y - pix.y) / pix.height * 2f - 1f;
                Vector4 a = invVP * new Vector4(nx, ny, -1f, 1f);
                Vector4 b = invVP * new Vector4(nx, ny, 1f, 1f);
                Vector3 o = new Vector3(a.x, a.y, a.z) / a.w;
                Vector3 d = new Vector3(b.x, b.y, b.z) / b.w - o;
                if (RaycastSource(o, d, out Vector3 hit, out Vector3 n)) line.Add(hit + n * (guideOffset / s));
            }
            if (line.Count >= 3) CreateGuideLine(line, guideParent, s);
        }
    }

    void CreateGuideLine(List<Vector3> pts, Transform guideParent, float scale)
    {
        Shader sh = Shader.Find("Sprites/Default");
        if (!sh) return;

        var go = new GameObject("CarveGuide");
        go.transform.SetParent(guideParent, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = false;
        lr.loop = true;
        lr.numCornerVertices = 3;
        lr.positionCount = pts.Count;
        lr.SetPositions(pts.ToArray());
        lr.widthMultiplier = guideWidth / scale;
        lr.material = new Material(sh);
        lr.startColor = lr.endColor = guideColor;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        guides.Add(go);
    }

    void ClearGuide()
    {
        for (int i = 0; i < guides.Count; i++) if (guides[i]) Destroy(guides[i]);
        guides.Clear();
    }

    // Ray (object-local) against the ORIGINAL mesh
    bool RaycastSource(Vector3 o, Vector3 d, out Vector3 hit, out Vector3 normal)
    {
        hit = normal = Vector3.zero;
        float best = float.MaxValue;
        int bestTri = -1;
        int[] t = samples.tris;
        Vector3[] vs = samples.verts;

        for (int i = 0; i < t.Length; i += 3)
        {
            Vector3 v0 = vs[t[i]];
            Vector3 e1 = vs[t[i + 1]] - v0;
            Vector3 e2 = vs[t[i + 2]] - v0;
            Vector3 p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (det > -1e-12f && det < 1e-12f) continue;
            float inv = 1f / det;
            Vector3 tv = o - v0;
            float u = Vector3.Dot(tv, p) * inv;
            if (u < 0f || u > 1f) continue;
            Vector3 q = Vector3.Cross(tv, e1);
            float v = Vector3.Dot(d, q) * inv;
            if (v < 0f || u + v > 1f) continue;
            float tt = Vector3.Dot(e2, q) * inv;
            if (tt > 1e-4f && tt < best) { best = tt; bestTri = i; }
        }
        if (bestTri < 0) return false;

        hit = o + d * best;
        Vector3 n = Vector3.Cross(vs[t[bestTri + 1]] - vs[t[bestTri]], vs[t[bestTri + 2]] - vs[t[bestTri]]).normalized;
        if (Vector3.Dot(n, d) > 0f) n = -n;      // face the viewer
        normal = n;
        return true;
    }
}
