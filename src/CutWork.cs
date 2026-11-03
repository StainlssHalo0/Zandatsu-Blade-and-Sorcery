using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;

namespace Zandatsu
{
    /// <summary>
    /// One spawnable piece: a skinned mesh (world-space bind pose) plus the skeleton bodies it is skinned to.
    /// Bone indices in data.w are local (index into bodies).
    /// </summary>
    public class PieceSpec
    {
        public MeshData data;
        public int[] bodies;          // skeleton body index per local bone
        public bool[] cut;            // body was split by this cut -> needs a fitted collider
        public ShapeSpec[] fitted;    // body-local collider for cut bodies
        public float[] massFraction;  // share of the source body's mass
        public int side;              // +1 = normal side, -1 = other side
        public bool hasCap;
        public Vector3 capCenter;
        public int capBody;
        public Bounds bounds;
    }

    public class CutResult
    {
        public readonly List<PieceSpec> pieces = new List<PieceSpec>();
        public int posVerts, negVerts;
        public float ms;
        public string msDetail = "";      // where the worker time went (diagnostics)
        public string cutBodies = "all";   // bodies the plane was allowed to cut (diagnostics)
        public (int normals, int tangents, int other) fixedSource, fixedPieces;   // MeshData.Sanitize repairs (diagnostics)
        public CreatureRecipe creature;     // hybrid cut: what stays the creature (null = everything became pieces)
        public int[] pieceBodyVerts;        // per skeleton body: vertices that went into pieces
    }

    /// <summary>Pure managed math, runs on a worker thread: no Unity API that needs the main thread.</summary>
    public static class CutWork
    {
        private const int MinPieceVerts = 40;
        private const int MinBodyVerts = 12;
        private const int MinCrossVerts = 4;

