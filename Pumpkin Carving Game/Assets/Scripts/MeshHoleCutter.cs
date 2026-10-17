using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.InputSystem;

/// <summary>
/// Runtime lasso mesh cutter + scrape brush.
///
/// MODES
///   [1] Rotate : left-mouse drag rotates the object.
///   [2] Cut    : left-mouse drag draws a shape; on release the area inside the shape is cut out.
///   [3] Scrape : left-mouse drag scrapes the skin: paints the light inner color and a generated normal map so it looks indented.
///   [Tab]      : cycles through the modes.
///
/// HOW CUTTING WORKS
///   1. Your mouse path is closed into a polygon and rounded (Chaikin smoothing).
///   2. Every vertex gets a signed distance to that polygon in SCREEN space (negative = inside).
///   3. Triangles near the outline are subdivided until they are only a few pixels wide.
///   4. Every edge that crosses the outline is split exactly ON the outline (marching-triangles style),
///      so the hole edge follows your drawn shape smoothly instead of being stair-stepped.
///   5. Front-facing triangles inside the shape are removed. Back faces are never touched.
///   6. Pieces that got disconnected from the main body are deleted ("loose parts").
///   7. Hole borders get an inward wall (+ optional inner shell) for a solid, thick look.
///
/// HOW SCRAPING WORKS
///   The brush raycasts the surface and gets the UV under the cursor. It stamps soft circles into a height map and a
///   mask in UV space (CPU). From those it rebuilds two runtime textures: albedo (original color blended toward the
///   inner color) and a normal map (from the height map gradient). Strokes are stored in the same edit history as cuts,
///   so undo / redo / save / load / verify all work.
///
/// INNER MATERIAL
///   Assign "Inner Material" and the inner shell (and optionally the hole walls) become a second submesh that uses it.
///   The renderer gets its second material slot automatically.
///
/// SETUP
///   - Put on an object that has MeshFilter + MeshRenderer.
///   - The mesh must have Read/Write enabled (Unity primitives already do).
///   - Use a uniform scale on the object.
///   - Scrape needs a UV unwrap WITHOUT overlapping islands (mirrored / overlapping UVs paint in several places at once).
///   - Scrape supports the Standard shader and URP Lit (_MainTex/_BaseMap + _BumpMap).
///   - Add "Scrape" to your EditTool enum.
/// </summary>
[DisallowMultipleComponent]
public class MeshHoleCutter : MonoBehaviour
{
    public enum Mode { Rotate = 0, Cut = 1, Scrape = 2 }

    // ------------------------------------------------------------------ Inspector
    [Header("General")]
    public Camera cam;
    public Mode mode = Mode.Rotate;
    public bool showUI = true;
    [Tooltip("The pumpkin GameObject whose mesh should be cut/rotated. Falls back to this GameObject.")]
    public GameObject targetPumpkin;
    public bool canCut = true;

    [Header("Rotate Mode")]
    [Tooltip("Degrees per pixel of mouse drag")]
    public float rotateSpeed = 0.4f;
    [Tooltip("Clamp the pumpkin rotation around the X axis when using rotate mode.")]
    public float minRotationX = -25f;
    [Tooltip("Clamp the pumpkin rotation around the X axis when using rotate mode.")]
    public float maxRotationX = 25f;
    [Tooltip("Multiplier for horizontal camera movement while dragging outside the pumpkin.")]
    public float cameraPanSpeed = 0.9f;
    [Tooltip("Left/right camera pan limit in world units.")]
    public float minCameraX = -4f;
    [Tooltip("Left/right camera pan limit in world units.")]
    public float maxCameraX = 4f;

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

    [Header("Scrape Mode (detail brush)")]
    [Tooltip("Light pumpkin-flesh color the scraped area is painted with.")]
    public Color scrapeColor = new Color(1f, 0.82f, 0.45f, 1f);
    [Tooltip("Brush radius in world units.")]
    public float scrapeRadius = 0.08f;
    [Tooltip("0 = soft brush, 1 = hard edged brush.")]
    [Range(0f, 0.95f)] public float scrapeHardness = 0.5f;
    [Tooltip("How deep the scrape is (stored per stroke).")]
    [Range(0.05f, 1f)] public float scrapeDepth = 0.6f;
    [Tooltip("Roughness of the scraped floor, like tool marks (stored per stroke).")]
    [Range(0f, 1f)] public float scrapeGrain = 0.25f;
    [Tooltip("How strong the indent looks in the normal map (look only, applies to all strokes).")]
    [Range(0.1f, 4f)] public float normalStrength = 1f;
    [Tooltip("Darkens the walls of the scrape a bit so it reads as a cavity (look only).")]
    [Range(0f, 1f)] public float scrapeRimDarken = 0.35f;
    [Tooltip("Resolution of the generated paint textures. 1024 is good for desktop, 512 for mobile.")]
    public int paintTextureSize = 1024;

    [Header("Hole Edge (solid look)")]
    [Tooltip("How far the hole rim is intruded / the shell thickness, in world units")]
    public float thickness = 0.04f;
    [Tooltip("ON: builds an inner surface so the object is a real thick shell. OFF: only a lip around each hole.")]
    public bool solidShell = true;

    [Header("Inner Material")]
    [Tooltip("Material for the inside of the pumpkin and the cut rims. Leave empty to use the normal material everywhere.")]
    public Material innerMaterial;
    [Tooltip("ON: the walls around cut holes also use the inner material. OFF: they keep the outer material.")]
    public bool wallsUseInnerMaterial = true;

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
    Transform rotationRoot;
    bool draggingPumpkin;

    // Turntable rotation state: rotation is rebuilt from these every frame, so it never accumulates roll.
    Quaternion baseRotation = Quaternion.identity;
    float totalYaw;
    float totalPitch;

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
    List<int> oT2 = new List<int>();     // triangles of the inner submesh (only used when innerMaterial is set)
    List<int> wallTris;                  // where BuildWalls writes its triangles
    bool addedInnerSlot;
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

