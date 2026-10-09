using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace XrSpatial.Core
{
    /// <summary>
    /// A small binary glTF (.glb) reader for runtime render models (controller models): static geometry only (positions, normals, triangles, node transforms).
    /// No materials, textures, skins or animation; the result is one merged mesh in Unity's left-handed space (glTF is right-handed, so z is flipped and the winding reversed).
    /// </summary>
    public static class GlbModel
    {
        public sealed class Data
        {
            public Vector3[] vertices, normals;
            public int[] triangles;
            public Bounds bounds;
        }

        [Serializable] class Gltf { public GNode[] nodes; public GMesh[] meshes; public GAccessor[] accessors; public GBufferView[] bufferViews; public GScene[] scenes; public int scene; }
        [Serializable] class GScene { public int[] nodes; }
        [Serializable] class GNode { public int mesh = -1; public int[] children; public float[] matrix; public float[] translation; public float[] rotation; public float[] scale; }
        [Serializable] class GMesh { public GPrim[] primitives; }
        [Serializable] class GPrim { public GAttr attributes; public int indices = -1; public int mode = 4; }
        [Serializable] class GAttr { public int POSITION = -1; public int NORMAL = -1; }
        [Serializable] class GAccessor { public int bufferView = -1; public int byteOffset; public int componentType; public bool normalized; public int count; public string type; }
        [Serializable] class GBufferView { public int buffer; public int byteOffset; public int byteLength; public int byteStride; }

        const uint Magic = 0x46546C67, ChunkJson = 0x4E4F534A, ChunkBin = 0x004E4942;

        public static Mesh ToMesh(Data d, string name = "ControllerModel")
        {
            var m = new Mesh { name = name, indexFormat = d.vertices.Length > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            m.vertices = d.vertices; m.normals = d.normals; m.triangles = d.triangles; m.bounds = d.bounds;
            return m;
        }

        public static bool TryParse(byte[] glb, out Data data, out string problem)
        {
            data = null; problem = null;
            try
            {
                if (glb == null || glb.Length < 20 || BitConverter.ToUInt32(glb, 0) != Magic) { problem = "not a binary glTF (no glTF header)"; return false; }
                int pos = 12, jsonAt = -1, jsonLen = 0, binAt = -1, binLen = 0;
                while (pos + 8 <= glb.Length)
                {
                    int len = (int)BitConverter.ToUInt32(glb, pos); uint type = BitConverter.ToUInt32(glb, pos + 4);
                    if (len < 0 || pos + 8 + len > glb.Length) { problem = "truncated glTF chunk"; return false; }
                    if (type == ChunkJson && jsonAt < 0) { jsonAt = pos + 8; jsonLen = len; }
                    else if (type == ChunkBin && binAt < 0) { binAt = pos + 8; binLen = len; }
                    pos += 8 + ((len + 3) & ~3);
                }
                if (jsonAt < 0) { problem = "glTF has no JSON chunk"; return false; }
                var g = JsonUtility.FromJson<Gltf>(Encoding.UTF8.GetString(glb, jsonAt, jsonLen));
                if (g == null || g.nodes == null || g.meshes == null || g.accessors == null || g.bufferViews == null) { problem = "glTF is missing nodes, meshes or accessors"; return false; }
                if (binAt < 0) { problem = "glTF has no binary chunk"; return false; }

                var verts = new List<Vector3>(); var norms = new List<Vector3>(); var tris = new List<int>();
                var roots = new List<int>();
                if (g.scenes != null && g.scenes.Length > 0 && g.scene >= 0 && g.scene < g.scenes.Length && g.scenes[g.scene].nodes != null) roots.AddRange(g.scenes[g.scene].nodes);
                else { var child = new HashSet<int>(); foreach (var n in g.nodes) if (n.children != null) foreach (int c in n.children) child.Add(c); for (int i = 0; i < g.nodes.Length; i++) if (!child.Contains(i)) roots.Add(i); }
                foreach (int r in roots) Visit(g, glb, binAt, binLen, r, Matrix4x4.identity, verts, norms, tris, 0);
                if (verts.Count == 0 || tris.Count == 0) { problem = "glTF has no triangle geometry"; return false; }

                var b = new Bounds(verts[0], Vector3.zero);
                foreach (var v in verts) b.Encapsulate(v);
                data = new Data { vertices = verts.ToArray(), normals = norms.ToArray(), triangles = tris.ToArray(), bounds = b };
                return true;
            }
            catch (Exception e) { problem = "glTF parse failed: " + e.Message; return false; }
        }

        static Matrix4x4 Local(GNode n)
        {
            if (n.matrix != null && n.matrix.Length == 16)
            {
                var m = Matrix4x4.identity;
                for (int c = 0; c < 4; c++) m.SetColumn(c, new Vector4(n.matrix[c * 4], n.matrix[c * 4 + 1], n.matrix[c * 4 + 2], n.matrix[c * 4 + 3]));
                return m;
            }
            var t = n.translation != null && n.translation.Length == 3 ? new Vector3(n.translation[0], n.translation[1], n.translation[2]) : Vector3.zero;
            var q = n.rotation != null && n.rotation.Length == 4 ? new Quaternion(n.rotation[0], n.rotation[1], n.rotation[2], n.rotation[3]) : Quaternion.identity;
            var s = n.scale != null && n.scale.Length == 3 ? new Vector3(n.scale[0], n.scale[1], n.scale[2]) : Vector3.one;
            return Matrix4x4.TRS(t, q, s);
        }

        static void Visit(Gltf g, byte[] bytes, int binAt, int binLen, int index, Matrix4x4 parent, List<Vector3> verts, List<Vector3> norms, List<int> tris, int depth)
        {
            if (depth > 32 || index < 0 || index >= g.nodes.Length) return;
            var n = g.nodes[index];
            var world = parent * Local(n);
            if (n.mesh >= 0 && n.mesh < g.meshes.Length && g.meshes[n.mesh].primitives != null)
            {
                var nm = world.inverse.transpose;
                foreach (var p in g.meshes[n.mesh].primitives)
                {
                    if (p.mode != 4 || p.attributes == null || p.attributes.POSITION < 0) continue;     // triangles only
                    var pos = ReadVec3(g, bytes, binAt, binLen, p.attributes.POSITION);
                    var nor = p.attributes.NORMAL >= 0 ? ReadVec3(g, bytes, binAt, binLen, p.attributes.NORMAL) : null;
                    int[] idx = p.indices >= 0 ? ReadIndices(g, bytes, binAt, binLen, p.indices) : null;
                    int baseIndex = verts.Count;
                    for (int i = 0; i < pos.Length; i++)
                    {
                        var v = world.MultiplyPoint3x4(pos[i]);
                        var nn = nor != null && i < nor.Length ? nm.MultiplyVector(nor[i]).normalized : Vector3.up;
                        verts.Add(new Vector3(v.x, v.y, -v.z)); norms.Add(new Vector3(nn.x, nn.y, -nn.z));   // right-handed to left-handed
                    }
                    int count = idx != null ? idx.Length : pos.Length;
                    for (int i = 0; i + 2 < count; i += 3)
                    {
                        int a = idx != null ? idx[i] : i, b = idx != null ? idx[i + 1] : i + 1, c = idx != null ? idx[i + 2] : i + 2;
                        if (a >= pos.Length || b >= pos.Length || c >= pos.Length) continue;
                        tris.Add(baseIndex + a); tris.Add(baseIndex + c); tris.Add(baseIndex + b);          // the z flip reverses the winding
                    }
                }
            }
            if (n.children != null) foreach (int c in n.children) Visit(g, bytes, binAt, binLen, c, world, verts, norms, tris, depth + 1);
        }

        static int ComponentSize(int type)
        {
            switch (type) { case 5120: case 5121: return 1; case 5122: case 5123: return 2; case 5125: case 5126: return 4; default: throw new Exception("unsupported component type " + type); }
        }

        static float ReadComponent(byte[] b, int at, int type, bool normalized)
        {
            switch (type)
            {
                case 5126: return BitConverter.ToSingle(b, at);
                case 5120: { float v = (sbyte)b[at]; return normalized ? Mathf.Max(v / 127f, -1f) : v; }
                case 5121: { float v = b[at]; return normalized ? v / 255f : v; }
                case 5122: { float v = BitConverter.ToInt16(b, at); return normalized ? Mathf.Max(v / 32767f, -1f) : v; }
                case 5123: { float v = BitConverter.ToUInt16(b, at); return normalized ? v / 65535f : v; }
                case 5125: return BitConverter.ToUInt32(b, at);
                default: throw new Exception("unsupported component type " + type);
            }
        }

        static void Locate(Gltf g, int binAt, int binLen, GAccessor a, int comps, out int start, out int stride)
        {
            if (a.bufferView < 0 || a.bufferView >= g.bufferViews.Length) throw new Exception("accessor without a buffer view");
            var bv = g.bufferViews[a.bufferView];
            int size = ComponentSize(a.componentType) * comps;
            stride = bv.byteStride > 0 ? bv.byteStride : size;
            start = binAt + bv.byteOffset + a.byteOffset;
            long end = (long)start + (long)(Math.Max(a.count, 1) - 1) * stride + size;
            if (bv.byteOffset + a.byteOffset < 0 || end > binAt + binLen) throw new Exception("accessor runs past the binary chunk");
        }

        static Vector3[] ReadVec3(Gltf g, byte[] bytes, int binAt, int binLen, int accessor)
        {
            if (accessor < 0 || accessor >= g.accessors.Length) throw new Exception("bad accessor index");
            var a = g.accessors[accessor];
            if (a.type != "VEC3") throw new Exception("expected a VEC3 accessor");
            Locate(g, binAt, binLen, a, 3, out int start, out int stride);
            int cs = ComponentSize(a.componentType);
            var r = new Vector3[a.count];
            for (int i = 0; i < a.count; i++)
            {
                int at = start + i * stride;
                r[i] = new Vector3(ReadComponent(bytes, at, a.componentType, a.normalized), ReadComponent(bytes, at + cs, a.componentType, a.normalized), ReadComponent(bytes, at + 2 * cs, a.componentType, a.normalized));
            }
            return r;
        }

        static int[] ReadIndices(Gltf g, byte[] bytes, int binAt, int binLen, int accessor)
        {
            if (accessor < 0 || accessor >= g.accessors.Length) throw new Exception("bad accessor index");
            var a = g.accessors[accessor];
            if (a.type != "SCALAR") throw new Exception("expected a SCALAR accessor for indices");
            Locate(g, binAt, binLen, a, 1, out int start, out int stride);
            var r = new int[a.count];
            for (int i = 0; i < a.count; i++) r[i] = (int)ReadComponent(bytes, start + i * stride, a.componentType, false);
            return r;
        }
    }
}