        /// <param name="hitBody">Body the blade struck, or -1 to cut everything the plane crosses.</param>
        /// <param name="metal">Materials of metal armour (cut edge = steel band), may be empty.</param>
        /// <param name="steel">Material for that band, or null.</param>
        /// <param name="rootBody">Hybrid cut: skeleton index of the creature's root part; the chunk holding it stays the
        /// creature (src must track origins). -1 = every chunk becomes a piece.</param>
        /// <param name="sourceCounts">Hybrid cut: vertex and triangle count of each source renderer.</param>
        public static CutResult SliceAndPrepare(MeshData src, Skeleton skel, Vector3 point, Vector3 normal, Material fill, int hitBody,
                                                HashSet<Material> metal = null, Material steel = null,
                                                int rootBody = -1, (int verts, int tris)[] sourceCounts = null, Material burn = null,
                                                BurnField burnField = null)
        {
            var sw = Stopwatch.StartNew();
            int weightCount = src.w.Count;
            Parallel.For(0, (weightCount + 4095) / 4096, c =>
            {
                for (int i = c * 4096, end = System.Math.Min(weightCount, i + 4096); i < end; i++) src.w[i] = Weights.Normalize(src.w[i]);
            });
            // The source is no longer sanitized before slicing: in every test it found nothing to fix (n0/t0/o0) and it
            // cost a full pass over 40-50k vertices. The pieces still are.
            var fixedSource = (0, 0, 0);
            sbyte[] forced = ForcedSides(src, skel, point, normal, hitBody, out string cutBodies, out sbyte[] bodySide);
            double tPrep = sw.Elapsed.TotalMilliseconds;
            if (!MeshSlicer.Slice(src, point, normal, fill, out MeshData a, out MeshData b, forced, metal, steel,
                                  VolumeShape.FromSkeleton(skel), burn, burnField)) return null;
            double tSlice = sw.Elapsed.TotalMilliseconds;
            string sliceDetail = MeshSlicer.LastDetail;

            var r = new CutResult { posVerts = a.VertexCount, negVerts = b.VertexCount, cutBodies = cutBodies, fixedSource = fixedSource };
            // Both sides split at once (the worker has the cores to itself; a cut used to wait for each step in turn).
            var chunks = new List<KeyValuePair<MeshData, int>>();
            int keptApartA = 0, keptApartB = 0;
            List<MeshData> splitA = null, splitB = null;
            Parallel.Invoke(
                () => splitA = SplitComponents(a, Detached(skel, point, normal, bodySide, +1), ref keptApartA),
                () => splitB = SplitComponents(b, Detached(skel, point, normal, bodySide, -1), ref keptApartB));
            int keptApart = keptApartA + keptApartB;
            foreach (MeshData g in splitA) chunks.Add(new KeyValuePair<MeshData, int>(g, +1));
            foreach (MeshData g in splitB) chunks.Add(new KeyValuePair<MeshData, int>(g, -1));
            // A half split into chunks was copied into them; one that stayed whole is a chunk itself.
            if (!splitA.Contains(a)) MeshData.Return(a);
            if (!splitB.Contains(b)) MeshData.Return(b);
            double tSplit = sw.Elapsed.TotalMilliseconds;

            int bodyCount = skel.bodies.Count;
            int keep = -1;
            if (rootBody >= 0 && rootBody < bodyCount && src.o != null && sourceCounts != null)
            {
                // The chunk with most of the root body stays the creature; everything else flies off.
                int best = 0;
                for (int c = 0; c < chunks.Count; c++)
                {
                    int count = 0;
                    foreach (BoneWeight bw in chunks[c].Key.w) if (bw.boneIndex0 == rootBody) count++;
                    if (count > best) { best = count; keep = c; }
                }
            }
            r.pieceBodyVerts = new int[bodyCount];
            for (int c = 0; c < chunks.Count; c++)
            {
                if (c == keep) continue;
                foreach (BoneWeight bw in chunks[c].Key.w)
                    if (bw.boneIndex0 >= 0 && bw.boneIndex0 < bodyCount) r.pieceBodyVerts[bw.boneIndex0]++;
            }
            for (int c = 0; c < chunks.Count; c++)
                if (c != keep) chunks[c] = new KeyValuePair<MeshData, int>(chunks[c].Key.Detach(), chunks[c].Value);   // pieces live on
            // The creature's recipe and every piece are independent of each other: built in parallel, kept in chunk order.
            var built = new PieceSpec[chunks.Count];
            var fixes = new (int normals, int tangents, int other)[chunks.Count];
            Parallel.For(0, chunks.Count, c =>
            {
                if (c == keep)
                {
                    r.creature = CreatureRecipe.Build(chunks[c].Key, chunks[c].Value, sourceCounts, bodyCount, fill);
                    return;
                }
                PieceSpec p = MakePiece(chunks[c].Key, skel, fill, chunks[c].Value);
                if (p == null) return;
                fixes[c] = p.data.Sanitize();   // cut edges blending opposite normals of thin cloth, caps
                built[c] = p;
            });
            for (int c = 0; c < chunks.Count; c++)
            {
                if (built[c] == null) continue;
                r.pieces.Add(built[c]);
                r.fixedPieces = (r.fixedPieces.normals + fixes[c].normals, r.fixedPieces.tangents + fixes[c].tangents,
                                 r.fixedPieces.other + fixes[c].other);
            }

            // A body present in more than one piece (or a piece and the creature) was split: it gets a fitted collider
            // and a share of its mass.
            var presence = new Dictionary<int, int>();
            var totalVerts = new Dictionary<int, int>();
            var counts = new List<int[]>();
            if (r.creature != null)
                for (int body = 0; body < bodyCount; body++)
                {
                    if (r.creature.bodyVerts[body] < MinBodyVerts) continue;
                    presence[body] = 1;
                    totalVerts[body] = r.creature.bodyVerts[body];
                }
            foreach (PieceSpec p in r.pieces)
            {
                int[] c = DominantCounts(p);
                counts.Add(c);
                for (int k = 0; k < p.bodies.Length; k++)
                {
                    presence.TryGetValue(p.bodies[k], out int n);
                    presence[p.bodies[k]] = n + 1;
                    totalVerts.TryGetValue(p.bodies[k], out int tv);
                    totalVerts[p.bodies[k]] = tv + c[k];
                }
            }
            for (int pi = 0; pi < r.pieces.Count; pi++)
            {
                PieceSpec p = r.pieces[pi];
                int[] c = counts[pi];
                p.cut = new bool[p.bodies.Length];
                p.fitted = new ShapeSpec[p.bodies.Length];
                p.massFraction = new float[p.bodies.Length];
                for (int k = 0; k < p.bodies.Length; k++)
                {
                    int body = p.bodies[k];
                    p.cut[k] = presence[body] > 1;
                    p.massFraction[k] = p.cut[k] ? (float)c[k] / Mathf.Max(1, totalVerts[body]) : 1f;
                    if (p.cut[k]) p.fitted[k] = FitShape(p.data, k, skel.bodies[body]);
                }
            }
            // Parts the corpse shares with a piece: a collider fitted to what the corpse kept of them (the original one
            // reaches into the empty space where the cut-off part was).
            if (r.creature != null)
                for (int body = 0; body < bodyCount; body++)
                    if (r.creature.bodyVerts[body] >= MinBodyVerts && r.pieceBodyVerts[body] > 0)
                        r.creature.fitted[body] = FitShape(chunks[keep].Key, body, skel.bodies[body]);
            if (keep >= 0) MeshData.Return(chunks[keep].Key);   // the recipe holds copies of all it needs from it
            r.ms = (float)sw.Elapsed.TotalMilliseconds;
            r.msDetail = $"weights {tPrep:0.0}, slice+caps {tSlice - tPrep:0.0} ({sliceDetail}), split {tSplit - tSlice:0.0}, pieces {r.ms - tSplit:0.0}" +
                         (keptApart > 0 ? $"; cut-off limbs kept apart {keptApart}" : "");
            return r;
        }

