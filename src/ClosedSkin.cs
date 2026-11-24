using System;
using System.Collections.Generic;
using UnityEngine;

namespace Zandatsu
{
    // Additional triangles used only to intersect the native skin as a solid. Indices refer to
    // the original mesh so the closures follow the wearer's bones and active blend shapes.
    internal sealed class ClosedSkin
    {
        internal readonly List<int> triangles = new List<int>();
        internal int closedRims, unresolvedEdges;

        internal static ClosedSkin Build(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> indices)
        {
            var result = new ClosedSkin();
            var weld = new Dictionary<(int, int, int), int>();
            var byIndex = new Dictionary<int, int>();
            int Id(int i)
            {
                if (byIndex.TryGetValue(i, out int canonical)) return canonical;
                Vector3 p = vertices[i];
                const float precision = 100000f;
                var key = (Mathf.RoundToInt(p.x * precision), Mathf.RoundToInt(p.y * precision), Mathf.RoundToInt(p.z * precision));
                if (!weld.TryGetValue(key, out canonical)) weld.Add(key, canonical = i);
                byIndex.Add(i, canonical);
                return canonical;
            }

            var third = new Dictionary<(int, int), int>();
            void Edge(int a, int b, int c)
            {
                // Multiple incident faces in this direction make a fan ambiguous.
                var key = (a, b);
                third[key] = third.ContainsKey(key) ? -1 : c;
            }
            for (int i = 0; i + 2 < indices.Count; i += 3)
            {
                int a = Id(indices[i]), b = Id(indices[i+1]), c = Id(indices[i+2]);
                if (a == b || b == c || c == a) continue;
                Edge(a,b,c); Edge(b,c,a); Edge(c,a,b);
            }

            // Walk the adjacent triangles around the tip of each boundary edge. Two rims
            // can touch at a vertex (the native face does this) and still be distinct cycles.
            var visited = new HashSet<(int, int)>();
            var patchEdges = new HashSet<(int, int)>();
            foreach (var edge in third)
            {
                var first = edge.Key;
                if (edge.Value < 0 || third.ContainsKey((first.Item2, first.Item1)) || visited.Contains(first)) continue;
                var ring = new List<int>();
                var current = first;
                bool valid = true;
                while (visited.Add(current))
                {
                    var (a,b) = current;
                    ring.Add(a);
                    int c = third[current];
                    int steps = 0;
                    while (c >= 0 && third.TryGetValue((c,b), out int next))
                    {
                        c = next;
                        if (++steps >= third.Count) { c = -1; break; }
                    }
                    if (c < 0) { valid = false; break; }
                    current = (b,c);
                }
                ring.Reverse();
                int start = result.triangles.Count;
                bool Occupied(int a,int b) => third.ContainsKey((a,b)) || third.ContainsKey((b,a)) || patchEdges.Contains((Math.Min(a,b), Math.Max(a,b)));
                if (!valid || current != first || ring.Count < 3 || !Seal(vertices, ring, result.triangles, Occupied))
                {
                    result.unresolvedEdges += ring.Count;
                    continue;
                }
                for (int i = start; i < result.triangles.Count; i += 3)
                    for (int j = 0; j < 3; j++)
                    {
                        int a = result.triangles[i+j], b = result.triangles[i+(j+1)%3];
                        patchEdges.Add((Math.Min(a,b), Math.Max(a,b)));
                    }
                result.closedRims++;
            }
            return result;
        }

        // Project only to choose diagonals. Every triangle still uses the exact 3D rim
        // vertices, including collinear vertices needed to avoid T-junctions on later cuts.
        private static bool Seal(IReadOnlyList<Vector3> vertices, List<int> ring, List<int> triangles, Func<int,int,bool> occupied)
        {
            Vector3 origin = vertices[ring[0]], normal = Vector3.zero;
            for (int i = 0; i < ring.Count; i++)
                normal += Vector3.Cross(vertices[ring[i]] - origin, vertices[ring[(i + 1) % ring.Count]] - origin);
            if (normal.sqrMagnitude < 1e-18f) return false;
            normal /= Mathf.Sqrt(normal.sqrMagnitude);
            Vector3 u = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 v = Vector3.Cross(normal, u);
            var points = new Vector2[ring.Count];
            var left = new List<int>(ring.Count);
            for (int i = 0; i < ring.Count; i++)
            {
                Vector3 p = vertices[ring[i]] - origin;
                points[i] = new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v));
                left.Add(i);
            }
            double Cross(int a, int b, int c) =>
                (double)(points[b].x - points[a].x) * (points[c].y - points[a].y) -
                (double)(points[b].y - points[a].y) * (points[c].x - points[a].x);
            const double eps = 1e-14;
            int start = triangles.Count;
            while (left.Count >= 3)
            {
                bool clipped = false;
                for (int i = 0; i < left.Count; i++)
                {
                    int a = left[(i + left.Count - 1) % left.Count], b = left[i], c = left[(i + 1) % left.Count];
                    if (left.Count > 3 && occupied(ring[a],ring[c])) continue;
                    if (Cross(a, b, c) <= eps) continue;
                    bool blocked = false;
                    foreach (int k in left)
                        if (k != a && k != b && k != c && Cross(a,b,k) >= -eps && Cross(b,c,k) >= -eps && Cross(c,a,k) >= -eps)
                        { blocked = true; break; }
                    if (blocked) continue;
                    triangles.Add(ring[a]); triangles.Add(ring[b]); triangles.Add(ring[c]);
                    left.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (clipped) continue;
                triangles.RemoveRange(start, triangles.Count - start);
                return false;
            }
            return true;
        }
    }
}
