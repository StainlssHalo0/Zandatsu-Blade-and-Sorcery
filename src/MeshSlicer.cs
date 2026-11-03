using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zandatsu
{
    /// <summary>
    /// Flat mesh soup: world-space vertices, several submeshes, one material (+ property block) per submesh.
    /// w = per-vertex skin weights whose bone indices are skeleton body indices (normalized, sorted, boneIndex0 dominant).
    /// </summary>
    public class MeshData
    {
        public List<Vector3> v;
        public List<Vector3> n;
        public List<Vector4> t;
        public List<Vector2> uv;
        public List<Vector2> uv1;      // Manikin occlusion: the body shader hides skin under clothes by this channel
        public List<Color32> col;
        public bool hasUv1, hasCol;    // whether any source mesh had them (only then they are uploaded)
        public List<BoneWeight> w;
        public List<List<int>> subs = new List<List<int>>();
        public List<Material> mats = new List<Material>();
        public List<MaterialPropertyBlock> blocks = new List<MaterialPropertyBlock>();
        // Hybrid cuts rebuild the creature's own meshes from what survives a cut, so a snapshot can remember where every
        // vertex and submesh came from. o (null = not tracked): source renderer + vertex (or the two it was cut between).
        // subTag: source renderer << 8 | submesh, or -1 for caps and untracked data; submeshes only merge with equal tags.
        public List<VertexOrigin> o;
        public List<int> subTag = new List<int>();

        public MeshData(int capacity = 1024, bool trackOrigins = false)
        {
            v = new List<Vector3>(capacity);
            n = new List<Vector3>(capacity);
            t = new List<Vector4>(capacity);
            uv = new List<Vector2>(capacity);
            uv1 = new List<Vector2>(capacity);
            col = new List<Color32>(capacity);
            w = new List<BoneWeight>(capacity);
            if (trackOrigins) o = new List<VertexOrigin>(capacity);
        }

        private static readonly Color32 White = new Color32(255, 255, 255, 255);

        /// <summary>Empty mesh data that uploads the same optional channels as this one.</summary>
        public MeshData Sibling(int capacity) => new MeshData(capacity, o != null) { hasUv1 = hasUv1, hasCol = hasCol };

        // ---------- pool ----------
        // Cutting an armoured enemy allocated ~25 MB of temporary vertex lists, and Mono's collector then stopped every
        // thread (the game's too) for 10-20 ms about every sixth cut. The big temporary buffers are handed back and reused.
        private static readonly List<MeshData> pool = new List<MeshData>();
        private const int MaxPooled = 6;
        private const int MaxPooledCapacity = 150000;   // vertices: bigger buffers are left to the collector
        private List<VertexOrigin> spareOrigins;          // origin list kept while pooled
        private bool rented;                              // from the pool: its lists may be far bigger than its content

        /// <summary>Empty mesh data with room for capacity vertices, reused from the pool when possible (any thread).</summary>
        public static MeshData Rent(int capacity, bool trackOrigins, bool hasUv1, bool hasCol)
        {
            MeshData m = null;
            lock (pool)
            {
                // The smallest buffer that fits, else the biggest (grown): mismatched sizes made every cut regrow one.
                int best = -1;
                for (int i = 0; i < pool.Count; i++)
                {
                    int cap = pool[i].v.Capacity;
                    if (best < 0) { best = i; continue; }
                    int bestCap = pool[best].v.Capacity;
                    bool fits = cap >= capacity, bestFits = bestCap >= capacity;
                    if (fits && (!bestFits || cap < bestCap) || !fits && !bestFits && cap > bestCap) best = i;
                }
                if (best >= 0)
                {
                    m = pool[best];
                    pool.RemoveAt(best);
                }
            }
            if (m == null) m = new MeshData(capacity, trackOrigins);
            else
            {
                Reserve(m.v, capacity);
                Reserve(m.n, capacity);
                Reserve(m.t, capacity);
                Reserve(m.uv, capacity);
                Reserve(m.uv1, capacity);
                Reserve(m.col, capacity);
                Reserve(m.w, capacity);
                if (trackOrigins)
                {
                    m.o = m.spareOrigins ?? new List<VertexOrigin>(capacity);
                    m.spareOrigins = null;
                    Reserve(m.o, capacity);
                }
            }
            m.hasUv1 = hasUv1;
            m.hasCol = hasCol;
            m.rented = true;
            return m;
        }

        /// <summary>
        /// For data that lives on (a piece): a copy sized to its content if this came from the pool, which then goes back
        /// to it. A pooled buffer sized for a whole enemy kept a severed head holding megabytes of empty room.
        /// </summary>
        public MeshData Detach()
        {
            if (!rented) return this;
            MeshData c = Sibling(VertexCount);
            c.v.AddRange(v);
            c.n.AddRange(n);
            c.t.AddRange(t);
            c.uv.AddRange(uv);
            c.uv1.AddRange(uv1);
            c.col.AddRange(col);
            c.w.AddRange(w);
            if (o != null) c.o.AddRange(o);
            c.subs.AddRange(subs);   // the triangle lists move over: Return only empties the list holding them
            c.mats.AddRange(mats);
            c.blocks.AddRange(blocks);
            c.subTag.AddRange(subTag);
            Return(this);
            return c;
        }

        /// <summary>Hands mesh data back for reuse: only when nothing refers to it or to any of its lists any more.</summary>
        public static void Return(MeshData m)
        {
            if (m == null) return;
            m.rented = false;
            if (m.v.Capacity > MaxPooledCapacity) return;
            m.v.Clear();
            m.n.Clear();
            m.t.Clear();
            m.uv.Clear();
            m.uv1.Clear();
            m.col.Clear();
            m.w.Clear();
            if (m.o != null)
            {
                m.o.Clear();
                m.spareOrigins = m.o;
                m.o = null;
            }
            m.subs.Clear();
            m.mats.Clear();
            m.blocks.Clear();
            m.subTag.Clear();
            lock (pool)
            {
                if (pool.Contains(m)) return;
                if (pool.Count < MaxPooled) pool.Add(m);
                else
                {
                    // Full: keep the bigger buffers.
                    int smallest = 0;
                    for (int i = 1; i < pool.Count; i++) if (pool[i].v.Capacity < pool[smallest].v.Capacity) smallest = i;
                    if (pool[smallest].v.Capacity < m.v.Capacity) pool[smallest] = m;
                }
            }
        }

        private static void Reserve<T>(List<T> list, int capacity)
        {
            if (list.Capacity < capacity) list.Capacity = capacity;
        }

        public int VertexCount => v.Count;

        public int TriangleCount
        {
            get
            {
                int c = 0;
                foreach (List<int> s in subs) c += s.Count / 3;
                return c;
            }
        }

        public int AddVertex(Vector3 pos, Vector3 nrm, Vector4 tan, Vector2 tex, BoneWeight weight)
        {
            return AddVertex(pos, nrm, tan, tex, Vector2.zero, White, weight);
        }

        public int AddVertex(Vector3 pos, Vector3 nrm, Vector4 tan, Vector2 tex, Vector2 tex1, Color32 color, BoneWeight weight)
        {
            v.Add(pos);
            n.Add(nrm);
            t.Add(tan);
            uv.Add(tex);
            uv1.Add(tex1);
            col.Add(color);
            w.Add(weight);
            o?.Add(VertexOrigin.None);
            return v.Count - 1;
        }

        /// <summary>Copies vertex i of src with all its channels.</summary>
        public int CopyVertex(MeshData src, int i)
        {
            int r = AddVertex(src.v[i], src.n[i], src.t[i], src.uv[i], src.uv1[i], src.col[i], src.w[i]);
            if (o != null && src.o != null) o[r] = src.o[i];
            return r;
        }

        /// <summary>
        /// Adds triangles to a submesh; caps (no property block) merge into an existing submesh with the same material.
        /// Submeshes of tracked creature meshes (tag >= 0) only merge with the same tag, so each keeps its source.
        /// </summary>
        public void AddTriangles(Material mat, MaterialPropertyBlock block, List<int> tris, int tag = -1)
        {
            if (tris.Count == 0) return;
            if (block == null || tag >= 0)
            {
                for (int i = 0; i < mats.Count; i++)
                {
                    if (subTag[i] != tag) continue;
                    if (tag >= 0 || (ReferenceEquals(mats[i], mat) && blocks[i] == null))
                    {
                        subs[i].AddRange(tris);
                        return;
                    }
                }
            }
            subs.Add(tris);
            mats.Add(mat);
            blocks.Add(block);
            subTag.Add(tag);
        }

        /// <summary>Appends a mesh transformed by m (rotation + translation only). Weights must already use body indices.</summary>
        /// <param name="source">Index of the source renderer for hybrid cuts (vertex/submesh origins), or -1.</param>
        public void Append(Mesh mesh, Matrix4x4 m, Material[] materials, MaterialPropertyBlock[] matBlocks, BoneWeight[] weights, int source = -1)
        {
            int baseIndex = v.Count;
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var tans = new List<Vector4>();
            var uvs = new List<Vector2>();
            var uvs1 = new List<Vector2>();
            var colors = new List<Color32>();
            mesh.GetVertices(verts);
            mesh.GetNormals(norms);
            mesh.GetTangents(tans);
            mesh.GetUVs(0, uvs);
            mesh.GetUVs(1, uvs1);
            mesh.GetColors(colors);
            if (uvs1.Count > 0) hasUv1 = true;
            if (colors.Count > 0) hasCol = true;
            int count = verts.Count;
            for (int i = 0; i < count; i++)
            {
                Vector3 nn = i < norms.Count ? m.MultiplyVector(norms[i]).normalized : Vector3.up;
                Vector4 tt = Vector4.zero;
                if (i < tans.Count)
                {
                    Vector3 td = m.MultiplyVector(new Vector3(tans[i].x, tans[i].y, tans[i].z)).normalized;
                    tt = new Vector4(td.x, td.y, td.z, tans[i].w);
                }
                int vi = AddVertex(m.MultiplyPoint3x4(verts[i]), nn, tt, i < uvs.Count ? uvs[i] : Vector2.zero,
                    i < uvs1.Count ? uvs1[i] : Vector2.zero, i < colors.Count ? colors[i] : White,
                    weights != null && i < weights.Length ? weights[i] : Weights.Single(0));
                if (o != null && source >= 0) o[vi] = new VertexOrigin(source, i, -1, 0f);
            }
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                int[] tri = mesh.GetTriangles(s);
                var list = new List<int>(tri.Length);
                for (int i = 0; i < tri.Length; i++) list.Add(tri[i] + baseIndex);
                Material mat = materials != null && materials.Length > 0 ? materials[Mathf.Min(s, materials.Length - 1)] : null;
                MaterialPropertyBlock blk = matBlocks != null && s < matBlocks.Length ? matBlocks[s] : null;
                subs.Add(list);
                mats.Add(mat);
                blocks.Add(blk);
                subTag.Add(o != null && source >= 0 ? VertexOrigin.Tag(source, s) : -1);
            }
        }

        /// <summary>Same topology, materials and weights with new positions (a fresh BakeMesh of a piece).</summary>
        public MeshData WithPose(List<Vector3> verts, List<Vector3> norms, List<Vector4> tans)
        {
            return new MeshData(0)
            {
                v = verts,
                n = norms.Count == verts.Count ? norms : new List<Vector3>(n),
                t = tans.Count == verts.Count ? tans : new List<Vector4>(t),
                uv = uv,
                uv1 = uv1,
                col = col,
                hasUv1 = hasUv1,
                hasCol = hasCol,
                w = w,
                subs = subs,
                mats = mats,
                blocks = blocks,
                subTag = subTag
            };
        }

        public Bounds GetBounds()
        {
            var b = new Bounds(v.Count > 0 ? v[0] : Vector3.zero, Vector3.zero);
            for (int i = 0; i < v.Count; i++) b.Encapsulate(v[i]);
            return b;
        }

        public void DropEmptySubmeshes()
        {
            for (int s = subs.Count - 1; s >= 0; s--)
            {
                if (subs[s].Count == 0)
                {
                    subs.RemoveAt(s);
                    mats.RemoveAt(s);
                    blocks.RemoveAt(s);
                    subTag.RemoveAt(s);
                }
            }
        }

        /// <summary>
        /// Repairs vertex data a lit shader cannot normalize. A zero or NaN normal/tangent becomes NaN in the shader, and
        /// bloom smears every NaN pixel into a blinding white blob (seen on bright levels like the Outpost). Sources: clothing
        /// without tangents (Append stores zero), and cut-edge vertices that blend opposite normals of thin cloth.
        /// Normals: renormalized; a zero/NaN one is rebuilt from the triangles around the vertex. Tangents: made
        /// perpendicular to the normal, rebuilt if that leaves nothing, w = ±1. Also clears NaN positions, uvs, weights.
        /// Returns how many vertices needed a fix (normals, tangents, other). Pure managed code, safe on a worker thread.
        /// </summary>
        public (int normals, int tangents, int other) Sanitize()
        {
            int count = v.Count, badN = 0, badT = 0, other = 0;
            bool[] rebuild = null, badPos = null;
            for (int i = 0; i < count; i++)
            {
                if (!Finite(v[i]))
                {
                    if (badPos == null) badPos = new bool[count];
                    badPos[i] = true;
                    other++;
                }
                Vector3 nn = n[i];
                float len = nn.sqrMagnitude;
                if (!Finite(nn) || len < 1e-10f)
                {
                    if (rebuild == null) rebuild = new bool[count];
                    rebuild[i] = true;
                    n[i] = Vector3.zero;   // accumulates face normals below
                    badN++;
                }
                else if (Mathf.Abs(len - 1f) > 1e-3f) n[i] = nn / Mathf.Sqrt(len);
                if (!Finite(uv[i])) { uv[i] = Vector2.zero; other++; }
                if (!Finite(w[i])) { w[i] = Weights.Single(Math.Max(w[i].boneIndex0, 0)); other++; }
            }

            if (badPos != null)
            {
                // NaN positions: move onto a finite vertex of a triangle that uses them (the triangle collapses).
                for (int s = 0; s < subs.Count; s++)
                {
                    List<int> tris = subs[s];
                    for (int k = 0; k + 2 < tris.Count; k += 3)
                    {
                        int a = tris[k], b = tris[k + 1], c = tris[k + 2];
                        int good = !badPos[a] ? a : !badPos[b] ? b : !badPos[c] ? c : -1;
                        if (good < 0) continue;
                        if (badPos[a]) { v[a] = v[good]; badPos[a] = false; }
                        if (badPos[b]) { v[b] = v[good]; badPos[b] = false; }
                        if (badPos[c]) { v[c] = v[good]; badPos[c] = false; }
                    }
                }
                for (int i = 0; i < count; i++) if (badPos[i]) v[i] = Vector3.zero;   // unused by any triangle
            }

            if (rebuild != null)
            {
                // Zero/NaN normals: area-weighted normal of the triangles around the vertex.
                for (int s = 0; s < subs.Count; s++)
                {
                    List<int> tris = subs[s];
                    for (int k = 0; k + 2 < tris.Count; k += 3)
                    {
                        int a = tris[k], b = tris[k + 1], c = tris[k + 2];
                        Vector3 pa = v[a], pb = v[b], pc = v[c];
                        Vector3 fn = Vector3.Cross(pb - pa, pc - pa);
                        if (!Finite(fn)) continue;
                        if (rebuild[a]) n[a] += fn;
                        if (rebuild[b]) n[b] += fn;
                        if (rebuild[c]) n[c] += fn;
                    }
                }
                for (int i = 0; i < count; i++)
                {
                    if (!rebuild[i]) continue;
                    Vector3 nn = n[i];
                    n[i] = Finite(nn) && nn.sqrMagnitude > 1e-20f ? nn.normalized : Vector3.up;
                }
            }

            for (int i = 0; i < count; i++)
            {
                Vector3 nn = n[i];
                Vector4 tt = t[i];
                var td = new Vector3(tt.x, tt.y, tt.z);
                bool ok = Finite(td);
                if (ok)
                {
                    td -= nn * Vector3.Dot(nn, td);
                    ok = td.sqrMagnitude > 1e-8f;
                }
                if (!ok)
                {
                    td = Vector3.Cross(nn, Mathf.Abs(nn.y) < 0.9f ? Vector3.up : Vector3.right);
                    badT++;
                }
                td.Normalize();
                float sign = float.IsNaN(tt.w) || tt.w >= 0f ? 1f : -1f;
                t[i] = new Vector4(td.x, td.y, td.z, sign);
            }
            return (badN, badT, other);
        }

        private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
        private static bool Finite(Vector2 a) => Finite(a.x) && Finite(a.y);
        private static bool Finite(Vector3 a) => Finite(a.x) && Finite(a.y) && Finite(a.z);
        private static bool Finite(BoneWeight b) => Finite(b.weight0) && Finite(b.weight1) && Finite(b.weight2) && Finite(b.weight3);

        public Mesh ToSkinnedMesh(string name, Matrix4x4[] bindposes)
        {
            DropEmptySubmeshes();
            var mesh = new Mesh { name = name };
            if (v.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(v);
            mesh.SetNormals(n);
            mesh.SetTangents(t);
            mesh.SetUVs(0, uv);
            if (hasUv1) mesh.SetUVs(1, uv1);
            if (hasCol) mesh.SetColors(col);
            mesh.boneWeights = w.ToArray();
            mesh.bindposes = bindposes;
            mesh.subMeshCount = subs.Count;
            for (int s = 0; s < subs.Count; s++) mesh.SetTriangles(subs[s], s, false);
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    /// <summary>
    /// Where a vertex of a creature snapshot came from: source renderer r, its vertex a, or a point between vertices a
    /// and b at t (a cut edge). r = -1: made by the slicer (caps, steel bands, fills).
    /// </summary>
    /// <summary>
    /// Plain arrays for the vertex channels of one mesh, filled from several threads and then copied into MeshData lists
    /// in one block each. Writing the lists themselves from several threads made every write bump the list's shared
    /// version counter, and the cores fought over it: slower than one thread. Pooled like MeshData.
    /// </summary>
    public sealed class VertexBuffers
    {
        public Vector3[] v = new Vector3[0], n = new Vector3[0];
        public Vector4[] t = new Vector4[0];
        public Vector2[] uv = new Vector2[0], uv1 = new Vector2[0];
        public Color32[] col = new Color32[0];
        public BoneWeight[] w = new BoneWeight[0];
        public VertexOrigin[] o = new VertexOrigin[0];

        private static readonly List<VertexBuffers> pool = new List<VertexBuffers>();
        private const int MaxPooled = 4;

        /// <summary>Buffers for at least count vertices (any thread): the smallest pooled set that fits, else grown.</summary>
        public static VertexBuffers Rent(int count)
        {
            VertexBuffers b = null;
            lock (pool)
            {
                int best = -1;
                for (int i = 0; i < pool.Count; i++)
                {
                    int cap = pool[i].v.Length;
                    if (best < 0) { best = i; continue; }
                    int bestCap = pool[best].v.Length;
                    bool fits = cap >= count, bestFits = bestCap >= count;
                    if (fits && (!bestFits || cap < bestCap) || !fits && !bestFits && cap > bestCap) best = i;
                }
                if (best >= 0)
                {
                    b = pool[best];
                    pool.RemoveAt(best);
                }
            }
            if (b == null) b = new VertexBuffers();
            if (b.v.Length < count)
            {
                b.v = new Vector3[count];
                b.n = new Vector3[count];
                b.t = new Vector4[count];
                b.uv = new Vector2[count];
                b.uv1 = new Vector2[count];
                b.col = new Color32[count];
                b.w = new BoneWeight[count];
                b.o = new VertexOrigin[count];
            }
            return b;
        }

        public static void Return(VertexBuffers b)
        {
            if (b == null || b.v.Length > 150000) return;
            lock (pool)
            {
                if (pool.Contains(b)) return;
                if (pool.Count < MaxPooled) pool.Add(b);
                else
                {
                    int smallest = 0;
                    for (int i = 1; i < pool.Count; i++) if (pool[i].v.Length < pool[smallest].v.Length) smallest = i;
                    if (pool[smallest].v.Length < b.v.Length) pool[smallest] = b;
                }
            }
        }

        /// <summary>Appends the first count vertices to m's lists (origins only if m tracks them).</summary>
        public void AppendTo(MeshData m, int count)
        {
            m.v.AddRange(new ArraySegment<Vector3>(v, 0, count));
            m.n.AddRange(new ArraySegment<Vector3>(n, 0, count));
            m.t.AddRange(new ArraySegment<Vector4>(t, 0, count));
            m.uv.AddRange(new ArraySegment<Vector2>(uv, 0, count));
            m.uv1.AddRange(new ArraySegment<Vector2>(uv1, 0, count));
            m.col.AddRange(new ArraySegment<Color32>(col, 0, count));
            m.w.AddRange(new ArraySegment<BoneWeight>(w, 0, count));
            if (m.o != null) m.o.AddRange(new ArraySegment<VertexOrigin>(o, 0, count));
        }
    }

    public struct VertexOrigin
    {
        public int r, a, b;
        public float t;

        public VertexOrigin(int r, int a, int b, float t)
        {
            this.r = r;
            this.a = a;
            this.b = b;
            this.t = t;
        }

        public static readonly VertexOrigin None = new VertexOrigin(-1, -1, -1, 0f);

        public static int Tag(int renderer, int submesh) => (renderer << 8) | (submesh & 0xff);
        public static int TagRenderer(int tag) => tag >> 8;
        public static int TagSubmesh(int tag) => tag & 0xff;

        /// <summary>A cut edge between two vertices of the same source renderer (snapshots only hold plain vertices).</summary>
        public static VertexOrigin Between(VertexOrigin x, VertexOrigin y, float t)
        {
            if (x.r < 0 || x.r != y.r || x.b >= 0 || y.b >= 0) return None;
            return new VertexOrigin(x.r, x.a, y.a, t);
        }
    }

    /// <summary>
    /// One ragdoll collider in world space at snapshot time (sphere: a + r; capsule: segment a-b + r; box: centre a,
    /// rotation, half extents). Pure managed math, safe on worker threads.
    /// </summary>
    public struct VolumeShape
    {
        public ShapeKind kind;
        public Vector3 a, b, half;
        public Quaternion rot;
        public float r;

        public bool Contains(Vector3 p, float margin)
        {
            switch (kind)
            {
                case ShapeKind.Sphere:
                    return (p - a).sqrMagnitude <= (r + margin) * (r + margin);
                case ShapeKind.Capsule:
                {
                    Vector3 ab = b - a;
                    float len2 = ab.sqrMagnitude;
                    float t = len2 > 1e-12f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2) : 0f;
                    return (p - (a + ab * t)).sqrMagnitude <= (r + margin) * (r + margin);
                }
                case ShapeKind.Box:
                {
                    Vector3 l = new Quaternion(-rot.x, -rot.y, -rot.z, rot.w) * (p - a);
                    return Mathf.Abs(l.x) <= half.x + margin && Mathf.Abs(l.y) <= half.y + margin && Mathf.Abs(l.z) <= half.z + margin;
                }
                default:
                    return false;
            }
        }

        /// <summary>The body's colliders in world space (convex mesh colliders are skipped).</summary>
        public static List<VolumeShape>[] FromSkeleton(Skeleton skel)
        {
            var result = new List<VolumeShape>[skel.bodies.Count];
            for (int bi = 0; bi < skel.bodies.Count; bi++)
            {
                SkelBody body = skel.bodies[bi];
                var list = new List<VolumeShape>();
                foreach (ShapeSpec sh in body.shapes)
                {
                    Vector3 s = new Vector3(Mathf.Abs(sh.scale.x), Mathf.Abs(sh.scale.y), Mathf.Abs(sh.scale.z));
                    Quaternion rot = body.rot * sh.localRot;
                    Vector3 c = body.pos + body.rot * (sh.localPos + sh.localRot * Vector3.Scale(s, sh.center));
                    switch (sh.kind)
                    {
                        case ShapeKind.Sphere:
                            list.Add(new VolumeShape { kind = ShapeKind.Sphere, a = c, r = sh.radius * Mathf.Max(s.x, Mathf.Max(s.y, s.z)) });
                            break;
                        case ShapeKind.Capsule:
                        {
                            int d = Mathf.Clamp(sh.direction, 0, 2);
                            Vector3 axis = rot * (d == 0 ? Vector3.right : d == 1 ? Vector3.up : Vector3.forward);
                            float r = sh.radius * Mathf.Max(s[(d + 1) % 3], s[(d + 2) % 3]);
                            float halfLen = Mathf.Max(0f, sh.height * s[d] * 0.5f - r);
                            list.Add(new VolumeShape { kind = ShapeKind.Capsule, a = c - axis * halfLen, b = c + axis * halfLen, r = r });
                            break;
                        }
                        case ShapeKind.Box:
                            list.Add(new VolumeShape { kind = ShapeKind.Box, a = c, rot = rot, half = Vector3.Scale(s, sh.size) * 0.5f });
                            break;
                    }
                }
                result[bi] = list;
            }
            return result;
        }
    }

    /// <summary>Skin weight helpers; pure managed code (safe on worker threads).</summary>
    public static class Weights
    {
        [ThreadStatic] private static int[] si;
        [ThreadStatic] private static float[] sw;

        public static BoneWeight Single(int bone) => new BoneWeight { boneIndex0 = bone, weight0 = 1f };

        /// <summary>Merges duplicate bones, keeps the 4 strongest, normalizes, sorts descending.</summary>
        public static BoneWeight Normalize(BoneWeight a) => Mix(a, 1f, a, 0f);

        public static BoneWeight Lerp(BoneWeight a, BoneWeight b, float t) => Mix(a, 1f - t, b, t);

        private static BoneWeight Mix(BoneWeight a, float fa, BoneWeight b, float fb)
        {
            Scratch();
            int c = 0;
            c = Add(c, a.boneIndex0, a.weight0 * fa);
            c = Add(c, a.boneIndex1, a.weight1 * fa);
            c = Add(c, a.boneIndex2, a.weight2 * fa);
            c = Add(c, a.boneIndex3, a.weight3 * fa);
            c = Add(c, b.boneIndex0, b.weight0 * fb);
            c = Add(c, b.boneIndex1, b.weight1 * fb);
            c = Add(c, b.boneIndex2, b.weight2 * fb);
            c = Add(c, b.boneIndex3, b.weight3 * fb);
            return Pack(c, a.boneIndex0);
        }

        /// <summary>Maps bones through remap (-1 = drop) and renormalizes. False if nothing remains.</summary>
        public static bool Remap(BoneWeight a, int[] remap, out BoneWeight r)
        {
            Scratch();
            int c = 0;
            c = Add(c, Map(remap, a.boneIndex0), a.weight0);
            c = Add(c, Map(remap, a.boneIndex1), a.weight1);
            c = Add(c, Map(remap, a.boneIndex2), a.weight2);
            c = Add(c, Map(remap, a.boneIndex3), a.weight3);
            r = c > 0 ? Pack(c, 0) : default;
            return c > 0;
        }

        private static void Scratch()
        {
            if (si != null) return;
            si = new int[8];
            sw = new float[8];
        }

        private static int Map(int[] remap, int bone) => bone >= 0 && bone < remap.Length ? remap[bone] : -1;

        private static int Add(int c, int bone, float weight)
        {
            if (weight <= 0f || bone < 0) return c;
            for (int i = 0; i < c; i++)
            {
                if (si[i] == bone) { sw[i] += weight; return c; }
            }
            si[c] = bone;
            sw[c] = weight;
            return c + 1;
        }

        private static BoneWeight Pack(int c, int fallback)
        {
            if (c == 0) return Single(Math.Max(fallback, 0));
            for (int i = 0; i < c - 1; i++)
            {
                int best = i;
                for (int j = i + 1; j < c; j++) if (sw[j] > sw[best]) best = j;
                if (best == i) continue;
                (si[i], si[best]) = (si[best], si[i]);
                (sw[i], sw[best]) = (sw[best], sw[i]);
            }
            if (c > 4) c = 4;
            float sum = 0f;
            for (int i = 0; i < c; i++) sum += sw[i];
            if (sum <= 0f) return Single(si[0]);
            var r = new BoneWeight { boneIndex0 = si[0], weight0 = sw[0] / sum };
            if (c > 1) { r.boneIndex1 = si[1]; r.weight1 = sw[1] / sum; }
            if (c > 2) { r.boneIndex2 = si[2]; r.weight2 = sw[2] / sum; }
            if (c > 3) { r.boneIndex3 = si[3]; r.weight3 = sw[3] / sum; }
            return r;
        }
    }

    /// <summary>
    /// A fire cut's distance field: for every spot of the cut face, how far in from the outline and where along it.
    /// Filled by the slice on a worker, turned into a texture on the main thread (FireCut.Ready).
    /// </summary>
    public sealed class BurnField
    {
        public int size;        // texels per side
        public byte[] texels;   // RGBA32: r = 1 on the outline .. 0 a band width in; g, b = cos, sin of the place along it
    }

    public static class MeshSlicer
    {
        private struct Seg
        {
            public Vector3 a, b;
            public BoneWeight wa, wb;
            public Vector3 n;   // the surface's outward normal there (its vertex normals): which side of the outline is solid
        }

        private const float RingWidth = 0.006f;   // m, glowing band along the cut edge of an armour plate

        /// <summary>
        /// Cuts src by the plane through point with normal. pos = side the normal points to.
        /// Both halves get a flesh cap made of the cross-section loops. Returns false if the plane misses.
        /// forced (optional, per vertex): 0 = cut by the plane, +1/-1 = keep on that side uncut (parts the blade never
        /// reached); a triangle touching a forced vertex is kept whole on the forced side.
        /// metal (optional): submesh materials of metal armour; their cross-section gets a thin band of steel
        /// (the plate's cut edge) instead of a flesh disc.
        /// holes (objects and weapons): an outline with empty space inside (the inner wall of a pot, a barrel, a cup; told
        /// by the surface's normals) is a hole in the cut face around it, not a face of its own.
        /// </summary>
        /// <param name="volumes">Per skeleton body, its colliders in world space: the fill under the caps follows them.</param>
        /// <summary>Where the last Slice on this thread spent its time (diagnostics).</summary>
        [ThreadStatic] public static string LastDetail;
        [ThreadStatic] private static string capShape;   // the last caps' outlines and holes, for LastDetail

        public static bool Slice(MeshData src, Vector3 point, Vector3 normal, Material fill, out MeshData pos, out MeshData neg,
            sbyte[] forced = null, HashSet<Material> metal = null, Material steel = null, List<VolumeShape>[] volumes = null,
            Material burn = null, BurnField burnField = null, bool holes = false, float hollow = 0f, Material inside = null)
        {
            pos = null;
            neg = null;
            LastDetail = "";
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int vc = src.v.Count;
            if (vc == 0) return false;
            normal.Normalize();

            var d = new float[vc];
            var isPos = new bool[vc];
            int posCount = 0;
            for (int i = 0; i < vc; i++)
            {
                d[i] = Vector3.Dot(src.v[i] - point, normal);
                isPos[i] = forced != null && forced[i] != 0 ? forced[i] > 0 : d[i] >= 0f;
                if (isPos[i]) posCount++;
            }
            if (posCount == 0 || posCount == vc) return false;

            // Groups of submeshes that share no vertex (in practice one per source renderer) are planned in parallel: which
            // source vertex or cut-edge point becomes each output vertex, in the order one pass over everything made them.
            // Then each group's vertices are written straight into two buffers (from the pool) at the group's offset, also
            // in parallel. Same result as one pass; time about that of the biggest group; no per-group buffers to throw away.
            double tClassify = clock.Elapsed.TotalMilliseconds;
            var mapPos = new int[vc];
            var mapNeg = new int[vc];
            for (int i = 0; i < vc; i++) { mapPos[i] = -1; mapNeg[i] = -1; }
            List<List<int>> groups = SubmeshGroups(src, out _);
            if (groups.Count == 0) return false;
            var parts = new Part[groups.Count];
            System.Threading.Tasks.Parallel.For(0, groups.Count, g =>
                parts[g] = PlanGroup(src, groups[g], d, isPos, forced, metal, steel, mapPos, mapNeg));

            double tGroups = clock.Elapsed.TotalMilliseconds;
            var offPos = new int[parts.Length];
            var offNeg = new int[parts.Length];
            int posTotal = 0, negTotal = 0, segCount = 0;
            for (int g = 0; g < parts.Length; g++)
            {
                offPos[g] = posTotal;
                offNeg[g] = negTotal;
                posTotal += parts[g].posOrder.Count;
                negTotal += parts[g].negOrder.Count;
                segCount += parts[g].segs.Count + parts[g].metalSegs.Count;
            }
            // Room for the caps, steel bands and fills added next: with too little, all eight lists doubled and copied.
            int capRoom = segCount * 4 + 1024;
            pos = MeshData.Rent(posTotal + capRoom, src.o != null, src.hasUv1, src.hasCol);
            neg = MeshData.Rent(negTotal + capRoom, src.o != null, src.hasUv1, src.hasCol);
            VertexBuffers bufPos = VertexBuffers.Rent(posTotal), bufNeg = VertexBuffers.Rent(negTotal);
            var writes = new List<(int part, bool positive, int start, int end)>();
            for (int g = 0; g < parts.Length; g++)
            {
                for (int start = 0; start < parts[g].posOrder.Count; start += WriteChunk)
                    writes.Add((g, true, start, Math.Min(parts[g].posOrder.Count, start + WriteChunk)));
                for (int start = 0; start < parts[g].negOrder.Count; start += WriteChunk)
                    writes.Add((g, false, start, Math.Min(parts[g].negOrder.Count, start + WriteChunk)));
            }
            bool origins = src.o != null;
            System.Threading.Tasks.Parallel.For(0, writes.Count, w =>
            {
                (int g, bool positive, int start, int end) = writes[w];
                Part part = parts[g];
                if (positive) Write(bufPos, offPos[g], part.posOrder, part.lerps, src, start, end, origins);
                else Write(bufNeg, offNeg[g], part.negOrder, part.lerps, src, start, end, origins);
            });
            bufPos.AppendTo(pos, posTotal);
            bufNeg.AppendTo(neg, negTotal);
            VertexBuffers.Return(bufPos);
            VertexBuffers.Return(bufNeg);
            var segs = new List<Seg>(512);
            var metalSegs = new List<Seg>();
            for (int g = 0; g < parts.Length; g++)
            {
                Part part = parts[g];
                for (int k = 0; k < part.subIndex.Count; k++)
                {
                    int sub = part.subIndex[k];
                    pos.AddTriangles(src.mats[sub], src.blocks[sub], Offset(part.lp[k], offPos[g]), src.subTag[sub]);
                    neg.AddTriangles(src.mats[sub], src.blocks[sub], Offset(part.ln[k], offNeg[g]), src.subTag[sub]);
                }
                segs.AddRange(part.segs);
                metalSegs.AddRange(part.metalSegs);
            }

            if (pos.TriangleCount == 0 || neg.TriangleCount == 0)
            {
                // Everything on one side after all: the rented buffers go back to the pool.
                MeshData.Return(pos);
                MeshData.Return(neg);
                pos = neg = null;
                return false;
            }

            double tTris = clock.Elapsed.TotalMilliseconds;
            int holesFound = !ReferenceEquals(fill, null) && segs.Count > 0 ? BuildCaps(segs, normal, fill, pos, neg, holes, hollow, inside) : 0;
            if (!ReferenceEquals(burn, null) && burnField == null && segs.Count > 0) BuildBurnEdge(segs, normal, burn, pos, neg);
            // The rim on the cut face itself also covers flesh only the body fill makes, so it comes after the fill.
            List<Seg> fleshSegs = !ReferenceEquals(burn, null) && burnField != null ? new List<Seg>(segs) : null;
            var fillRegions = fleshSegs != null ? new List<FillRegion>() : null;
            double tCaps = clock.Elapsed.TotalMilliseconds;
            if (metalSegs.Count > 0) BuildRings(metalSegs, normal, steel, pos, neg);
            double tRings = clock.Elapsed.TotalMilliseconds;
            // Not under a hollow object's cut face (a cup, a basket): the fill spans its outline and covered the hole.
            if (!ReferenceEquals(fill, null) && segs.Count + metalSegs.Count > 0 && holesFound == 0)
            {
                segs.AddRange(metalSegs);
                BuildBodyFill(segs, normal, fill, pos, neg, volumes, fillRegions);
            }
            double tFill = clock.Elapsed.TotalMilliseconds;
            if (fleshSegs != null) BuildBurnCap(fleshSegs, normal, burn, pos, neg, burnField, fillRegions);
            LastDetail = $"tris {tTris:0.0} (classify {tClassify:0.0}, {groups.Count} groups {tGroups - tClassify:0.0}, join {tTris - tGroups:0.0}), caps {tCaps - tTris:0.0} ({capShape}), rings {tRings - tCaps:0.0}, fill {tFill - tRings:0.0}" + (fleshSegs != null ? $", burn {clock.Elapsed.TotalMilliseconds - tFill:0.0}" : "");
            return true;
        }

        /// <summary>
        /// One group of submeshes planned on its own: per side the output vertices in order (a source vertex, or -(k+1)
        /// for cut-edge point k), triangles per submesh in local indices, cut segments.
        /// </summary>
        private sealed class Part
        {
            public readonly List<int> posOrder = new List<int>(), negOrder = new List<int>();
            public readonly List<Lerp> lerps = new List<Lerp>();
            public readonly List<Seg> segs = new List<Seg>(), metalSegs = new List<Seg>();
            public readonly List<int> subIndex = new List<int>();   // source submesh of lp[k] / ln[k]
            public readonly List<List<int>> lp = new List<List<int>>(), ln = new List<List<int>>();
        }

        /// <summary>A point on the cut edge between source vertices a and b.</summary>
        private struct Lerp
        {
            public int a, b;
            public float t;
            public BoneWeight w;
        }

        private const int WriteChunk = 4096;   // output vertices per parallel write task

        /// <summary>
        /// Submeshes grouped so that no two groups share a vertex (overlapping vertex index ranges join a group): each
        /// group's submeshes in order, groups in the order of their first submesh. spans: vertex index range per group.
        /// </summary>
        private static List<List<int>> SubmeshGroups(MeshData src, out List<int> spans)
        {
            int sc = src.subs.Count;
            var lo = new int[sc];
            var hi = new int[sc];
            var order = new List<int>(sc);
            for (int s = 0; s < sc; s++)
            {
                List<int> tris = src.subs[s];
                int a = int.MaxValue, b = -1;
                for (int k = 0; k < tris.Count; k++)
                {
                    int i = tris[k];
                    if (i < a) a = i;
                    if (i > b) b = i;
                }
                lo[s] = a;
                hi[s] = b;
                if (b >= 0) order.Add(s);   // empty submeshes add nothing
            }
            order.Sort((x, y) => lo[x] != lo[y] ? lo[x].CompareTo(lo[y]) : x.CompareTo(y));
            var groups = new List<List<int>>();
            var groupLo = new List<int>();
            var groupHi = new List<int>();
            foreach (int s in order)
            {
                int g = groups.Count - 1;
                if (g < 0 || lo[s] > groupHi[g])
                {
                    groups.Add(new List<int>());
                    groupLo.Add(lo[s]);
                    groupHi.Add(hi[s]);
                    g++;
                }
                groups[g].Add(s);
                groupHi[g] = Math.Max(groupHi[g], hi[s]);
            }
            var index = new List<int>(groups.Count);
            for (int g = 0; g < groups.Count; g++)
            {
                groups[g].Sort();
                index.Add(g);
            }
            index.Sort((x, y) => groups[x][0].CompareTo(groups[y][0]));
            var sorted = new List<List<int>>(groups.Count);
            spans = new List<int>(groups.Count);
            foreach (int g in index)
            {
                sorted.Add(groups[g]);
                spans.Add(groupHi[g] - groupLo[g] + 1);
            }
            return sorted;
        }

        private static List<int> Offset(List<int> tris, int offset)
        {
            if (offset != 0) for (int i = 0; i < tris.Count; i++) tris[i] += offset;
            return tris;
        }

        /// <summary>
        /// Plans one group of submeshes (any thread; groups touch disjoint parts of mapPos/mapNeg, which hold local indices).
        /// Every decision is the one slicing made before; only the vertex data is written later (Write).
        /// </summary>
        private static Part PlanGroup(MeshData src, List<int> group, float[] d, bool[] isPos, sbyte[] forced,
            HashSet<Material> metal, Material steel, int[] mapPos, int[] mapNeg)
        {
            var part = new Part();
            List<int> pos = part.posOrder, neg = part.negOrder;
            List<Seg> segs = part.segs, metalSegs = part.metalSegs;
            foreach (int s in group)
            {
                List<int> tris = src.subs[s];
                bool isMetal = !ReferenceEquals(steel, null) && metal != null && metal.Contains(src.mats[s]);
                var lp = new List<int>(tris.Count);
                var ln = new List<int>(tris.Count / 2);
                for (int k = 0; k + 2 < tris.Count; k += 3)
                {
                    int i0 = tris[k], i1 = tris[k + 1], i2 = tris[k + 2];
                    bool p0 = isPos[i0], p1 = isPos[i1], p2 = isPos[i2];
                    if (p0 && p1 && p2)
                    {
                        lp.Add(Map(pos, mapPos, i0)); lp.Add(Map(pos, mapPos, i1)); lp.Add(Map(pos, mapPos, i2));
                        continue;
                    }
                    if (!p0 && !p1 && !p2)
                    {
                        ln.Add(Map(neg, mapNeg, i0)); ln.Add(Map(neg, mapNeg, i1)); ln.Add(Map(neg, mapNeg, i2));
                        continue;
                    }
                    if (forced != null && (forced[i0] != 0 || forced[i1] != 0 || forced[i2] != 0))
                    {
                        // Seam between a cut body and an untouched one: keep the triangle whole on the untouched side.
                        int f = forced[i0] != 0 ? forced[i0] : forced[i1] != 0 ? forced[i1] : forced[i2];
                        if (f > 0) { lp.Add(Map(pos, mapPos, i0)); lp.Add(Map(pos, mapPos, i1)); lp.Add(Map(pos, mapPos, i2)); }
                        else { ln.Add(Map(neg, mapNeg, i0)); ln.Add(Map(neg, mapNeg, i1)); ln.Add(Map(neg, mapNeg, i2)); }
                        continue;
                    }

                    // Rotate so x is the lone vertex, keeping winding.
                    int x, y, z;
                    if (p0 != p1 && p0 != p2) { x = i0; y = i1; z = i2; }
                    else if (p1 != p0 && p1 != p2) { x = i1; y = i2; z = i0; }
                    else { x = i2; y = i0; z = i1; }

                    bool xPos = isPos[x];
                    List<int> lone = xPos ? pos : neg;
                    List<int> other = xPos ? neg : pos;
                    int[] mapLone = xPos ? mapPos : mapNeg;
                    int[] mapOther = xPos ? mapNeg : mapPos;
                    List<int> listLone = xPos ? lp : ln;
                    List<int> listOther = xPos ? ln : lp;

                    float txy = d[x] / (d[x] - d[y]);
                    float txz = d[x] / (d[x] - d[z]);
                    BoneWeight wxy = Weights.Lerp(src.w[x], src.w[y], txy);
                    BoneWeight wxz = Weights.Lerp(src.w[x], src.w[z], txz);
                    int lxy = part.lerps.Count;
                    part.lerps.Add(new Lerp { a = x, b = y, t = txy, w = wxy });
                    part.lerps.Add(new Lerp { a = x, b = z, t = txz, w = wxz });

                    int xyL = AddLerp(lone, lxy);
                    int xzL = AddLerp(lone, lxy + 1);
                    listLone.Add(Map(lone, mapLone, x)); listLone.Add(xyL); listLone.Add(xzL);

                    int xyO = AddLerp(other, lxy);
                    int xzO = AddLerp(other, lxy + 1);
                    int yO = Map(other, mapOther, y);
                    int zO = Map(other, mapOther, z);
                    listOther.Add(xyO); listOther.Add(yO); listOther.Add(zO);
                    listOther.Add(xyO); listOther.Add(zO); listOther.Add(xzO);

                    (isMetal ? metalSegs : segs).Add(new Seg
                    {
                        a = Vector3.LerpUnclamped(src.v[x], src.v[y], txy),
                        b = Vector3.LerpUnclamped(src.v[x], src.v[z], txz),
                        wa = wxy,
                        wb = wxz,
                        n = src.n.Count > Math.Max(x, Math.Max(y, z)) ? src.n[x] + src.n[y] + src.n[z] : Vector3.zero
                    });
                }
                part.subIndex.Add(s);
                part.lp.Add(lp);
                part.ln.Add(ln);
            }
            return part;
        }

        /// <summary>Local index of source vertex i on one side (planned on first use).</summary>
        private static int Map(List<int> order, int[] map, int i)
        {
            int r = map[i];
            if (r >= 0) return r;
            r = order.Count;
            order.Add(i);
            map[i] = r;
            return r;
        }

        /// <summary>A new local vertex on one side at cut-edge point k.</summary>
        private static int AddLerp(List<int> order, int k)
        {
            order.Add(-(k + 1));
            return order.Count - 1;
        }

        /// <summary>Writes planned vertices start..end of one side at offset (any thread; ranges never overlap).</summary>
        private static void Write(VertexBuffers dst, int offset, List<int> order, List<Lerp> lerps, MeshData src, int start, int end,
            bool origins)
        {
            for (int j = start; j < end; j++)
            {
                int r = offset + j, e = order[j];
                if (e >= 0)
                {
                    dst.v[r] = src.v[e];
                    dst.n[r] = src.n[e];
                    dst.t[r] = src.t[e];
                    dst.uv[r] = src.uv[e];
                    dst.uv1[r] = src.uv1[e];
                    dst.col[r] = src.col[e];
                    dst.w[r] = src.w[e];
                    if (origins) dst.o[r] = src.o[e];
                    continue;
                }
                Lerp l = lerps[-e - 1];
                int a = l.a, b = l.b;
                float t = l.t;
                Vector4 tan = Vector4.LerpUnclamped(src.t[a], src.t[b], t);
                tan.w = src.t[a].w;
                dst.v[r] = Vector3.LerpUnclamped(src.v[a], src.v[b], t);
                dst.n[r] = Vector3.LerpUnclamped(src.n[a], src.n[b], t).normalized;
                dst.t[r] = tan;
                dst.uv[r] = Vector2.LerpUnclamped(src.uv[a], src.uv[b], t);
                dst.uv1[r] = src.uv1[t < 0.5f ? a : b];   // uv1 holds occlusion region ids: never blend them
                dst.col[r] = Color32.Lerp(src.col[a], src.col[b], t);
                dst.w[r] = l.w;
                if (origins) dst.o[r] = VertexOrigin.Between(src.o[a], src.o[b], t);
            }
        }

        // Cross-section: group segments into connected loops (union-find on quantized endpoints) and fan each loop
        // from its own centroid. Works for several separate loops (torso + both arms) without ordering the edges.
        private static List<List<int>> Loops(List<Seg> segs, out List<Vector3> pts, out List<BoneWeight> ptW, out int[] segA, out int[] segB)
        {
            var ids = new Dictionary<Vector3Int, int>(segs.Count * 2);
            var parent = new List<int>(segs.Count * 2);
            var points = new List<Vector3>(segs.Count * 2);
            var pointW = new List<BoneWeight>(segs.Count * 2);
            var a = new int[segs.Count];
            var b = new int[segs.Count];

            int Id(Vector3 p, BoneWeight wt)
            {
                var key = new Vector3Int(Mathf.RoundToInt(p.x * 2000f), Mathf.RoundToInt(p.y * 2000f), Mathf.RoundToInt(p.z * 2000f));
                if (!ids.TryGetValue(key, out int id))
                {
                    id = parent.Count;
                    ids[key] = id;
                    parent.Add(id);
                    points.Add(p);
                    pointW.Add(wt);
                }
                return id;
            }

            int Find(int i)
            {
                while (parent[i] != i)
                {
                    parent[i] = parent[parent[i]];
                    i = parent[i];
                }
                return i;
            }

            for (int i = 0; i < segs.Count; i++)
            {
                a[i] = Id(segs[i].a, segs[i].wa);
                b[i] = Id(segs[i].b, segs[i].wb);
                int ra = Find(a[i]), rb = Find(b[i]);
                if (ra != rb) parent[ra] = rb;
            }

            var comps = new Dictionary<int, List<int>>();
            for (int i = 0; i < segs.Count; i++)
            {
                if (a[i] == b[i]) continue;
                int r = Find(a[i]);
                if (!comps.TryGetValue(r, out List<int> list))
                {
                    list = new List<int>();
                    comps[r] = list;
                }
                list.Add(i);
            }
            pts = points;
            ptW = pointW;
            segA = a;
            segB = b;
            return new List<List<int>>(comps.Values);
        }

        private static void Plane(Vector3 normal, out Vector3 t1, out Vector3 t2)
        {
            t1 = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            t2 = Vector3.Cross(normal, t1);
        }

        private static int BuildCaps(List<Seg> segs, Vector3 normal, Material fill, MeshData pos, MeshData neg, bool holes,
            float hollow, Material inside)
        {
            Loops(segs, out List<Vector3> pts, out List<BoneWeight> ptW, out int[] segA, out int[] segB);
            Plane(normal, out Vector3 t1, out Vector3 t2);
            var capPos = new List<int>();
            var capNeg = new List<int>();
            // Outlines are clipped independently (in parallel), then added in order.
            List<List<int>> outlines = Outlines(segs.Count, pts, segA, segB);
            int joined = 0, found = outlines.Count;
            if (holes && outlines.Count > 1) outlines = WithHoles(outlines, segs, segA, segB, pts, normal, t1, t2, out joined);
            capShape = $"{found} outlines" + (joined > 0 ? $", {joined} holes" : "");
            List<List<int>> insides = null;
            List<List<int>> solid = null;
            if (hollow > 0f && joined == 0 && !ReferenceEquals(inside, null))
            {
                solid = new List<List<int>>();
                foreach (List<int> o in outlines) solid.Add(new List<int>(o));
                insides = Hollow(outlines, pts, ptW, t1, t2, hollow, out float wall);
                if (insides != null) capShape += $", hollow {wall * 1000f:0} mm";
            }
            var clipped = new List<int>[outlines.Count];
            var rests = new List<int>[outlines.Count];
            System.Threading.Tasks.Parallel.For(0, outlines.Count, k => clipped[k] = Triangulate(outlines[k], pts, t1, t2, out rests[k]));
            if (insides != null && Array.Exists(rests, r => r != null))
            {
                // The wall did not clip into triangles (a fan from its middle would cover the inside): solid after all.
                insides = null;
                outlines = solid;
                capShape += ", hollow dropped";
                clipped = new List<int>[outlines.Count];
                rests = new List<int>[outlines.Count];
                System.Threading.Tasks.Parallel.For(0, outlines.Count, k => clipped[k] = Triangulate(outlines[k], pts, t1, t2, out rests[k]));
            }
            int fanned = 0;
            foreach (List<int> r in rests) if (r != null) fanned++;
            if (fanned > 0) capShape += $", {fanned} fanned";
            for (int k = 0; k < outlines.Count; k++)
            {
                AddPolygon(pos, outlines[k], clipped[k], rests[k], pts, ptW, -normal, t1, t2, capPos);
                AddPolygon(neg, outlines[k], clipped[k], rests[k], pts, ptW, normal, t1, t2, capNeg);
            }
            pos.AddTriangles(fill, null, capPos);
            neg.AddTriangles(fill, null, capNeg);
            if (insides == null) return joined;
            var inPos = new List<int>();
            var inNeg = new List<int>();
            foreach (List<int> poly in insides)
            {
                List<int> tris = Triangulate(poly, pts, t1, t2, out List<int> rest);
                AddPolygon(pos, poly, tris, rest, pts, ptW, -normal, t1, t2, inPos);
                AddPolygon(neg, poly, tris, rest, pts, ptW, normal, t1, t2, inNeg);
            }
            pos.AddTriangles(inside, null, inPos);
            neg.AddTriangles(inside, null, inNeg);
            return joined + insides.Count;
        }

        /// <summary>
        /// A vessel the game models closed, with no inner wall (a lidded pot or vase, a barrel): its biggest outline
        /// becomes a wall about as thick as given (at most a third of the outline's half-width) around an inside of its
        /// own (filled with the inside material: dark), so a cut one looks hollow instead of solid. Outlines lying within
        /// it (hoops, bands round it) are dropped; ones outside it (handles) stay. Only a roughly convex outline. Returns
        /// the insides to fill, null when the outline does not allow it (outlines unchanged then).
        /// </summary>
        private static List<List<int>> Hollow(List<List<int>> outlines, List<Vector3> pts, List<BoneWeight> ptW, Vector3 t1, Vector3 t2,
            float thickness, out float wall)
        {
            wall = 0f;
            Vector2 P(int id) => new Vector2(Vector3.Dot(pts[id], t1), Vector3.Dot(pts[id], t2));
            int big = -1;
            double bigArea = 0;
            for (int k = 0; k < outlines.Count; k++)
            {
                double a = Math.Abs(SignedArea(outlines[k], P));
                if (a > bigArea) { bigArea = a; big = k; }
            }
            if (big < 0 || outlines[big].Count < 3) return null;
            var outer = new List<int>(outlines[big]);
            if (SignedArea(outer, P) < 0) outer.Reverse();   // counter-clockwise
            int n = outer.Count;
            double perimeter = 0;
            for (int i = 0; i < n; i++) perimeter += (P(outer[(i + 1) % n]) - P(outer[i])).magnitude;
            wall = Mathf.Min(thickness, (float)(2 * bigArea / Math.Max(perimeter, 1e-9)) / 3f);   // 2A/P: the half-width of a round outline
            if (wall < 0.002f) return null;
            if (bigArea < 0.8 * HullArea(outer, P)) return null;   // not round enough for a wall of even thickness

            // The inner side of the wall follows the outline's points no closer than most of the wall's thickness apart:
            // small bumps and notches offset by the whole wall crossed each other.
            var sparse = new List<int>(n);
            float spacing = Mathf.Max(wall * 0.75f, 0.003f);
            foreach (int id in outer)
                if (sparse.Count == 0 || (P(id) - P(sparse[sparse.Count - 1])).magnitude >= spacing) sparse.Add(id);
            while (sparse.Count > 3 && (P(sparse[0]) - P(sparse[sparse.Count - 1])).magnitude < spacing) sparse.RemoveAt(sparse.Count - 1);
            if (sparse.Count < 3) return null;
            n = sparse.Count;
            var inner = new List<int>(n);
            int first = pts.Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = P(sparse[(i + n - 1) % n]), b = P(sparse[i]), c = P(sparse[(i + 1) % n]);
                Vector2 e1 = b - a, e2 = c - b;
                Vector2 n1 = e1.sqrMagnitude > 1e-14f ? new Vector2(-e1.y, e1.x).normalized : Vector2.zero;   // inward: left of the walk
                Vector2 n2 = e2.sqrMagnitude > 1e-14f ? new Vector2(-e2.y, e2.x).normalized : Vector2.zero;
                Vector2 m = n1 + n2;
                if (m.sqrMagnitude < 1e-12f) m = n1.sqrMagnitude > 0f ? n1 : n2;
                if (m.sqrMagnitude < 1e-12f) { pts.RemoveRange(first, pts.Count - first); ptW.RemoveRange(first, ptW.Count - first); return null; }
                m.Normalize();
                Vector2 side = n1.sqrMagnitude > 0f ? n1 : n2;
                float d = wall / Mathf.Max(0.35f, Vector2.Dot(m, side));
                pts.Add(pts[sparse[i]] + (m.x * t1 + m.y * t2) * d);
                ptW.Add(ptW[sparse[i]]);
                inner.Add(pts.Count - 1);
            }
            bool ok = SignedArea(inner, P) > 0;
            for (int i = 0; ok && i < inner.Count; i++) ok = Inside(outer, P(inner[i]), P);
            List<int> ring = ok ? Bridged(outer, new List<List<int>> { inner }, P) : null;
            if (ring == null)
            {
                pts.RemoveRange(first, pts.Count - first);
                ptW.RemoveRange(first, ptW.Count - first);
                return null;
            }
            var keep = new List<List<int>>(outlines.Count);
            for (int k = 0; k < outlines.Count; k++)
                if (k == big) keep.Add(ring);
                else if (!Inside(outer, P(outlines[k][0]), P)) keep.Add(outlines[k]);
            outlines.Clear();
            outlines.AddRange(keep);
            return new List<List<int>> { inner };
        }

        private static double HullArea(List<int> poly, Func<int, Vector2> P)
        {
            var p = new List<Vector2>(poly.Count);
            foreach (int id in poly) p.Add(P(id));
            p.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            double Turn(Vector2 o, Vector2 a, Vector2 b) => (double)(a.x - o.x) * (b.y - o.y) - (double)(a.y - o.y) * (b.x - o.x);
            var hull = new List<Vector2>();
            for (int pass = 0; pass < 2; pass++)
            {
                int start = hull.Count;
                for (int i = 0; i < p.Count; i++)
                {
                    Vector2 q = pass == 0 ? p[i] : p[p.Count - 1 - i];
                    while (hull.Count >= start + 2 && Turn(hull[hull.Count - 2], hull[hull.Count - 1], q) <= 0) hull.RemoveAt(hull.Count - 1);
                    hull.Add(q);
                }
                hull.RemoveAt(hull.Count - 1);
            }
            double area = 0;
            for (int i = 0, j = hull.Count - 1; i < hull.Count; j = i++) area += (double)hull[j].x * hull[i].y - (double)hull[i].x * hull[j].y;
            return Math.Abs(area) * 0.5;
        }

        /// <summary>
        /// Holes joined into the outline around them by a cut across to it (a bridge), so ear clipping fills only the ring
        /// between: a pot, a barrel, a cup cut across showed a solid disc over its inside (inner wall filled as a face of
        /// its own on top of the outer one). An outline's inside is empty when the surface's outward normals along it point
        /// into it. Unclear normals (less than a third of the outline's length agreeing), or a hole inside no solid
        /// outline, or a bridge not found: filled as before. A part inside another (a blade in its grip) stays solid.
        /// </summary>
        private static List<List<int>> WithHoles(List<List<int>> outlines, List<Seg> segs, int[] segA, int[] segB, List<Vector3> pts,
            Vector3 normal, Vector3 t1, Vector3 t2, out int joined)
        {
            joined = 0;
            var edgeN = new Dictionary<long, Vector3>(segs.Count);
            for (int i = 0; i < segs.Count; i++)
            {
                int a = segA[i], b = segB[i];
                if (a == b) continue;
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                edgeN[key] = (edgeN.TryGetValue(key, out Vector3 n) ? n : Vector3.zero) + segs[i].n;
            }
            Vector2 P(int id) => new Vector2(Vector3.Dot(pts[id], t1), Vector3.Dot(pts[id], t2));
            int count = outlines.Count;
            var area = new double[count];
            var hole = new bool[count];
            for (int k = 0; k < count; k++)
            {
                List<int> o = outlines[k];
                double score = 0, len = 0;
                for (int i = 0; i < o.Count; i++)
                {
                    int a = o[i], b = o[(i + 1) % o.Count];
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (!edgeN.TryGetValue(key, out Vector3 n)) continue;   // a bridged gap
                    Vector3 e = pts[b] - pts[a];
                    Vector3 side = Vector3.ProjectOnPlane(n, normal);
                    if (e.sqrMagnitude < 1e-18f || side.sqrMagnitude < 1e-12f) continue;
                    score += Vector3.Dot(Vector3.Cross(normal, e), side.normalized);   // > 0: outward to the left of the walk
                    len += e.magnitude;
                }
                area[k] = SignedArea(o, P);
                // Inside to the left when counter-clockwise; solid to the left when the outward normal points right.
                hole[k] = len > 0 && Math.Abs(score) > len / 3 && (score < 0) != (area[k] > 0);
            }
            var holesOf = new Dictionary<int, List<List<int>>>();
            for (int k = 0; k < count; k++)
            {
                if (!hole[k]) continue;
                Vector2 q = P(outlines[k][0]);
                int around = -1;
                for (int s = 0; s < count; s++)
                    if (!hole[s] && Math.Abs(area[s]) > Math.Abs(area[k]) && (around < 0 || Math.Abs(area[s]) < Math.Abs(area[around])) &&
                        Inside(outlines[s], q, P))
                        around = s;
                if (around < 0) { hole[k] = false; continue; }
                if (!holesOf.TryGetValue(around, out List<List<int>> list)) holesOf[around] = list = new List<List<int>>();
                list.Add(outlines[k]);
            }
            var result = new List<List<int>>(count);
            for (int k = 0; k < count; k++)
            {
                if (hole[k]) continue;
                if (!holesOf.TryGetValue(k, out List<List<int>> hs)) { result.Add(outlines[k]); continue; }
                List<int> ring = Bridged(outlines[k], hs, P);
                if (ring != null) { result.Add(ring); joined += hs.Count; continue; }
                result.Add(outlines[k]);
                result.AddRange(hs);   // as before
            }
            return result;
        }

        private static double SignedArea(List<int> poly, Func<int, Vector2> P)
        {
            double a = 0;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                Vector2 pj = P(poly[j]), pi = P(poly[i]);
                a += (double)pj.x * pi.y - (double)pi.x * pj.y;
            }
            return a * 0.5;
        }

        private static bool Inside(List<int> poly, Vector2 q, Func<int, Vector2> P)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                Vector2 a = P(poly[i]), b = P(poly[j]);
                if ((a.y > q.y) != (b.y > q.y) && q.x < (b.x - a.x) * (q.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        /// <summary>
        /// The outline with its holes spliced in, rightmost hole first: from the hole's rightmost point straight right to
        /// the first edge of the outline, to that edge's right end, or to the reflex corner in between that the view
        /// would hit first (Eberly). Null when a hole finds no edge.
        /// </summary>
        private static List<int> Bridged(List<int> outer, List<List<int>> holes, Func<int, Vector2> P)
        {
            var poly = new List<int>(outer);
            if (SignedArea(poly, P) < 0) poly.Reverse();   // counter-clockwise
            var hs = new List<(List<int> ring, int right)>();
            foreach (List<int> h in holes)
            {
                var ring = new List<int>(h);
                if (SignedArea(ring, P) > 0) ring.Reverse();   // clockwise
                int right = 0;
                for (int i = 1; i < ring.Count; i++) if (P(ring[i]).x > P(ring[right]).x) right = i;
                hs.Add((ring, right));
            }
            hs.Sort((a, b) => P(b.ring[b.right]).x.CompareTo(P(a.ring[a.right]).x));
            double Cross(Vector2 a, Vector2 b, Vector2 c) => (double)(b.x - a.x) * (c.y - a.y) - (double)(b.y - a.y) * (c.x - a.x);
            foreach ((List<int> ring, int right) in hs)
            {
                Vector2 m = P(ring[right]);
                int n = poly.Count, edge = -1;
                double hitX = double.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = P(poly[i]), b = P(poly[(i + 1) % n]);
                    if (a.y == b.y || m.y < Math.Min(a.y, b.y) || m.y > Math.Max(a.y, b.y)) continue;
                    double x = a.x + (double)(m.y - a.y) * (b.x - a.x) / (b.y - a.y);
                    if (x < m.x || x >= hitX) continue;
                    hitX = x;
                    edge = i;
                }
                if (edge < 0) return null;
                int ia = edge, ib = (edge + 1) % n;
                int v = P(poly[ia]).x > P(poly[ib]).x ? ia : ib;
                var hit = new Vector2((float)hitX, m.y);
                Vector2 pv = P(poly[v]);
                double bestAngle = double.MaxValue, bestDist = double.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    Vector2 r = P(poly[i]);
                    if (Cross(P(poly[(i + n - 1) % n]), r, P(poly[(i + 1) % n])) >= 0) continue;   // not reflex
                    double c1 = Cross(m, hit, r), c2 = Cross(hit, pv, r), c3 = Cross(pv, m, r);
                    bool inTri = (c1 >= 0 && c2 >= 0 && c3 >= 0) || (c1 <= 0 && c2 <= 0 && c3 <= 0);
                    if (!inTri || (r - m).sqrMagnitude < 1e-14f) continue;
                    double angle = Math.Atan2(Math.Abs(r.y - m.y), r.x - m.x), dist = (r - m).sqrMagnitude;
                    if (angle < bestAngle || (angle == bestAngle && dist < bestDist)) { bestAngle = angle; bestDist = dist; v = i; }
                }
                var joined = new List<int>(n + ring.Count + 2);
                for (int i = 0; i <= v; i++) joined.Add(poly[i]);
                for (int k = 0; k <= ring.Count; k++) joined.Add(ring[(right + k) % ring.Count]);
                joined.Add(poly[v]);
                for (int i = v + 1; i < n; i++) joined.Add(poly[i]);
                poly = joined;
            }
            return poly;
        }

        // ---------- cross-section outlines ----------
        // The cross-section used to be filled as a fan from each loop's centroid: every gap in the outline (the seam
        // where an uncut body keeps its triangles whole, an open clothing shell) left a triangular hole reaching to the
        // centre, and a concave outline spilled flesh outside the body. Now the segments are walked into ordered
        // outlines, gaps up to JoinDistance are bridged, and each outline is ear-clipped.

        private const float JoinDistance = 0.04f;   // m
        private const int MaxEarClipPoints = 2000;  // larger outlines are fanned (ear clipping is quadratic)
        private const float MinOutlineStep = 0.0003f;   // m, closer outline points are merged before clipping

        /// <summary>Closed outlines as ordered point ids (the closing edge last -> first is implicit).</summary>
        private static List<List<int>> Outlines(int segCount, List<Vector3> pts, int[] segA, int[] segB)
        {
            var seen = new HashSet<long>();
            var ea = new List<int>(segCount);
            var eb = new List<int>(segCount);
            var nodeEdges = new Dictionary<int, List<int>>();
            var nodes = new List<int>();
            void Link(int node, int e)
            {
                if (!nodeEdges.TryGetValue(node, out List<int> list))
                {
                    list = new List<int>(2);
                    nodeEdges[node] = list;
                    nodes.Add(node);
                }
                list.Add(e);
            }
            for (int i = 0; i < segCount; i++)
            {
                int a = segA[i], b = segB[i];
                if (a == b) continue;
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (!seen.Add(key)) continue;   // coincident layers give the same edge twice
                Link(a, ea.Count);
                Link(b, ea.Count);
                ea.Add(a);
                eb.Add(b);
            }

            var used = new bool[ea.Count];
            var left = new Dictionary<int, int>(nodes.Count);
            foreach (int node in nodes) left[node] = nodeEdges[node].Count;

            var closed = new List<List<int>>();
            var open = new List<List<int>>();
            void Walk(int start)
            {
                var chain = new List<int> { start };
                int cur = start;
                while (true)
                {
                    int next = -1;
                    foreach (int e in nodeEdges[cur])
                    {
                        if (used[e]) continue;
                        used[e] = true;
                        left[ea[e]]--;
                        left[eb[e]]--;
                        next = ea[e] == cur ? eb[e] : ea[e];
                        break;
                    }
                    if (next < 0) break;
                    chain.Add(next);
                    cur = next;
                    if (cur == start) break;
                }
                if (chain.Count > 3 && chain[chain.Count - 1] == start)
                {
                    chain.RemoveAt(chain.Count - 1);
                    closed.Add(chain);
                }
                else if (chain.Count >= 2) open.Add(chain);
            }
            // Open ends first (odd degree), so open chains come out whole; then the closed loops.
            foreach (int node in nodes) while (left[node] % 2 == 1) Walk(node);
            foreach (int node in nodes) while (left[node] > 0) Walk(node);

            Bridge(open, closed, pts);
            return closed;
        }

        /// <summary>
        /// Bridges gaps between open chains: the nearest pair of free chain ends (within JoinDistance) is joined first,
        /// a chain whose own two ends are the nearest pair is closed, and so on; whatever is left open is closed straight
        /// across. Joining keeps the remaining ends where they are, so going once through all close pairs sorted by
        /// distance does exactly that. It used to search all pairs again after every join: cubic in the number of chains,
        /// and ragged cloth along a seam gives hundreds of them (60 ms in one cut, 0.6 s for 400 chains offline).
        /// </summary>
        private static void Bridge(List<List<int>> open, List<List<int>> closed, List<Vector3> pts)
        {
            int k = open.Count;
            if (k == 0) return;
            float maxSq = JoinDistance * JoinDistance;
            // End e = chain * 2 + (0 start, 1 last); the grid finds ends near each other.
            Vector3 EndPos(int e) => pts[(e & 1) == 0 ? open[e >> 1][0] : open[e >> 1][open[e >> 1].Count - 1]];
            Vector3Int Cell(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / JoinDistance), Mathf.FloorToInt(p.y / JoinDistance),
                                                         Mathf.FloorToInt(p.z / JoinDistance));
            var grid = new Dictionary<Vector3Int, List<int>>();
            for (int e = 0; e < k * 2; e++)
            {
                Vector3Int c = Cell(EndPos(e));
                if (!grid.TryGetValue(c, out List<int> list)) grid[c] = list = new List<int>(2);
                list.Add(e);
            }
            var pairs = new List<(float d, int a, int b)>();
            for (int e = 0; e < k * 2; e++)
            {
                Vector3 p = EndPos(e);
                Vector3Int c = Cell(p);
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!grid.TryGetValue(new Vector3Int(c.x + dx, c.y + dy, c.z + dz), out List<int> list)) continue;
                    foreach (int f in list)
                    {
                        if (f <= e) continue;
                        float d = (EndPos(f) - p).sqrMagnitude;
                        if (d <= maxSq) pairs.Add((d, e, f));
                    }
                }
            }
            pairs.Sort((x, y) => x.d != y.d ? x.d.CompareTo(y.d) : x.a != y.a ? x.a.CompareTo(y.a) : x.b.CompareTo(y.b));

            // Chains as they get joined: each original chain points to the joined chain (union-find) whose two ends
            // (headEnd, tailEnd: original end ids) are its free ends.
            var root = new int[k];
            var chain = new List<int>[k];
            var headEnd = new int[k];
            var tailEnd = new int[k];
            var done = new bool[k];
            var free = new bool[k * 2];
            for (int i = 0; i < k; i++)
            {
                root[i] = i;
                chain[i] = open[i];
                headEnd[i] = i * 2;
                tailEnd[i] = i * 2 + 1;
                free[i * 2] = free[i * 2 + 1] = true;
            }
            int Find(int i)
            {
                while (root[i] != i)
                {
                    root[i] = root[root[i]];
                    i = root[i];
                }
                return i;
            }
            foreach ((float d, int a, int b) in pairs)
            {
                if (!free[a] || !free[b]) continue;
                int ra = Find(a >> 1), rb = Find(b >> 1);
                if (ra == rb)
                {
                    // The two ends of one chain: close it.
                    if (chain[ra].Count < 3) continue;
                    closed.Add(chain[ra]);
                    done[ra] = true;
                    free[a] = free[b] = false;
                    continue;
                }
                List<int> ca = chain[ra], cb = chain[rb];
                if (headEnd[ra] == a)   // a's chain must end at a
                {
                    ca.Reverse();
                    (headEnd[ra], tailEnd[ra]) = (tailEnd[ra], headEnd[ra]);
                }
                if (tailEnd[rb] == b)   // b's chain must start at b
                {
                    cb.Reverse();
                    (headEnd[rb], tailEnd[rb]) = (tailEnd[rb], headEnd[rb]);
                }
                int skip = ca[ca.Count - 1] == cb[0] ? 1 : 0;
                for (int i = skip; i < cb.Count; i++) ca.Add(cb[i]);
                tailEnd[ra] = tailEnd[rb];
                root[rb] = ra;
                chain[rb] = null;
                free[a] = free[b] = false;
            }
            // Whatever is still open is closed straight across, as the old fan did.
            for (int i = 0; i < k; i++)
                if (root[i] == i && !done[i] && chain[i].Count >= 3) closed.Add(chain[i]);
        }

        /// <summary>
        /// Ear clipping in the cut plane. Returns triangles as indices into poly. rest = outline that could not be
        /// clipped (self-touching), to be fanned from its centroid; null when everything was clipped.
        /// </summary>
        private static List<int> Triangulate(List<int> poly, List<Vector3> pts, Vector3 t1, Vector3 t2, out List<int> rest)
        {
            rest = null;
            // Neighbours a hair apart (endpoints quantized into different cells) block every ear next to them.
            for (int i = poly.Count - 1; i >= 0 && poly.Count > 3; i--)
            {
                int prev = poly[(i + poly.Count - 1) % poly.Count];
                if ((pts[poly[i]] - pts[prev]).sqrMagnitude < MinOutlineStep * MinOutlineStep) poly.RemoveAt(i);
            }
            int n = poly.Count;
            var tris = new List<int>(Math.Max(0, (n - 2) * 3));
            if (n < 3) return tris;
            if (n > MaxEarClipPoints)
            {
                rest = new List<int>(n);
                for (int i = 0; i < n; i++) rest.Add(i);
                return tris;
            }

            // Plane coordinates relative to the first point (doubles: segments are sub-millimetre).
            Vector3 o = pts[poly[0]];
            var x = new double[n];
            var y = new double[n];
            double area = 0;
            for (int i = 0; i < n; i++)
            {
                Vector3 d = pts[poly[i]] - o;
                x[i] = Vector3.Dot(d, t1);
                y[i] = Vector3.Dot(d, t2);
            }
            for (int i = 0, j = n - 1; i < n; j = i++) area += x[j] * y[i] - x[i] * y[j];
            var idx = new List<int>(n);
            for (int i = 0; i < n; i++) idx.Add(area >= 0 ? i : n - 1 - i);   // counter-clockwise

            const double Eps = 1e-14;
            double Cross(int a, int b, int c) => (x[b] - x[a]) * (y[c] - y[a]) - (y[b] - y[a]) * (x[c] - x[a]);
            bool Same(int a, int b) => Math.Abs(x[a] - x[b]) < 1e-7 && Math.Abs(y[a] - y[b]) < 1e-7;

            bool IsEar(int a, int b, int c)
            {
                if (Cross(a, b, c) <= Eps) return false;   // reflex or flat
                // Only points within the ear's bounding box can lie in it: the full test ran for every point of the
                // outline for every candidate, which made big ragged outlines (cloth) take tens of milliseconds.
                double minX = Math.Min(x[a], Math.Min(x[b], x[c])), maxX = Math.Max(x[a], Math.Max(x[b], x[c]));
                double minY = Math.Min(y[a], Math.Min(y[b], y[c])), maxY = Math.Max(y[a], Math.Max(y[b], y[c]));
                foreach (int v in idx)
                {
                    if (x[v] < minX || x[v] > maxX || y[v] < minY || y[v] > maxY) continue;
                    if (v == a || v == b || v == c || Same(v, a) || Same(v, b) || Same(v, c)) continue;
                    if (Cross(a, b, v) >= 0 && Cross(b, c, v) >= 0 && Cross(c, a, v) >= 0) return false;
                }
                return true;
            }

            int at = 0, fails = 0;
            while (idx.Count > 3)
            {
                int cnt = idx.Count;
                if (fails >= cnt)
                {
                    // No ear left: drop one flat vertex and retry, else give up on the rest.
                    int flat = -1;
                    for (int k = 0; k < cnt && flat < 0; k++)
                        if (Math.Abs(Cross(idx[(k + cnt - 1) % cnt], idx[k], idx[(k + 1) % cnt])) <= Eps) flat = k;
                    if (flat < 0)
                    {
                        rest = idx;
                        return tris;
                    }
                    idx.RemoveAt(flat);
                    fails = 0;
                    at = 0;
                    continue;
                }
                at %= cnt;
                int pa = idx[(at + cnt - 1) % cnt], pb = idx[at], pc = idx[(at + 1) % cnt];
                if (IsEar(pa, pb, pc))
                {
                    tris.Add(pa); tris.Add(pb); tris.Add(pc);
                    idx.RemoveAt(at);
                    fails = 0;
                }
                else
                {
                    at++;
                    fails++;
                }
            }
            if (Math.Abs(Cross(idx[0], idx[1], idx[2])) > Eps) { tris.Add(idx[0]); tris.Add(idx[1]); tris.Add(idx[2]); }
            return tris;
        }

        /// <summary>One outline's cap on one side; every triangle is turned to face capNormal.</summary>
        private static void AddPolygon(MeshData m, List<int> poly, List<int> tris, List<int> rest, List<Vector3> pts,
            List<BoneWeight> ptW, Vector3 capNormal, Vector3 t1, Vector3 t2, List<int> outTris, Vector3 offset = default)
        {
            var tan = new Vector4(t1.x, t1.y, t1.z, 1f);
            var local = new Dictionary<int, int>();
            int Vert(int k)
            {
                int id = poly[k];
                if (!local.TryGetValue(id, out int vi))
                {
                    Vector3 p = pts[id];
                    vi = m.AddVertex(p + offset, capNormal, tan, CapUV(p, t1, t2), ptW[id]);
                    local[id] = vi;
                }
                return vi;
            }
            void Tri(int a, int b, int c)
            {
                // Unity front face: normal = Cross(b - a, c - a) for (a, b, c).
                if (Vector3.Dot(Vector3.Cross(m.v[b] - m.v[a], m.v[c] - m.v[a]), capNormal) >= 0f)
                {
                    outTris.Add(a); outTris.Add(b); outTris.Add(c);
                }
                else
                {
                    outTris.Add(a); outTris.Add(c); outTris.Add(b);
                }
            }

            for (int k = 0; k + 2 < tris.Count; k += 3) Tri(Vert(tris[k]), Vert(tris[k + 1]), Vert(tris[k + 2]));
            if (rest == null || rest.Count < 3) return;

            // Fallback: fan from the centroid, centre on the bone that dominates the outline.
            Vector3 c0 = Vector3.zero;
            var votes = new Dictionary<int, int>();
            foreach (int k in rest)
            {
                c0 += pts[poly[k]];
                int b = ptW[poly[k]].boneIndex0;
                votes.TryGetValue(b, out int cnt);
                votes[b] = cnt + 1;
            }
            c0 /= rest.Count;
            int centreBone = 0, bestVotes = -1;
            foreach (KeyValuePair<int, int> kv in votes)
                if (kv.Value > bestVotes) { bestVotes = kv.Value; centreBone = kv.Key; }
            int ic = m.AddVertex(c0 + offset, capNormal, tan, CapUV(c0, t1, t2), Weights.Single(centreBone));
            for (int k = 0; k < rest.Count; k++) Tri(ic, Vert(rest[k]), Vert(rest[(k + 1) % rest.Count]));
        }

        // ---------- burnt rim (fire cuts) ----------
        private const float BurnWidth = 0.06f;           // m, how far the burnt rim reaches in from the outer edge
        // m, in front of the cut faces: every layer (skin, shirt, coat) has its own cap on the plane, and a rim lying
        // closer showed only in slivers between them.
        private const float BurnLift = 0.0015f;
        private const float BurnTileAspect = 1024f / 192f;   // BurnEdge.png: one repeat along the outline per this many widths
        private const float BurnSmooth = 0.025f;          // m, half-length of outline the rim's inward direction is taken over

        private static bool PointInPolygon(Vector2 q, Vector2[] poly)
        {
            bool inside = false;
            for (int k = 0, j = poly.Length - 1; k < poly.Length; j = k++)
                if ((poly[k].y > q.y) != (poly[j].y > q.y) &&
                    q.x < (poly[j].x - poly[k].x) * (q.y - poly[k].y) / (poly[j].y - poly[k].y) + poly[k].x)
                    inside = !inside;
            return inside;
        }

        /// <summary>
        /// How far the polygon reaches from its point k along direction d (into it) before hitting its far side; max if
        /// nothing is hit within max.
        /// </summary>
        private static float Across(Vector2[] poly, int k, Vector2 d, float max)
        {
            if (d.sqrMagnitude < 1e-12f) return 0f;
            int n = poly.Length;
            Vector2 o = poly[k];
            float best = max;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                if (i == k || j == k) continue;   // the point's own edges
                Vector2 a = poly[i], e = poly[j] - a;
                float den = d.x * e.y - d.y * e.x;
                if (Mathf.Abs(den) < 1e-12f) continue;
                Vector2 ao = a - o;
                float t = (ao.x * e.y - ao.y * e.x) / den;   // along d
                float u = (ao.x * d.y - ao.y * d.x) / den;   // along the edge
                if (t > 1e-5f && u >= 0f && u <= 1f && t < best) best = t;
            }
            return best;
        }

        /// <summary>
        /// A band along every outermost flesh outline, lying on top of the cut face: outer edge on the outline (texture top:
        /// solid crust), inner edge pulled in by the band width (texture bottom: transparent). The texture's ragged alpha
        /// cuts the burn line. Outlines inside another one (the skin under the shirt under the coat) get none: one rim
        /// around the whole cut, not a ring per layer. Repeats a whole number of times, so the start meets the end.
        /// </summary>
        private static void BuildBurnEdge(List<Seg> segs, Vector3 normal, Material burn, MeshData pos, MeshData neg)
        {
            Loops(segs, out List<Vector3> pts, out List<BoneWeight> ptW, out int[] segA, out int[] segB);
            Plane(normal, out Vector3 t1, out Vector3 t2);
            List<List<int>> outlines = Outlines(segs.Count, pts, segA, segB);
            var triPos = new List<int>();
            var triNeg = new List<int>();
            var tan = new Vector4(t1.x, t1.y, t1.z, 1f);
            bool[] outer = OuterOutlines(outlines, pts, t1, t2, out Vector2[][] polys, out double[] areas);

            for (int oi = 0; oi < outlines.Count; oi++)
            {
                if (!outer[oi]) continue;
                List<int> o = outlines[oi];
                int n = o.Count;
                Vector2[] p2 = polys[oi];
                double area = areas[oi];
                float sign = area > 0 ? 1f : -1f;              // counter-clockwise: the inside is on the left
                float width = Mathf.Min(BurnWidth, 0.4f * Mathf.Sqrt((float)System.Math.Abs(area)));
                Vector2 Left(Vector2 d) => new Vector2(-d.y, d.x) * sign;

                var along = new float[n + 1];
                for (int k = 0; k < n; k++) along[k + 1] = along[k] + (p2[(k + 1) % n] - p2[k]).magnitude;
                float total = along[n];
                if (total < 1e-4f) continue;

                // The cut outline is jagged (short mesh edges, sharp turns): inward directions taken from neighbouring
                // edges jumped about, and mitred out at sharp corners they shot wedges of crust right across the cut.
                // Directions now come from the outline smoothed over a few centimetres, the outer edge stays on the
                // outline itself, and each point reaches in at most 45% of the way to the opposite side.
                Vector2 At(float s)
                {
                    s %= total;
                    if (s < 0f) s += total;
                    int lo = 0, hi = n;   // along[lo] <= s < along[lo + 1]
                    while (hi - lo > 1)
                    {
                        int mid = (lo + hi) >> 1;
                        if (along[mid] <= s) lo = mid; else hi = mid;
                    }
                    float seg = along[lo + 1] - along[lo];
                    return Vector2.Lerp(p2[lo], p2[(lo + 1) % n], seg > 1e-9f ? (s - along[lo]) / seg : 0f);
                }
                float h = Mathf.Min(BurnSmooth, total / 8f);
                var dir = new Vector2[n];
                var reach = new float[n];
                for (int k = 0; k < n; k++)
                {
                    Vector2 tng = At(along[k] + h) - At(along[k] - h);
                    if (tng.sqrMagnitude < 1e-12f) tng = p2[(k + 1) % n] - p2[k];
                    dir[k] = tng.sqrMagnitude > 1e-12f ? Left(tng.normalized) : Vector2.zero;
                    reach[k] = Mathf.Min(width, 0.45f * Across(p2, k, dir[k], width / 0.45f));
                }
                // Even out the reach along the outline (three passes of a 5-point mean, never above the limit).
                var tmp = new float[n];
                for (int pass = 0; pass < 3; pass++)
                {
                    for (int k = 0; k < n; k++)
                    {
                        float sum = 0f;
                        for (int j = -2; j <= 2; j++) sum += reach[(k + j + n) % n];
                        tmp[k] = sum / 5f;
                    }
                    for (int k = 0; k < n; k++) reach[k] = Mathf.Min(reach[k], tmp[k]) * 0.5f + tmp[k] * 0.5f;
                }
                // The inner edge itself: in a deep notch (an armpit) neighbouring points crossed over and the band folded.
                // Relax it into an even line, a point at a time, never outside the cut and never further in than allowed.
                var inner = new Vector2[n];
                for (int k = 0; k < n; k++) inner[k] = p2[k] + dir[k] * reach[k];
                var relaxed = new Vector2[n];
                for (int pass = 0; pass < 6; pass++)
                {
                    for (int k = 0; k < n; k++)
                    {
                        Vector2 q = (inner[(k - 1 + n) % n] + inner[k] * 2f + inner[(k + 1) % n]) * 0.25f;
                        Vector2 off = q - p2[k];
                        if (off.magnitude > width) q = p2[k] + off.normalized * width;
                        relaxed[k] = PointInPolygon(q, p2) ? q : inner[k];
                    }
                    (inner, relaxed) = (relaxed, inner);
                }
                var inward = new Vector3[n];
                for (int k = 0; k < n; k++)
                {
                    Vector2 off = inner[k] - p2[k];
                    inward[k] = t1 * off.x + t2 * off.y;
                }
                float repeats = Mathf.Max(1f, Mathf.Round(total / (width * BurnTileAspect)));
                float uScale = repeats / total;
                float u0 = (o[0] * 0.6180339f) % 1f;   // a different stretch of the burn line on every outline

                void Band(MeshData m, Vector3 capNormal, List<int> tris)
                {
                    Vector3 lift = capNormal * BurnLift;
                    int first = m.VertexCount;
                    for (int k = 0; k <= n; k++)
                    {
                        int id = o[k % n];
                        float u = u0 + along[k] * uScale;
                        m.AddVertex(pts[id] + lift, capNormal, tan, new Vector2(u, 1f), ptW[id]);
                        m.AddVertex(pts[id] + inward[k % n] + lift, capNormal, tan, new Vector2(u, 0f), ptW[id]);
                    }
                    for (int k = 0; k < n; k++)
                    {
                        int oa = first + k * 2, ia = oa + 1, ob = oa + 2, ib = oa + 3;
                        bool front = Vector3.Dot(Vector3.Cross(m.v[ob] - m.v[oa], m.v[ia] - m.v[oa]), capNormal) >= 0f;
                        if (front) { tris.Add(oa); tris.Add(ob); tris.Add(ia); tris.Add(ob); tris.Add(ib); tris.Add(ia); }
                        else { tris.Add(oa); tris.Add(ia); tris.Add(ob); tris.Add(ob); tris.Add(ia); tris.Add(ib); }
                    }
                }
                Band(pos, -normal, triPos);
                Band(neg, normal, triNeg);
            }
            pos.AddTriangles(burn, null, triPos);
            neg.AddTriangles(burn, null, triNeg);
        }

        /// <summary>
        /// Every outline in plane coordinates and its signed area; true for the outermost ones that get a rim: not inside a
        /// bigger outline (the skin under the shirt under the coat) and not a sliver.
        /// </summary>
        private static bool[] OuterOutlines(List<List<int>> outlines, List<Vector3> pts, Vector3 t1, Vector3 t2,
            out Vector2[][] polys, out double[] areas)
        {
            int oc = outlines.Count;
            polys = new Vector2[oc][];
            areas = new double[oc];
            for (int i = 0; i < oc; i++)
            {
                List<int> o = outlines[i];
                var p = new Vector2[o.Count];
                for (int k = 0; k < o.Count; k++) p[k] = new Vector2(Vector3.Dot(pts[o[k]], t1), Vector3.Dot(pts[o[k]], t2));
                double a2 = 0;
                for (int k = 0; k < p.Length; k++)
                {
                    Vector2 a = p[k], b = p[(k + 1) % p.Length];
                    a2 += 0.5 * (a.x * b.y - a.y * b.x);
                }
                polys[i] = p;
                areas[i] = a2;
            }
            var outer = new bool[oc];
            for (int i = 0; i < oc; i++)
            {
                if (polys[i].Length < 3 || System.Math.Abs(areas[i]) < 2e-5) continue;   // < 0.2 cm²: a sliver, no rim
                // The outline's centre of points: a point of the outline itself can sit right on a neighbour's edge.
                Vector2 c = Vector2.zero;
                foreach (Vector2 q in polys[i]) c += q;
                c /= polys[i].Length;
                bool inner = false;
                for (int j = 0; j < oc && !inner; j++)
                    if (j != i && System.Math.Abs(areas[j]) > System.Math.Abs(areas[i]) && polys[j].Length >= 3 &&
                        PointInPolygon(c, polys[j]))
                        inner = true;
                outer[i] = !inner;
            }
            return outer;
        }

        private const float FieldStep = 0.008f;    // m, the outline is resampled this finely for the distance field
        private const float FieldTexel = 0.006f;   // m per texel (the field is smooth: finer only cost time, 35 ms on a torso)
        private const int FieldMin = 16, FieldMax = 96;
        // The crust reaches in at most this share of the deepest point of its outline: on a narrow cut (an arm cut at a
        // slant) the bands from both sides met in the middle and the burn line fanned out into a star there.
        private const float FieldDepthShare = 0.6f;

        /// <summary>Flesh made by the body fill (inside plate armour no skin is loaded): its triangles on each side.</summary>
        private sealed class FillRegion
        {
            public readonly List<int> posTris = new List<int>(), negTris = new List<int>();
        }

        /// <summary>
        /// The burnt rim drawn on the cut face itself (our own shader, zandatsu.shaders): a copy of the outermost caps
        /// lifted a hair above them, mapped onto a small texture that holds for every spot of the face how far in from the
        /// outline it lies (r: 1 on the outline, 0 a band width in) and where along the outline (g/b: cos/sin of the place
        /// in the burn line, so it wraps round). The shader reads the burn line there: the rim follows the outline evenly
        /// however jagged the outline is, with no strip to fold over in a notch. Flesh that only the body fill makes (under
        /// plate armour) gets the rim along the fill's own edge.
        /// </summary>
        private static void BuildBurnCap(List<Seg> segs, Vector3 normal, Material burn, MeshData pos, MeshData neg,
            BurnField field, List<FillRegion> fills)
        {
            Plane(normal, out Vector3 t1, out Vector3 t2);
            List<Vector3> pts = null;
            List<BoneWeight> ptW = null;
            List<List<int>> outlines = new List<List<int>>();
            bool[] outer = new bool[0];
            Vector2[][] polys = new Vector2[0][];
            double[] areas = new double[0];
            if (segs.Count > 0)
            {
                Loops(segs, out pts, out ptW, out int[] segA, out int[] segB);
                outlines = Outlines(segs.Count, pts, segA, segB);
                outer = OuterOutlines(outlines, pts, t1, t2, out polys, out areas);
            }
            Vector2 InPlane(Vector3 p) => new Vector2(Vector3.Dot(p, t1), Vector3.Dot(p, t2));

            // Each outline resampled evenly (the field is smooth, the mesh outline is not), with its place in the burn
            // line at every point: a whole number of repeats round it, so the start meets the end.
            var lines = new List<(Vector2[] p, float[] u, float repeats, float width)>();
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            void AddLine(Vector2[] p2, double area, int seed)
            {
                int n = p2.Length;
                if (n < 3 || System.Math.Abs(area) < 2e-5) return;
                var along = new float[n + 1];
                for (int k = 0; k < n; k++) along[k + 1] = along[k] + (p2[(k + 1) % n] - p2[k]).magnitude;
                float total = along[n];
                if (total < 1e-4f) return;
                float width = Mathf.Min(BurnWidth, 0.4f * Mathf.Sqrt((float)System.Math.Abs(area)));
                float repeats = Mathf.Max(1f, Mathf.Round(total / (width * BurnTileAspect)));
                float u0 = (seed * 0.6180339f) % 1f;   // a different stretch of the burn line on every outline
                int m = Mathf.Max(8, Mathf.CeilToInt(total / FieldStep));
                var rp = new Vector2[m];
                var ru = new float[m];
                int lo = 0;
                for (int j = 0; j < m; j++)
                {
                    float s = total * j / m;
                    while (lo < n - 1 && along[lo + 1] <= s) lo++;
                    float seg = along[lo + 1] - along[lo];
                    rp[j] = Vector2.Lerp(p2[lo], p2[(lo + 1) % n], seg > 1e-9f ? (s - along[lo]) / seg : 0f);
                    ru[j] = u0 + s * repeats / total;
                }
                // Smoothed over a few centimetres (less on a small outline, which it would shrink): on a jagged outline
                // the nearest point jumped from tooth to tooth and tore the burn line into patches; in a notch it fanned
                // out into rings round the tip.
                int reachPts = Mathf.Max(1, Mathf.RoundToInt(Mathf.Min(BurnSmooth, total / 20f) / (total / m)));
                var tmp = new Vector2[m];
                for (int pass = 0; pass < 2; pass++)
                {
                    for (int j = 0; j < m; j++)
                    {
                        Vector2 sum = Vector2.zero;
                        for (int i = -reachPts; i <= reachPts; i++) sum += rp[((j + i) % m + m) % m];
                        tmp[j] = sum / (2 * reachPts + 1);
                    }
                    (rp, tmp) = (tmp, rp);
                }
                lines.Add((rp, ru, repeats, width));
                foreach (Vector2 q in p2)
                {
                    min = Vector2.Min(min, q);
                    max = Vector2.Max(max, q);
                }
            }

            for (int oi = 0; oi < outlines.Count; oi++)
                if (outer[oi]) AddLine(polys[oi], areas[oi], outlines[oi][0]);

            // Body fill not covered by a flesh outline: its own edge (where no triangle is on the other side).
            var bare = new List<FillRegion>();
            if (fills != null)
                foreach (FillRegion fr in fills)
                {
                    if (fr.posTris.Count < 3) continue;
                    Vector2 c = Vector2.zero;
                    for (int k = 0; k < fr.posTris.Count; k++) c += InPlane(pos.v[fr.posTris[k]]);
                    c /= fr.posTris.Count;
                    bool covered = false;
                    for (int oi = 0; oi < outlines.Count && !covered; oi++)
                        if (outer[oi] && PointInPolygon(c, polys[oi])) covered = true;
                    if (covered) continue;
                    bare.Add(fr);
                    foreach (Vector2[] loop in EdgeLoops(pos, fr.posTris, InPlane))
                    {
                        double a2 = 0;
                        for (int k = 0; k < loop.Length; k++)
                        {
                            Vector2 a = loop[k], b = loop[(k + 1) % loop.Length];
                            a2 += 0.5 * (a.x * b.y - a.y * b.x);
                        }
                        AddLine(loop, a2, fr.posTris[0]);
                    }
                }
            if (lines.Count == 0) return;

            float side = Mathf.Max(max.x - min.x, max.y - min.y);
            int size = Mathf.Clamp(Mathf.CeilToInt(side / FieldTexel) + 2, FieldMin, FieldMax);
            float texel = side / (size - 2);
            Vector2 origin = min - new Vector2(texel, texel);
            float span = texel * size;
            int count = size * size;
            var dist = new float[count];
            var place = new float[count];
            var nearest = new int[count];
            var inside = new bool[count];
            System.Threading.Tasks.Parallel.For(0, size, row =>
            {
                for (int col = 0; col < size; col++)
                {
                    var q = new Vector2(origin.x + (col + 0.5f) * texel, origin.y + (row + 0.5f) * texel);
                    float best = float.MaxValue, bu = 0f;
                    int bl = 0;
                    bool odd = false;
                    for (int li = 0; li < lines.Count; li++)
                    {
                        (Vector2[] rp, float[] ru, float repeats, _) = lines[li];
                        if (PointInPolygon(q, rp)) odd = !odd;
                        int m = rp.Length;
                        for (int j = 0; j < m; j++)
                        {
                            Vector2 a = rp[j], b = rp[j + 1 < m ? j + 1 : 0];
                            float ex = b.x - a.x, ey = b.y - a.y, qx = q.x - a.x, qy = q.y - a.y;
                            float len2 = ex * ex + ey * ey;
                            float t = len2 > 1e-12f ? (qx * ex + qy * ey) / len2 : 0f;
                            t = t < 0f ? 0f : t > 1f ? 1f : t;
                            float dx = qx - ex * t, dy = qy - ey * t;
                            float d2 = dx * dx + dy * dy;
                            if (d2 >= best) continue;
                            best = d2;
                            float next = j + 1 < m ? ru[j + 1] : ru[0] + repeats;
                            bu = ru[j] + (next - ru[j]) * t;
                            bl = li;
                        }
                    }
                    int i = row * size + col;
                    dist[i] = Mathf.Sqrt(best);
                    place[i] = bu;
                    nearest[i] = bl;
                    inside[i] = odd;
                }
            });
            // How deep each outline's flesh goes (its deepest spot from the edge): the crust stays well short of it.
            var deepest = new float[lines.Count];
            for (int i = 0; i < count; i++)
                if (inside[i] && dist[i] > deepest[nearest[i]]) deepest[nearest[i]] = dist[i];
            var reach = new float[lines.Count];
            for (int li = 0; li < lines.Count; li++)
                reach[li] = Mathf.Max(texel, Mathf.Min(lines[li].width, FieldDepthShare * deepest[li]));
            var texels = new byte[count * 4];
            for (int i = 0; i < count; i++)
            {
                // Outside the smoothed line (a tooth of the real outline sticking out past it): all crust.
                float v = inside[i] ? Mathf.Clamp01(1f - dist[i] / reach[nearest[i]]) : 1f;
                float ang = place[i] * 2f * Mathf.PI;
                texels[i * 4] = (byte)Mathf.RoundToInt(v * 255f);
                texels[i * 4 + 1] = (byte)Mathf.RoundToInt((Mathf.Cos(ang) * 0.5f + 0.5f) * 255f);
                texels[i * 4 + 2] = (byte)Mathf.RoundToInt((Mathf.Sin(ang) * 0.5f + 0.5f) * 255f);
                texels[i * 4 + 3] = 255;
            }
            field.size = size;
            field.texels = texels;

            var triPos = new List<int>();
            var triNeg = new List<int>();
            int firstPos = pos.VertexCount, firstNeg = neg.VertexCount;
            for (int oi = 0; oi < outlines.Count; oi++)
            {
                if (!outer[oi]) continue;
                List<int> tris = Triangulate(outlines[oi], pts, t1, t2, out List<int> rest);
                AddPolygon(pos, outlines[oi], tris, rest, pts, ptW, -normal, t1, t2, triPos, -normal * BurnLift);
                AddPolygon(neg, outlines[oi], tris, rest, pts, ptW, normal, t1, t2, triNeg, normal * BurnLift);
            }
            // The fill lies sunk into the piece: its copy comes up in front of the cut face like the caps' copies.
            foreach (FillRegion fr in bare)
            {
                Lift(pos, fr.posTris, -normal * (UnderfillDepth + BurnLift), triPos);
                Lift(neg, fr.negTris, normal * (UnderfillDepth + BurnLift), triNeg);
            }
            for (int i = firstPos; i < pos.VertexCount; i++) pos.uv[i] = (InPlane(pos.v[i]) - origin) / span;
            for (int i = firstNeg; i < neg.VertexCount; i++) neg.uv[i] = (InPlane(neg.v[i]) - origin) / span;
            pos.AddTriangles(burn, null, triPos);
            neg.AddTriangles(burn, null, triNeg);
        }

        /// <summary>Copies triangles of m (and their vertices) moved by offset.</summary>
        private static void Lift(MeshData m, List<int> tris, Vector3 offset, List<int> outTris)
        {
            var map = new Dictionary<int, int>();
            foreach (int vi in tris)
            {
                if (!map.TryGetValue(vi, out int ni))
                {
                    ni = m.AddVertex(m.v[vi] + offset, m.n[vi], m.t[vi], m.uv[vi], m.w[vi]);
                    map[vi] = ni;
                }
                outTris.Add(ni);
            }
        }

        /// <summary>The edges of a set of triangles no other of them shares, walked into closed loops (plane coordinates).</summary>
        private static List<Vector2[]> EdgeLoops(MeshData m, List<int> tris, Func<Vector3, Vector2> inPlane)
        {
            var uses = new Dictionary<long, int>();
            long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            for (int k = 0; k + 2 < tris.Count; k += 3)
                for (int e = 0; e < 3; e++)
                {
                    long key = Key(tris[k + e], tris[k + (e + 1) % 3]);
                    uses.TryGetValue(key, out int c);
                    uses[key] = c + 1;
                }
            var next = new Dictionary<int, List<int>>();
            foreach (KeyValuePair<long, int> kv in uses)
            {
                if (kv.Value != 1) continue;
                int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
                if (!next.TryGetValue(a, out List<int> la)) next[a] = la = new List<int>();
                if (!next.TryGetValue(b, out List<int> lb)) next[b] = lb = new List<int>();
                la.Add(b);
                lb.Add(a);
            }
            var loops = new List<Vector2[]>();
            var done = new HashSet<int>();
            foreach (int start in next.Keys)
            {
                if (done.Contains(start)) continue;
                var loop = new List<Vector2>();
                int prev = -1, cur = start;
                while (cur >= 0 && done.Add(cur))
                {
                    loop.Add(inPlane(m.v[cur]));
                    int go = -1;
                    foreach (int nb in next[cur]) if (nb != prev && !done.Contains(nb)) { go = nb; break; }
                    prev = cur;
                    cur = go;
                }
                if (loop.Count >= 3) loops.Add(loop.ToArray());
            }
            return loops;
        }

        // Armour is a thin shell around the body: the cut edge of the plate is a narrow steel band reaching from the
        // loop towards its centre.
        private static void BuildRings(List<Seg> segs, Vector3 normal, Material steel, MeshData pos, MeshData neg)
        {
            List<List<int>> comps = Loops(segs, out List<Vector3> pts, out List<BoneWeight> ptW, out int[] segA, out int[] segB);
            Plane(normal, out Vector3 t1, out Vector3 t2);
            var ringPos = new List<int>();
            var ringNeg = new List<int>();
            foreach (List<int> comp in comps)
            {
                Vector3 c = Vector3.zero;
                foreach (int i in comp) c += segs[i].a + segs[i].b;
                c /= comp.Count * 2;
                AddRing(pos, comp, segA, segB, pts, ptW, c, normal, -normal, t1, t2, ringPos);
                AddRing(neg, comp, segA, segB, pts, ptW, c, normal, normal, t1, t2, ringNeg);
            }
            pos.AddTriangles(steel, null, ringPos);
            neg.AddTriangles(steel, null, ringNeg);
        }

        // ---------- body fill ----------
        // Manikin does not load the body under fully covering clothes (the officer in plate has only head, mouth, eyes
        // and armour), so a cut through the torso met nothing but plates: each plate's section is a thin closed sliver
        // (outer face, rim, inner face), and filling those slivers left the inside of the armour black. Each ragdoll
        // body's section points are filled by their convex hull, sunk slightly into the piece: real flesh caps and
        // steel bands, on the plane, stay in front of it. Points of one body farther apart than the cluster cell
        // (a pouch hanging off the belt) get their own hull, so the fill does not span open air.
        private const float UnderfillDepth = 0.0015f;   // m
        private const float FillCell = 0.05f;           // m, cluster grid: occupied neighbouring cells join

        private static void BuildBodyFill(List<Seg> segs, Vector3 normal, Material fill, MeshData pos, MeshData neg,
            List<VolumeShape>[] volumes, List<FillRegion> regions = null)
        {
            Loops(segs, out List<Vector3> pts, out List<BoneWeight> ptW, out _, out _);
            Plane(normal, out Vector3 t1, out Vector3 t2);

            // Cluster key: (dominant body, grid cell); union-find over occupied cells of the same body.
            var cellId = new Dictionary<long, int>();
            var cellParent = new List<int>();
            var ptCell = new int[pts.Count];
            long Key(int body, int cx, int cy, int cz) =>
                ((long)(body & 0xFF) << 48) | ((long)(cx & 0xFFFF) << 32) | ((long)(cy & 0xFFFF) << 16) | (long)(cz & 0xFFFF);
            int FindCell(int i)
            {
                while (cellParent[i] != i)
                {
                    cellParent[i] = cellParent[cellParent[i]];
                    i = cellParent[i];
                }
                return i;
            }
            var cellPos = new List<Vector3Int>();
            var cellBody = new List<int>();
            for (int i = 0; i < pts.Count; i++)
            {
                int body = Dominant(ptW[i]);
                Vector3 p = pts[i] / FillCell;
                int cx = Mathf.FloorToInt(p.x), cy = Mathf.FloorToInt(p.y), cz = Mathf.FloorToInt(p.z);
                long key = Key(body, cx, cy, cz);
                if (!cellId.TryGetValue(key, out int id))
                {
                    id = cellParent.Count;
                    cellId[key] = id;
                    cellParent.Add(id);
                    cellPos.Add(new Vector3Int(cx, cy, cz));
                    cellBody.Add(body);
                }
                ptCell[i] = id;
            }
            for (int c = 0; c < cellPos.Count; c++)
            {
                Vector3Int cp = cellPos[c];
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!cellId.TryGetValue(Key(cellBody[c], cp.x + dx, cp.y + dy, cp.z + dz), out int o)) continue;
                    int ra = FindCell(c), rb = FindCell(o);
                    if (ra != rb) cellParent[ra] = rb;
                }
            }

            var clusters = new Dictionary<int, List<int>>();
            for (int i = 0; i < pts.Count; i++)
            {
                int r = FindCell(ptCell[i]);
                if (!clusters.TryGetValue(r, out List<int> list)) clusters[r] = list = new List<int>();
                list.Add(i);
            }

            var fillPos = new List<int>();
            var fillNeg = new List<int>();

            // Bodies with colliders: one fill per body, the section of its collider volume clipped to the convex outline of
            // all its section points. Plates in front of and behind the torso are 20-30 cm apart (separate cell clusters,
            // so the middle stayed hollow); the colliders know the torso is solid between them, and a pouch hanging off
            // the belt lies outside them.
            var byBody = new Dictionary<int, List<int>>();
            var radial = new HashSet<int>();
            if (volumes != null)
            {
                for (int i = 0; i < pts.Count; i++)
                {
                    int body = Dominant(ptW[i]);
                    if (body < 0 || body >= volumes.Length || volumes[body] == null || volumes[body].Count == 0) continue;
                    if (!byBody.TryGetValue(body, out List<int> list)) byBody[body] = list = new List<int>();
                    list.Add(i);
                }
                foreach (KeyValuePair<int, List<int>> kv in byBody)
                {
                    int fp = fillPos.Count, fn = fillNeg.Count;
                    if (!RadialFill(kv.Value, kv.Key, volumes[kv.Key], pts, ptW, normal, t1, t2, pos, neg, fillPos, fillNeg)) continue;
                    radial.Add(kv.Key);
                    Region(fp, fn);
                }
            }

            foreach (List<int> cluster in clusters.Values)
            {
                if (radial.Contains(Dominant(ptW[cluster[0]]))) continue;
                List<int> hull = Hull(cluster, pts, t1, t2);
                if (hull.Count < 3) continue;
                int hp = fillPos.Count, hn = fillNeg.Count;
                var tris = new List<int>((hull.Count - 2) * 3);
                for (int k = 1; k + 1 < hull.Count; k++) { tris.Add(0); tris.Add(k); tris.Add(k + 1); }
                // pos lies on the +normal side: into the piece is +normal for pos, -normal for neg.
                AddPolygon(pos, hull, tris, null, pts, ptW, -normal, t1, t2, fillPos, normal * UnderfillDepth);
                AddPolygon(neg, hull, tris, null, pts, ptW, normal, t1, t2, fillNeg, -normal * UnderfillDepth);
                Region(hp, hn);
            }
            if (fillPos.Count > 0) pos.AddTriangles(fill, null, fillPos);
            if (fillNeg.Count > 0) neg.AddTriangles(fill, null, fillNeg);

            // Each fill polygon, for a burnt rim on flesh no skin covers (FillRegion).
            void Region(int fromPos, int fromNeg)
            {
                if (regions == null) return;
                var r = new FillRegion();
                r.posTris.AddRange(fillPos.GetRange(fromPos, fillPos.Count - fromPos));
                r.negTris.AddRange(fillNeg.GetRange(fromNeg, fillNeg.Count - fromNeg));
                regions.Add(r);
            }
        }

        private const int FillRays = 32;
        private const float VolumeMargin = 0.08f;   // m: colliders sit inside the skin, plate and padding stand off it

        /// <summary>
        /// Fill for one body: rays from a point inside both its collider volume and the convex outline of its section
        /// points; each ray stops at whichever boundary comes first. False if no such point exists (old fill is used).
        /// </summary>
        private static bool RadialFill(List<int> ids, int body, List<VolumeShape> shapes, List<Vector3> pts, List<BoneWeight> ptW,
            Vector3 normal, Vector3 t1, Vector3 t2, MeshData pos, MeshData neg, List<int> fillPos, List<int> fillNeg)
        {
            List<int> hull = Hull(ids, pts, t1, t2);
            if (hull.Count < 3) return false;
            Vector3 o = pts[hull[0]];
            var hx = new double[hull.Count];
            var hy = new double[hull.Count];
            double area = 0;
            for (int k = 0; k < hull.Count; k++)
            {
                Vector3 d = pts[hull[k]] - o;
                hx[k] = Vector3.Dot(d, t1);
                hy[k] = Vector3.Dot(d, t2);
            }
            for (int k = 0; k < hull.Count; k++)
            {
                int n = (k + 1) % hull.Count;
                area += hx[k] * hy[n] - hx[n] * hy[k];
            }
            if (Math.Abs(area) < 2e-4) return false;   // twice the area: under 1 cm², not worth a fill
            double sign = area > 0 ? 1 : -1;

            bool InHull(Vector3 p)
            {
                Vector3 d = p - o;
                double x = Vector3.Dot(d, t1), y = Vector3.Dot(d, t2);
                for (int k = 0; k < hull.Count; k++)
                {
                    int n = (k + 1) % hull.Count;
                    double cross = (hx[n] - hx[k]) * (y - hy[k]) - (hy[n] - hy[k]) * (x - hx[k]);
                    if (cross * sign < -1e-9) return false;
                }
                return true;
            }
            bool InVolume(Vector3 p)
            {
                foreach (VolumeShape s in shapes) if (s.Contains(p, VolumeMargin)) return true;
                return false;
            }
            Vector3 OnPlane(Vector3 p) => p - normal * Vector3.Dot(p - o, normal);

            // A start point inside both: the hull centroid, else a collider centre or capsule axis on the plane.
            var candidates = new List<Vector3>();
            Vector3 centroid = Vector3.zero;
            foreach (int id in hull) centroid += pts[id];
            candidates.Add(centroid / hull.Count);
            foreach (VolumeShape s in shapes)
            {
                candidates.Add(OnPlane(s.a));
                if (s.kind == ShapeKind.Capsule)
                {
                    float da = Vector3.Dot(s.a - o, normal), db = Vector3.Dot(s.b - o, normal);
                    if (da * db < 0f) candidates.Add(s.a + (s.b - s.a) * (da / (da - db)));
                    candidates.Add(OnPlane((s.a + s.b) * 0.5f));
                }
            }
            Vector3 c0 = default;
            bool found = false;
            foreach (Vector3 c in candidates)
                if (InHull(c) && InVolume(c)) { c0 = c; found = true; break; }
            if (!found) return false;

            float maxR = 0f;
            foreach (int id in hull) maxR = Mathf.Max(maxR, (pts[id] - c0).magnitude);
            BoneWeight w = Weights.Single(body);
            int center = pts.Count;
            pts.Add(c0);
            ptW.Add(w);
            var poly = new List<int> { center };
            for (int k = 0; k < FillRays; k++)
            {
                float ang = k * Mathf.PI * 2f / FillRays;
                Vector3 dir = t1 * Mathf.Cos(ang) + t2 * Mathf.Sin(ang);
                float lo = 0f, hi = maxR + 0.01f;
                for (int it = 0; it < 12; it++)   // ~0.25 mm on a 1 m ray; the union of colliders is star-shaped enough
                {
                    float mid = (lo + hi) * 0.5f;
                    Vector3 p = c0 + dir * mid;
                    if (InHull(p) && InVolume(p)) lo = mid;
                    else hi = mid;
                }
                poly.Add(pts.Count);
                pts.Add(c0 + dir * lo);
                ptW.Add(w);
            }
            var tris = new List<int>(FillRays * 3);
            for (int k = 1; k <= FillRays; k++) { tris.Add(0); tris.Add(k); tris.Add(k == FillRays ? 1 : k + 1); }
            // pos lies on the +normal side: into the piece is +normal for pos, -normal for neg.
            AddPolygon(pos, poly, tris, null, pts, ptW, -normal, t1, t2, fillPos, normal * UnderfillDepth);
            AddPolygon(neg, poly, tris, null, pts, ptW, normal, t1, t2, fillNeg, -normal * UnderfillDepth);
            return true;
        }

        private static int Dominant(BoneWeight w)
        {
            int b = w.boneIndex0;
            float best = w.weight0;
            if (w.weight1 > best) { best = w.weight1; b = w.boneIndex1; }
            if (w.weight2 > best) { best = w.weight2; b = w.boneIndex2; }
            if (w.weight3 > best) { b = w.boneIndex3; }
            return b;
        }

        /// <summary>Convex hull (monotone chain) of the points in the cut plane, as point ids in order.</summary>
        private static List<int> Hull(List<int> ids, List<Vector3> pts, Vector3 t1, Vector3 t2)
        {
            var hull = new List<int>();
            if (ids.Count < 3) return hull;
            Vector3 o = pts[ids[0]];
            var xy = new Dictionary<int, Vector2d>(ids.Count);
            foreach (int id in ids)
            {
                Vector3 d = pts[id] - o;
                xy[id] = new Vector2d(Vector3.Dot(d, t1), Vector3.Dot(d, t2));
            }
            var sorted = new List<int>(ids);
            sorted.Sort((a, b) => xy[a].x != xy[b].x ? xy[a].x.CompareTo(xy[b].x) : xy[a].y.CompareTo(xy[b].y));
            double Cross(int a, int b, int c) =>
                (xy[b].x - xy[a].x) * (xy[c].y - xy[a].y) - (xy[b].y - xy[a].y) * (xy[c].x - xy[a].x);

            var stack = new List<int>(sorted.Count + 1);
            foreach (int id in sorted)   // lower
            {
                while (stack.Count >= 2 && Cross(stack[stack.Count - 2], stack[stack.Count - 1], id) <= 1e-12) stack.RemoveAt(stack.Count - 1);
                stack.Add(id);
            }
            int lower = stack.Count + 1;
            for (int k = sorted.Count - 2; k >= 0; k--)   // upper
            {
                int id = sorted[k];
                while (stack.Count >= lower && Cross(stack[stack.Count - 2], stack[stack.Count - 1], id) <= 1e-12) stack.RemoveAt(stack.Count - 1);
                stack.Add(id);
            }
            stack.RemoveAt(stack.Count - 1);   // the first point again
            return stack;
        }

        private struct Vector2d
        {
            public readonly double x, y;
            public Vector2d(double x, double y) { this.x = x; this.y = y; }
        }

        private static void AddRing(MeshData m, List<int> comp, int[] segA, int[] segB, List<Vector3> pts, List<BoneWeight> ptW,
            Vector3 c, Vector3 planeNormal, Vector3 capNormal, Vector3 t1, Vector3 t2, List<int> tris)
        {
            var tan = new Vector4(t1.x, t1.y, t1.z, 1f);
            var outer = new Dictionary<int, int>();
            var inner = new Dictionary<int, int>();

            void Vert(int id, out int o, out int inn)
            {
                if (!outer.TryGetValue(id, out o))
                {
                    Vector3 p = pts[id];
                    Vector3 toC = Vector3.ProjectOnPlane(c - p, planeNormal);
                    float dist = toC.magnitude;
                    Vector3 q = dist > 1e-5f ? p + toC * (Mathf.Min(RingWidth, dist * 0.5f) / dist) : p;
                    o = m.AddVertex(p, capNormal, tan, CapUV(p, t1, t2), ptW[id]);
                    outer[id] = o;
                    inner[id] = m.AddVertex(q, capNormal, tan, CapUV(q, t1, t2), ptW[id]);
                }
                inn = inner[id];
            }

            foreach (int i in comp)
            {
                Vert(segA[i], out int oa, out int ia);
                Vert(segB[i], out int ob, out int ib);
                Vector3 a = m.v[oa], b = m.v[ob], bi = m.v[ib];
                // Same front-face rule as the caps: normal of (x, y, z) = Cross(y - x, z - x).
                if (Vector3.Dot(Vector3.Cross(b - a, bi - a), capNormal) >= 0f)
                {
                    tris.Add(oa); tris.Add(ob); tris.Add(ib);
                    tris.Add(oa); tris.Add(ib); tris.Add(ia);
                }
                else
                {
                    tris.Add(oa); tris.Add(ib); tris.Add(ob);
                    tris.Add(oa); tris.Add(ia); tris.Add(ib);
                }
            }
        }

        private static Vector2 CapUV(Vector3 p, Vector3 t1, Vector3 t2)
        {
            return new Vector2(Vector3.Dot(p, t1), Vector3.Dot(p, t2)) * 3f;
        }
    }
}