        // ---------- which bodies get cut ----------

        /// <summary>
        /// The plane is infinite, the blade is not: cutting off a hand must not also slice the torso and the other hand
        /// lying in the same plane. Only the struck body is cut, plus neighbours (joint tree) the plane also passes
        /// through, recursively. Every other body stays whole on the side of the cut body it hangs from.
        /// Returns null when everything may be cut.
        /// </summary>
        /// <param name="bodySide">Side every body outside the cut set went to (0 = cut by the plane), or null.</param>
        private static sbyte[] ForcedSides(MeshData m, Skeleton skel, Vector3 point, Vector3 normal, int hitBody, out string cutBodies,
                                           out sbyte[] bodySide)
        {
            cutBodies = "all";
            bodySide = null;
            int bc = skel.bodies.Count;
            if (hitBody < 0 || hitBody >= bc) return null;
            normal.Normalize();

            int n = m.VertexCount;
            var posC = new int[bc];
            var negC = new int[bc];
            for (int i = 0; i < n; i++)
            {
                int b = m.w[i].boneIndex0;
                if (b < 0 || b >= bc) continue;
                if (Vector3.Dot(m.v[i] - point, normal) >= 0f) posC[b]++;
                else negC[b]++;
            }

            var adj = new List<int>[bc];
            for (int b = 0; b < bc; b++) adj[b] = new List<int>();
            for (int b = 0; b < bc; b++)
            {
                int p = skel.bodies[b].parent;
                if (p < 0 || p >= bc || p == b) continue;
                adj[b].Add(p);
                adj[p].Add(b);
            }

            var inSet = new bool[bc];
            var queue = new Queue<int>();
            inSet[hitBody] = true;
            queue.Enqueue(hitBody);
            int setCount = 1;
            while (queue.Count > 0)
            {
                int b = queue.Dequeue();
                foreach (int nb in adj[b])
                {
                    if (inSet[nb] || posC[nb] < MinCrossVerts || negC[nb] < MinCrossVerts) continue;
                    inSet[nb] = true;
                    setCount++;
                    queue.Enqueue(nb);
                }
            }

            // Untouched bodies: side of the body they hang from, spreading out from the cut set. A body next to the
            // cut set takes the side most of its own vertices are on.
            var side = new sbyte[bc];
            var visited = new bool[bc];
            for (int b = 0; b < bc; b++)
                if (inSet[b]) { visited[b] = true; queue.Enqueue(b); }
            while (queue.Count > 0)
            {
                int b = queue.Dequeue();
                foreach (int nb in adj[b])
                {
                    if (visited[nb]) continue;
                    visited[nb] = true;
                    side[nb] = inSet[b] ? (sbyte)(posC[nb] >= negC[nb] ? 1 : -1) : side[b];
                    queue.Enqueue(nb);
                }
            }
            bool anyForced = false;
            for (int b = 0; b < bc; b++)
            {
                if (inSet[b]) continue;
                if (!visited[b]) side[b] = (sbyte)(posC[b] >= negC[b] ? 1 : -1);   // not connected: plain plane side
                if ((side[b] > 0 ? negC[b] : posC[b]) > 0) anyForced = true;       // the plane alone would put it elsewhere
            }

            var names = new List<string>();
            for (int b = 0; b < bc; b++) if (inSet[b]) names.Add(skel.bodies[b].name);
            cutBodies = string.Join(",", names);
            bodySide = side;
            if (!anyForced) return null;

            var forced = new sbyte[n];
            for (int i = 0; i < n; i++)
            {
                int b = m.w[i].boneIndex0;
                if (b >= 0 && b < bc && !inSet[b]) forced[i] = side[b];
            }
            return forced;
        }

