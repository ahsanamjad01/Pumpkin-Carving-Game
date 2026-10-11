using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Runtime lasso mesh cutter.
///
/// MODES
///   [1] Rotate : left-mouse drag rotates the object.
///   [2] Cut    : left-mouse drag draws a shape; on release the area inside the shape is cut out.
///   [Tab]      : toggles between the two modes.
///
/// HOW IT WORKS
///   1. Your mouse path is closed into a polygon and rounded (Chaikin smoothing).
///   2. Every vertex gets a signed distance to that polygon in SCREEN space (negative = inside).
///   3. Triangles near the outline are subdivided until they are only a few pixels wide.
///   4. Every edge that crosses the outline is split exactly ON the outline (marching-triangles style),
///      so the hole edge follows your drawn shape smoothly instead of being stair-stepped.
///   5. Front-facing triangles inside the shape are removed. Back faces are never touched.
///   6. Pieces that got disconnected from the main body are deleted ("loose parts").
///   7. Hole borders get an inward wall (+ optional inner shell) for a solid, thick look.
///
/// SETUP
///   - Put on an object that has MeshFilter + MeshRenderer.
///   - The mesh must have Read/Write enabled (Unity primitives already do).
///   - Use a uniform scale on the object.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class MeshHoleCutter : MonoBehaviour
{
    public enum Mode { Rotate = 0, Cut = 1 }

    // ------------------------------------------------------------------ Inspector
    [Header("General")]
    public Camera cam;
    public Mode mode = Mode.Rotate;
    public bool showUI = true;

    [Header("Rotate Mode")]
    [Tooltip("Degrees per pixel of mouse drag")]
    public float rotateSpeed = 0.4f;

    [Header("Cut Mode (lasso)")]
    [Tooltip("Distance in pixels between recorded mouse points")]
    public float lassoPointSpacing = 8f;
    [Tooltip("How many times the drawn outline is rounded off. 0 = raw polygon")]
    [Range(0, 3)] public int lassoSmoothing = 2;
    [Tooltip("Shapes smaller than this (pixels^2) are ignored")]
    public float minLassoArea = 150f;
    [Tooltip("ON: only triangles facing the camera are cut, so the back side is never touched.")]
    public bool frontFacesOnly = true;
    [Tooltip("World units behind the nearest visible surface that can still be cut. 0 = unlimited. Stops far hidden surfaces (e.g. inside of a torus) from being cut.")]
    public float cutDepth = 1.5f;
    [Tooltip("Deletes pieces that are disconnected from the main body after a cut")]
    public bool removeLooseParts = true;
    public Color lassoColor = new Color(1f, 0.85f, 0.2f, 1f);
    public float lassoWidthPixels = 2.5f;

    [Header("Hole Edge (solid look)")]
    [Tooltip("How far the hole rim is intruded / the shell thickness, in world units")]
    public float thickness = 0.04f;
    [Tooltip("ON: builds an inner surface so the object is a real thick shell. OFF: only a lip around each hole.")]
    public bool solidShell = true;

    [Header("Quality / Performance")]
    [Tooltip("Target triangle edge size along the cut outline, in screen pixels (smaller = smoother hole, more triangles)")]
    public float edgePixels = 8f;
    [Tooltip("Pushes new vertices onto the curved surface (uses vertex normals). 0 = off")]
    [Range(0f, 1f)] public float curvatureSmoothing = 0.5f;
    public int maxRefinePasses = 12;
    public int maxTriangles = 250000;
    public bool recalculateTangents = false;

    // ------------------------------------------------------------------ State
    struct MidInfo { public Vector3 pos; public float t; public float sd; }

    MeshFilter meshFilter;
    MeshCollider meshCollider;
    Mesh mesh, sourceMesh;

    // Base surface (single layer, with holes). Everything else is derived from this.
    List<Vector3> V = new List<Vector3>();
    List<Vector3> N = new List<Vector3>();
    List<Vector2> UV = new List<Vector2>();
    List<int> W = new List<int>();   // welded position id per vertex (topology ignores UV/normal seams)
    List<int> T = new List<int>();

    // scratch buffers
    List<Vector3> V2 = new List<Vector3>(), N2 = new List<Vector3>();
    List<Vector2> UV2 = new List<Vector2>();
    List<int> W2 = new List<int>(), tmpT = new List<int>();
    List<Vector3> oV = new List<Vector3>(), oN = new List<Vector3>();
    List<Vector2> oUV = new List<Vector2>();
    List<int> oT = new List<int>();
    int[] remap;
    Vector3[] weldDir;

    // welding / topology
    Dictionary<Vector3Int, int> weldMap = new Dictionary<Vector3Int, int>();
    List<int> weldComp = new List<int>();                    // connected-component label per weld id
    int weldCount;
    float weldCell = 1e-5f;
    HashSet<long> openEdges = new HashSet<long>();           // boundary edges that existed in the source mesh (no walls there)
    Dictionary<long, int> edgeCount = new Dictionary<long, int>();
    Dictionary<long, int> edgeCorner = new Dictionary<long, int>();

    // per-cut caches (parallel to V)
    List<Vector3> P2 = new List<Vector3>();                  // x,y = screen pixels, z = view depth (<=0 : behind camera)
    List<float> SDv = new List<float>();                     // signed distance to lasso polygon, NaN = not computed yet
    Matrix4x4 viewFromLocal, proj;
    Rect pix;
    Vector3 camPosLocal, camFwdLocal;
    bool ortho, clipStage;
    float depthLimit;

    // lasso polygon (screen space)
    List<Vector2> lasso = new List<Vector2>();
    Vector2[] poly;
    Vector2 polyMin, polyMax;
    LineRenderer lassoLine;

    // refinement scratch
    HashSet<long> splitSet = new HashSet<long>();
    Dictionary<long, int> midCache = new Dictionary<long, int>();
    Dictionary<long, MidInfo> midInfo = new Dictionary<long, MidInfo>();

    // wall scratch
    Dictionary<int, int> wallTop = new Dictionary<int, int>();
    Dictionary<int, Vector3> wallNrm = new Dictionary<int, Vector3>();

    // loose-part scratch
    int[] uf;
    Dictionary<int, float> areaByRoot = new Dictionary<int, float>();
    Dictionary<int, int> bestRoot = new Dictionary<int, int>();
    Dictionary<int, float> bestArea = new Dictionary<int, float>();

    // interaction
    bool dirty, colliderDirty, dragging, justPressed;
    Vector2 lastMouse;
    Rect uiRect = new Rect(10, 10, 250, 120);
    static readonly string[] ModeLabels = { "Rotate [1]", "Cut [2]" };

    // ------------------------------------------------------------------ Unity
    void Awake()
    {
        if (!cam) cam = Camera.main;
        meshFilter = GetComponent<MeshFilter>();
        meshCollider = GetComponent<MeshCollider>();
        sourceMesh = meshFilter.sharedMesh;

        if (!sourceMesh)
        {
            Debug.LogError("MeshHoleCutter: no mesh on MeshFilter.", this);
            enabled = false; return;
        }
        if (!sourceMesh.isReadable)
        {
            Debug.LogError("MeshHoleCutter: enable Read/Write on the mesh import settings.", this);
            enabled = false; return;
        }

        mesh = new Mesh { name = sourceMesh.name + " (Cuttable)" };
        mesh.MarkDynamic();
        meshFilter.sharedMesh = mesh;
        LoadFromSource();
    }

    void OnDestroy()
    {
        if (lassoLine) Destroy(lassoLine.gameObject);
        if (mesh) Destroy(mesh);
    }

    void Update()
    {
        if (!cam) { cam = Camera.main; if (!cam) return; }

        Vector2 mp = CutInput.MousePosition;
        bool overUI = showUI && uiRect.Contains(new Vector2(mp.x, Screen.height - mp.y));

        if (CutInput.Key1) SetMode(Mode.Rotate);
        if (CutInput.Key2) SetMode(Mode.Cut);
        if (CutInput.Tab) SetMode(mode == Mode.Rotate ? Mode.Cut : Mode.Rotate);

        if (mode == Mode.Rotate) UpdateRotate(mp, overUI);
        else UpdateCut(mp, overUI);

        if (dirty) { RebuildMesh(); dirty = false; }
        if (colliderDirty && !dragging)
        {
            if (meshCollider) meshCollider.sharedMesh = mesh;
            colliderDirty = false;
        }
        lastMouse = mp;
    }

    void OnGUI()
    {
        if (!showUI) return;
        GUILayout.BeginArea(uiRect, GUI.skin.box);
        int sel = GUILayout.Toolbar((int)mode, ModeLabels);
        if (sel != (int)mode) SetMode((Mode)sel);

        GUILayout.Label(mode == Mode.Cut ? "Hold left mouse and draw a shape to cut it out" : "Hold left mouse + drag to rotate");
        if (GUILayout.Button("Reset mesh")) ResetMesh();
        GUILayout.Label("Triangles: " + (T.Count / 3));
        GUILayout.EndArea();
    }

    // ------------------------------------------------------------------ Public API
    public void SetMode(Mode m)
    {
        mode = m;
        dragging = false;
        lasso.Clear();
        HideLine();
    }

    public void ResetMesh()
    {
        if (!sourceMesh) return;
        LoadFromSource();
        dirty = false;
        colliderDirty = true;
    }

    // ------------------------------------------------------------------ Rotate mode
    void UpdateRotate(Vector2 mp, bool overUI)
    {
        if (CutInput.PressedThisFrame && !overUI) { dragging = true; justPressed = true; }

        if (dragging && CutInput.IsHeld)
        {
            if (!justPressed)
            {
                Vector2 d = mp - lastMouse;
                transform.Rotate(cam.transform.up, -d.x * rotateSpeed, Space.World);
                transform.Rotate(cam.transform.right, d.y * rotateSpeed, Space.World);
            }
            justPressed = false;
        }
        if (!CutInput.IsHeld) dragging = false;
    }

    // ------------------------------------------------------------------ Cut mode: lasso input
    void UpdateCut(Vector2 mp, bool overUI)
    {
        if (CutInput.PressedThisFrame && !overUI)
        {
            dragging = true;
            lasso.Clear();
            lasso.Add(mp);
            BeginLine(mp);
        }
        else if (dragging && CutInput.IsHeld)
        {
            if (Vector2.Distance(mp, lasso[lasso.Count - 1]) >= lassoPointSpacing)
            {
                lasso.Add(mp);
                AddLinePoint(mp);
            }
        }

        if (dragging && !CutInput.IsHeld)
        {
            dragging = false;
            FinishLasso();
        }
    }

    void FinishLasso()
    {
        HideLine();
        if (lasso.Count < 3) { lasso.Clear(); return; }

        List<Vector2> pts = new List<Vector2>(lasso);
        lasso.Clear();
        for (int i = 0; i < lassoSmoothing; i++) pts = Chaikin(pts);

        if (Mathf.Abs(SignedArea(pts)) < minLassoArea) return;

        poly = pts.ToArray();
        polyMin = polyMax = poly[0];
        float perimeter = 0f;
        for (int i = 0; i < poly.Length; i++)
        {
            polyMin = Vector2.Min(polyMin, poly[i]);
            polyMax = Vector2.Max(polyMax, poly[i]);
            perimeter += Vector2.Distance(poly[i], poly[(i + 1) % poly.Length]);
        }

        // small shapes get finer triangles so their outline never looks coarse
        float targetPx = Mathf.Clamp(perimeter / 64f, 1.5f, Mathf.Max(1.5f, edgePixels));
        PerformCut(targetPx);
    }

    static List<Vector2> Chaikin(List<Vector2> p)
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

    static float SignedArea(List<Vector2> p)
    {
        float a = 0f;
        for (int i = 0; i < p.Count; i++)
        {
            Vector2 u = p[i], v = p[(i + 1) % p.Count];
            a += u.x * v.y - v.x * u.y;
        }
        return a * 0.5f;
    }

    // ------------------------------------------------------------------ Cut pipeline
    void PerformCut(float targetPx)
    {
        if (T.Count / 3 >= maxTriangles) return;

        // camera data
        viewFromLocal = cam.worldToCameraMatrix * transform.localToWorldMatrix;
        proj = cam.projectionMatrix;
        pix = cam.pixelRect;
        ortho = cam.orthographic;
        camPosLocal = transform.InverseTransformPoint(cam.transform.position);
        camFwdLocal = transform.InverseTransformDirection(cam.transform.forward);

        // per-vertex caches
        P2.Clear(); SDv.Clear();
        for (int i = 0; i < V.Count; i++) { P2.Add(Project(V[i])); SDv.Add(float.NaN); }

        LabelComponents();

        // nearest visible surface inside the lasso -> depth window
        depthLimit = float.MaxValue;
        if (cutDepth > 0f)
        {
            float zNear = FindNearestDepthInsideShape();
            if (float.IsInfinity(zNear)) return;      // shape did not touch the object
            depthLimit = zNear + cutDepth;
        }

        RefineToPolygon(targetPx);   // stage 1: small triangles around the outline
        ClipPass();                  // stage 2: split edges exactly on the outline, drop what is inside
        if (removeLooseParts) RemoveLooseParts();

        dirty = true;
        colliderDirty = true;
    }

    float FindNearestDepthInsideShape()
    {
        float z = float.PositiveInfinity;
        for (int i = 0; i < T.Count; i += 3)
        {
            int a = T[i], b = T[i + 1], c = T[i + 2];
            if (!Eligible(a, b, c)) continue;
            if (GetSD(a) < 0f) z = Mathf.Min(z, P2[a].z);
            if (GetSD(b) < 0f) z = Mathf.Min(z, P2[b].z);
            if (GetSD(c) < 0f) z = Mathf.Min(z, P2[c].z);
        }

        if (float.IsInfinity(z))
        {
            // shape fits inside a single triangle: use a ray through its center
            Vector2 center = (polyMin + polyMax) * 0.5f;
            if (RaycastBase(cam.ScreenPointToRay(center), out Vector3 hit))
            {
                Vector3 p = Project(hit);
                if (p.z > 0f) z = p.z;
            }
        }
        return z;
    }

    // Stage 1 -----------------------------------------------------------
    void RefineToPolygon(float targetPx)
    {
        for (int pass = 0; pass < maxRefinePasses; pass++)
        {
            if (T.Count / 3 >= maxTriangles) break;

            splitSet.Clear();
            int count = T.Count;
            for (int i = 0; i < count; i += 3)
            {
                int a = T[i], b = T[i + 1], c = T[i + 2];
                if (!Eligible(a, b, c)) continue;

                Vector3 pa = P2[a], pb = P2[b], pc = P2[c];
                if (Mathf.Max(pa.x, pb.x, pc.x) < polyMin.x || Mathf.Min(pa.x, pb.x, pc.x) > polyMax.x ||
                    Mathf.Max(pa.y, pb.y, pc.y) < polyMin.y || Mathf.Min(pa.y, pb.y, pc.y) > polyMax.y) continue;

                float l0 = Dist2(pa, pb), l1 = Dist2(pb, pc), l2 = Dist2(pc, pa);
                float longest = Mathf.Max(l0, l1, l2);
                if (longest <= targetPx) continue;

                float sa = GetSD(a), sb = GetSD(b), sc = GetSD(c);
                float mn = Mathf.Min(sa, sb, sc), mx = Mathf.Max(sa, sb, sc);
                bool mixed = mn < 0f && mx > 0f;
                if (!mixed)
                {
                    if (mx <= 0f) continue;          // completely inside: will be removed whole
                    if (mn >= longest) continue;     // too far from the outline to be touched by it
                }

                float thr = longest * 0.7f;          // split edges close to the longest one -> well shaped triangles
                if (l0 >= thr) MarkSplit(a, b);
                if (l1 >= thr) MarkSplit(b, c);
                if (l2 >= thr) MarkSplit(c, a);
            }

            if (splitSet.Count == 0) break;
            ApplySplits(false);
        }
    }

    // Stage 2 -----------------------------------------------------------
    void ClipPass()
    {
        splitSet.Clear();
        int count = T.Count;
        for (int i = 0; i < count; i += 3)
        {
            int a = T[i], b = T[i + 1], c = T[i + 2];
            if (!Eligible(a, b, c)) continue;
            float sa = GetSD(a), sb = GetSD(b), sc = GetSD(c);
            if (Crosses(sa, sb)) MarkSplit(a, b);
            if (Crosses(sb, sc)) MarkSplit(b, c);
            if (Crosses(sc, sa)) MarkSplit(c, a);
        }
        ApplySplits(true);   // splits the crossing edges and discards everything inside the shape
    }

    static bool Crosses(float x, float y) { return (x < 0f && y > 0f) || (x > 0f && y < 0f); }
    static float Dist2(Vector3 a, Vector3 b) { float dx = a.x - b.x, dy = a.y - b.y; return Mathf.Sqrt(dx * dx + dy * dy); }

    // ------------------------------------------------------------------ Eligibility
    bool FacingOK(int a, int b, int c)
    {
        if (!frontFacesOnly) return true;
        Vector3 n = Vector3.Cross(V[b] - V[a], V[c] - V[a]);   // Unity: front face normal = cross(p1-p0, p2-p0)
        if (ortho) return Vector3.Dot(n, camFwdLocal) < 0f;
        Vector3 g = (V[a] + V[b] + V[c]) * (1f / 3f);
        return Vector3.Dot(n, camPosLocal - g) > 0f;
    }

    bool Eligible(int a, int b, int c)
    {
        Vector3 pa = P2[a], pb = P2[b], pc = P2[c];
        if (pa.z <= 0f || pb.z <= 0f || pc.z <= 0f) return false;
        if (!FacingOK(a, b, c)) return false;
        return (pa.z + pb.z + pc.z) * (1f / 3f) <= depthLimit;
    }

    // ------------------------------------------------------------------ Projection + signed distance
    Vector3 Project(Vector3 localPos)
    {
        Vector3 v = viewFromLocal.MultiplyPoint3x4(localPos);
        float depth = -v.z;
        Vector4 clip = proj * new Vector4(v.x, v.y, v.z, 1f);
        if (depth <= 1e-5f || clip.w <= 1e-5f) return new Vector3(0f, 0f, -1f);
        float nx = clip.x / clip.w, ny = clip.y / clip.w;
        return new Vector3(pix.x + (nx * 0.5f + 0.5f) * pix.width,
                           pix.y + (ny * 0.5f + 0.5f) * pix.height,
                           depth);
    }

    float GetSD(int i)
    {
        float s = SDv[i];
        if (!float.IsNaN(s)) return s;
        s = ComputeSD(P2[i]);
        SDv[i] = s;
        return s;
    }

    float ComputeSD(Vector3 p)
    {
        if (p.z <= 0f) return 1e6f;
        float dx = Mathf.Max(Mathf.Max(polyMin.x - p.x, p.x - polyMax.x), 0f);
        float dy = Mathf.Max(Mathf.Max(polyMin.y - p.y, p.y - polyMax.y), 0f);
        if (dx > 0f || dy > 0f) return Mathf.Sqrt(dx * dx + dy * dy);   // outside the bounding box -> outside the shape
        return PolySD(p.x, p.y);
    }

    float PolySD(float x, float y)
    {
        int n = poly.Length;
        bool inside = false;
        float best = float.MaxValue;
        Vector2 b = poly[n - 1];
        for (int i = 0; i < n; i++)
        {
            Vector2 a = b; b = poly[i];
            if ((a.y > y) != (b.y > y))
            {
                float xi = a.x + (y - a.y) * (b.x - a.x) / (b.y - a.y);
                if (x < xi) inside = !inside;
            }
            float ex = b.x - a.x, ey = b.y - a.y;
            float len2 = ex * ex + ey * ey;
            float t = len2 > 1e-12f ? Mathf.Clamp01(((x - a.x) * ex + (y - a.y) * ey) / len2) : 0f;
            float px = a.x + ex * t - x, py = a.y + ey * t - y;
            float d2 = px * px + py * py;
            if (d2 < best) best = d2;
        }
        float d = Mathf.Sqrt(best);
        return inside ? -d : d;
    }

    // ------------------------------------------------------------------ Edge splitting (crack-free)
    void MarkSplit(int a, int b)
    {
        int wa = W[a], wb = W[b];
        if (wa != wb) splitSet.Add(Key(wa, wb));
    }

    bool IsSplit(int a, int b)
    {
        int wa = W[a], wb = W[b];
        return wa != wb && splitSet.Contains(Key(wa, wb));
    }

    // Splits marked edges. The decision is per (welded) edge, so both triangles that share an edge split it
    // identically -> no cracks / T-junctions. In clip mode, eligible sub-triangles inside the shape are dropped.
    void ApplySplits(bool clip)
    {
        clipStage = clip;
        midCache.Clear();
        midInfo.Clear();
        tmpT.Clear();

        int count = T.Count;
        for (int i = 0; i < count; i += 3)
        {
            int a = T[i], b = T[i + 1], c = T[i + 2];
            bool elig = clip && Eligible(a, b, c);
            bool f0 = IsSplit(a, b), f1 = IsSplit(b, c), f2 = IsSplit(c, a);
            int n = (f0 ? 1 : 0) + (f1 ? 1 : 0) + (f2 ? 1 : 0);

            if (n == 0) { AddTriC(a, b, c, elig); continue; }

            if (n == 3)
            {
                int m0 = Mid(a, b), m1 = Mid(b, c), m2 = Mid(c, a);
                AddTriC(a, m0, m2, elig);
                AddTriC(m0, b, m1, elig);
                AddTriC(m2, m1, c, elig);
                AddTriC(m0, m1, m2, elig);
            }
            else if (n == 1)
            {
                int i0, i1, i2;
                if (f0) { i0 = a; i1 = b; i2 = c; }
                else if (f1) { i0 = b; i1 = c; i2 = a; }
                else { i0 = c; i1 = a; i2 = b; }
                int m = Mid(i0, i1);
                AddTriC(i0, m, i2, elig);
                AddTriC(m, i1, i2, elig);
            }
            else // n == 2 : rotate so that the un-split edge is (i2 -> i0); i1 is the isolated corner
            {
                int i0, i1, i2;
                if (!f0) { i2 = a; i0 = b; i1 = c; }
                else if (!f1) { i2 = b; i0 = c; i1 = a; }
                else { i2 = c; i0 = a; i1 = b; }

                int m01 = Mid(i0, i1), m12 = Mid(i1, i2);
                AddTriC(m01, i1, m12, elig);
                float dA = (V[i0] - V[m12]).sqrMagnitude;
                float dB = (V[m01] - V[i2]).sqrMagnitude;
                if (dA <= dB) { AddTriC(i0, m01, m12, elig); AddTriC(i0, m12, i2, elig); }
                else { AddTriC(i0, m01, i2, elig); AddTriC(m01, m12, i2, elig); }
            }
        }

        var swap = T; T = tmpT; tmpT = swap;
    }

    void AddTri(int a, int b, int c) { tmpT.Add(a); tmpT.Add(b); tmpT.Add(c); }

    void AddTriC(int a, int b, int c, bool eligible)
    {
        if (eligible && GetSD(a) + GetSD(b) + GetSD(c) < 0f) return;   // inside the cut shape -> removed
        AddTri(a, b, c);
    }

    // Point on edge lo->hi at parameter t, pulled toward the curved surface (Phong tessellation style)
    Vector3 EdgePoint(int lo, int hi, float t)
    {
        Vector3 pa = V[lo], pb = V[hi];
        Vector3 p = Vector3.Lerp(pa, pb, t);
        if (curvatureSmoothing > 0f)
        {
            Vector3 na = N[lo], nb = N[hi];
            Vector3 qa = p - Vector3.Dot(p - pa, na) * na;
            Vector3 qb = p - Vector3.Dot(p - pb, nb) * nb;
            p = Vector3.Lerp(p, Vector3.Lerp(qa, qb, t), curvatureSmoothing);
        }
        return p;
    }

    int Mid(int a, int b)
    {
        long k = Key(a, b);
        if (midCache.TryGetValue(k, out int m)) return m;

        int wa = W[a], wb = W[b];
        long wk = Key(wa, wb);
        bool aIsLo = wa < wb;
        int lo = aIsLo ? a : b, hi = aIsLo ? b : a;

        if (!midInfo.TryGetValue(wk, out MidInfo mi))
        {
            float t = 0.5f, sd = float.NaN;
            if (clipStage)
            {
                // find where the edge crosses the outline (bisection on the real projected signed distance)
                float sLo = GetSD(lo), sHi = GetSD(hi);
                bool loNeg = sLo < 0f;
                float tl = 0f, th = 1f;
                for (int it = 0; it < 8; it++)
                {
                    float tm = it == 0 ? Mathf.Clamp(sLo / (sLo - sHi), 0.05f, 0.95f) : 0.5f * (tl + th);
                    float f = ComputeSD(Project(EdgePoint(lo, hi, tm)));
                    if ((f < 0f) == loNeg) tl = tm; else th = tm;
                    t = tm; sd = f;
                }
            }
            mi = new MidInfo { pos = EdgePoint(lo, hi, t), t = t, sd = sd };
            midInfo[wk] = mi;   // shared by UV/normal-seam duplicates -> identical positions, no cracks
        }

        float tab = aIsLo ? mi.t : 1f - mi.t;
        Vector3 nrm = Vector3.Lerp(N[a], N[b], tab);
        nrm = nrm.sqrMagnitude > 1e-8f ? nrm.normalized : N[a];

        m = V.Count;
        V.Add(mi.pos);
        N.Add(nrm);
        UV.Add(Vector2.Lerp(UV[a], UV[b], tab));
        int wm = Weld(mi.pos);
        W.Add(wm);
        weldComp[wm] = weldComp[wa];
        P2.Add(Project(mi.pos));
        SDv.Add(mi.sd);

        // keep "original open boundary" info alive when such an edge is split
        if (openEdges.Contains(wk))
        {
            openEdges.Add(Key(wa, wm));
            openEdges.Add(Key(wm, wb));
        }

        midCache[k] = m;
        return m;
    }

    // ------------------------------------------------------------------ Loose parts
    void LabelComponents()
    {
        EnsureUF();
        for (int i = 0; i < weldCount; i++) { uf[i] = i; weldComp[i] = -1; }
        for (int i = 0; i < T.Count; i += 3)
        {
            Union(W[T[i]], W[T[i + 1]]);
            Union(W[T[i + 1]], W[T[i + 2]]);
        }
        for (int i = 0; i < T.Count; i++)
        {
            int w = W[T[i]];
            weldComp[w] = Find(w);
        }
    }

    // After a cut, every original body keeps only its largest connected piece.
    void RemoveLooseParts()
    {
        EnsureUF();
        for (int i = 0; i < weldCount; i++) uf[i] = i;
        for (int i = 0; i < T.Count; i += 3)
        {
            Union(W[T[i]], W[T[i + 1]]);
            Union(W[T[i + 1]], W[T[i + 2]]);
        }

        areaByRoot.Clear(); bestRoot.Clear(); bestArea.Clear();
        for (int i = 0; i < T.Count; i += 3)
        {
            int a = T[i], b = T[i + 1], c = T[i + 2];
            int r = Find(W[a]);
            float ar = Vector3.Cross(V[b] - V[a], V[c] - V[a]).magnitude * 0.5f;
            areaByRoot.TryGetValue(r, out float cur);
            areaByRoot[r] = cur + ar;
        }

        bool anyLoose = false;
        foreach (var kv in areaByRoot)
        {
            int label = weldComp[kv.Key];
            if (!bestArea.TryGetValue(label, out float ba))
            {
                bestArea[label] = kv.Value; bestRoot[label] = kv.Key;
            }
            else
            {
                anyLoose = true;
                if (kv.Value > ba) { bestArea[label] = kv.Value; bestRoot[label] = kv.Key; }
            }
        }
        if (!anyLoose) return;

        tmpT.Clear();
        for (int i = 0; i < T.Count; i += 3)
        {
            int w = W[T[i]];
            if (bestRoot[weldComp[w]] == Find(w)) AddTri(T[i], T[i + 1], T[i + 2]);
        }
        var swap = T; T = tmpT; tmpT = swap;
    }

    void EnsureUF()
    {
        if (uf == null || uf.Length < weldCount) uf = new int[weldCount + weldCount / 2 + 16];
    }

    int Find(int x)
    {
        while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; }
        return x;
    }

    void Union(int a, int b)
    {
        a = Find(a); b = Find(b);
        if (a != b) uf[a] = b;
    }

    // ------------------------------------------------------------------ Raycast against the live mesh (fallback only)
    bool RaycastBase(Ray worldRay, out Vector3 hit)
    {
        hit = Vector3.zero;
        Vector3 o = transform.InverseTransformPoint(worldRay.origin);
        Vector3 d = transform.InverseTransformVector(worldRay.direction);

        float best = float.MaxValue;
        for (int i = 0; i < T.Count; i += 3)
        {
            Vector3 v0 = V[T[i]];
            Vector3 e1 = V[T[i + 1]] - v0;
            Vector3 e2 = V[T[i + 2]] - v0;

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

            float t = Vector3.Dot(e2, q) * inv;
            if (t > 1e-4f && t < best) best = t;
        }
        if (best == float.MaxValue) return false;
        hit = o + d * best;
        return true;
    }

    // ------------------------------------------------------------------ Mesh build (base -> display mesh)
    void RebuildMesh()
    {
        Compact();
        float th = thickness / UniformScale();
        int vc = V.Count;

        // inward direction per welded vertex (area weighted, shared across seams -> watertight walls)
        if (weldDir == null || weldDir.Length < weldCount) weldDir = new Vector3[weldCount + 256];
        System.Array.Clear(weldDir, 0, weldCount);
        for (int i = 0; i < T.Count; i += 3)
        {
            int a = T[i], b = T[i + 1], c = T[i + 2];
            Vector3 fn = Vector3.Cross(V[b] - V[a], V[c] - V[a]);
            weldDir[W[a]] += fn; weldDir[W[b]] += fn; weldDir[W[c]] += fn;
        }
        for (int w = 0; w < weldCount; w++)
        {
            Vector3 d = weldDir[w];
            weldDir[w] = d.sqrMagnitude > 1e-20f ? -d.normalized : Vector3.zero;
        }

        oV.Clear(); oN.Clear(); oUV.Clear(); oT.Clear();

        // outer surface
        oV.AddRange(V); oN.AddRange(N); oUV.AddRange(UV); oT.AddRange(T);

        // inner surface (solidify)
        bool hasThickness = th > 1e-7f;
        if (solidShell && hasThickness)
        {
            for (int i = 0; i < vc; i++)
            {
                oV.Add(V[i] + weldDir[W[i]] * th);
                oN.Add(-N[i]);
                oUV.Add(UV[i]);
            }
            for (int i = 0; i < T.Count; i += 3)
            {
                oT.Add(vc + T[i]); oT.Add(vc + T[i + 2]); oT.Add(vc + T[i + 1]); // flipped
            }
        }

        // hole walls
        if (hasThickness) BuildWalls(th);

        mesh.Clear();
        mesh.indexFormat = oV.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.SetVertices(oV);
        mesh.SetNormals(oN);
        mesh.SetUVs(0, oUV);
        mesh.SetTriangles(oT, 0, true);
        if (recalculateTangents) mesh.RecalculateTangents();
    }

    void BuildWalls(float th)
    {
        CountEdges();
        wallTop.Clear();
        wallNrm.Clear();

        foreach (var kv in edgeCount)
        {
            if (kv.Value != 1 || openEdges.Contains(kv.Key)) continue;   // only hole boundaries

            int code = edgeCorner[kv.Key];
            int start = code >> 2, corner = code & 3;
            int ia = T[start + corner];
            int ib = T[start + (corner + 1) % 3];
            int ic = T[start + (corner + 2) % 3];

            Vector3 pa = V[ia], pb = V[ib], pc = V[ic];
            Vector3 e = pb - pa;
            Vector3 nTri = Vector3.Cross(e, pc - pa).normalized;
            Vector3 h = Vector3.Cross(nTri, e).normalized;                // in-plane, perpendicular to the edge
            if (Vector3.Dot(h, (pa + pb) * 0.5f - pc) < 0f) h = -h;      // point toward the hole

            int ta = WallVert(W[ia], ia, th);
            int tb = WallVert(W[ib], ib, th);
            wallNrm[W[ia]] += h;
            wallNrm[W[ib]] += h;

            AddOriented(ta, tb, tb + 1, h);
            AddOriented(ta, tb + 1, ta + 1, h);
        }

        foreach (var kv in wallTop)
        {
            Vector3 n = wallNrm[kv.Key];
            n = n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.up;
            oN[kv.Value] = n;
            oN[kv.Value + 1] = n;
        }
    }

    int WallVert(int w, int srcIndex, float th)
    {
        if (wallTop.TryGetValue(w, out int idx)) return idx;
        idx = oV.Count;
        Vector3 p = V[srcIndex];
        oV.Add(p);                    oN.Add(Vector3.up); oUV.Add(UV[srcIndex]);
        oV.Add(p + weldDir[w] * th);  oN.Add(Vector3.up); oUV.Add(UV[srcIndex]);
        wallTop[w] = idx;
        wallNrm[w] = Vector3.zero;
        return idx;
    }

    void AddOriented(int i, int j, int k, Vector3 wantNormal)
    {
        Vector3 n = Vector3.Cross(oV[j] - oV[i], oV[k] - oV[i]);
        if (Vector3.Dot(n, wantNormal) < 0f) { int t = j; j = k; k = t; }
        oT.Add(i); oT.Add(j); oT.Add(k);
    }

    // Removes vertices no triangle uses any more
    void Compact()
    {
        int vc = V.Count;
        if (remap == null || remap.Length < vc) remap = new int[vc + vc / 2 + 16];
        for (int i = 0; i < vc; i++) remap[i] = -1;

        V2.Clear(); N2.Clear(); UV2.Clear(); W2.Clear();
        for (int i = 0; i < T.Count; i++)
        {
            int o = T[i];
            int n = remap[o];
            if (n < 0)
            {
                n = V2.Count;
                remap[o] = n;
                V2.Add(V[o]); N2.Add(N[o]); UV2.Add(UV[o]); W2.Add(W[o]);
            }
            T[i] = n;
        }

        var a = V; V = V2; V2 = a;
        var b = N; N = N2; N2 = b;
        var c = UV; UV = UV2; UV2 = c;
        var d = W; W = W2; W2 = d;
    }

    // ------------------------------------------------------------------ Loading / topology helpers
    void LoadFromSource()
    {
        Vector3[] vs = sourceMesh.vertices;
        Vector3[] ns = sourceMesh.normals;
        Vector2[] uvs = sourceMesh.uv;
        int[] tris = sourceMesh.triangles;

        if (ns.Length != vs.Length)
        {
            var tmp = Instantiate(sourceMesh);
            tmp.RecalculateNormals();
            ns = tmp.normals;
            Destroy(tmp);
        }
        if (uvs.Length != vs.Length) uvs = new Vector2[vs.Length];

        V.Clear(); N.Clear(); UV.Clear(); W.Clear(); T.Clear();
        weldMap.Clear(); weldComp.Clear(); weldCount = 0; openEdges.Clear();
        weldCell = Mathf.Max(sourceMesh.bounds.size.magnitude * 1e-5f, 1e-7f);

        for (int i = 0; i < vs.Length; i++)
        {
            V.Add(vs[i]); N.Add(ns[i]); UV.Add(uvs[i]); W.Add(Weld(vs[i]));
        }
        T.AddRange(tris);

        // remember edges that were already open in the source mesh (e.g. a plane's border)
        CountEdges();
        foreach (var kv in edgeCount) if (kv.Value == 1) openEdges.Add(kv.Key);

        RebuildMesh();
    }

    void CountEdges()
    {
        edgeCount.Clear();
        edgeCorner.Clear();
        for (int i = 0; i < T.Count; i += 3)
        {
            for (int c = 0; c < 3; c++)
            {
                int wa = W[T[i + c]];
                int wb = W[T[i + (c + 1) % 3]];
                if (wa == wb) continue;
                long k = Key(wa, wb);
                edgeCount.TryGetValue(k, out int n);
                edgeCount[k] = n + 1;
                edgeCorner[k] = (i << 2) | c;
            }
        }
    }

    int Weld(Vector3 p)
    {
        var key = new Vector3Int(
            Mathf.RoundToInt(p.x / weldCell),
            Mathf.RoundToInt(p.y / weldCell),
            Mathf.RoundToInt(p.z / weldCell));
        if (!weldMap.TryGetValue(key, out int id))
        {
            id = weldCount++;
            weldMap[key] = id;
            weldComp.Add(-1);
        }
        return id;
    }

    static long Key(int a, int b)
    {
        return a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
    }

    float UniformScale()
    {
        Vector3 s = transform.lossyScale;
        return Mathf.Max((Mathf.Abs(s.x) + Mathf.Abs(s.y) + Mathf.Abs(s.z)) / 3f, 1e-6f);
    }

    // ------------------------------------------------------------------ Lasso line preview
    void EnsureLine()
    {
        if (lassoLine) return;
        Shader sh = Shader.Find("Sprites/Default");
        if (!sh) return;

        var go = new GameObject("LassoPreview");
        go.hideFlags = HideFlags.DontSave;
        lassoLine = go.AddComponent<LineRenderer>();
        lassoLine.loop = true;                  // shows the closing edge, i.e. what will be cut
        lassoLine.useWorldSpace = true;
        lassoLine.numCornerVertices = 3;
        lassoLine.material = new Material(sh);
        lassoLine.shadowCastingMode = ShadowCastingMode.Off;
        lassoLine.receiveShadows = false;
    }

    void BeginLine(Vector2 p)
    {
        EnsureLine();
        if (!lassoLine) return;
        lassoLine.enabled = true;
        lassoLine.positionCount = 0;
        lassoLine.startColor = lassoLine.endColor = lassoColor;
        AddLinePoint(p);
    }

    void AddLinePoint(Vector2 p)
    {
        if (!lassoLine) return;
        float d = cam.nearClipPlane + 0.1f;
        Vector3 w = cam.ScreenToWorldPoint(new Vector3(p.x, p.y, d));
        int n = lassoLine.positionCount;
        lassoLine.positionCount = n + 1;
        lassoLine.SetPosition(n, w);

        float pxWorld = cam.orthographic
            ? 2f * cam.orthographicSize / cam.pixelHeight
            : 2f * d * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / cam.pixelHeight;
        lassoLine.widthMultiplier = lassoWidthPixels * pxWorld;
    }

    void HideLine()
    {
        if (lassoLine) { lassoLine.positionCount = 0; lassoLine.enabled = false; }
    }
}

/// <summary>Works with the old Input Manager and the new Input System package.</summary>
static class CutInput
{
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
    public static Vector2 MousePosition => Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
    public static bool PressedThisFrame => Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
    public static bool IsHeld => Mouse.current != null && Mouse.current.leftButton.isPressed;
    public static bool Key1 => Keyboard.current != null && Keyboard.current.digit1Key.wasPressedThisFrame;
    public static bool Key2 => Keyboard.current != null && Keyboard.current.digit2Key.wasPressedThisFrame;
    public static bool Tab => Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame;
#else
    public static Vector2 MousePosition => Input.mousePosition;
    public static bool PressedThisFrame => Input.GetMouseButtonDown(0);
    public static bool IsHeld => Input.GetMouseButton(0);
    public static bool Key1 => Input.GetKeyDown(KeyCode.Alpha1);
    public static bool Key2 => Input.GetKeyDown(KeyCode.Alpha2);
    public static bool Tab => Input.GetKeyDown(KeyCode.Tab);
#endif
}