    // scrape / paint layer
    const float SeamBreak = 8f;                              // UV jump bigger than this many brush radii = new island, don't connect
    MeshRenderer paintRenderer;
    Material paintMat;
    Texture2D albedoTex, normalTex;
    Color32[] baseCol;                                       // original albedo (tint baked in)
    float[] heightMap;                                       // 0 = surface, -1 = deepest scrape
    float[] maskMap;                                         // 0..1 how much the inner color shows
    int pw, ph;
    bool paintReady, paintDirty, anyPaint;
    int dirtyMinX, dirtyMinY, dirtyMaxX, dirtyMaxY;
    float sHard, sGrain, sDepth;                             // stamp parameters of the stroke being executed
    bool scraping;
    Vector2 scrapeLastScreen;
    float lastRadiusUV = 0.03f;
    List<Vector2> strokeUV = new List<Vector2>();
    List<float> strokeR = new List<float>();
    List<Vector3> strokeP3 = new List<Vector3>();

    // interaction
    bool dirty, colliderDirty, dragging, justPressed;
    Vector2 lastMouse;
    Rect uiRect = new Rect(10, 10, 300, 140);
    static readonly string[] ModeLabels = { "Rotate [1]", "Cut [2]", "Scrape [3]" };

    // edit history
    List<MeshEdit> history = new List<MeshEdit>();
    int cursor;                          // number of edits currently applied (undo / redo position)
    int sourceTriCount;
    bool removedAny;
    List<Vector3> outline3D = new List<Vector3>();
    CameraSnapshot activeCam;            // camera of the edit being executed
    Matrix4x4 invVP;
    struct EditResult { public bool changed; public float areaRemoved; public int trisBefore, trisAfter; }

    // ------------------------------------------------------------------ Unity
    void Awake()
    {
        if (!cam) cam = Camera.main;
        if (targetPumpkin) SetTargetPumpkin(targetPumpkin);
    }

    public void SetTargetPumpkin(GameObject pumpkin)
    {
        if (!pumpkin) return;

        // a different pumpkin gets its own paint layer
        if (paintReady && (!paintRenderer || paintRenderer.gameObject != pumpkin)) DetachPaintLayer();

        targetPumpkin = pumpkin;
        rotationRoot = targetPumpkin.transform;
        baseRotation = rotationRoot.localRotation;
        totalYaw = 0f;
        totalPitch = 0f;
        meshFilter = targetPumpkin.GetComponent<MeshFilter>();
        meshCollider = targetPumpkin.GetComponent<MeshCollider>();
        sourceMesh = meshFilter ? meshFilter.sharedMesh : null;

        if (!sourceMesh)
        {
            Debug.LogError("MeshHoleCutter: no mesh on target pumpkin MeshFilter.", this);
            return;
        }
        if (!sourceMesh.isReadable)
        {
            Debug.LogError("MeshHoleCutter: enable Read/Write on the mesh import settings.", this);
            return;
        }

        if (meshFilter && meshFilter.sharedMesh != mesh) meshFilter.sharedMesh = mesh;
        if (meshCollider && meshCollider.sharedMesh != mesh)
        {
            meshCollider.sharedMesh = mesh;
        }

        if (!mesh)
        {
            mesh = new Mesh { name = sourceMesh.name + " (Cuttable)" };
            mesh.MarkDynamic();
        }
        meshFilter.sharedMesh = mesh;
        LoadFromSource();
    }

    void OnDestroy()
    {
        if (lassoLine) Destroy(lassoLine.gameObject);
        if (mesh) Destroy(mesh);
        if (albedoTex) Destroy(albedoTex);
        if (normalTex) Destroy(normalTex);
        if (paintMat) Destroy(paintMat);
    }

    void Update()
    {
        if (!cam) { cam = Camera.main; if (!cam) return; }
        bool hasCuttableMesh = targetPumpkin && rotationRoot && HasCuttableMesh();

        Vector2 mp = CutInput.MousePosition;
        Vector2 guiMouse = new Vector2(mp.x, Screen.height - mp.y);
        bool overUI = (showUI && uiRect.Contains(guiMouse)) || extraBlockedRect.Contains(guiMouse);

        if (CutInput.Key1) SetMode(Mode.Rotate);
        if (CutInput.Key2) SetMode(Mode.Cut);
        if (CutInput.Key3) SetMode(Mode.Scrape);
        if (CutInput.Tab) SetMode((Mode)(((int)mode + 1) % 3));
        if (!dragging)
        {
            if (CutInput.UndoPressed) Undo();
            if (CutInput.RedoPressed) Redo();
        }

        if (mode == Mode.Rotate || !hasCuttableMesh) UpdateRotate(mp, overUI, hasCuttableMesh);
        else if (mode == Mode.Cut) UpdateCut(mp, overUI);
        else UpdateScrape(mp, overUI);

        if (hasCuttableMesh && dirty) { RebuildMesh(); dirty = false; }
        if (hasCuttableMesh && colliderDirty && !dragging)
        {
            if (meshCollider) meshCollider.sharedMesh = mesh;
            colliderDirty = false;
        }
        FlushPaint();
        lastMouse = mp;
    }
    void OnGUI()
    {
        if (!showUI) return;
        GUILayout.BeginArea(uiRect, GUI.skin.box);
        int sel = GUILayout.Toolbar((int)mode, ModeLabels);
        if (sel != (int)mode) SetMode((Mode)sel);

        string hint;
        if (!HasCuttableMesh()) hint = "No cuttable mesh; hold left mouse + drag to pan";
        else if (mode == Mode.Cut) hint = "Hold left mouse and draw a shape to cut it out";
        else if (mode == Mode.Scrape) hint = "Hold left mouse and drag to scrape the skin";
        else hint = "Hold left mouse + drag to rotate";
        GUILayout.Label(hint);

        GUILayout.BeginHorizontal();
        GUI.enabled = CanUndo;
        if (GUILayout.Button("Undo")) Undo();
        GUI.enabled = CanRedo;
        if (GUILayout.Button("Redo")) Redo();
        GUI.enabled = true;
        if (GUILayout.Button("Reset mesh")) ResetMesh();
        GUILayout.EndHorizontal();
        GUILayout.Label("Triangles: " + (T.Count / 3) + "   Edits: " + cursor + "/" + history.Count);
        GUILayout.EndArea();
    }