        // ---------- connected components ----------

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        private static void Union(int[] parent, int a, int b)
        {
            a = Find(parent, a);
            b = Find(parent, b);
            if (a != b) parent[a] = b;
        }

        /// <summary>
        /// Splits a half into physically separate chunks (a hand cut off in front of the torso must not stay glued to it).
        /// Vertices are welded by position (UV seams), triangles connect them; overlapping layers (clothes over skin) that
        /// share a body and overlap in space are merged back together.
        /// </summary>
        /// <summary>
        /// Per body, for one side of the plane: true if it no longer hangs from the root there, because its joint (or the
        /// joint of a body above it) lies on the other side. An arm cut above the elbow: forearm and hand are off, even
        /// while they lie against the chest.
        /// </summary>
        private static bool[] Detached(Skeleton skel, Vector3 point, Vector3 normal, sbyte[] bodySide, int side)
        {
            int n = skel.bodies.Count;
            var state = new sbyte[n];   // 0 unknown, 1 hangs from the root, 2 cut off
            bool Anchored(int b, int depth)
            {
                if (state[b] != 0) return state[b] == 1;
                SkelBody body = skel.bodies[b];
                bool ok;
                if (body.parent < 0 || body.parent >= n || body.parent == b || depth > 64) ok = true;
                else
                {
                    int pivotSide;
                    if (bodySide != null && b < bodySide.Length && bodySide[b] != 0) pivotSide = bodySide[b];
                    else
                    {
                        Vector3 pivot = body.joint != null ? body.pos + body.rot * body.joint.anchor : body.pos;
                        pivotSide = Vector3.Dot(pivot - point, normal) >= 0f ? 1 : -1;
                    }
                    ok = pivotSide == side && Anchored(body.parent, depth + 1);
                }
                state[b] = ok ? (sbyte)1 : (sbyte)2;
                return ok;
            }
            var result = new bool[n];
            for (int b = 0; b < n; b++) result[b] = !Anchored(b, 0);
            return result;
        }

