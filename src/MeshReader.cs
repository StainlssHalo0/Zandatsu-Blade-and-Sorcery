using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zandatsu
{
    /// <summary>
    /// A readable copy of a mesh the game ships without CPU access (Read/Write off: many props, food, crates, chairs).
    /// Its vertex and index buffers are copied back from the graphics card once, decoded by their vertex layout and kept,
    /// one copy per mesh. Static meshes only (positions, normals, tangents, two UV sets, colours; no skin weights).
    /// The game's compressed meshes (half-float vertices: crates, chairs, tables, food) read back as zeros: they are
    /// drawn once through our shader instead, which writes their triangles out (Capture).
    /// </summary>
    public static class MeshReader
    {
        private static readonly Dictionary<Mesh, Mesh> copies = new Dictionary<Mesh, Mesh>();
        private static readonly Dictionary<Mesh, string> failed = new Dictionary<Mesh, string>();

        /// <summary>The mesh itself if readable, else its readable copy (null and why if it cannot be read).</summary>
        public static Mesh Readable(Mesh mesh, out string error)
        {
            error = null;
            if (!mesh) { error = "no mesh"; return null; }
            if (mesh.isReadable) return mesh;
            if (copies.TryGetValue(mesh, out Mesh copy) && copy) return copy;
            if (failed.TryGetValue(mesh, out error)) return null;
            bool retry = false;
            try { copy = Copy(mesh, out error, out retry); }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; copy = null; }
            if (!copy)
            {
                if (!retry) failed[mesh] = error ?? "unknown";
                return null;
            }
            copies[mesh] = copy;
            return copy;
        }

        public static void ClearAll()
        {
            foreach (Mesh m in copies.Values) if (m) UnityEngine.Object.Destroy(m);
            copies.Clear();
            failed.Clear();
        }

        private static Mesh Copy(Mesh src, out string error, out bool retry)
        {
            error = null;
            retry = false;
            int n = src.vertexCount;
            if (n == 0) { error = "empty"; return null; }
            // Half-float vertices (the game's compressed meshes): their buffers read back as zeros, so they are drawn once
            // through our shader instead, which writes their triangles out.
            foreach (VertexAttributeDescriptor a in src.GetVertexAttributes())
                if (a.format == VertexAttributeFormat.Float16) return Capture(src, out error, out retry);
            var streams = new byte[src.vertexBufferCount][];
            var strides = new int[streams.Length];
            for (int s = 0; s < streams.Length; s++)
            {
                using (GraphicsBuffer vb = src.GetVertexBuffer(s))
                {
                    if (vb == null) { error = "no vertex buffer " + s; return null; }
                    // Not drawn yet, so not on the graphics card yet: maybe later.
                    if (vb.GetNativeBufferPtr() == IntPtr.Zero) { error = "not on the graphics card yet"; retry = true; return null; }
                    strides[s] = vb.stride;
                    streams[s] = new byte[vb.count * vb.stride];
                    vb.GetData(streams[s]);
                }
            }
            Vector3[] pos = null, nrm = null;
            Vector4[] tan = null;
            Vector2[] uv0 = null, uv1 = null;
            Color[] col = null;
            foreach (VertexAttributeDescriptor a in src.GetVertexAttributes())
            {
                int stream = src.GetVertexAttributeStream(a.attribute);
                int offset = src.GetVertexAttributeOffset(a.attribute);
                if (stream < 0 || stream >= streams.Length || offset < 0) continue;
                byte[] buf = streams[stream];
                int stride = strides[stream], size = Size(a.format), dim = a.dimension;
                if (size == 0 || offset + size * dim > stride || (long)stride * n > buf.Length) continue;
                float C(int i, int k) => Comp(buf, i * stride + offset + k * size, a.format);
                switch (a.attribute)
                {
                    case VertexAttribute.Position:
                        pos = new Vector3[n];
                        for (int i = 0; i < n; i++) pos[i] = new Vector3(C(i, 0), dim > 1 ? C(i, 1) : 0f, dim > 2 ? C(i, 2) : 0f);
                        break;
                    case VertexAttribute.Normal:
                        if (dim < 3) break;
                        nrm = new Vector3[n];
                        for (int i = 0; i < n; i++) nrm[i] = new Vector3(C(i, 0), C(i, 1), C(i, 2));
                        break;
                    case VertexAttribute.Tangent:
                        if (dim < 4) break;
                        tan = new Vector4[n];
                        for (int i = 0; i < n; i++) tan[i] = new Vector4(C(i, 0), C(i, 1), C(i, 2), C(i, 3) < 0f ? -1f : 1f);
                        break;
                    case VertexAttribute.TexCoord0:
                    case VertexAttribute.TexCoord1:
                        if (dim < 2) break;
                        var uv = new Vector2[n];
                        for (int i = 0; i < n; i++) uv[i] = new Vector2(C(i, 0), C(i, 1));
                        if (a.attribute == VertexAttribute.TexCoord0) uv0 = uv; else uv1 = uv;
                        break;
                    case VertexAttribute.Color:
                        col = new Color[n];
                        for (int i = 0; i < n; i++)
                            col[i] = new Color(C(i, 0), dim > 1 ? C(i, 1) : 0f, dim > 2 ? C(i, 2) : 0f, dim > 3 ? C(i, 3) : 1f);
                        break;
                }
            }
            if (pos == null) { error = "no positions"; return null; }

            int[] indices;
            using (GraphicsBuffer ib = src.GetIndexBuffer())
            {
                if (ib == null) { error = "no index buffer"; return null; }
                var raw = new byte[ib.count * ib.stride];
                ib.GetData(raw);
                indices = new int[ib.count];
                if (ib.stride == 2) for (int i = 0; i < indices.Length; i++) indices[i] = BitConverter.ToUInt16(raw, i * 2);
                else for (int i = 0; i < indices.Length; i++) indices[i] = BitConverter.ToInt32(raw, i * 4);
            }

            var copy = new Mesh { name = src.name + " (readable)" };
            if (n > 65000) copy.indexFormat = IndexFormat.UInt32;
            copy.vertices = pos;
            if (nrm != null) copy.normals = nrm;
            if (tan != null) copy.tangents = tan;
            if (uv0 != null) copy.uv = uv0;
            if (uv1 != null) copy.uv2 = uv1;
            if (col != null) copy.colors = col;
            copy.subMeshCount = src.subMeshCount;
            for (int s = 0; s < src.subMeshCount; s++)
            {
                SubMeshDescriptor d = src.GetSubMesh(s);
                var tris = new List<int>(d.indexCount);
                if (d.topology == MeshTopology.Triangles)
                    for (int k = 0; k < d.indexCount && d.indexStart + k < indices.Length; k++)
                    {
                        int v = indices[d.indexStart + k] + d.baseVertex;
                        tris.Add(v >= 0 && v < n ? v : 0);
                    }
                copy.SetTriangles(tris, s, false);
            }
            if (nrm == null) copy.RecalculateNormals();
            if (tan == null && uv0 != null) copy.RecalculateTangents();
            copy.RecalculateBounds();
            // Read back wrong (a layout not understood): the copy would not sit where the object is drawn.
            Bounds want = src.bounds, got = copy.bounds;
            float slack = 0.01f + want.size.magnitude * 0.05f;
            if ((got.center - want.center).magnitude > slack || (got.size - want.size).magnitude > slack)
            {
                error = $"read back wrong (bounds {got.center} {got.size}, drawn {want.center} {want.size})";
                UnityEngine.Object.Destroy(copy);
                return null;
            }
            return copy;
        }

        private const string CaptureShaderAsset = "Assets/Zandatsu/MeshCapture.shader";
        private const int CornerFloats = 18;   // position 3, normal 3, tangent 4, uv 2, uv2 2, colour 4: as the shader writes them
        private static Material captureMaterial;
        private static bool captureTried;

        /// <summary>
        /// A compressed mesh read through our shader (zandatsu.shaders, Hidden/Zandatsu/MeshCapture): each submesh drawn
        /// once, off screen, its triangles' corners (decoded by the graphics card) written into a buffer we can read, the
        /// same corners joined back into shared vertices.
        /// </summary>
        private static Mesh Capture(Mesh src, out string error, out bool retry)
        {
            error = null;
            retry = false;
            if (!captureTried)
            {
                captureTried = true;
                Shader s = FireCut.BundleShader(CaptureShaderAsset);
                if (s) captureMaterial = new Material(s) { name = "Zandatsu_MeshCapture" };
            }
            if (!captureMaterial) { error = "compressed vertices (half floats), kept on the graphics card only (no zandatsu.shaders to read them)"; return null; }
            using (GraphicsBuffer vb = src.GetVertexBuffer(0))
            {
                if (vb == null) { error = "no vertex buffer"; return null; }
                if (vb.GetNativeBufferPtr() == IntPtr.Zero) { error = "not on the graphics card yet"; retry = true; return null; }
            }

            var pos = new List<Vector3>();
            var nrm = new List<Vector3>();
            var tan = new List<Vector4>();
            var uv0 = new List<Vector2>();
            var uv1 = new List<Vector2>();
            var col = new List<Color>();
            var shared = new Dictionary<(Vector3, Vector3, Vector4, Vector2, Vector2, Color), int>();
            var tris = new List<int>[src.subMeshCount];
            RenderTexture target = RenderTexture.GetTemporary(4, 4, 0);
            RenderTexture was = RenderTexture.active;
            try
            {
                for (int s = 0; s < src.subMeshCount; s++)
                {
                    tris[s] = new List<int>();
                    SubMeshDescriptor d = src.GetSubMesh(s);
                    if (d.topology != MeshTopology.Triangles || d.indexCount < 3) continue;
                    int corners = d.indexCount - d.indexCount % 3;
                    var data = new float[corners * CornerFloats];
                    for (int i = 0; i < data.Length; i += CornerFloats) data[i] = float.NaN;   // corners not written stay NaN
                    using (var buffer = new ComputeBuffer(corners, CornerFloats * 4))
                    using (var cmd = new CommandBuffer { name = "Zandatsu mesh capture" })
                    {
                        buffer.SetData(data);
                        cmd.SetRenderTarget(target);
                        cmd.SetRandomWriteTarget(1, buffer);
                        cmd.DrawMesh(src, Matrix4x4.identity, captureMaterial, s, 0);
                        cmd.ClearRandomWriteTargets();
                        Graphics.ExecuteCommandBuffer(cmd);
                        buffer.GetData(data);
                    }
                    for (int c = 0; c < corners; c++)
                    {
                        int o = c * CornerFloats;
                        if (float.IsNaN(data[o])) { error = $"read back wrong (submesh {s}: corner {c} of {corners} not written)"; return null; }
                        var key = (new Vector3(data[o], data[o + 1], data[o + 2]), new Vector3(data[o + 3], data[o + 4], data[o + 5]),
                                   new Vector4(data[o + 6], data[o + 7], data[o + 8], data[o + 9] < 0f ? -1f : 1f),
                                   new Vector2(data[o + 10], data[o + 11]), new Vector2(data[o + 12], data[o + 13]),
                                   new Color(data[o + 14], data[o + 15], data[o + 16], data[o + 17]));
                        if (!shared.TryGetValue(key, out int v))
                        {
                            shared[key] = v = pos.Count;
                            pos.Add(key.Item1);
                            nrm.Add(key.Item2);
                            tan.Add(key.Item3);
                            uv0.Add(key.Item4);
                            uv1.Add(key.Item5);
                            col.Add(key.Item6);
                        }
                        tris[s].Add(v);
                    }
                }
            }
            finally
            {
                RenderTexture.active = was;
                RenderTexture.ReleaseTemporary(target);
            }

            var copy = new Mesh { name = src.name + " (readable)" };
            if (pos.Count > 65000) copy.indexFormat = IndexFormat.UInt32;
            copy.SetVertices(pos);
            if (src.HasVertexAttribute(VertexAttribute.Normal)) copy.SetNormals(nrm);
            if (src.HasVertexAttribute(VertexAttribute.Tangent)) copy.SetTangents(tan);
            if (src.HasVertexAttribute(VertexAttribute.TexCoord0)) copy.SetUVs(0, uv0);
            if (src.HasVertexAttribute(VertexAttribute.TexCoord1)) copy.SetUVs(1, uv1);
            if (src.HasVertexAttribute(VertexAttribute.Color)) copy.SetColors(col);
            copy.subMeshCount = src.subMeshCount;
            for (int s = 0; s < src.subMeshCount; s++) copy.SetTriangles(tris[s], s, false);
            if (!src.HasVertexAttribute(VertexAttribute.Normal)) copy.RecalculateNormals();
            if (!src.HasVertexAttribute(VertexAttribute.Tangent) && src.HasVertexAttribute(VertexAttribute.TexCoord0)) copy.RecalculateTangents();
            copy.RecalculateBounds();
            Bounds want = src.bounds, got = copy.bounds;
            float slack = 0.01f + want.size.magnitude * 0.05f;
            if ((got.center - want.center).magnitude > slack || (got.size - want.size).magnitude > slack)
            {
                error = $"read back wrong (bounds {got.center} {got.size}, drawn {want.center} {want.size})";
                UnityEngine.Object.Destroy(copy);
                return null;
            }
            return copy;
        }

        private static int Size(VertexAttributeFormat f)
        {
            switch (f)
            {
                case VertexAttributeFormat.Float32:
                case VertexAttributeFormat.UInt32:
                case VertexAttributeFormat.SInt32: return 4;
                case VertexAttributeFormat.Float16:
                case VertexAttributeFormat.UNorm16:
                case VertexAttributeFormat.SNorm16:
                case VertexAttributeFormat.UInt16:
                case VertexAttributeFormat.SInt16: return 2;
                case VertexAttributeFormat.UNorm8:
                case VertexAttributeFormat.SNorm8:
                case VertexAttributeFormat.UInt8:
                case VertexAttributeFormat.SInt8: return 1;
                default: return 0;
            }
        }

        private static float Comp(byte[] b, int at, VertexAttributeFormat f)
        {
            switch (f)
            {
                case VertexAttributeFormat.Float32: return BitConverter.ToSingle(b, at);
                case VertexAttributeFormat.Float16: return Mathf.HalfToFloat(BitConverter.ToUInt16(b, at));
                case VertexAttributeFormat.UNorm8: return b[at] / 255f;
                case VertexAttributeFormat.SNorm8: return Mathf.Max((sbyte)b[at] / 127f, -1f);
                case VertexAttributeFormat.UNorm16: return BitConverter.ToUInt16(b, at) / 65535f;
                case VertexAttributeFormat.SNorm16: return Mathf.Max(BitConverter.ToInt16(b, at) / 32767f, -1f);
                case VertexAttributeFormat.UInt8: return b[at];
                case VertexAttributeFormat.SInt8: return (sbyte)b[at];
                case VertexAttributeFormat.UInt16: return BitConverter.ToUInt16(b, at);
                case VertexAttributeFormat.SInt16: return BitConverter.ToInt16(b, at);
                case VertexAttributeFormat.UInt32: return BitConverter.ToUInt32(b, at);
                case VertexAttributeFormat.SInt32: return BitConverter.ToInt32(b, at);
                default: return 0f;
            }
        }
    }
}