    // ------------------------------------------------------------------ Public API
    public void SetMode(Mode m)
    {
        if (scraping) { scraping = false; FinishScrape(); }
        mode = m;
        dragging = false;
        lasso.Clear();
        HideLine();
    }

    public void ResetMesh()
    {
        if (!HasCuttableMesh()) return;
        history.Clear();
        cursor = 0;
        HistoryVersion++;
        LoadFromSource();
        dirty = false;
        colliderDirty = true;
    }

    // ------------------------------------------------------------------ Edit history (record / undo / redo / save / load / verify)
    public IReadOnlyList<MeshEdit> History { get { return history; } }
    public Mesh SourceMesh { get { return sourceMesh; } }
    public int HistoryVersion { get; private set; }     // changes whenever the applied edits change (cut, undo, redo, import, reset)
    [HideInInspector] public Rect extraBlockedRect;     // other IMGUI panels set this (GUI coordinates) so clicks on them don't start a cut / rotation
    public int AppliedEditCount { get { return cursor; } }
    public bool CanUndo { get { return HasCuttableMesh() && cursor > 0; } }
    public bool CanRedo { get { return HasCuttableMesh() && cursor < history.Count; } }

    public void Undo() { if (CanUndo) Replay(cursor - 1); }
    public void Redo() { if (CanRedo) Replay(cursor + 1); }

    bool HasCuttableMesh()
    {
        return meshFilter && mesh && sourceMesh && sourceMesh.isReadable;
    }

    // Rebuilds the mesh from the original and re-applies the first `count` edits
    void Replay(int count)
    {
        LoadFromSource();
        for (int i = 0; i < count; i++) Execute(history[i]);
        cursor = count;
        HistoryVersion++;
        dirty = true;
        colliderDirty = true;
    }

    /// <summary>JSON of all applied edits. Put it in a file, PlayerPrefs, a server...</summary>
    public string ExportLog(bool prettyPrint = false)
    {
        var log = new MeshEditLog
        {
            meshName = sourceMesh.name,
            sourceVertexCount = sourceMesh.vertexCount,
            sourceTriangleCount = sourceTriCount,
            edits = history.GetRange(0, cursor)
        };
        return JsonUtility.ToJson(log, prettyPrint);
    }

    /// <summary>Loads a log and rebuilds the mesh from it.</summary>
    public bool ImportLog(string json)
    {
        MeshEditLog log = JsonUtility.FromJson<MeshEditLog>(json);
        if (log == null || log.edits == null) return false;
        if (log.sourceVertexCount != sourceMesh.vertexCount || log.sourceTriangleCount != sourceTriCount)
        {
            Debug.LogWarning("MeshHoleCutter: this log was recorded on a different mesh.");
            return false;
        }
        history = log.edits;
        Replay(history.Count);
        return true;
    }

    public void SaveLog(string path) { System.IO.File.WriteAllText(path, ExportLog(true)); }
    public bool LoadLog(string path) { return System.IO.File.Exists(path) && ImportLog(System.IO.File.ReadAllText(path)); }

    /// <summary>
    /// Replays the whole history on a fresh copy of the source mesh and checks that every edit
    /// removes the same surface area that was stored with it. The mesh ends up fully replayed.
    /// </summary>
    public bool VerifyHistory(float relativeTolerance = 1e-3f)
    {
        LoadFromSource();
        bool ok = true;
        for (int i = 0; i < history.Count; i++)
        {
            EditResult r = Execute(history[i]);
            float stored = history[i].areaRemoved;
            if (!r.changed || Mathf.Abs(r.areaRemoved - stored) > relativeTolerance * Mathf.Max(stored, 1e-6f))
            {
                ok = false;
                Debug.LogWarning("MeshHoleCutter: edit " + i + " does not reproduce (stored area " + stored + ", replayed " + r.areaRemoved + ")");
            }
        }
        cursor = history.Count;
        HistoryVersion++;
        dirty = true;
        colliderDirty = true;
        return ok;
    }

    /// <summary>Compares the drawn outlines of two recorded edits.</summary>
    public ShapeScore CompareEdits(int a, int b, bool alignShapes = false)
    {
        return EditCompare.Compare2D(history[a].points2D, history[b].points2D, alignShapes);
    }

    /// <summary>Compares the 3D hole outlines of two recorded edits (independent of camera angle).</summary>
    public float CompareOutlines3D(int a, int b, out float mean, out float max)
    {
        return EditCompare.Chamfer3D(history[a].points3D, history[b].points3D, out mean, out max);
    }

    // Every tool goes through Execute: live input and replay run exactly the same code.
    EditResult Execute(MeshEdit e)
    {
        switch (e.tool)
        {
            case EditTool.LassoCut: return ExecuteLasso(e);
            case EditTool.Scrape: return ExecuteScrape(e);
            // case EditTool.YourNewTool: return ExecuteYourNewTool(e);
            default:
                Debug.LogWarning("MeshHoleCutter: unknown edit tool " + e.tool);
                return new EditResult();
        }
    }

    // Applies a new edit and records it if it actually changed the mesh.
    // alreadyApplied: the edit was already executed live (scrape strokes), only record it.
    void Commit(MeshEdit e, bool alreadyApplied = false)
    {
        EditResult r;
        if (alreadyApplied)
        {
            r = new EditResult { changed = true, trisBefore = T.Count / 3, trisAfter = T.Count / 3 };
        }
        else
        {
            r = Execute(e);
        }
        if (!r.changed) return;

        e.areaRemoved = r.areaRemoved;
        e.trianglesBefore = r.trisBefore;
        e.trianglesAfter = r.trisAfter;
        e.points3D = outline3D.ToArray();
        e.time = Time.realtimeSinceStartup;

        if (cursor < history.Count) history.RemoveRange(cursor, history.Count - cursor);   // new edit drops the redo branch
        history.Add(e);
        cursor = history.Count;
        HistoryVersion++;
    }