        /// <param name="detached">Per body: cut off from the root on this side (see Detached), or null.</param>
        /// <param name="keptApart">Counts merges refused between a cut-off limb and what still hangs from the root.</param>
        private static List<MeshData> SplitComponents(MeshData m, bool[] detached, ref int keptApart)
        {
            int n = m.VertexCount;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            var weld = new Dictionary<Vector3Int, int>(n);
            for (int i = 0; i < n; i++)
            {
                Vector3 p = m.v[i];
                var key = new Vector3Int(Mathf.RoundToInt(p.x * 1000f), Mathf.RoundToInt(p.y * 1000f), Mathf.RoundToInt(p.z * 1000f));
                if (weld.TryGetValue(key, out int j)) Union(parent, i, j);
                else weld[key] = i;
            }
            foreach (List<int> tris in m.subs)
                for (int k = 0; k + 2 < tris.Count; k += 3)
                {
                    Union(parent, tris[k], tris[k + 1]);
                    Union(parent, tris[k], tris[k + 2]);
                }

            var compOf = new int[n];
            var rootToComp = new Dictionary<int, int>();
            var compSize = new List<int>();
            for (int i = 0; i < n; i++)
            {
                int r = Find(parent, i);
                if (!rootToComp.TryGetValue(r, out int c))
                {
                    c = compSize.Count;
                    rootToComp[r] = c;
                    compSize.Add(0);
                }
                compOf[i] = c;
                compSize[c]++;
            }
            int compCount = compSize.Count;
            if (compCount == 1) return new List<MeshData> { m };

            // Vertices of cut-off bodies and of bodies still hanging from the root, per component.
            var compDet = new int[compCount];
            var compAnc = new int[compCount];
            for (int i = 0; i < n; i++)
            {
                int body = m.w[i].boneIndex0;
                if (detached != null && body >= 0 && body < detached.Length && detached[body]) compDet[compOf[i]]++;
                else compAnc[compOf[i]]++;
            }
            // Does the component really carry this kind of body, or only a few blended seam vertices of it?
            bool Carries(int c, bool det)
            {
                int count = det ? compDet[c] : compAnc[c];
                return count * 20 >= compSize[c] && (count >= MinPieceVerts || count * 2 >= compSize[c]);
            }

            // body -> comp -> bounds of the vertices that body dominates.
            var perBody = new Dictionary<int, Dictionary<int, Bounds>>();
            for (int i = 0; i < n; i++)
            {
                int body = m.w[i].boneIndex0;
                if (!perBody.TryGetValue(body, out Dictionary<int, Bounds> byComp))
                {
                    byComp = new Dictionary<int, Bounds>();
                    perBody[body] = byComp;
                }
                int c = compOf[i];
                if (byComp.TryGetValue(c, out Bounds bb)) { bb.Encapsulate(m.v[i]); byComp[c] = bb; }
                else byComp[c] = new Bounds(m.v[i], Vector3.zero);
            }

            var cparent = new int[compCount];
            for (int i = 0; i < compCount; i++) cparent[i] = i;
            foreach (KeyValuePair<int, Dictionary<int, Bounds>> kvBody in perBody)
            {
                Dictionary<int, Bounds> byComp = kvBody.Value;
                if (byComp.Count < 2) continue;
                // Islands of one body that touch belong together (armour plate and the skin under it), but not across a
                // cut joint: the stump of a cut upper arm shares that body with the blended shoulder seam of the chest,
                // and joining them kept a cut-off arm hanging from the corpse on an invisible joint.
                bool det = detached != null && kvBody.Key >= 0 && kvBody.Key < detached.Length && detached[kvBody.Key];
                var list = new List<KeyValuePair<int, Bounds>>(byComp);
                for (int x = 0; x < list.Count; x++)
                {
                    Bounds bx = list[x].Value;
                    bx.Expand(0.04f);
                    for (int y = x + 1; y < list.Count; y++)
                    {
                        if (!bx.Intersects(list[y].Value)) continue;
                        if (detached != null && (!Carries(list[x].Key, det) || !Carries(list[y].Key, det))) { keptApart++; continue; }
                        Union(cparent, list[x].Key, list[y].Key);
                    }
                }
            }

            var groupOf = new int[compCount];
            var groupSize = new List<int>();
            var groupBodies = new List<HashSet<int>>();
            var rootToGroup = new Dictionary<int, int>();
            for (int c = 0; c < compCount; c++)
            {
                int r = Find(cparent, c);
                if (!rootToGroup.TryGetValue(r, out int g))
                {
                    g = groupSize.Count;
                    rootToGroup[r] = g;
                    groupSize.Add(0);
                    groupBodies.Add(new HashSet<int>());
                }
                groupOf[c] = g;
                groupSize[g] += compSize[c];
            }
            int groupCount = groupSize.Count;
            if (groupCount == 1) return new List<MeshData> { m };
            for (int i = 0; i < n; i++) groupBodies[groupOf[compOf[i]]].Add(m.w[i].boneIndex0);

            // Tiny groups (buttons, hair strands) join the biggest group sharing a body, or are dropped.
            var finalGroup = new int[groupCount];
            for (int g = 0; g < groupCount; g++)
            {
                finalGroup[g] = g;
                if (groupSize[g] >= MinPieceVerts) continue;
                int best = -1;
                for (int h = 0; h < groupCount; h++)
                {
                    if (h == g || groupSize[h] < MinPieceVerts) continue;
                    if (best >= 0 && groupSize[h] <= groupSize[best]) continue;
                    if (groupBodies[h].Overlaps(groupBodies[g])) best = h;
                }
                finalGroup[g] = best;
            }

            var outIndex = new Dictionary<int, int>();
            var result = new List<MeshData>();
            var vmap = new int[n];
            // The biggest chunk (usually what stays the creature, which is only needed until its recipe is made) takes a
            // pooled buffer; the others get buffers of their own size, as pieces they live on.
            int biggest = -1;
            for (int g = 0; g < groupCount; g++)
                if (finalGroup[g] == g && (biggest < 0 || groupSize[g] > groupSize[biggest])) biggest = g;
            for (int i = 0; i < n; i++)
            {
                int g = finalGroup[groupOf[compOf[i]]];
                if (g < 0) { vmap[i] = -1; continue; }
                if (!outIndex.TryGetValue(g, out int oi))
                {
                    oi = result.Count;
                    outIndex[g] = oi;
                    result.Add(g == biggest ? MeshData.Rent(groupSize[g] + 64, m.o != null, m.hasUv1, m.hasCol) : m.Sibling(groupSize[g] + 64));
                }
                vmap[i] = result[oi].CopyVertex(m, i);
            }

            for (int s = 0; s < m.subs.Count; s++)
            {
                var perOut = new List<int>[result.Count];
                List<int> tris = m.subs[s];
                for (int k = 0; k + 2 < tris.Count; k += 3)
                {
                    int i0 = tris[k];
                    if (vmap[i0] < 0) continue;
                    int g = outIndex[finalGroup[groupOf[compOf[i0]]]];
                    if (perOut[g] == null) perOut[g] = new List<int>();
                    perOut[g].Add(vmap[i0]);
                    perOut[g].Add(vmap[tris[k + 1]]);
                    perOut[g].Add(vmap[tris[k + 2]]);
                }
                for (int g = 0; g < result.Count; g++)
                    if (perOut[g] != null) result[g].AddTriangles(m.mats[s], m.blocks[s], perOut[g], m.subTag[s]);
            }
            return result;
        }

