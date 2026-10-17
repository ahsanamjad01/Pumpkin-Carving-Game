using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

/// <summary>
/// Attach to any mesh object (e.g. a Sphere). Hold the left mouse button over it
/// to slowly sculpt the surface inward toward the center. When enough of the mesh
/// has been sculpted away, the GameObject is destroyed.
/// </summary>
[RequireComponent(typeof(MeshFilter))]
public class MeshSculptToZero : MonoBehaviour
{
    [Header("Progress")]
    [Range(0f, 100f)]
    [Tooltip("How much of the mesh has been sculpted away (read-only, updated automatically).")]
    public float percentFinished = 0f;

    [Tooltip("Object is destroyed once percentFinished reaches this value.")]
    [Range(50f, 100f)]
    public float destroyAtPercent = 98f;

    [Header("Brush")]
    [Tooltip("Brush radius in world units.")]
    public float brushRadius = 0.3f;

    [Tooltip("How fast vertices are pushed inward at the brush center (world units per second).")]
    public float sculptSpeed = 0.2f;

    public enum SculptButton { Left, Right }

    [Tooltip("Mouse button used for sculpting.")]
    public SculptButton mouseButton = SculptButton.Left;

    [Header("Performance")]
    [Tooltip("How often (seconds) the MeshCollider is rebuilt while sculpting.")]
    public float colliderUpdateInterval = 0.1f;

    private Mesh mesh;
    private MeshCollider meshCollider;
    private Vector3[] vertices;
    private Vector3 center;           // local-space center vertices collapse toward
    private float totalOriginalDist;  // sum of original vertex distances from center
    private float currentTotalDist;   // sum of current vertex distances from center
    private float colliderTimer;
    private bool colliderDirty;

    private void Awake()
    {
        // Work on our own copy of the mesh so the original asset isn't modified.
        MeshFilter mf = GetComponent<MeshFilter>();
        mesh = Instantiate(mf.sharedMesh);
        mesh.name = mf.sharedMesh.name + " (Sculpt)";
        mesh.MarkDynamic();
        mf.sharedMesh = mesh;

        vertices = mesh.vertices;
        center = mesh.bounds.center;

        for (int i = 0; i < vertices.Length; i++)
            totalOriginalDist += (vertices[i] - center).magnitude;
        currentTotalDist = totalOriginalDist;

        // Replace any primitive collider (e.g. SphereCollider) with a MeshCollider
        // so raycasts follow the sculpted surface.
        foreach (Collider c in GetComponents<Collider>())
            if (!(c is MeshCollider)) Destroy(c);

        meshCollider = GetComponent<MeshCollider>();
        if (meshCollider == null) meshCollider = gameObject.AddComponent<MeshCollider>();
        meshCollider.sharedMesh = mesh;
    }

    private void Update()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null) return;

        ButtonControl button = mouseButton == SculptButton.Left ? mouse.leftButton : mouse.rightButton;

        if (button.isPressed)
        {
            Camera cam = Camera.main;
            if (cam != null)
            {
                Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
                if (meshCollider.Raycast(ray, out RaycastHit hit, 1000f))
                    Sculpt(hit.point);
            }

            // Periodically refresh the collider while sculpting.
            colliderTimer += Time.deltaTime;
            if (colliderDirty && colliderTimer >= colliderUpdateInterval)
                RefreshCollider();
        }
        else if (colliderDirty)
        {
            RefreshCollider(); // make sure collider is up to date on release
        }
    }

    private void Sculpt(Vector3 worldHitPoint)
    {
        Vector3 localHit = transform.InverseTransformPoint(worldHitPoint);

        // Convert world-space brush values to local space (handles object scale).
        Vector3 s = transform.lossyScale;
        float avgScale = (Mathf.Abs(s.x) + Mathf.Abs(s.y) + Mathf.Abs(s.z)) / 3f;
        if (avgScale < 1e-5f) return;

        float radius = brushRadius / avgScale;
        float radiusSqr = radius * radius;
        float amount = (sculptSpeed * Time.deltaTime) / avgScale;

        bool changed = false;

        for (int i = 0; i < vertices.Length; i++)
        {
            float distSqr = (vertices[i] - localHit).sqrMagnitude;
            if (distSqr > radiusSqr) continue;

            // Smooth falloff: strongest at brush center, zero at the edge.
            float t = 1f - Mathf.Sqrt(distSqr) / radius;
            float falloff = t * t * (3f - 2f * t);

            Vector3 fromCenter = vertices[i] - center;
            float m = fromCenter.magnitude;
            if (m < 1e-6f) continue;

            float newM = Mathf.Max(0f, m - amount * falloff);
            vertices[i] = center + fromCenter * (newM / m);

            currentTotalDist -= (m - newM);
            changed = true;
        }

        if (!changed) return;

        mesh.vertices = vertices;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        colliderDirty = true;

        percentFinished = Mathf.Clamp01(1f - currentTotalDist / totalOriginalDist) * 100f;

        if (percentFinished >= destroyAtPercent)
        {
            percentFinished = 100f;
            Destroy(gameObject);
        }
    }

    private void RefreshCollider()
    {
        meshCollider.sharedMesh = null; // forces a rebuild
        meshCollider.sharedMesh = mesh;
        colliderDirty = false;
        colliderTimer = 0f;
    }

    private void OnDestroy()
    {
        if (mesh != null) Destroy(mesh);
    }
}