    EditResult ExecuteLasso(MeshEdit e)
    {
        var r = new EditResult();
        float[] v = e.values;
        if (e.points2D == null || e.points2D.Length < 3 || v == null || v.Length < LassoValues.Count) return r;

        // use the settings stored with this edit, so a replay is identical even if the inspector changed
        bool sFront = frontFacesOnly, sLoose = removeLooseParts;
        float sDepthCut = cutDepth, sCurv = curvatureSmoothing, sEdge = edgePixels;
        int sPasses = maxRefinePasses;
        frontFacesOnly = v[LassoValues.FrontOnly] > 0.5f;
        removeLooseParts = v[LassoValues.RemoveLoose] > 0.5f;
        cutDepth = v[LassoValues.Depth];
        curvatureSmoothing = v[LassoValues.Curvature];
        edgePixels = v[LassoValues.EdgePx];
        maxRefinePasses = Mathf.RoundToInt(v[LassoValues.MaxPasses]);

        // snapshot so a cut that removes nothing can be rolled back completely
        var sV = new List<Vector3>(V); var sN = new List<Vector3>(N); var sUV = new List<Vector2>(UV);
        var sW = new List<int>(W); var sT = new List<int>(T);

        removedAny = false;
        outline3D.Clear();
        activeCam = e.camera;
        r.trisBefore = T.Count / 3;
        float areaBefore = SurfaceArea();

        CutWithPolygon(e);

        frontFacesOnly = sFront; removeLooseParts = sLoose;
        cutDepth = sDepthCut; curvatureSmoothing = sCurv; edgePixels = sEdge; maxRefinePasses = sPasses;

        if (!removedAny)
        {
            V = sV; N = sN; UV = sUV; W = sW; T = sT;
            return r;
        }

        r.changed = true;
        r.trisAfter = T.Count / 3;
        r.areaRemoved = areaBefore - SurfaceArea();
        return r;
    }

    CameraSnapshot CaptureCamera()
    {
        return new CameraSnapshot
        {
            viewFromLocal = cam.worldToCameraMatrix * meshFilter.transform.localToWorldMatrix,
            projection = cam.projectionMatrix,
            pixelRect = cam.pixelRect,
            orthographic = cam.orthographic
        };
    }

    // surface area of the base mesh in world units^2
    float SurfaceArea()
    {
        float a = 0f;
        for (int i = 0; i < T.Count; i += 3)
            a += Vector3.Cross(V[T[i + 1]] - V[T[i]], V[T[i + 2]] - V[T[i]]).magnitude * 0.5f;
        float s = UniformScale();
        return a * s * s;
    }

    // screen point + NDC depth (-1 near, +1 far) -> object-local position
    Vector3 Unproject(Vector2 sp, float ndcZ)
    {
        float nx = (sp.x - pix.x) / pix.width * 2f - 1f;
        float ny = (sp.y - pix.y) / pix.height * 2f - 1f;
        Vector4 p = invVP * new Vector4(nx, ny, ndcZ, 1f);
        return new Vector3(p.x, p.y, p.z) / p.w;
    }

    // ------------------------------------------------------------------ Rotate mode
    void UpdateRotate(Vector2 mp, bool overUI, bool canRotatePumpkin)
    {
        if (CutInput.PressedThisFrame && !overUI)
        {
            dragging = true;
            justPressed = true;
            draggingPumpkin = canRotatePumpkin && PointerOverTargetPumpkin(mp);

            if (draggingPumpkin)
            {
                // Re-sync with whatever the pumpkin's real rotation is right now
                // (spawned later, rotated by another script, reparented, etc.),
                // while keeping the accumulated yaw/pitch so the clamp still works.
                Quaternion applied =
                    Quaternion.AngleAxis(totalPitch, Vector3.right) *
                    Quaternion.AngleAxis(totalYaw, Vector3.up);
                baseRotation = Quaternion.Inverse(applied) * rotationRoot.localRotation;
            }
        }

        if (dragging && CutInput.IsHeld)
        {
            if (!justPressed)
            {
                Vector2 d = mp - lastMouse;
                if (draggingPumpkin)
                {
                    totalYaw -= d.x * rotateSpeed;
                    totalPitch = Mathf.Clamp(totalPitch + d.y * rotateSpeed, minRotationX, maxRotationX);

                    // Pitch is outermost (world right), yaw spins the object around its own up axis.
                    // Rebuilt from the base rotation every time, so nothing accumulates into roll.
                    rotationRoot.localRotation =
                        Quaternion.AngleAxis(totalPitch, Vector3.right) *
                        Quaternion.AngleAxis(totalYaw, Vector3.up) *
                        baseRotation;
                }
                else if (cam != null && cam.pixelHeight > 0)
                {
                    float depth = targetPumpkin
                        ? Mathf.Max(Vector3.Dot(targetPumpkin.transform.position - cam.transform.position, cam.transform.forward), cam.nearClipPlane)
                        : Mathf.Max(1f, cam.nearClipPlane);
                    float worldUnitsPerPixel = cam.orthographic
                        ? 2f * cam.orthographicSize / cam.pixelHeight
                        : 2f * depth * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / cam.pixelHeight;

                    Vector3 pos = cam.transform.position;
                    pos.x = Mathf.Clamp(
                        pos.x - d.x * worldUnitsPerPixel * cameraPanSpeed,
                        minCameraX,
                        maxCameraX);
                    cam.transform.position = pos;
                }
            }
            justPressed = false;
        }
        if (!CutInput.IsHeld)
        {
            dragging = false;
            draggingPumpkin = false;
        }
    }

    bool PointerOverTargetPumpkin(Vector2 screenPosition)
    {
        Ray ray = cam.ScreenPointToRay(screenPosition);
        if (!Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            return false;

        Transform hitTransform = hit.collider.transform;
        return hitTransform == targetPumpkin.transform || hitTransform.IsChildOf(targetPumpkin.transform);
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
            if (canCut)
            {
                FinishLasso();
            }
            else
            {
                HideLine();
                lasso.Clear();
            }
        }
    }