        // ---------- bodies ----------

        private static PieceSpec MakePiece(MeshData m, Skeleton skel, Material fill, int side)
        {
            if (m.VertexCount < MinPieceVerts || m.TriangleCount < 16) return null;
            int bodyCount = skel.bodies.Count;
            var dom = new int[bodyCount];
            for (int i = 0; i < m.VertexCount; i++)
            {
                int b = m.w[i].boneIndex0;
                if (b >= 0 && b < bodyCount) dom[b]++;
            }

            int biggest = 0;
            for (int b = 1; b < bodyCount; b++) if (dom[b] > dom[biggest]) biggest = b;
            var present = new List<int>();
            for (int b = 0; b < bodyCount; b++) if (dom[b] >= MinBodyVerts || b == biggest) present.Add(b);

            var remap = new int[bodyCount];
            for (int b = 0; b < bodyCount; b++) remap[b] = -1;
            for (int k = 0; k < present.Count; k++) remap[present[k]] = k;

            for (int i = 0; i < m.VertexCount; i++)
            {
                if (Weights.Remap(m.w[i], remap, out BoneWeight bw))
                {
                    m.w[i] = bw;
                    continue;
                }
                // Weighted only to bodies that are not in this piece: follow the hierarchy up to one that is.
                int b = m.w[i].boneIndex0, guard = 0;
                while (b >= 0 && b < bodyCount && remap[b] < 0 && guard++ < 32) b = skel.bodies[b].parent;
                m.w[i] = Weights.Single(b >= 0 && b < bodyCount && remap[b] >= 0 ? remap[b] : remap[biggest]);
            }

            var p = new PieceSpec { data = m, bodies = present.ToArray(), side = side, bounds = m.GetBounds() };

            // Cap centre + the body under it, for the blood effect.
            Vector3 sum = Vector3.zero;
            int count = 0;
            var capVotes = new int[present.Count];
            for (int s = 0; s < m.subs.Count; s++)
            {
                if (!ReferenceEquals(m.mats[s], fill)) continue;
                foreach (int vi in m.subs[s])
                {
                    sum += m.v[vi];
                    count++;
                    capVotes[m.w[vi].boneIndex0]++;
                }
            }
            if (count > 0)
            {
                p.hasCap = true;
                p.capCenter = sum / count;
                for (int k = 1; k < capVotes.Length; k++) if (capVotes[k] > capVotes[p.capBody]) p.capBody = k;
            }
            return p;
        }

