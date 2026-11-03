using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Zandatsu
{
    /// <summary>
    /// Mono compiles every method the first time it runs: the first cut of a session spent ~70 ms more than the next
    /// ones, most of it compiling the slicer, and the main-thread half of the cut paid the same once. At load, on a
    /// background thread, a small cut is run through the worker code (with the generic collections it uses) and every
    /// other method of the mod is compiled without running it.
    /// </summary>
    public static class Warmup
    {
        private static bool started;

        public static void Run()
        {
            if (started) return;
            started = true;
            // A managed shell, never drawn: the worker code only stores and compares material references.
            var mat = (Material)FormatterServices.GetUninitializedObject(typeof(Material));
            Task.Run(() =>
            {
                var clock = Stopwatch.StartNew();
                string cut;
                try { cut = TestCut(mat); }
                catch (Exception e) { cut = "test cut failed: " + e.Message; }
                double tCut = clock.Elapsed.TotalMilliseconds;
                int compiled = CompileAll();
                Debug.Log($"[Zandatsu] warm-up: {cut} in {tCut:0} ms, {compiled} methods compiled in {clock.Elapsed.TotalMilliseconds - tCut:0} ms");
            });
        }

        private static string TestCut(Material mat)
        {
            // A torso with two arms, cut across all three; both the snapshot and the hybrid recipe paths run.
            var skel = new Skeleton();
            skel.bodies.Add(new SkelBody { name = "Torso", pos = new Vector3(0, 0.3f, 0), rot = Quaternion.identity, parent = -1, mass = 10 });
            skel.bodies.Add(new SkelBody { name = "ArmR", pos = new Vector3(0.25f, 0.6f, 0), rot = Quaternion.identity, parent = 0, mass = 3 });
            skel.bodies.Add(new SkelBody { name = "ArmL", pos = new Vector3(-0.25f, 0.6f, 0), rot = Quaternion.identity, parent = 0, mass = 3 });
            var snap = new CreatureSnapshot
            {
                centres = new List<KeyValuePair<int, Vector3>> { new KeyValuePair<int, Vector3>(0, skel.bodies[0].pos) },
                bodyNames = new[] { "Torso", "ArmR", "ArmL" },
                track = true
            };
            snap.renderers.Add(Tube(0, 0, 0f, 0f, 0.7f, 0.15f, mat));
            snap.renderers.Add(Tube(1, 1, 0.25f, 0f, 0.6f, 0.05f, mat));
            snap.renderers.Add(Tube(2, 2, -0.25f, 0f, 0.6f, 0.05f, mat));
            var counts = new (int verts, int tris)[snap.renderers.Count];
            for (int i = 0; i < counts.Length; i++)
            {
                snap.vertexCount += snap.renderers[i].mesh.vertexCount;
                counts[i] = (snap.renderers[i].mesh.vertexCount, snap.renderers[i].mesh.triCount);
            }
            MeshData world = snap.Build();
            CutResult r = CutWork.SliceAndPrepare(world, skel, new Vector3(0, 0.35f, 0), new Vector3(0.25f, 1f, 0.1f).normalized, mat, 0,
                                                  new HashSet<Material>(), null, 0, counts);
            int verts = world.VertexCount;
            MeshData.Return(world);
            return r == null ? "test cut missed" : $"test cut {verts}v -> {r.pieces.Count} pieces";
        }

        private static RendererSnap Tube(int source, int body, float cx, float y0, float y1, float radius, Material mat)
        {
            const int rings = 16, segs = 24;
            int n = (rings + 1) * segs;
            var m = new MeshCache
            {
                vertexCount = n, v = new Vector3[n], n = new Vector3[n], t = new Vector4[n], uv = new Vector2[n],
                uv1 = new Vector2[0], col = new Color32[0], w = new BoneWeight[n], bind = new[] { Matrix4x4.identity }, tris = new int[1][]
            };
            for (int i = 0; i <= rings; i++)
                for (int j = 0; j < segs; j++)
                {
                    int k = i * segs + j;
                    float a = j * 2f * Mathf.PI / segs;
                    var nrm = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                    m.v[k] = new Vector3(cx, Mathf.Lerp(y0, y1, i / (float)rings), 0f) + nrm * radius;
                    m.n[k] = nrm;
                    m.t[k] = new Vector4(-nrm.z, 0f, nrm.x, 1f);
                    m.uv[k] = new Vector2(j / (float)segs, i / (float)rings);
                    m.w[k] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
                }
            var tris = new int[rings * segs * 6];
            int q = 0;
            for (int i = 0; i < rings; i++)
                for (int j = 0; j < segs; j++)
                {
                    int a = i * segs + j, b = i * segs + (j + 1) % segs, c = a + segs, d = b + segs;
                    tris[q++] = a; tris[q++] = c; tris[q++] = b;
                    tris[q++] = b; tris[q++] = c; tris[q++] = d;
                }
            m.tris[0] = tris;
            m.triCount = tris.Length / 3;
            return new RendererSnap
            {
                mesh = m, rigid = Matrix4x4.identity, skin = new[] { Matrix4x4.identity }, boneBody = new[] { body },
                mats = new[] { mat }, blocks = new MaterialPropertyBlock[1], source = source
            };
        }

        /// <summary>Compiles every method of the mod without running it (GetFunctionPointer makes Mono compile it).</summary>
        private static int CompileAll()
        {
            Type[] types;
            try { types = typeof(Warmup).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            const BindingFlags all = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static;
            int compiled = 0;
            foreach (Type t in types)
            {
                if (t == null || t.ContainsGenericParameters) continue;
                var methods = new List<MethodBase>();
                try
                {
                    methods.AddRange(t.GetMethods(all));
                    methods.AddRange(t.GetConstructors(all));
                }
                catch { continue; }
                foreach (MethodBase m in methods)
                {
                    if (m.IsAbstract || m.ContainsGenericParameters || m.IsStatic && m.IsConstructor) continue;
                    try
                    {
                        m.MethodHandle.GetFunctionPointer();
                        compiled++;
                    }
                    catch { }
                }
            }
            return compiled;
        }
    }
}