    void FinishLasso()
    {
        HideLine();
        if (lasso.Count < 3) { lasso.Clear(); return; }

        // input filter: ignore tiny shapes
        List<Vector2> preview = new List<Vector2>(lasso);
        for (int i = 0; i < lassoSmoothing; i++) preview = Chaikin(preview);
        if (Mathf.Abs(SignedArea(preview)) < minLassoArea) { lasso.Clear(); return; }

        // record what the player did; Commit applies it and stores it in the history
        var e = new MeshEdit
        {
            tool = EditTool.LassoCut,
            points2D = lasso.ToArray(),
            values = new float[LassoValues.Count],
            camera = CaptureCamera()
        };
        e.values[LassoValues.Smoothing] = lassoSmoothing;
        e.values[LassoValues.FrontOnly] = frontFacesOnly ? 1f : 0f;
        e.values[LassoValues.Depth] = cutDepth;
        e.values[LassoValues.EdgePx] = edgePixels;
        e.values[LassoValues.Curvature] = curvatureSmoothing;
        e.values[LassoValues.RemoveLoose] = removeLooseParts ? 1f : 0f;
        e.values[LassoValues.MaxPasses] = maxRefinePasses;
        lasso.Clear();
        Commit(e);
    }

    // Cuts the polygon stored in the edit (settings were already applied by ExecuteLasso)
    void CutWithPolygon(MeshEdit e)
    {
        List<Vector2> pts = new List<Vector2>(e.points2D);
        int smooth = Mathf.RoundToInt(e.values[LassoValues.Smoothing]);
        for (int i = 0; i < smooth; i++) pts = Chaikin(pts);

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

    // ------------------------------------------------------------------ Scrape mode: input
    void UpdateScrape(Vector2 mp, bool overUI)
    {
        if (CutInput.PressedThisFrame && !overUI && EnsurePaintLayer())
        {
            scraping = true;
            dragging = true;
            strokeUV.Clear(); strokeR.Clear(); strokeP3.Clear();

            // stamp settings are fixed for the whole stroke and stored with it
            sHard = scrapeHardness; sGrain = scrapeGrain; sDepth = scrapeDepth;

            AddScrapePoint(mp);
            scrapeLastScreen = mp;
        }
        else if (scraping && CutInput.IsHeld)
        {
            if (strokeUV.Count == 0)
            {
                AddScrapePoint(mp);
                scrapeLastScreen = mp;
            }
            else
            {
                float dist = Vector2.Distance(mp, scrapeLastScreen);
                if (dist >= 1.5f)
                {
                    // fast mouse moves are filled in with extra samples so the stroke has no gaps
                    int steps = Mathf.Clamp(Mathf.CeilToInt(dist / 4f), 1, 12);
                    for (int i = 1; i <= steps; i++)
                        AddScrapePoint(Vector2.Lerp(scrapeLastScreen, mp, i / (float)steps));
                    scrapeLastScreen = mp;
                }
            }
        }

        if (scraping && !CutInput.IsHeld)
        {
            scraping = false;
            dragging = false;
            FinishScrape();
        }
    }

    void AddScrapePoint(Vector2 screen)
    {
        if (!RaycastSurface(screen, out Vector2 uv, out Vector3 localHit, out float uvPerLocal)) return;

        float r = uvPerLocal > 0f ? scrapeRadius / UniformScale() * uvPerLocal : lastRadiusUV;
        if (strokeUV.Count > 0) r = Mathf.Lerp(lastRadiusUV, r, 0.5f);   // less jitter between triangles
        lastRadiusUV = r;

        strokeUV.Add(uv);
        strokeR.Add(r);
        strokeP3.Add(localHit);

        int n = strokeUV.Count;
        if (n == 1) StampSegment(uv, r, uv, r);
        else StampSegment(strokeUV[n - 2], strokeR[n - 2], uv, r);
    }

    // Records the finished stroke in the history (it is already painted)
    void FinishScrape()
    {
        if (strokeUV.Count == 0) return;

        var e = new MeshEdit
        {
            tool = EditTool.Scrape,
            points2D = strokeUV.ToArray(),                       // UV positions
            values = new float[ScrapeValues.Header + strokeUV.Count],
            camera = CaptureCamera()
        };
        e.values[ScrapeValues.Hardness] = sHard;
        e.values[ScrapeValues.Grain] = sGrain;
        e.values[ScrapeValues.Depth] = sDepth;
        for (int i = 0; i < strokeR.Count; i++) e.values[ScrapeValues.Header + i] = strokeR[i];

        outline3D.Clear();
        outline3D.AddRange(strokeP3);                            // where the stroke touched the object (local space)

        strokeUV.Clear(); strokeR.Clear(); strokeP3.Clear();
        Commit(e, true);
    }

    // Replays a recorded stroke (undo / redo / load / verify)
    EditResult ExecuteScrape(MeshEdit e)
    {
        var r = new EditResult();
        float[] v = e.values;
        if (e.points2D == null || e.points2D.Length < 1 || v == null) return r;
        if (v.Length < ScrapeValues.Header + e.points2D.Length) return r;
        if (!EnsurePaintLayer()) return r;

        sHard = v[ScrapeValues.Hardness];
        sGrain = v[ScrapeValues.Grain];
        sDepth = v[ScrapeValues.Depth];

        outline3D.Clear();
        for (int i = 0; i < e.points2D.Length; i++)
        {
            float rad = v[ScrapeValues.Header + i];
            if (i == 0) StampSegment(e.points2D[0], rad, e.points2D[0], rad);
            else StampSegment(e.points2D[i - 1], v[ScrapeValues.Header + i - 1], e.points2D[i], rad);
        }

        r.changed = true;
        r.trisBefore = r.trisAfter = T.Count / 3;
        return r;
    }

    // ------------------------------------------------------------------ Scrape mode: picking
    // Finds the UV under a screen position. Uses the MeshCollider when it holds the live mesh, otherwise tests the mesh directly.
    bool RaycastSurface(Vector2 screen, out Vector2 uv, out Vector3 localHit, out float uvPerLocal)
    {
        uv = default; localHit = default; uvPerLocal = 0f;
        Ray ray = cam.ScreenPointToRay(screen);
        Transform tr = meshFilter.transform;

        if (meshCollider && meshCollider.enabled && meshCollider.sharedMesh == mesh)
        {
            if (!meshCollider.Raycast(ray, out RaycastHit hit, Mathf.Infinity)) return false;

            // triangles are ordered: submesh 0 (outer skin) first, then submesh 1 (inner material)
            int ti = hit.triangleIndex * 3;
            if (innerMaterial && ti >= oT.Count) return false;      // inside surface: the scrape paint is not visible there

            uv = hit.textureCoord;
            localHit = tr.InverseTransformPoint(hit.point);
            if (ti >= 0 && ti + 2 < oT.Count)
            {
                int a = oT[ti], b = oT[ti + 1], c = oT[ti + 2];
                uvPerLocal = UVDensity(oV[a], oV[b], oV[c], oUV[a], oUV[b], oUV[c]);
            }
            return true;
        }

        // fallback: brute force over the base surface (Moller-Trumbore, two sided)
        Vector3 o = tr.InverseTransformPoint(ray.origin);
        Vector3 d = tr.InverseTransformVector(ray.direction);
        float best = float.MaxValue; int bi = -1; float bu = 0f, bv = 0f;
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
            float w = Vector3.Dot(d, q) * inv;
            if (w < 0f || u + w > 1f) continue;
            float t = Vector3.Dot(e2, q) * inv;
            if (t > 1e-4f && t < best) { best = t; bi = i; bu = u; bv = w; }
        }
        if (bi < 0) return false;

        int ia = T[bi], ib = T[bi + 1], ic = T[bi + 2];
        uv = UV[ia] * (1f - bu - bv) + UV[ib] * bu + UV[ic] * bv;
        localHit = o + d * best;
        uvPerLocal = UVDensity(V[ia], V[ib], V[ic], UV[ia], UV[ib], UV[ic]);
        return true;
    }