        private static int[] DominantCounts(PieceSpec p)
        {
            var c = new int[p.bodies.Length];
            foreach (BoneWeight bw in p.data.w) c[bw.boneIndex0]++;
            return c;
        }

        /// <summary>
        /// Collider for the part of a body that remains in this piece, fitted to its vertices in the body's own frame
        /// (so it hugs the limb instead of an arbitrary PCA box). Elongated -> capsule (rolls like a limb), else box.
        /// </summary>
        /// <param name="local">Body index the vertices' dominant bone must have (local index in a piece, skeleton index on the creature).</param>
        private static ShapeSpec FitShape(MeshData data, int local, SkelBody body)
        {
            Quaternion q = body.rot;
            var inv = new Quaternion(-q.x, -q.y, -q.z, q.w);   // inverse of a unit quaternion, without an engine call
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = -min;
            int count = 0;
            for (int i = 0; i < data.VertexCount; i++)
            {
                if (data.w[i].boneIndex0 != local) continue;
                Vector3 lp = inv * (data.v[i] - body.pos);
                min = Vector3.Min(min, lp);
                max = Vector3.Max(max, lp);
                count++;
            }
            if (count == 0) return new ShapeSpec { kind = ShapeKind.Sphere, radius = 0.03f };

            // Clothes vertices stick out a bit; shrink slightly so pieces rest on the ground instead of hovering.
            Vector3 size = Vector3.Max((max - min) * 0.9f, Vector3.one * 0.03f);
            Vector3 center = (min + max) * 0.5f;

            int longest = 0;
            for (int a = 1; a < 3; a++) if (size[a] > size[longest]) longest = a;
            int o1 = (longest + 1) % 3, o2 = (longest + 2) % 3;
            float second = Mathf.Max(size[o1], size[o2]);
            if (size[longest] > second * 1.6f)
            {
                float radius = 0.25f * (size[o1] + size[o2]);
                return new ShapeSpec
                {
                    kind = ShapeKind.Capsule,
                    center = center,
                    radius = radius,
                    height = Mathf.Max(size[longest], radius * 2f),
                    direction = longest
                };
            }
            return new ShapeSpec { kind = ShapeKind.Box, center = center, size = size };
        }
    }
}
