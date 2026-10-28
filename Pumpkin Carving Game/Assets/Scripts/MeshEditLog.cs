using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
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

    const int PackedMagic = 0x314C454D; // MEL1
    const string PackedFormat = "MEL1";
    const float ScreenPointPrecision = 256f;
    const int MaxCollectionLength = 1000000;

    [Serializable]
    sealed class PackedEnvelope
    {
        public string format;
        public string data;
    }

    /// <summary>Writes compressed JSON with subpixel point precision. Legacy expanded JSON remains readable.</summary>
    public static string ToJson(MeshEditLog log, bool prettyPrint = false)
    {
        if (log == null) throw new ArgumentNullException(nameof(log));
        if (log.edits == null) throw new InvalidDataException("An edit log must contain an edit list.");

        byte[] payload;
        using (var raw = new MemoryStream())
        {
            using (var writer = new BinaryWriter(raw, Encoding.UTF8, true))
                WritePackedLog(writer, log);

            raw.Position = 0;
            using (var compressed = new MemoryStream())
            {
                using (var gzip = new GZipStream(compressed, System.IO.Compression.CompressionLevel.Optimal, true))
                    raw.CopyTo(gzip);
                payload = compressed.ToArray();
            }
        }

        return JsonUtility.ToJson(new PackedEnvelope
        {
            format = PackedFormat,
            data = Convert.ToBase64String(payload)
        }, prettyPrint);
    }

    /// <summary>Reads both compact logs and the original JsonUtility format.</summary>
    public static MeshEditLog FromJson(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;

        if (json.IndexOf("\"format\"", StringComparison.Ordinal) >= 0)
        {
            PackedEnvelope envelope = JsonUtility.FromJson<PackedEnvelope>(json);
            if (envelope != null && envelope.format == PackedFormat)
                return FromPackedBytes(Convert.FromBase64String(envelope.data));
        }

        return JsonUtility.FromJson<MeshEditLog>(json);
    }

    static void WritePackedLog(BinaryWriter writer, MeshEditLog log)
    {
        writer.Write(PackedMagic);
        writer.Write(log.version);
        writer.Write(log.meshName ?? string.Empty);
        writer.Write(log.sourceVertexCount);
        writer.Write(log.sourceTriangleCount);

        var cameras = new List<CameraSnapshot>();
        var cameraIndices = new int[log.edits.Count];
        for (int i = 0; i < log.edits.Count; i++)
        {
            MeshEdit edit = log.edits[i];
            if (edit == null) throw new InvalidDataException("An edit log cannot contain a null edit.");

            int cameraIndex = FindCamera(cameras, edit.camera);
            if (cameraIndex < 0)
            {
                cameraIndex = cameras.Count;
                cameras.Add(edit.camera);
            }
            cameraIndices[i] = cameraIndex;
        }

        writer.Write(cameras.Count);
        for (int i = 0; i < cameras.Count; i++)
            WriteCamera(writer, cameras[i]);

        writer.Write(log.edits.Count);
        for (int i = 0; i < log.edits.Count; i++)
            WriteEdit(writer, log.edits[i], cameraIndices[i]);
    }

    static MeshEditLog FromPackedBytes(byte[] payload)
    {
        using (var compressed = new MemoryStream(payload, false))
        using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
        using (var reader = new BinaryReader(gzip, Encoding.UTF8))
        {
            if (reader.ReadInt32() != PackedMagic)
                throw new InvalidDataException("The edit log has an invalid packed-data header.");

            var log = new MeshEditLog
            {
                version = reader.ReadInt32(),
                meshName = reader.ReadString(),
                sourceVertexCount = reader.ReadInt32(),
                sourceTriangleCount = reader.ReadInt32()
            };

            int cameraCount = ReadCount(reader, "camera");
            var cameras = new CameraSnapshot[cameraCount];
            for (int i = 0; i < cameraCount; i++)
                cameras[i] = ReadCamera(reader);

            int editCount = ReadCount(reader, "edit");
            log.edits = new List<MeshEdit>(editCount);
            for (int i = 0; i < editCount; i++)
                log.edits.Add(ReadEdit(reader, cameras));

            return log;
        }
    }

    static void WriteEdit(BinaryWriter writer, MeshEdit edit, int cameraIndex)
    {
        writer.Write(edit.tool);

        Vector2[] points2D = edit.points2D ?? new Vector2[0];
        writer.Write(points2D.Length);
        int lastX = 0, lastY = 0;
        for (int i = 0; i < points2D.Length; i++)
        {
            int x = QuantizeScreenPoint(points2D[i].x);
            int y = QuantizeScreenPoint(points2D[i].y);
            WriteSignedVarInt(writer, unchecked(x - lastX));
            WriteSignedVarInt(writer, unchecked(y - lastY));
            lastX = x;
            lastY = y;
        }

        float[] values = edit.values ?? new float[0];
        writer.Write(values.Length);
        for (int i = 0; i < values.Length; i++)
            writer.Write(values[i]);

        writer.Write(cameraIndex);
        WritePoints3D(writer, edit.points3D);
        writer.Write(edit.areaRemoved);
        writer.Write(edit.trianglesBefore);
        writer.Write(edit.trianglesAfter);
        writer.Write(edit.time);
    }

    static MeshEdit ReadEdit(BinaryReader reader, CameraSnapshot[] cameras)
    {
        int tool = reader.ReadInt32();
        int pointCount = ReadCount(reader, "2D point");
        var points2D = new Vector2[pointCount];
        int x = 0, y = 0;
        for (int i = 0; i < pointCount; i++)
        {
            x = unchecked(x + ReadVarInt(reader));
            y = unchecked(y + ReadVarInt(reader));
            points2D[i] = new Vector2(x / ScreenPointPrecision, y / ScreenPointPrecision);
        }

        int valueCount = ReadCount(reader, "value");
        var values = new float[valueCount];
        for (int i = 0; i < valueCount; i++)
            values[i] = reader.ReadSingle();

        int cameraIndex = reader.ReadInt32();
        if (cameraIndex < 0 || cameraIndex >= cameras.Length)
            throw new InvalidDataException("The edit log contains an invalid camera reference.");

        return new MeshEdit
        {
            tool = tool,
            points2D = points2D,
            values = values,
            camera = cameras[cameraIndex],
            points3D = ReadPoints3D(reader),
            areaRemoved = reader.ReadSingle(),
            trianglesBefore = reader.ReadInt32(),
            trianglesAfter = reader.ReadInt32(),
            time = reader.ReadSingle()
        };
    }

    static void WritePoints3D(BinaryWriter writer, Vector3[] points)
    {
        int count = points == null ? 0 : points.Length;
        writer.Write(count);
        if (count == 0) return;

        Vector3 min = points[0], max = points[0];
        for (int i = 0; i < count; i++)
        {
            EnsureFinite(points[i].x);
            EnsureFinite(points[i].y);
            EnsureFinite(points[i].z);
            min = Vector3.Min(min, points[i]);
            max = Vector3.Max(max, points[i]);
        }

        WriteVector3(writer, min);
        WriteVector3(writer, max);
        for (int i = 0; i < count; i++)
        {
            writer.Write(QuantizeCoordinate(points[i].x, min.x, max.x));
            writer.Write(QuantizeCoordinate(points[i].y, min.y, max.y));
            writer.Write(QuantizeCoordinate(points[i].z, min.z, max.z));
        }
    }

    static Vector3[] ReadPoints3D(BinaryReader reader)
    {
        int count = ReadCount(reader, "3D point");
        if (count == 0) return new Vector3[0];

        Vector3 min = ReadVector3(reader);
        Vector3 max = ReadVector3(reader);
        var points = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            points[i] = new Vector3(
                DequantizeCoordinate(reader.ReadUInt16(), min.x, max.x),
                DequantizeCoordinate(reader.ReadUInt16(), min.y, max.y),
                DequantizeCoordinate(reader.ReadUInt16(), min.z, max.z));
        }
        return points;
    }

    static ushort QuantizeCoordinate(float value, float min, float max)
    {
        float range = max - min;
        if (range <= 0f) return 0;
        return (ushort)Mathf.Clamp(Mathf.RoundToInt((value - min) / range * ushort.MaxValue), 0, ushort.MaxValue);
    }

    static float DequantizeCoordinate(ushort value, float min, float max)
    {
        return Mathf.Lerp(min, max, value / (float)ushort.MaxValue);
    }

    static int QuantizeScreenPoint(float value)
    {
        EnsureFinite(value);
        double quantized = Math.Round(value * ScreenPointPrecision);
        if (quantized < int.MinValue || quantized > int.MaxValue)
            throw new InvalidDataException("A screen point is outside the compact edit-log range.");
        return (int)quantized;
    }

    static void EnsureFinite(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            throw new InvalidDataException("Edit logs cannot store non-finite point coordinates.");
    }

    static void WriteVarInt(BinaryWriter writer, uint value)
    {
        while (value >= 0x80)
        {
            writer.Write((byte)(value | 0x80));
            value >>= 7;
        }
        writer.Write((byte)value);
    }

    static void WriteSignedVarInt(BinaryWriter writer, int value)
    {
        WriteVarInt(writer, unchecked((uint)((value << 1) ^ (value >> 31))));
    }

    static int ReadVarInt(BinaryReader reader)
    {
        uint value = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            byte next = reader.ReadByte();
            value |= (uint)(next & 0x7F) << shift;
            if ((next & 0x80) == 0)
                return unchecked((int)((value >> 1) ^ (uint)-(int)(value & 1)));
        }
        throw new InvalidDataException("The edit log contains an invalid point delta.");
    }

    static int ReadCount(BinaryReader reader, string description)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > MaxCollectionLength)
            throw new InvalidDataException("The edit log contains an invalid " + description + " count.");
        return count;
    }

    static int FindCamera(List<CameraSnapshot> cameras, CameraSnapshot camera)
    {
        for (int i = 0; i < cameras.Count; i++)
        {
            CameraSnapshot candidate = cameras[i];
            if (SameMatrix(candidate.viewFromLocal, camera.viewFromLocal) &&
                SameMatrix(candidate.projection, camera.projection) &&
                candidate.pixelRect.x == camera.pixelRect.x &&
                candidate.pixelRect.y == camera.pixelRect.y &&
                candidate.pixelRect.width == camera.pixelRect.width &&
                candidate.pixelRect.height == camera.pixelRect.height &&
                candidate.orthographic == camera.orthographic)
                return i;
        }
        return -1;
    }

    static bool SameMatrix(Matrix4x4 a, Matrix4x4 b)
    {
        for (int i = 0; i < 16; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    static void WriteCamera(BinaryWriter writer, CameraSnapshot camera)
    {
        WriteMatrix(writer, camera.viewFromLocal);
        WriteMatrix(writer, camera.projection);
        writer.Write(camera.pixelRect.x);
        writer.Write(camera.pixelRect.y);
        writer.Write(camera.pixelRect.width);
        writer.Write(camera.pixelRect.height);
        writer.Write(camera.orthographic);
    }

    static CameraSnapshot ReadCamera(BinaryReader reader)
    {
        return new CameraSnapshot
        {
            viewFromLocal = ReadMatrix(reader),
            projection = ReadMatrix(reader),
            pixelRect = new Rect(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            orthographic = reader.ReadBoolean()
        };
    }

    static void WriteMatrix(BinaryWriter writer, Matrix4x4 matrix)
    {
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                writer.Write(matrix[row, column]);
    }

    static Matrix4x4 ReadMatrix(BinaryReader reader)
    {
        var matrix = new Matrix4x4();
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                matrix[row, column] = reader.ReadSingle();
        return matrix;
    }

    static void WriteVector3(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.x);
        writer.Write(value.y);
        writer.Write(value.z);
    }

    static Vector3 ReadVector3(BinaryReader reader)
    {
        return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }
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