    // how many UV units one local-space unit covers on this triangle
    static float UVDensity(Vector3 p0, Vector3 p1, Vector3 p2, Vector2 u0, Vector2 u1, Vector2 u2)
    {
        float la = Vector3.Cross(p1 - p0, p2 - p0).magnitude;
        Vector2 e1 = u1 - u0, e2 = u2 - u0;
        float ua = Mathf.Abs(e1.x * e2.y - e1.y * e2.x);
        if (la < 1e-12f || ua < 1e-12f) return 0f;
        return Mathf.Sqrt(ua / la);
    }

    // ------------------------------------------------------------------ Scrape mode: paint layer
    // Creates the runtime albedo + normal textures and hooks them into the material. Returns false if that is impossible.
    bool EnsurePaintLayer()
    {
        if (paintReady) return true;
        if (!targetPumpkin) return false;

        paintRenderer = targetPumpkin.GetComponent<MeshRenderer>();
        if (!paintRenderer) return false;

        paintMat = paintRenderer.material;      // instance of the FIRST material slot, so other objects sharing it are not touched
        string texName = paintMat.HasProperty("_BaseMap") ? "_BaseMap" : (paintMat.HasProperty("_MainTex") ? "_MainTex" : null);
        string colName = paintMat.HasProperty("_BaseColor") ? "_BaseColor" : (paintMat.HasProperty("_Color") ? "_Color" : null);
        if (texName == null || !paintMat.HasProperty("_BumpMap"))
        {
            Debug.LogWarning("MeshHoleCutter: scrape needs a Standard or URP Lit material (_MainTex/_BaseMap + _BumpMap).", this);
            return false;
        }

        pw = ph = Mathf.Clamp(paintTextureSize, 128, 4096);
        int n = pw * ph;
        Color tint = colName != null ? paintMat.GetColor(colName) : Color.white;
        Color32 tint32 = tint;

        // original albedo, read back through a render texture so it works even if the texture is not readable
        baseCol = new Color32[n];
        Texture src = paintMat.GetTexture(texName);
        if (src)
        {
            RenderTexture rt = RenderTexture.GetTemporary(pw, ph, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, rt);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tmp = new Texture2D(pw, ph, TextureFormat.RGBA32, false);
            tmp.ReadPixels(new Rect(0, 0, pw, ph), 0, 0);
            tmp.Apply(false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            baseCol = tmp.GetPixels32();
            Destroy(tmp);

            // the material tint is baked into the texture (tint gets reset to white below)
            for (int i = 0; i < n; i++)
            {
                Color32 c = baseCol[i];
                c.r = (byte)(c.r * tint32.r / 255); c.g = (byte)(c.g * tint32.g / 255); c.b = (byte)(c.b * tint32.b / 255);
                c.a = 255;
                baseCol[i] = c;
            }
        }
        else
        {
            tint32.a = 255;
            for (int i = 0; i < n; i++) baseCol[i] = tint32;
        }

        albedoTex = new Texture2D(pw, ph, TextureFormat.RGBA32, false, false) { name = "Pumpkin Albedo (Scrape)", filterMode = FilterMode.Bilinear };
        normalTex = new Texture2D(pw, ph, TextureFormat.RGBA32, false, true) { name = "Pumpkin Normal (Scrape)", filterMode = FilterMode.Bilinear };
        heightMap = new float[n];
        maskMap = new float[n];

        paintMat.SetTexture(texName, albedoTex);
        paintMat.SetTextureScale(texName, Vector2.one);
        paintMat.SetTextureOffset(texName, Vector2.zero);
        if (colName != null) paintMat.SetColor(colName, Color.white);
        paintMat.SetTexture("_BumpMap", normalTex);
        paintMat.SetTextureScale("_BumpMap", Vector2.one);
        paintMat.SetTextureOffset("_BumpMap", Vector2.zero);
        if (paintMat.HasProperty("_BumpScale")) paintMat.SetFloat("_BumpScale", 1f);
        paintMat.EnableKeyword("_NORMALMAP");

        paintReady = true;
        anyPaint = false;
        paintDirty = false;
        MarkDirty(0, 0, pw - 1, ph - 1);
        FlushPaint();                           // fills both textures once so the object never shows an empty texture
        return true;
    }

    // Called when a different pumpkin becomes the target
    void DetachPaintLayer()
    {
        if (!paintRenderer)
        {
            // the old pumpkin is gone, free its textures
            if (albedoTex) Destroy(albedoTex);
            if (normalTex) Destroy(normalTex);
            if (paintMat) Destroy(paintMat);
        }
        albedoTex = normalTex = null;
        paintMat = null;
        paintRenderer = null;
        paintReady = false;
        paintDirty = false;
        anyPaint = false;
        addedInnerSlot = false;
    }

    // Back to the unscraped look (used whenever the mesh is reloaded from the source / replayed)
    void ResetPaintLayer()
    {
        if (!paintReady) return;     // created lazily on the first scrape
        if (!anyPaint) return;
        System.Array.Clear(heightMap, 0, heightMap.Length);
        System.Array.Clear(maskMap, 0, maskMap.Length);
        anyPaint = false;
        MarkDirty(0, 0, pw - 1, ph - 1);
    }

    void MarkDirty(int x0, int y0, int x1, int y1)
    {
        if (!paintDirty)
        {
            dirtyMinX = x0; dirtyMinY = y0; dirtyMaxX = x1; dirtyMaxY = y1;
            paintDirty = true;
        }
        else
        {
            dirtyMinX = Mathf.Min(dirtyMinX, x0); dirtyMinY = Mathf.Min(dirtyMinY, y0);
            dirtyMaxX = Mathf.Max(dirtyMaxX, x1); dirtyMaxY = Mathf.Max(dirtyMaxY, y1);
        }
    }

    // Stamps circles along a segment in UV space. A big UV jump means the stroke crossed a UV seam -> do not connect.
    void StampSegment(Vector2 a, float ra, Vector2 b, float rb)
    {
        float rMax = Mathf.Max(ra, rb);
        float dist = Vector2.Distance(a, b);
        if (dist < 1e-6f || dist > rMax * SeamBreak) { Stamp(b, rb); return; }

        float stepUV = Mathf.Max(rMax * 0.25f, 1f / pw);
        int n = Mathf.Max(1, Mathf.CeilToInt(dist / stepUV));
        for (int i = 1; i <= n; i++)
        {
            float t = i / (float)n;
            Stamp(Vector2.Lerp(a, b, t), Mathf.Lerp(ra, rb, t));
        }
    }

    // One soft round dab. Uses min() for the height and max() for the mask, so overlapping dabs never add up
    // (smooth stroke, and stamping the same stroke twice gives the same result).
    void Stamp(Vector2 uv, float rUV)
    {
        float rPx = Mathf.Max(rUV * pw, 1.5f);
        float cx = uv.x * pw - 0.5f, cy = uv.y * ph - 0.5f;
        int x0 = Mathf.Max(0, Mathf.FloorToInt(cx - rPx)), x1 = Mathf.Min(pw - 1, Mathf.CeilToInt(cx + rPx));
        int y0 = Mathf.Max(0, Mathf.FloorToInt(cy - rPx)), y1 = Mathf.Min(ph - 1, Mathf.CeilToInt(cy + rPx));
        if (x0 > x1 || y0 > y1) return;

        float soft = Mathf.Max(1e-3f, 1f - sHard);
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float dx = x - cx, dy = y - cy;
                float d = Mathf.Sqrt(dx * dx + dy * dy) / rPx;
                if (d >= 1f) continue;

                float t = Mathf.Clamp01((d - sHard) / soft);
                float f = 1f - t * t * (3f - 2f * t);                         // 1 in the middle, 0 at the rim

                float noise = 0.5f * Mathf.PerlinNoise(x * 0.12f, y * 0.12f) + 0.5f * Mathf.PerlinNoise(x * 0.5f + 17f, y * 0.5f + 5f);
                float target = -sDepth * f * (1f - sGrain * noise);

                int i = y * pw + x;
                if (target < heightMap[i]) heightMap[i] = target;
                float mk = Mathf.Clamp01(f * 2f);
                if (mk > maskMap[i]) maskMap[i] = mk;
            }
        }

