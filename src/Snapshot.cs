using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Zandatsu
{
    /// <summary>
    /// Static vertex data of a mesh (an asset, or a creature mesh rebuilt by a hybrid cut), read once on the main thread
    /// and shared by every snapshot after it. Never changed once built, so worker threads read it freely.
    /// </summary>
    public sealed class MeshCache
    {
        public int vertexCount;
        public Vector3[] v, n;
        public Vector4[] t;
        public Vector2[] uv, uv1;
        public Color32[] col;
        public BoneWeight[] w;
        public Matrix4x4[] bind;
        public int[][] tris;   // per submesh, null when not a triangle list
        public int triCount;
        public int shapeCount;
        private Bounds bounds;
        private Mesh source;
        private readonly Dictionary<long, Frame> frames = new Dictionary<long, Frame>();

        public sealed class Frame
        {
            public Vector3[] dv, dn, dt;
        }

        private static readonly Dictionary<Mesh, MeshCache> cache = new Dictionary<Mesh, MeshCache>();
        private const int MaxCached = 256;

        /// <summary>Cached data of a mesh (main thread), or null if the mesh cannot be read.</summary>
        public static MeshCache Get(Mesh mesh)
        {
            // The vertex count and bounds catch a mesh rewritten in place since it was read.
            if (cache.TryGetValue(mesh, out MeshCache c) && c.vertexCount == mesh.vertexCount && c.bounds == mesh.bounds) return c;
            if (cache.Count >= MaxCached) Sweep();
            c = Read(mesh);
            if (c != null) cache[mesh] = c;
            else cache.Remove(mesh);
            return c;
        }

        public static bool Has(Mesh mesh) => mesh && cache.ContainsKey(mesh);

        /// <summary>Forgets destroyed meshes (creature meshes a later hybrid cut rebuilt again).</summary>
        public static void Sweep()
        {
            var dead = new List<Mesh>();
            foreach (KeyValuePair<Mesh, MeshCache> kv in cache) if (!kv.Key) dead.Add(kv.Key);
            foreach (Mesh m in dead) cache.Remove(m);
            if (cache.Count >= MaxCached) cache.Clear();
        }

        public static void Clear() => cache.Clear();

        /// <summary>Reads a mesh without caching it (null if it is not readable).</summary>
        public static MeshCache Read(Mesh mesh)
        {
            if (!mesh || !mesh.isReadable) return null;
            var c = new MeshCache
            {
                source = mesh,
                bounds = mesh.bounds,
                vertexCount = mesh.vertexCount,
                v = mesh.vertices,
                n = mesh.normals,
                t = mesh.tangents,
                uv = mesh.uv,
                uv1 = mesh.uv2,
                col = mesh.colors32,
                w = mesh.boneWeights,
                bind = mesh.bindposes,
                shapeCount = mesh.blendShapeCount,
                tris = new int[mesh.subMeshCount][]
            };
            for (int s = 0; s < mesh.subMeshCount; s++)
                if (mesh.GetTopology(s) == MeshTopology.Triangles)
                {
                    c.tris[s] = mesh.GetTriangles(s);
                    c.triCount += c.tris[s].Length / 3;
                }
            return c;
        }

        /// <summary>Deltas of one blend shape frame (main thread, cached).</summary>
        public Frame GetFrame(int shape, int frame)
        {
            long key = ((long)shape << 16) | (uint)frame;
            if (frames.TryGetValue(key, out Frame f)) return f;
            f = new Frame { dv = new Vector3[vertexCount], dn = new Vector3[vertexCount], dt = new Vector3[vertexCount] };
            source.GetBlendShapeFrameVertices(shape, frame, f.dv, f.dn, f.dt);
            frames[key] = f;
            return f;
        }

        public Mesh Source => source;

        public float FrameWeight(int shape, int frame) => source.GetBlendShapeFrameWeight(shape, frame);

        public int FrameCount(int shape) => source.GetBlendShapeFrameCount(shape);
    }

    /// <summary>
    /// One skinned renderer frozen on the main thread: the mesh data, the skinning matrices of its bones and the blend
    /// shapes in use. The vertices themselves are skinned later on a worker thread, as the GPU does it.
    /// </summary>
    public sealed class RendererSnap
    {
        public MeshCache mesh;
        public Matrix4x4[] skin;     // per bone: bone.localToWorld * bindpose (missing bones: rigid); null = unskinned
        public Matrix4x4 rigid;      // the renderer's localToWorld
        public int influences = 4;   // bones per vertex the GPU uses (quality setting)
        public Shape[] shapes;       // blend shapes with a weight, or null
        public Vector3[] bakedV, bakedN;   // fallback: a BakeMesh snapshot in renderer space (then skin is not used)
        public Vector4[] bakedT;
        public int[] boneBody;       // per bone: skeleton body or -1; null = the whole mesh follows rigidBody
        public int rigidBody = -1;
        public Material[] mats;
        public bool[] dropMat;       // material that cannot be drawn and has no fill to stand in: its triangles are dropped
        public MaterialPropertyBlock[] blocks;
        public int source = -1;      // hybrid cut: index of the source renderer
        public bool restPose;        // mesh is the renderer's own mesh data (not a baked snapshot): a hybrid rebuild can use it
        public StringBuilder log;    // diagnostics of this renderer, or null
        public string rootBone;
        public int nullBones, lostBones;

        /// <summary>A blend shape at its current weight: delta = a * wa + b * wb (b may be null).</summary>
        public struct Shape
        {
            public MeshCache.Frame a, b;
            public float wa, wb;
        }

        /// <summary>The blend shape frames and factors for a weight, as Unity interpolates (and extrapolates) them.</summary>
        public static bool MakeShape(MeshCache m, int shape, float weight, out Shape s)
        {
            s = default;
            int frames = m.FrameCount(shape);
            if (frames <= 0 || Mathf.Abs(weight) < 1e-4f) return false;
            float f0 = m.FrameWeight(shape, 0);
            if (frames == 1 || weight <= f0)
            {
                if (Mathf.Abs(f0) < 1e-6f) return false;
                s.a = m.GetFrame(shape, 0);
                s.wa = weight / f0;
                return true;
            }
            int k = 0;
            while (k + 2 < frames && weight > m.FrameWeight(shape, k + 1)) k++;
            float fa = m.FrameWeight(shape, k), fb = m.FrameWeight(shape, k + 1);
            float x = fb - fa > 1e-6f ? (weight - fa) / (fb - fa) : 1f;
            s.a = m.GetFrame(shape, k);
            s.b = m.GetFrame(shape, k + 1);
            s.wa = 1f - x;
            s.wb = x;
            return true;
        }

        private struct Acc
        {
            public float m00, m01, m02, m03, m10, m11, m12, m13, m20, m21, m22, m23, sum;

            public void Add(ref Matrix4x4 m, float w)
            {
                m00 += m.m00 * w; m01 += m.m01 * w; m02 += m.m02 * w; m03 += m.m03 * w;
                m10 += m.m10 * w; m11 += m.m11 * w; m12 += m.m12 * w; m13 += m.m13 * w;
                m20 += m.m20 * w; m21 += m.m21 * w; m22 += m.m22 * w; m23 += m.m23 * w;
                sum += w;
            }
        }

        private void AddBone(ref Acc acc, int bone, float weight)
        {
            if (weight <= 0f) return;
            if (bone >= 0 && bone < skin.Length) acc.Add(ref skin[bone], weight);
            else acc.Add(ref rigid, weight);
        }

        /// <summary>
        /// Vertex i in world space as drawn now: blend shapes, then linear blend skinning. Pure managed code (any thread).
        /// hasTangent: the mesh has tangents (otherwise tan is zero, like BakeMesh of a mesh without them).
        /// </summary>
        public void Vertex(int i, out Vector3 pos, out Vector3 nrm, out Vector4 tan)
        {
            MeshCache m = mesh;
            Vector3 p, nn;
            Vector3 td = Vector3.zero;
            float tw = 1f;
            bool hasN, hasT;
            if (bakedV != null)
            {
                p = bakedV[i];
                hasN = i < bakedN.Length;
                nn = hasN ? bakedN[i] : Vector3.up;
                hasT = i < bakedT.Length;
                if (hasT) { td = new Vector3(bakedT[i].x, bakedT[i].y, bakedT[i].z); tw = bakedT[i].w; }
            }
            else
            {
                p = m.v[i];
                hasN = i < m.n.Length;
                nn = hasN ? m.n[i] : Vector3.up;
                hasT = i < m.t.Length;
                if (hasT) { td = new Vector3(m.t[i].x, m.t[i].y, m.t[i].z); tw = m.t[i].w; }
                if (shapes != null)
                    for (int k = 0; k < shapes.Length; k++)
                    {
                        Shape s = shapes[k];
                        p += s.a.dv[i] * s.wa;
                        nn += s.a.dn[i] * s.wa;
                        td += s.a.dt[i] * s.wa;
                        if (s.b == null) continue;
                        p += s.b.dv[i] * s.wb;
                        nn += s.b.dn[i] * s.wb;
                        td += s.b.dt[i] * s.wb;
                    }
            }

            var acc = new Acc();
            if (skin != null && bakedV == null && i < m.w.Length)
            {
                BoneWeight bw = m.w[i];
                AddBone(ref acc, bw.boneIndex0, bw.weight0);
                if (influences >= 2) AddBone(ref acc, bw.boneIndex1, bw.weight1);
                if (influences >= 4)
                {
                    AddBone(ref acc, bw.boneIndex2, bw.weight2);
                    AddBone(ref acc, bw.boneIndex3, bw.weight3);
                }
            }
            if (acc.sum <= 1e-6f)
            {
                acc = new Acc();
                acc.Add(ref rigid, 1f);
            }
            else if (Mathf.Abs(acc.sum - 1f) > 1e-5f)
            {
                float k = 1f / acc.sum;
                acc.m00 *= k; acc.m01 *= k; acc.m02 *= k; acc.m03 *= k;
                acc.m10 *= k; acc.m11 *= k; acc.m12 *= k; acc.m13 *= k;
                acc.m20 *= k; acc.m21 *= k; acc.m22 *= k; acc.m23 *= k;
            }

            pos = new Vector3(acc.m00 * p.x + acc.m01 * p.y + acc.m02 * p.z + acc.m03,
                              acc.m10 * p.x + acc.m11 * p.y + acc.m12 * p.z + acc.m13,
                              acc.m20 * p.x + acc.m21 * p.y + acc.m22 * p.z + acc.m23);
            if (hasN)
                nrm = new Vector3(acc.m00 * nn.x + acc.m01 * nn.y + acc.m02 * nn.z,
                                  acc.m10 * nn.x + acc.m11 * nn.y + acc.m12 * nn.z,
                                  acc.m20 * nn.x + acc.m21 * nn.y + acc.m22 * nn.z).normalized;
            else nrm = Vector3.up;
            if (hasT)
            {
                Vector3 d = new Vector3(acc.m00 * td.x + acc.m01 * td.y + acc.m02 * td.z,
                                        acc.m10 * td.x + acc.m11 * td.y + acc.m12 * td.z,
                                        acc.m20 * td.x + acc.m21 * td.y + acc.m22 * td.z).normalized;
                tan = new Vector4(d.x, d.y, d.z, tw);
            }
            else tan = Vector4.zero;
        }
    }

    /// <summary>
    /// A creature frozen on the main thread in a few milliseconds (bone matrices, blend shape weights, materials); the
    /// world-space mesh soup is built from it on the worker thread. Before, BakeMesh and per-vertex copies of ~45k vertices
    /// held up the frame of every cut by 10-55 ms.
    /// </summary>
    public sealed class CreatureSnapshot
    {
        public readonly List<RendererSnap> renderers = new List<RendererSnap>();
        public List<KeyValuePair<int, Vector3>> centres;   // body origins and collider centres (unmapped vertices)
        public string[] bodyNames;
        public bool track;
        public int vertexCount;
        public string logHead;      // "[Zandatsu] renderers ...:" (diagnostics once per outfit), or null
        public readonly List<StringBuilder> logParts = new List<StringBuilder>();   // per renderer, skipped ones too

        private static readonly Color32 White = new Color32(255, 255, 255, 255);

        private int Nearest(Vector3 p)
        {
            int best = 0;
            float bestD = float.MaxValue;
            foreach (KeyValuePair<int, Vector3> c in centres)
            {
                float d = (c.Value - p).sqrMagnitude;
                if (d < bestD) { bestD = d; best = c.Key; }
            }
            return best;
        }

        /// <summary>
        /// Hybrid cut (worker thread): the vertex channels of every creature mesh the cut rebuilds, interpolated from the
        /// snapshot's mesh data, so the main thread only uploads them (it took up to 15 ms there on armoured enemies).
        /// The same rules as Hybrid.Rebuild used.
        /// </summary>
        public void PrepareRebuild(CreatureRecipe rec)
        {
            System.Threading.Tasks.Parallel.ForEach(rec.renderers, rp =>
            {
                if (rp.removed || rp.renderer < 0 || rp.renderer >= renderers.Count || !renderers[rp.renderer].restPose) return;
                MeshCache m = renderers[rp.renderer].mesh;
                int srcCount = m.vertexCount, n = rp.verts.Count;
                bool valid = true;
                foreach (VertexOrigin og in rp.verts)
                    if (og.a < 0 || og.a >= srcCount || og.b >= srcCount) { valid = false; break; }
                if (!valid) return;
                var v = new Vector3[n];
                Vector3[] nn = m.n.Length == srcCount ? new Vector3[n] : null;
                Vector4[] tt = m.t.Length == srcCount ? new Vector4[n] : null;
                Vector2[] u0 = m.uv.Length == srcCount ? new Vector2[n] : null;
                Vector2[] u1 = m.uv1.Length == srcCount ? new Vector2[n] : null;
                Color32[] cc = m.col.Length == srcCount ? new Color32[n] : null;
                for (int i = 0; i < n; i++)
                {
                    VertexOrigin og = rp.verts[i];
                    int a = og.a, b = og.b >= 0 ? og.b : og.a;
                    float t = og.b >= 0 ? og.t : 0f;
                    v[i] = Vector3.LerpUnclamped(m.v[a], m.v[b], t);
                    if (nn != null)
                    {
                        Vector3 x = Vector3.LerpUnclamped(m.n[a], m.n[b], t);
                        nn[i] = x.sqrMagnitude > 1e-10f ? x.normalized : (m.n[a].sqrMagnitude > 1e-10f ? m.n[a] : Vector3.up);
                    }
                    if (tt != null)
                    {
                        Vector4 x = Vector4.LerpUnclamped(m.t[a], m.t[b], t);
                        var d = new Vector3(x.x, x.y, x.z);
                        if (d.sqrMagnitude < 1e-10f) d = new Vector3(m.t[a].x, m.t[a].y, m.t[a].z);
                        d = d.sqrMagnitude > 1e-10f ? d.normalized : Vector3.right;
                        tt[i] = new Vector4(d.x, d.y, d.z, m.t[a].w < 0f ? -1f : 1f);
                    }
                    if (u0 != null) u0[i] = Vector2.LerpUnclamped(m.uv[a], m.uv[b], t);
                    if (u1 != null) u1[i] = t < 0.5f ? m.uv1[a] : m.uv1[b];   // occlusion region ids: never blended
                    if (cc != null) cc[i] = Color32.Lerp(m.col[a], m.col[b], t);
                }
                rp.pn = nn;
                rp.pt = tt;
                rp.pu0 = u0;
                rp.pu1 = u1;
                rp.pc = cc;
                rp.preparedFrom = m.Source;
                rp.pv = v;   // last: pv marks the set as complete
            });
        }

        private const int SkinChunk = 4096;   // vertices per parallel task: one big mesh (a cuirass) no longer sets the pace

        /// <summary>
        /// The world-space mesh soup (worker thread): skinned in parallel chunks straight into the buffers, which come from
        /// the pool and go back to it once the cut is sliced (ZandatsuScript).
        /// </summary>
        public MeshData Build()
        {
            var bases = new int[renderers.Count];
            int total = 0;
            bool uv1 = false, col = false;
            for (int k = 0; k < renderers.Count; k++)
            {
                MeshCache m = renderers[k].mesh;
                bases[k] = total;
                total += m.vertexCount;
                uv1 |= m.uv1.Length > 0;
                col |= m.col.Length > 0;
            }
            MeshData world = MeshData.Rent(total + 256, track, uv1, col);
            VertexBuffers buf = VertexBuffers.Rent(total);
            bool[] nearest = logHead != null ? new bool[total] : null;
            var chunks = new List<(int renderer, int start, int end)>();
            for (int k = 0; k < renderers.Count; k++)
                for (int start = 0, count = renderers[k].mesh.vertexCount; start < count; start += SkinChunk)
                    chunks.Add((k, start, Mathf.Min(count, start + SkinChunk)));
            System.Threading.Tasks.Parallel.For(0, chunks.Count, c =>
                Skin(buf, renderers[chunks[c].renderer], bases[chunks[c].renderer], chunks[c].start, chunks[c].end, nearest));
            buf.AppendTo(world, total);
            VertexBuffers.Return(buf);
            for (int k = 0; k < renderers.Count; k++)
            {
                if (renderers[k].log != null) LogStats(renderers[k], world, bases[k], nearest);
                AddTriangles(world, renderers[k], bases[k]);
            }
            if (logHead != null)
            {
                var line = new StringBuilder(logHead);
                foreach (StringBuilder part in logParts) line.Append(part);
                Debug.Log(line.ToString());
            }
            return world;
        }

        /// <summary>Vertices start..end of one renderer, written at first + i (any thread, chunks never overlap).</summary>
        private void Skin(VertexBuffers world, RendererSnap r, int first, int start, int end, bool[] nearest)
        {
            MeshCache m = r.mesh;
            bool remap = r.boneBody != null && m.w.Length == m.vertexCount;
            int Body(int bone) => bone >= 0 && bone < r.boneBody.Length ? r.boneBody[bone] : -1;
            for (int i = start; i < end; i++)
            {
                int vi = first + i;
                r.Vertex(i, out Vector3 p, out Vector3 nrm, out Vector4 tan);
                BoneWeight w;
                if (remap)
                {
                    BoneWeight s = m.w[i];
                    w = new BoneWeight
                    {
                        boneIndex0 = Body(s.boneIndex0), weight0 = s.weight0,
                        boneIndex1 = Body(s.boneIndex1), weight1 = s.weight1,
                        boneIndex2 = Body(s.boneIndex2), weight2 = s.weight2,
                        boneIndex3 = Body(s.boneIndex3), weight3 = s.weight3
                    };
                }
                else w = Weights.Single(r.rigidBody);
                // A vertex whose bones all map to nothing used to fall onto body 0 (the hips): a helmet then followed the
                // torso instead of the head. It follows the body it sits on instead.
                float mapped = (w.boneIndex0 >= 0 ? w.weight0 : 0f) + (w.boneIndex1 >= 0 ? w.weight1 : 0f) +
                               (w.boneIndex2 >= 0 ? w.weight2 : 0f) + (w.boneIndex3 >= 0 ? w.weight3 : 0f);
                if (mapped <= 1e-4f)
                {
                    w = Weights.Single(Nearest(p));
                    if (nearest != null) nearest[vi] = true;
                }
                world.v[vi] = p;
                world.n[vi] = nrm;
                world.t[vi] = tan;
                world.w[vi] = w;
                world.uv[vi] = i < m.uv.Length ? m.uv[i] : Vector2.zero;
                world.uv1[vi] = i < m.uv1.Length ? m.uv1[i] : Vector2.zero;
                world.col[vi] = i < m.col.Length ? m.col[i] : White;
                world.o[vi] = r.source >= 0 ? new VertexOrigin(r.source, i, -1, 0f) : VertexOrigin.None;
            }
        }

        /// <summary>Diagnostics of one renderer (once per outfit).</summary>
        private void LogStats(RendererSnap r, MeshData world, int first, bool[] nearest)
        {
            int zeroN = 0, zeroT = 0, nearestCount = 0;
            var perBody = new int[bodyNames.Length];
            for (int vi = first, end = first + r.mesh.vertexCount; vi < end; vi++)
            {
                if (!(world.n[vi].sqrMagnitude > 1e-10f)) zeroN++;
                Vector4 tan = world.t[vi];
                if (!(tan.x * tan.x + tan.y * tan.y + tan.z * tan.z > 1e-10f)) zeroT++;
                if (nearest[vi]) nearestCount++;
                int b0 = world.w[vi].boneIndex0;
                if (b0 >= 0 && b0 < perBody.Length) perBody[b0]++;
            }
            if (r.mesh.t.Length == 0 && r.bakedT == null) r.log.Append(" noTangents");
            else if (zeroT > 0) r.log.Append(" zeroT ").Append(zeroT);
            if (zeroN > 0) r.log.Append(" zeroN ").Append(zeroN);
            int top = 0;
            for (int b = 1; b < perBody.Length; b++) if (perBody[b] > perBody[top]) top = b;
            r.log.Append(" -> ").Append(perBody.Length > 0 ? bodyNames[top] : "-");
            if (r.rootBone != null) r.log.Append(" root ").Append(r.rootBone);
            if (r.nullBones > 0) r.log.Append(" nullBones ").Append(r.nullBones);
            if (r.lostBones > 0) r.log.Append(" lostBones ").Append(r.lostBones);
            if (nearestCount > 0) r.log.Append(" nearest ").Append(nearestCount).Append('v');
        }

        private static void AddTriangles(MeshData world, RendererSnap r, int first)
        {
            MeshCache m = r.mesh;
            for (int s = 0; s < m.tris.Length; s++)
            {
                int[] tri = m.tris[s];
                if (tri == null) continue;
                int mi = r.mats.Length > 0 ? Mathf.Min(s, r.mats.Length - 1) : -1;
                bool drop = mi >= 0 && r.dropMat != null && r.dropMat[mi];
                var list = new List<int>(drop ? 0 : tri.Length);
                if (!drop) for (int i = 0; i < tri.Length; i++) list.Add(tri[i] + first);
                world.subs.Add(list);
                world.mats.Add(mi >= 0 ? r.mats[mi] : null);
                world.blocks.Add(r.blocks != null && s < r.blocks.Length ? r.blocks[s] : null);
                world.subTag.Add(world.o != null && r.source >= 0 ? VertexOrigin.Tag(r.source, s) : -1);
            }
        }
    }
}