        anyPaint = true;
        MarkDirty(x0 - 1, y0 - 1, x1 + 1, y1 + 1);   // +1: neighbours need a new normal too
    }

    // Rebuilds albedo + normal map for the changed rectangle and uploads them
    void FlushPaint()
    {
        if (!paintReady || !paintDirty || !albedoTex || !normalTex) return;

        int x0 = Mathf.Max(0, dirtyMinX), x1 = Mathf.Min(pw - 1, dirtyMaxX);
        int y0 = Mathf.Max(0, dirtyMinY), y1 = Mathf.Min(ph - 1, dirtyMaxY);
        paintDirty = false;
        if (x0 > x1 || y0 > y1) return;

        NativeArray<Color32> aRaw = albedoTex.GetRawTextureData<Color32>();
        NativeArray<Color32> nRaw = normalTex.GetRawTextureData<Color32>();
        Color32 sc = scrapeColor;
        float gain = normalStrength * (pw / 128f);

        for (int y = y0; y <= y1; y++)
        {
            int yU = Mathf.Min(y + 1, ph - 1) * pw, yD = Mathf.Max(y - 1, 0) * pw, row = y * pw;
            for (int x = x0; x <= x1; x++)
            {
                int i = row + x;
                float hL = heightMap[row + Mathf.Max(x - 1, 0)];
                float hR = heightMap[row + Mathf.Min(x + 1, pw - 1)];
                float hD = heightMap[yD + x];
                float hU = heightMap[yU + x];
                float gx = (hR - hL) * 0.5f * gain;
                float gy = (hU - hD) * 0.5f * gain;

                // tangent space normal (+X = U, +Y = V). Alpha stays 255 so it decodes right on desktop and mobile.
                float inv = 1f / Mathf.Sqrt(gx * gx + gy * gy + 1f);
                nRaw[i] = new Color32(
                    (byte)((-gx * inv * 0.5f + 0.5f) * 255f + 0.5f),
                    (byte)((-gy * inv * 0.5f + 0.5f) * 255f + 0.5f),
                    (byte)((inv * 0.5f + 0.5f) * 255f + 0.5f),
                    255);

                Color32 c = baseCol[i];
                float m = maskMap[i];
                if (m > 0.001f)
                {
                    float shade = 1f - scrapeRimDarken * Mathf.Clamp01(Mathf.Sqrt(gx * gx + gy * gy));
                    c.r = (byte)(c.r + (sc.r * shade - c.r) * m + 0.5f);
                    c.g = (byte)(c.g + (sc.g * shade - c.g) * m + 0.5f);
                    c.b = (byte)(c.b + (sc.b * shade - c.b) * m + 0.5f);
                }
                aRaw[i] = c;
            }
        }

        albedoTex.Apply(false);
        normalTex.Apply(false);
    }

    // ------------------------------------------------------------------ Cut pipeline
    void PerformCut(float targetPx)
    {
        if (T.Count / 3 >= maxTriangles) return;

        // camera data (taken from the edit, so a replay uses exactly the same view)
        viewFromLocal = activeCam.viewFromLocal;
        proj = activeCam.projection;
        pix = activeCam.pixelRect;
        ortho = activeCam.orthographic;
        Matrix4x4 viewInv = viewFromLocal.inverse;
        camPosLocal = viewInv.MultiplyPoint3x4(Vector3.zero);
        camFwdLocal = viewInv.MultiplyVector(new Vector3(0f, 0f, -1f)).normalized;
        invVP = (proj * viewFromLocal).inverse;

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
            Vector3 rayOrigin = Unproject(center, -1f);
            if (RaycastLocal(rayOrigin, Unproject(center, 1f) - rayOrigin, out Vector3 hit))
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
        if (eligible && GetSD(a) + GetSD(b) + GetSD(c) < 0f) { removedAny = true; return; }   // inside the cut shape -> removed
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
            if (clipStage) outline3D.Add(mi.pos);   // these lie exactly on the cut outline = the 3D shape of the change
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
        removedAny = true;

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
    bool RaycastLocal(Vector3 o, Vector3 d, out Vector3 hit)
    {
        hit = Vector3.zero;

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

    // ------------------------------------------------------------------ Materials
    // Makes sure the renderer has a second material slot when an inner material is set
    void ApplyMaterials()
    {
        if (!targetPumpkin) return;
        var r = targetPumpkin.GetComponent<MeshRenderer>();
        if (!r) return;

        Material[] mats = r.sharedMaterials;
        if (innerMaterial)
        {
            if (mats.Length != 2) System.Array.Resize(ref mats, 2);
            mats[1] = innerMaterial;
            addedInnerSlot = true;
            r.sharedMaterials = mats;
        }
        else if (addedInnerSlot)
        {
            System.Array.Resize(ref mats, 1);
            addedInnerSlot = false;
            r.sharedMaterials = mats;
        }
    }

    // ------------------------------------------------------------------ Mesh build (base -> display mesh)
    void RebuildMesh()
    {
        ApplyMaterials();
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

        oV.Clear(); oN.Clear(); oUV.Clear(); oT.Clear(); oT2.Clear();
        bool split = innerMaterial != null;
        List<int> innerTris = split ? oT2 : oT;
        wallTris = (split && !wallsUseInnerMaterial) ? oT : innerTris;

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
                innerTris.Add(vc + T[i]); innerTris.Add(vc + T[i + 2]); innerTris.Add(vc + T[i + 1]); // flipped
            }
        }

        // hole walls
        if (hasThickness) BuildWalls(th);

        mesh.Clear();
        mesh.indexFormat = oV.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.SetVertices(oV);
        mesh.SetNormals(oN);
        mesh.SetUVs(0, oUV);
        if (split)
        {
            mesh.subMeshCount = 2;
            mesh.SetTriangles(oT, 0, true);
            mesh.SetTriangles(oT2, 1, false);
        }
        else
        {
            mesh.subMeshCount = 1;
            mesh.SetTriangles(oT, 0, true);
        }
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
        wallTris.Add(i); wallTris.Add(j); wallTris.Add(k);
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
        sourceTriCount = tris.Length / 3;

        // remember edges that were already open in the source mesh (e.g. a plane's border)
        CountEdges();
        foreach (var kv in edgeCount) if (kv.Value == 1) openEdges.Add(kv.Key);

        ResetPaintLayer();      // scrape strokes are replayed on top of a clean paint layer
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
        Vector3 s = meshFilter.transform.lossyScale;
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

/// <summary>Layout of MeshEdit.values for a scrape stroke: 3 settings, then one brush radius (in UV units) per point.</summary>
static class ScrapeValues
{
    public const int Hardness = 0, Grain = 1, Depth = 2, Header = 3;
}

/// <summary>Reads mouse and keyboard input from the Input System package.</summary>
static class CutInput
{
    public static Vector2 MousePosition => Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
    public static bool PressedThisFrame => Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
    public static bool IsHeld => Mouse.current != null && Mouse.current.leftButton.isPressed;
    public static bool Key1 => Keyboard.current != null && Keyboard.current.digit1Key.wasPressedThisFrame;
    public static bool Key2 => Keyboard.current != null && Keyboard.current.digit2Key.wasPressedThisFrame;
    public static bool Key3 => Keyboard.current != null && Keyboard.current.digit3Key.wasPressedThisFrame;
    public static bool Tab => Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame;
    static bool Ctrl => Keyboard.current != null && (Keyboard.current.ctrlKey.isPressed || Keyboard.current.leftCommandKey.isPressed);
    public static bool UndoPressed => Ctrl && Keyboard.current.zKey.wasPressedThisFrame;
    public static bool RedoPressed => Ctrl && Keyboard.current.yKey.wasPressedThisFrame;
}