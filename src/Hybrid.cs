using System.Collections.Generic;
using ThunderRoad;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zandatsu
{
    /// <summary>What one source renderer of the creature keeps after a hybrid cut.</summary>
    public class RendererRecipe
    {
        public int renderer;
        public readonly List<VertexOrigin> verts = new List<VertexOrigin>();          // new vertex -> source vertex / cut edge
        public readonly Dictionary<int, List<int>> subs = new Dictionary<int, List<int>>(); // source submesh -> new triangles
        public bool removed;     // nothing of it survives: the renderer is switched off
        public bool unchanged;   // everything survives as it was: the mesh is left alone
        // The new mesh's vertex channels, interpolated on the worker (CreatureSnapshot.PrepareRebuild); null = absent.
        // pv null: not prepared, Rebuild interpolates them itself.
        public Vector3[] pv, pn;
        public Vector4[] pt;
        public Vector2[] pu0, pu1;
        public Color32[] pc;
        public Mesh preparedFrom;   // the mesh they were interpolated from (used only if the renderer still has it)
    }

    /// <summary>
    /// The chunk of a hybrid cut that stays the creature, worked out on the worker thread: per source renderer the
    /// vertices and triangles it keeps (in its own bind space, rebuilt on the main thread), plus the new caps.
    /// </summary>
    public class CreatureRecipe
    {
        public readonly List<RendererRecipe> renderers = new List<RendererRecipe>();
        public MeshData caps;           // world space at snapshot time, weights = skeleton body indices
        public int capKeptBody = -1;    // set by Hybrid.Apply: the body the cut face bleeds from (never a severed one)
        public int side;                // +1 = the creature kept the side the cut normal points to
        public int[] bodyVerts;         // per skeleton body: vertices the creature kept
        public Vector3 capCenter;
        public int capBody = -1;
        public readonly Dictionary<int, ShapeSpec> fitted = new Dictionary<int, ShapeSpec>();   // body -> collider for what is left of it

        public static CreatureRecipe Build(MeshData c, int side, (int verts, int tris)[] sourceCounts, int bodyCount, Material fill)
        {
            var rec = new CreatureRecipe { side = side, bodyVerts = new int[bodyCount] };
            foreach (BoneWeight bw in c.w)
                if (bw.boneIndex0 >= 0 && bw.boneIndex0 < bodyCount) rec.bodyVerts[bw.boneIndex0]++;

            var per = new RendererRecipe[sourceCounts.Length];
            for (int r = 0; r < per.Length; r++) per[r] = new RendererRecipe { renderer = r };
            var map = new int[c.VertexCount];
            for (int i = 0; i < map.Length; i++) map[i] = -1;
            var capMap = new int[c.VertexCount];
            for (int i = 0; i < capMap.Length; i++) capMap[i] = -1;
            rec.caps = new MeshData(256) { hasUv1 = c.hasUv1, hasCol = c.hasCol };
            var lerped = new bool[per.Length];

            for (int s = 0; s < c.subs.Count; s++)
            {
                List<int> tris = c.subs[s];
                int tag = c.subTag[s];
                if (tag < 0)
                {
                    // Caps, steel bands and fills made by the slicer.
                    var list = new List<int>(tris.Count);
                    foreach (int vi in tris)
                    {
                        if (capMap[vi] < 0) capMap[vi] = rec.caps.CopyVertex(c, vi);
                        list.Add(capMap[vi]);
                    }
                    rec.caps.AddTriangles(c.mats[s], null, list);
                    continue;
                }
                int rr = VertexOrigin.TagRenderer(tag), sub = VertexOrigin.TagSubmesh(tag);
                if (rr < 0 || rr >= per.Length) continue;
                RendererRecipe rp = per[rr];
                if (!rp.subs.TryGetValue(sub, out List<int> outTris)) rp.subs[sub] = outTris = new List<int>(tris.Count);
                for (int k = 0; k + 2 < tris.Count; k += 3)
                {
                    int i0 = tris[k], i1 = tris[k + 1], i2 = tris[k + 2];
                    if (c.o[i0].r != rr || c.o[i1].r != rr || c.o[i2].r != rr) continue;   // not traceable: dropped
                    outTris.Add(Local(rp, c, map, i0, lerped));
                    outTris.Add(Local(rp, c, map, i1, lerped));
                    outTris.Add(Local(rp, c, map, i2, lerped));
                }
            }

            for (int r = 0; r < per.Length; r++)
            {
                RendererRecipe rp = per[r];
                int tris = 0;
                foreach (List<int> t in rp.subs.Values) tris += t.Count / 3;
                if (tris == 0) rp.removed = sourceCounts[r].verts > 0;
                else rp.unchanged = !lerped[r] && rp.verts.Count == sourceCounts[r].verts && tris == sourceCounts[r].tris;
                rec.renderers.Add(rp);
            }

            if (rec.caps.TriangleCount > 0)
            {
                rec.caps.Sanitize();
                Vector3 sum = Vector3.zero;
                var votes = new int[bodyCount];
                for (int i = 0; i < rec.caps.VertexCount; i++)
                {
                    sum += rec.caps.v[i];
                    int b = rec.caps.w[i].boneIndex0;
                    if (b >= 0 && b < bodyCount) votes[b]++;
                }
                rec.capCenter = sum / rec.caps.VertexCount;
                for (int b = 0; b < bodyCount; b++) if (rec.capBody < 0 || votes[b] > votes[rec.capBody]) rec.capBody = b;
            }
            return rec;
        }

        private static int Local(RendererRecipe rp, MeshData c, int[] map, int vi, bool[] lerped)
        {
            if (map[vi] >= 0) return map[vi];
            VertexOrigin og = c.o[vi];
            if (og.b >= 0) lerped[rp.renderer] = true;
            map[vi] = rp.verts.Count;
            rp.verts.Add(og);
            return map[vi];
        }
    }

    /// <summary>
    /// Everything a hybrid cut changed on a live creature, undone when it despawns (creatures are pooled and their
    /// Manikin parts reused): original meshes, renderers switched off, cap renderers, severed ragdoll parts, forced LOD.
    /// </summary>
    public class HybridState
    {
        private static readonly Dictionary<Creature, HybridState> states = new Dictionary<Creature, HybridState>();

        public Creature creature;
        public readonly PieceFamily family = new PieceFamily();   // owns cap materials, light volume registration
        public Material fill, steel;
        private readonly Dictionary<SkinnedMeshRenderer, Mesh> originals = new Dictionary<SkinnedMeshRenderer, Mesh>();
        private readonly List<Renderer> disabled = new List<Renderer>();
        private readonly List<Object> owned = new List<Object>();
        private readonly List<SeveredPart> severed = new List<SeveredPart>();
        private bool lodForced;

        private class SeveredPart
        {
            public RagdollPart part;
            public List<KeyValuePair<Collider, bool>> colliders = new List<KeyValuePair<Collider, bool>>();
        }

        public static HybridState Get(Creature creature, Material fill)
        {
            if (states.TryGetValue(creature, out HybridState s)) return s;
            s = new HybridState { creature = creature };
            s.fill = s.family.OwnFill(fill);
            states[creature] = s;
            return s;
        }

        public static bool Has(Creature creature) => creature && states.ContainsKey(creature);

        public static bool IsSevered(Creature creature, RagdollPart part) =>
            creature && states.TryGetValue(creature, out HybridState s) && s.IsSevered(part);

        private bool dynamicLogged;

        /// <summary>
        /// Some ragdoll state changes make every part dynamic again (Ragdoll.ResetPartsToOrigin); a severed part has no
        /// colliders and fell through the floor. Called every frame.
        /// </summary>
        public static void KeepSeveredStill()
        {
            foreach (HybridState s in states.Values)
            {
                if (!s.creature) continue;
                foreach (SeveredPart sp in s.severed)
                {
                    if (!sp.part || sp.part.physicBody == null || sp.part.physicBody.isKinematic) continue;
                    sp.part.physicBody.isKinematic = true;
                    if (s.dynamicLogged) continue;
                    s.dynamicLogged = true;
                    Debug.Log($"[Zandatsu] {s.creature.name}: the game made severed part {sp.part.name} dynamic again " +
                              $"(ragdoll {s.creature.ragdoll.state}); kept kinematic");
                }
            }
        }

        public bool IsSevered(RagdollPart part) => severed.Exists(sp => sp.part == part);

        public bool IsGhost(RagdollPart part) => part && ghosts.Contains(part);

        /// <summary>The collider fitted to what is left of a cut part (null if the part was not refitted).</summary>
        public Collider RefitCollider(RagdollPart part) =>
            part && refits.TryGetValue(part, out GameObject go) && go ? go.GetComponent<Collider>() : null;

        private readonly List<KeyValuePair<Collider, bool>> refitOriginals = new List<KeyValuePair<Collider, bool>>();
        private readonly Dictionary<RagdollPart, GameObject> refits = new Dictionary<RagdollPart, GameObject>();
        private readonly List<HandleRagdoll> handlesOff = new List<HandleRagdoll>();

        /// <summary>
        /// A part split between the corpse and a piece: its colliders are swapped for one fitted to what the corpse kept
        /// (the original reached into the empty space where the cut-off part was, propping the corpse up on nothing and
        /// hooking pieces). shape is in metres in the part's frame at snapshot time.
        /// </summary>
        public void Refit(RagdollPart part, ShapeSpec shape)
        {
            if (!part || shape == null || !part.colliderGroup || part.colliderGroup.colliders == null) return;
            PhysicMaterial pm = null;
            int layer = part.gameObject.layer;
            foreach (Collider c in part.colliderGroup.colliders)
            {
                if (!c) continue;
                if (!pm) pm = c.sharedMaterial;
                layer = c.gameObject.layer;
                if (!refits.ContainsKey(part)) refitOriginals.Add(new KeyValuePair<Collider, bool>(c, c.enabled));
                c.enabled = false;
            }
            if (refits.TryGetValue(part, out GameObject old) && old) Object.Destroy(old);   // cut again: replace the last fit

            // Under the part's ColliderGroup like the colliders it replaces: the game looks the group up from the collider
            // (GetComponentInParent) on every contact, and a collider outside it threw in CollisionHandler.MeshRaycast.
            var go = new GameObject("Zandatsu_CutCollider") { layer = layer };
            Transform holder = part.colliderGroup.transform;
            go.transform.SetParent(holder, false);
            go.transform.SetPositionAndRotation(part.transform.position, part.transform.rotation);
            Vector3 ls = holder.lossyScale;
            go.transform.localScale = new Vector3(1f / Mathf.Max(ls.x, 1e-4f), 1f / Mathf.Max(ls.y, 1e-4f), 1f / Mathf.Max(ls.z, 1e-4f));
            Collider nc;
            if (shape.kind == ShapeKind.Capsule)
            {
                var cc = go.AddComponent<CapsuleCollider>();
                cc.center = shape.center; cc.radius = shape.radius; cc.height = shape.height; cc.direction = shape.direction;
                nc = cc;
            }
            else if (shape.kind == ShapeKind.Sphere)
            {
                var sc = go.AddComponent<SphereCollider>();
                sc.center = shape.center; sc.radius = shape.radius;
                nc = sc;
            }
            else
            {
                var bc = go.AddComponent<BoxCollider>();
                bc.center = shape.center; bc.size = shape.size;
                nc = bc;
            }
            if (pm) nc.sharedMaterial = CleanMaterial(pm);
            // The ragdoll's own parts never collide with each other; this new collider would, pushing the corpse apart from
            // the inside until it spun on the ground.
            foreach (RagdollPart other in creature.ragdoll.parts)
                if (other && other.colliderGroup && other.colliderGroup.colliders != null)
                    foreach (Collider c in other.colliderGroup.colliders)
                        if (c) Physics.IgnoreCollision(nc, c, true);
            foreach (KeyValuePair<RagdollPart, GameObject> kv in refits)
                if (kv.Value && kv.Value != go)
                {
                    Collider oc = kv.Value.GetComponent<Collider>();
                    if (oc) Physics.IgnoreCollision(nc, oc, true);
                }
            refits[part] = go;
        }

        private readonly Dictionary<string, PhysicMaterial> cleanMaterials = new Dictionary<string, PhysicMaterial>();

        /// <summary>
        /// The game names physics materials by "id (Instance)", from collider.material, which instantiates the shared one.
        /// The part's own colliders already hold such an instance; shared with a new collider it would come back as
        /// "id (Instance) (Instance)" and match nothing ("Target physics material not found").
        /// </summary>
        private PhysicMaterial CleanMaterial(PhysicMaterial pm)
        {
            const string suffix = " (Instance)";
            string name = pm.name;
            while (name.EndsWith(suffix)) name = name.Substring(0, name.Length - suffix.Length);
            if (name == pm.name) return pm;
            if (cleanMaterials.TryGetValue(name, out PhysicMaterial clean) && clean) return clean;
            clean = new PhysicMaterial(name)
            {
                dynamicFriction = pm.dynamicFriction,
                staticFriction = pm.staticFriction,
                bounciness = pm.bounciness,
                frictionCombine = pm.frictionCombine,
                bounceCombine = pm.bounceCombine
            };
            cleanMaterials[name] = clean;
            owned.Add(clean);
            return clean;
        }

        /// <summary>A part that leaves the corpse (severed, ghost) loses its fitted collider too, or it floats there unseen.</summary>
        private void DropRefit(RagdollPart part)
        {
            if (!refits.TryGetValue(part, out GameObject go)) return;
            if (go) Object.Destroy(go);
            refits.Remove(part);
        }

        /// <summary>The part's grab handles that now hang in the air beyond the cut (on the piece side) stop working.</summary>
        public void HandlesOffBeyond(RagdollPart part, Matrix4x4 bind, Vector3 point, Vector3 normal, int keptSide)
        {
            if (!part) return;
            Vector3 pL = bind.MultiplyPoint3x4(point);
            Vector3 nL = bind.MultiplyVector(normal);
            foreach (HandleRagdoll h in part.handles)
            {
                if (!h || handlesOff.Contains(h)) continue;
                Vector3 hL = part.transform.InverseTransformPoint(h.transform.position);
                if (Vector3.Dot(hL - pL, nL) * keptSide >= 0f) continue;
                h.SetTouch(false);
                handlesOff.Add(h);
            }
        }

        private readonly Dictionary<SkinnedMeshRenderer, Transform[]> originalBones = new Dictionary<SkinnedMeshRenderer, Transform[]>();
        private readonly HashSet<RagdollPart> ghosts = new HashSet<RagdollPart>();

        /// <param name="bones">New bone list (severed bones' weights moved to appended kept bones), or null to keep it.</param>
        public void SetMesh(SkinnedMeshRenderer smr, Mesh mesh, Transform[] bones = null)
        {
            if (!originals.ContainsKey(smr)) originals[smr] = smr.sharedMesh;
            if (!originalBones.ContainsKey(smr)) originalBones[smr] = smr.bones;
            owned.Add(mesh);
            if (bones != null) smr.bones = bones;
            smr.sharedMesh = mesh;
        }

        /// <summary>An invisible link of the corpse: its mesh went into a piece, kept parts still hang from it.</summary>
        public void Ghost(RagdollPart part)
        {
            if (!part || !ghosts.Add(part)) return;
            DropRefit(part);
            if (part.colliderGroup && part.colliderGroup.colliders != null)
                foreach (Collider c in part.colliderGroup.colliders)
                    if (c)
                    {
                        refitOriginals.Add(new KeyValuePair<Collider, bool>(c, c.enabled));
                        c.enabled = false;
                    }
            foreach (HandleRagdoll h in part.handles)
                if (h && !handlesOff.Contains(h))
                {
                    h.SetTouch(false);
                    handlesOff.Add(h);
                }
        }

        public void Disable(Renderer r)
        {
            if (!r || !r.enabled || r.forceRenderingOff) return;
            r.enabled = false;
            // In dungeons the area culling calls Creature.Hide(false) every few frames, which sets enabled = true on every
            // creature renderer: the hidden head came back, skinned to its severed bone in the air. The game never touches
            // forceRenderingOff.
            r.forceRenderingOff = true;
            disabled.Add(r);
        }

        public void Own(Object o) => owned.Add(o);

        public int HiddenLodCount { get; private set; }

        /// <summary>LOD1+ renderers are whole, uncut bodies: switched off for good (and LOD0 forced) after a hybrid cut.</summary>
        public void HideLowerLods()
        {
            if (lodForced || !creature.lodGroup) return;
            LOD[] lods = creature.lodGroup.GetLODs();
            var lod0 = new HashSet<Renderer>();
            if (lods.Length > 0) foreach (Renderer r in lods[0].renderers) if (r) lod0.Add(r);
            for (int l = 1; l < lods.Length; l++)
                foreach (Renderer r in lods[l].renderers)
                {
                    if (!r || lod0.Contains(r) || !r.enabled || r.forceRenderingOff) continue;
                    Disable(r);
                    HiddenLodCount++;
                }
            creature.lodGroup.ForceLOD(0);
            // Off altogether: Manikin rebuilds the LOD list now and then (SetLODs), the forced level went with it, and far
            // away the cut creature vanished with its LOD0 while the cut face stayed, floating. Its LOD1+ are hidden anyway.
            creature.lodGroup.enabled = false;
            lodForced = true;
        }

        /// <summary>
        /// A part whose mesh went entirely into pieces: like a vanilla sliced part (no joint, no collisions, no handles),
        /// kinematic so it doesn't fall forever. Ragdoll.OnDespawn recreates the joint of sliced parts.
        /// </summary>
        public void Sever(RagdollPart part)
        {
            if (!part || IsSevered(part) || part == creature.ragdoll.rootPart) return;
            DropRefit(part);
            var sp = new SeveredPart { part = part };
            if (part.colliderGroup && part.colliderGroup.colliders != null)
                foreach (Collider c in part.colliderGroup.colliders)
                    if (c)
                    {
                        sp.colliders.Add(new KeyValuePair<Collider, bool>(c, c.enabled));
                        c.enabled = false;
                    }
            foreach (HandleRagdoll h in part.handles)
                if (h) h.SetTouch(false);
            if (part is RagdollHand hand)
            {
                try { hand.TryRelease(); } catch { }
            }
            part.DestroyCharJoint();
            if (part.physicBody != null) part.physicBody.isKinematic = true;
            part.isSliced = true;
            creature.ragdoll.isSliced = true;
            severed.Add(sp);
        }

        public static void OnDespawn(Creature creature)
        {
            if (!creature || !states.TryGetValue(creature, out HybridState s)) return;
            states.Remove(creature);
            s.Restore();
        }

        public static void ClearAll()
        {
            foreach (HybridState s in new List<HybridState>(states.Values)) s.Restore();
            states.Clear();
        }

        private void Restore()
        {
            foreach (KeyValuePair<SkinnedMeshRenderer, Mesh> kv in originals)
            {
                if (!kv.Key) continue;
                if (originalBones.TryGetValue(kv.Key, out Transform[] b) && b != null) kv.Key.bones = b;
                if (kv.Value) kv.Key.sharedMesh = kv.Value;
            }
            originals.Clear();
            originalBones.Clear();
            ghosts.Clear();
            foreach (Renderer r in disabled)
                if (r)
                {
                    r.forceRenderingOff = false;
                    r.enabled = true;
                }
            disabled.Clear();
            foreach (SeveredPart sp in severed)
            {
                if (!sp.part) continue;
                foreach (KeyValuePair<Collider, bool> kv in sp.colliders) if (kv.Key) kv.Key.enabled = kv.Value;
                foreach (HandleRagdoll h in sp.part.handles) if (h) h.SetTouch(true);
            }
            severed.Clear();
            foreach (KeyValuePair<RagdollPart, GameObject> kv in refits) if (kv.Value) Object.Destroy(kv.Value);
            refits.Clear();
            for (int i = refitOriginals.Count - 1; i >= 0; i--)   // backwards: the first record holds the real original
                if (refitOriginals[i].Key) refitOriginals[i].Key.enabled = refitOriginals[i].Value;
            refitOriginals.Clear();
            cleanMaterials.Clear();
            foreach (HandleRagdoll h in handlesOff) if (h) h.SetTouch(true);
            handlesOff.Clear();
            if (lodForced && creature && creature.lodGroup)
            {
                creature.lodGroup.enabled = true;
                creature.lodGroup.ForceLOD(-1);
            }
            lodForced = false;
            foreach (Object o in owned)
            {
                if (!o) continue;
                if (o is GameObject go) Object.Destroy(go);
                else Object.Destroy(o);
            }
            owned.Clear();
            family.TryRelease();
        }
    }

    /// <summary>Main-thread side of a hybrid cut: rebuilds the creature's meshes, adds its caps, severs parts.</summary>
    public static class Hybrid
    {
        public const string RebuiltSuffix = " (Zandatsu)";   // name of a creature mesh a hybrid cut rebuilt
        private const int MinBodyVerts = 12;

        /// <returns>A one-line summary for the log.</returns>
        public static string Apply(Creature creature, CreatureRecipe rec, List<SkinnedMeshRenderer> sources, Skeleton skel,
                                   Dictionary<Transform, int> meshBoneToBody, int[] pieceBodyVerts, Material fill, Material steel,
                                   EffectData blood, Vector3 point, Vector3 normal)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            HybridState state = HybridState.Get(creature, fill);
            int bodyCount = skel.bodies.Count;

            // Severed parts stay where they were cut (kinematic): anything still skinned to them on the creature would
            // hang in the air or stretch towards them, so their weights move to the nearest kept ancestor.
            var severedNames = new List<string>();
            var ghostNames = new List<string>();
            var sev = new bool[bodyCount];
            var cand = new bool[bodyCount];
            var parts = new RagdollPart[bodyCount];
            var toSever = new List<RagdollPart>();
            var toGhost = new List<RagdollPart>();
            for (int b = 0; b < bodyCount; b++)
            {
                RagdollPart part = skel.bodies[b].live ? skel.bodies[b].live.GetComponent<RagdollPart>() : null;
                parts[b] = part;
                if (!part) continue;
                if (state.IsSevered(part)) { sev[b] = true; continue; }
                if (rec.bodyVerts[b] >= MinBodyVerts || pieceBodyVerts == null || pieceBodyVerts[b] == 0) continue;
                if (part == creature.ragdoll.rootPart) continue;
                cand[b] = true;
            }
            // A part that went into a piece but still carries kept parts of the corpse (the chest is gone, the arm hanging
            // from it stayed) must not freeze in the air, or the whole corpse dangles from that point and spins around it.
            // It stays an invisible link of the ragdoll: joint and physics as they were, no colliders, no handles.
            bool IsAncestor(int a, int d)
            {
                for (int k = skel.bodies[d].parent, guard = 0; k >= 0 && k < bodyCount && guard < 64; k = skel.bodies[k].parent, guard++)
                    if (k == a) return true;
                return false;
            }
            for (int b = 0; b < bodyCount; b++)
            {
                if (!cand[b]) continue;
                bool carries = false;
                for (int d = 0; d < bodyCount && !carries; d++)
                    if (!cand[d] && !sev[d] && rec.bodyVerts[d] > 0 && IsAncestor(b, d)) carries = true;
                if (carries)
                {
                    toGhost.Add(parts[b]);
                    ghostNames.Add(skel.bodies[b].name);
                }
                else
                {
                    sev[b] = true;
                    toSever.Add(parts[b]);
                    severedNames.Add(skel.bodies[b].name);
                }
            }
            var keptBody = new int[bodyCount];
            for (int b = 0; b < bodyCount; b++)
            {
                int k = b;
                for (int guard = 0; k >= 0 && k < bodyCount && sev[k] && guard < 64; guard++) k = skel.bodies[k].parent;
                keptBody[b] = k >= 0 && k < bodyCount && !sev[k] ? k : -1;
            }

            int rebuilt = 0, removed = 0, kept = 0, reskinned = 0;
            var hiddenNames = new List<string>();
            foreach (RendererRecipe rp in rec.renderers)
            {
                SkinnedMeshRenderer smr = rp.renderer < sources.Count ? sources[rp.renderer] : null;
                if (!smr) continue;
                if (rp.removed)
                {
                    state.Disable(smr);
                    hiddenNames.Add(smr.name);
                    removed++;
                    continue;
                }
                int[] boneRemap = SeveredBoneRemap(smr, smr.sharedMesh, meshBoneToBody, sev, keptBody, skel, out bool anySevered,
                                                   out Transform[] bones, out Matrix4x4[] bindposes);
                if (rp.unchanged && !anySevered) { kept++; continue; }
                Mesh mesh = Rebuild(smr.sharedMesh, rp, anySevered ? boneRemap : null, bindposes);
                if (!mesh) continue;
                state.SetMesh(smr, mesh, bones);
                if (rp.unchanged) reskinned++;
                else rebuilt++;
            }
            if (rebuilt + removed + reskinned > 0) state.HideLowerLods();   // LOD1+ meshes are uncut whole bodies

            int capVerts = 0;
            if (rec.caps != null && rec.caps.TriangleCount > 0)
            {
                capVerts = rec.caps.VertexCount;
                for (int i = 0; i < rec.caps.VertexCount; i++) rec.caps.w[i] = MoveOffSevered(rec.caps.w[i], keptBody, bodyCount);
                AddCaps(state, rec, skel, fill, steel);
            }

            foreach (RagdollPart part in toSever) state.Sever(part);
            foreach (RagdollPart part in toGhost) state.Ghost(part);

            int refitted = 0;
            foreach (KeyValuePair<int, ShapeSpec> kv in rec.fitted)
            {
                if (kv.Key < 0 || kv.Key >= bodyCount || sev[kv.Key] || !skel.bodies[kv.Key].live) continue;
                RagdollPart part = skel.bodies[kv.Key].live.GetComponent<RagdollPart>();
                if (!part || state.IsGhost(part)) continue;
                state.Refit(part, kv.Value);
                state.HandlesOffBeyond(part, skel.bodies[kv.Key].bind, point, normal, rec.side);
                refitted++;
            }
            DropHolsteredBeyond(state, skel, meshBoneToBody, pieceBodyVerts, point, normal, rec.side);

            // A cut face weighted mostly to a severed hand bled from where that hand hung, frozen in the air.
            rec.capKeptBody = rec.capBody >= 0 && rec.capBody < bodyCount ? keptBody[rec.capBody] : -1;
            if (blood != null && rec.capKeptBody >= 0 && skel.bodies[rec.capKeptBody].live)
            {
                try
                {
                    Transform parent = skel.bodies[rec.capKeptBody].live;
                    EffectInstance ei = blood.Spawn(rec.capCenter, Quaternion.LookRotation(-normal * rec.side), parent, null, true, null, false, 1f, 1f);
                    ei.SetIntensity(1f);
                    ei.Play();
                }
                catch (System.Exception e) { Debug.LogWarning("[Zandatsu] creature blood: " + e.Message); }
            }
            return $"creature kept in {clock.Elapsed.TotalMilliseconds:0.0} ms: {rebuilt} meshes rebuilt, {reskinned} re-skinned off severed bones, {removed} hidden " +
                   $"[{string.Join(",", hiddenNames)}], {kept} untouched, {state.HiddenLodCount} LOD renderers hidden, {refitted} cut parts refitted, caps {capVerts}v, " +
                   $"severed [{string.Join(",", severedNames)}], ghost links [{string.Join(",", ghostNames)}]";
        }

        /// <summary>
        /// A sword or mace holstered where the corpse no longer is stayed there: on a severed part it hung in the air, and
        /// on the back of a chest cut through it stuck out of the cut face where the shoulders had been. Holders on a
        /// severed or ghost part, or on a part this cut went through and beyond the cut, drop their items the way the game
        /// does at death (BrainModuleDeath.DropAllItems).
        /// </summary>
        private static void DropHolsteredBeyond(HybridState state, Skeleton skel, Dictionary<Transform, int> meshBoneToBody,
            int[] pieceBodyVerts, Vector3 point, Vector3 normal, int keptSide)
        {
            Creature creature = state.creature;
            if (creature.holders == null) return;
            int bodyCount = skel.bodies.Count;
            foreach (Holder h in creature.holders)
            {
                if (!h || h.items == null || h.items.Count == 0) continue;
                int body = -1;
                for (Transform t = h.transform; t && body < 0; t = t.parent)
                    if (meshBoneToBody != null && meshBoneToBody.TryGetValue(t, out int b)) body = b;
                if (body < 0)
                {
                    // Not under any bone of the skeleton: the nearest part carries it.
                    float best = float.MaxValue;
                    for (int b = 0; b < bodyCount; b++)
                    {
                        if (!skel.bodies[b].live) continue;
                        float d = (skel.bodies[b].live.position - h.transform.position).sqrMagnitude;
                        if (d < best) { best = d; body = b; }
                    }
                }
                if (body < 0 || body >= bodyCount || !skel.bodies[body].live) continue;
                RagdollPart part = skel.bodies[body].live.GetComponent<RagdollPart>();
                if (!part) continue;
                string why;
                if (state.IsSevered(part)) why = "severed";
                else if (state.IsGhost(part)) why = "cut off";
                else if (pieceBodyVerts != null && body < pieceBodyVerts.Length && pieceBodyVerts[body] > 0)
                {
                    Matrix4x4 bind = skel.bodies[body].bind;
                    Vector3 pL = bind.MultiplyPoint3x4(point);
                    Vector3 nL = bind.MultiplyVector(normal);
                    Vector3 hL = part.transform.InverseTransformPoint(h.transform.position);
                    if (Vector3.Dot(hL - pL, nL) * keptSide >= 0f) continue;
                    why = "beyond the cut";
                }
                else continue;
                int n = h.items.Count;
                try { h.UnSnapAll(); }
                catch (System.Exception e) { Debug.LogWarning("[Zandatsu] holster drop: " + e.Message); continue; }
                Debug.Log($"[Zandatsu] {creature.name}: {n} holstered item(s) dropped from {h.name} on {part.name} ({why})");
            }
        }

        /// <summary>
        /// Per bone of the renderer: itself, or (if its body is severed) an extra bone appended to the renderer: the kept
        /// ancestor part, with a bind pose that keeps the vertex exactly where the severed bone holds it right now. The
        /// renderer's own bone list often lacks a kept ancestor (armour), which left vertices stretched towards parts
        /// hanging in the air. anySevered: some bone of this renderer belongs to a severed body.
        /// </summary>
        private static int[] SeveredBoneRemap(SkinnedMeshRenderer smr, Mesh src, Dictionary<Transform, int> meshBoneToBody, bool[] sev,
            int[] keptBody, Skeleton skel, out bool anySevered, out Transform[] newBones, out Matrix4x4[] newBindposes)
        {
            anySevered = false;
            newBones = null;
            newBindposes = null;
            Transform[] bones = smr.bones;
            if (bones == null || bones.Length == 0 || meshBoneToBody == null || !src) return null;
            Matrix4x4[] bind = src.bindposes;
            if (bind.Length != bones.Length) return null;
            int BodyOf(Transform t)
            {
                for (int depth = 0; t && depth < 32; depth++, t = t.parent)
                    if (meshBoneToBody.TryGetValue(t, out int body)) return body;
                return -1;
            }
            var extraBones = new List<Transform>();
            var extraBind = new List<Matrix4x4>();
            var remap = new int[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                remap[i] = i;
                int body = BodyOf(bones[i]);
                if (body < 0 || body >= sev.Length || !sev[body]) continue;
                anySevered = true;
                int k = keptBody[body];
                Transform kept = k >= 0 && k < skel.bodies.Count ? skel.bodies[k].live : null;
                if (!kept || !bones[i]) continue;
                extraBones.Add(kept);
                extraBind.Add(kept.worldToLocalMatrix * bones[i].localToWorldMatrix * bind[i]);
                remap[i] = bones.Length + extraBones.Count - 1;
            }
            if (extraBones.Count > 0)
            {
                newBones = new Transform[bones.Length + extraBones.Count];
                bones.CopyTo(newBones, 0);
                extraBones.CopyTo(newBones, bones.Length);
                newBindposes = new Matrix4x4[bind.Length + extraBind.Count];
                bind.CopyTo(newBindposes, 0);
                extraBind.CopyTo(newBindposes, bind.Length);
            }
            return remap;
        }

        private static BoneWeight MoveOffSevered(BoneWeight w, int[] keptBody, int bodyCount)
        {
            int Map(int b) => b >= 0 && b < bodyCount && keptBody[b] >= 0 ? keptBody[b] : b;
            return Weights.Normalize(new BoneWeight
            {
                boneIndex0 = Map(w.boneIndex0), weight0 = w.weight0,
                boneIndex1 = Map(w.boneIndex1), weight1 = w.weight1,
                boneIndex2 = Map(w.boneIndex2), weight2 = w.weight2,
                boneIndex3 = Map(w.boneIndex3), weight3 = w.weight3
            });
        }

        /// <summary>The surviving part of one renderer, in its own bind space: kept vertices plus cut-edge vertices
        /// blended from the source with the same t as the world-space cut (skin weights and blend shapes too).</summary>
        private static Mesh Rebuild(Mesh src, RendererRecipe rp, int[] boneRemap, Matrix4x4[] bindposes)
        {
            if (!src) return null;
            int srcCount = src.vertexCount;
            int n = rp.verts.Count;
            foreach (VertexOrigin og in rp.verts)
                if (og.a < 0 || og.a >= srcCount || og.b >= srcCount) return null;   // the mesh changed since the snapshot

            var mesh = new Mesh { name = src.name.EndsWith(RebuiltSuffix) ? src.name : src.name + RebuiltSuffix };
            if (n > 65000) mesh.indexFormat = IndexFormat.UInt32;
            if (rp.pv != null && rp.pv.Length == n && ReferenceEquals(rp.preparedFrom, src))
            {
                // Interpolated on the worker already.
                mesh.SetVertices(rp.pv);
                if (rp.pn != null) mesh.SetNormals(rp.pn);
                if (rp.pt != null) mesh.SetTangents(rp.pt);
                if (rp.pu0 != null) mesh.SetUVs(0, rp.pu0);
                if (rp.pu1 != null) mesh.SetUVs(1, rp.pu1);
                if (rp.pc != null) mesh.SetColors(rp.pc);
            }
            else SetChannels(src, mesh, rp);
            CopySkin(src, mesh, rp, boneRemap, bindposes);
            mesh.subMeshCount = src.subMeshCount;
            for (int s = 0; s < src.subMeshCount; s++)
                mesh.SetTriangles(rp.subs.TryGetValue(s, out List<int> tris) ? tris : new List<int>(), s, false);
            CopyBlendShapes(src, mesh, rp);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>The new mesh's vertex channels interpolated here (when the worker did not prepare them).</summary>
        private static void SetChannels(Mesh src, Mesh mesh, RendererRecipe rp)
        {
            int srcCount = src.vertexCount;
            int n = rp.verts.Count;
            var sv = new List<Vector3>(); src.GetVertices(sv);
            var sn = new List<Vector3>(); src.GetNormals(sn);
            var st = new List<Vector4>(); src.GetTangents(st);
            var su0 = new List<Vector2>(); src.GetUVs(0, su0);
            var su1 = new List<Vector2>(); src.GetUVs(1, su1);
            var sc = new List<Color32>(); src.GetColors(sc);

            var v = new List<Vector3>(n);
            var nn = sn.Count == srcCount ? new List<Vector3>(n) : null;
            var tt = st.Count == srcCount ? new List<Vector4>(n) : null;
            var u0 = su0.Count == srcCount ? new List<Vector2>(n) : null;
            var u1 = su1.Count == srcCount ? new List<Vector2>(n) : null;
            var cc = sc.Count == srcCount ? new List<Color32>(n) : null;
            foreach (VertexOrigin og in rp.verts)
            {
                int a = og.a, b = og.b >= 0 ? og.b : og.a;
                float t = og.b >= 0 ? og.t : 0f;
                v.Add(Vector3.LerpUnclamped(sv[a], sv[b], t));
                if (nn != null)
                {
                    Vector3 x = Vector3.LerpUnclamped(sn[a], sn[b], t);
                    nn.Add(x.sqrMagnitude > 1e-10f ? x.normalized : (sn[a].sqrMagnitude > 1e-10f ? sn[a] : Vector3.up));
                }
                if (tt != null)
                {
                    Vector4 x = Vector4.LerpUnclamped(st[a], st[b], t);
                    var d = new Vector3(x.x, x.y, x.z);
                    if (d.sqrMagnitude < 1e-10f) d = new Vector3(st[a].x, st[a].y, st[a].z);
                    d = d.sqrMagnitude > 1e-10f ? d.normalized : Vector3.right;
                    tt.Add(new Vector4(d.x, d.y, d.z, st[a].w < 0f ? -1f : 1f));
                }
                u0?.Add(Vector2.LerpUnclamped(su0[a], su0[b], t));
                u1?.Add(t < 0.5f ? su1[a] : su1[b]);   // occlusion region ids: never blended
                cc?.Add(Color32.Lerp(sc[a], sc[b], t));
            }
            mesh.SetVertices(v);
            if (nn != null) mesh.SetNormals(nn);
            if (tt != null) mesh.SetTangents(tt);
            if (u0 != null) mesh.SetUVs(0, u0);
            if (u1 != null) mesh.SetUVs(1, u1);
            if (cc != null) mesh.SetColors(cc);
        }

        private static void CopySkin(Mesh src, Mesh dst, RendererRecipe rp, int[] boneRemap, Matrix4x4[] bindposes)
        {
            dst.bindposes = bindposes ?? src.bindposes;
            NativeArray<byte> perVertex = src.GetBonesPerVertex();
            if (perVertex.Length != src.vertexCount) return;   // not skinned
            NativeArray<BoneWeight1> all = src.GetAllBoneWeights();
            var start = new int[perVertex.Length];
            for (int i = 1; i < start.Length; i++) start[i] = start[i - 1] + perVertex[i - 1];

            var counts = new NativeArray<byte>(rp.verts.Count, Allocator.Temp);
            var weights = new List<BoneWeight1>(rp.verts.Count * 4);
            var mix = new Dictionary<int, float>();
            var sorted = new List<KeyValuePair<int, float>>();
            for (int k = 0; k < rp.verts.Count; k++)
            {
                VertexOrigin og = rp.verts[k];
                if (og.b < 0)
                {
                    // A kept vertex whose bones all stay is copied as it is (almost all of them: mixing every vertex
                    // through a dictionary and a sort made the main-thread step of a cut take up to 20 ms).
                    int s0 = start[og.a], n0 = perVertex[og.a];
                    bool plain = n0 > 0;
                    if (plain && boneRemap != null)
                        for (int i = 0; i < n0 && plain; i++)
                        {
                            int bi = all[s0 + i].boneIndex;
                            if (bi >= 0 && bi < boneRemap.Length && boneRemap[bi] != bi) plain = false;
                        }
                    if (plain)
                    {
                        for (int i = 0; i < n0; i++) weights.Add(all[s0 + i]);
                        counts[k] = (byte)n0;
                        continue;
                    }
                }
                mix.Clear();
                Accumulate(all, start[og.a], perVertex[og.a], og.b >= 0 ? 1f - og.t : 1f, mix, boneRemap);
                if (og.b >= 0) Accumulate(all, start[og.b], perVertex[og.b], og.t, mix, boneRemap);
                sorted.Clear();
                sorted.AddRange(mix);
                sorted.Sort((x, y) => y.Value.CompareTo(x.Value));   // Unity wants weights in descending order
                int keep = Mathf.Min(sorted.Count, 255);
                float sum = 0f;
                for (int i = 0; i < keep; i++) sum += sorted[i].Value;
                if (keep == 0 || sum <= 0f)
                {
                    weights.Add(new BoneWeight1 { boneIndex = 0, weight = 1f });
                    counts[k] = 1;
                    continue;
                }
                for (int i = 0; i < keep; i++) weights.Add(new BoneWeight1 { boneIndex = sorted[i].Key, weight = sorted[i].Value / sum });
                counts[k] = (byte)keep;
            }
            var w = new NativeArray<BoneWeight1>(weights.ToArray(), Allocator.Temp);
            dst.SetBoneWeights(counts, w);
            counts.Dispose();
            w.Dispose();
        }

        private static void Accumulate(NativeArray<BoneWeight1> all, int start, int count, float scale, Dictionary<int, float> mix, int[] boneRemap)
        {
            if (scale <= 0f) return;
            for (int i = 0; i < count; i++)
            {
                BoneWeight1 bw = all[start + i];
                if (bw.weight <= 0f) continue;
                int bone = boneRemap != null && bw.boneIndex >= 0 && bw.boneIndex < boneRemap.Length ? boneRemap[bw.boneIndex] : bw.boneIndex;
                mix.TryGetValue(bone, out float cur);
                mix[bone] = cur + bw.weight * scale;
            }
        }

        private static void CopyBlendShapes(Mesh src, Mesh dst, RendererRecipe rp)
        {
            int count = src.vertexCount, n = rp.verts.Count;
            var dv = new Vector3[count];
            var dn = new Vector3[count];
            var dt = new Vector3[count];
            var ov = new Vector3[n];
            var on = new Vector3[n];
            var ot = new Vector3[n];
            for (int shape = 0; shape < src.blendShapeCount; shape++)
            {
                string name = src.GetBlendShapeName(shape);
                int frames = src.GetBlendShapeFrameCount(shape);
                for (int f = 0; f < frames; f++)
                {
                    src.GetBlendShapeFrameVertices(shape, f, dv, dn, dt);
                    for (int k = 0; k < n; k++)
                    {
                        VertexOrigin og = rp.verts[k];
                        int a = og.a, b = og.b >= 0 ? og.b : og.a;
                        float t = og.b >= 0 ? og.t : 0f;
                        ov[k] = Vector3.LerpUnclamped(dv[a], dv[b], t);
                        on[k] = Vector3.LerpUnclamped(dn[a], dn[b], t);
                        ot[k] = Vector3.LerpUnclamped(dt[a], dt[b], t);
                    }
                    dst.AddBlendShapeFrame(name, src.GetBlendShapeFrameWeight(shape, f), ov, on, ot);
                }
            }
        }

        /// <summary>The creature's own cut faces: a skinned mesh on the creature driven by its ragdoll part transforms.</summary>
        private static void AddCaps(HybridState state, CreatureRecipe rec, Skeleton skel, Material fill, Material steel)
        {
            MeshData caps = rec.caps;
            int bodyCount = skel.bodies.Count;
            var remap = new int[bodyCount];
            for (int b = 0; b < bodyCount; b++) remap[b] = -1;
            var used = new List<int>();
            foreach (BoneWeight bw in caps.w)
                foreach (int b in new[] { bw.boneIndex0, bw.boneIndex1, bw.boneIndex2, bw.boneIndex3 })
                    if (b >= 0 && b < bodyCount && remap[b] < 0 && skel.bodies[b].live) { remap[b] = used.Count; used.Add(b); }
            if (used.Count == 0) return;
            for (int i = 0; i < caps.VertexCount; i++)
            {
                if (Weights.Remap(caps.w[i], remap, out BoneWeight bw)) caps.w[i] = bw;
                else caps.w[i] = Weights.Single(0);
            }

            var bones = new Transform[used.Count];
            var bindposes = new Matrix4x4[used.Count];
            for (int k = 0; k < used.Count; k++)
            {
                bones[k] = skel.bodies[used[k]].live;
                bindposes[k] = skel.bodies[used[k]].bind;   // its world-to-local at snapshot time: caps are in snapshot world space
            }

            // Each cut its own (it cools on its own, and a later fire cut glows even after a cold one), only when this cut
            // went through metal: a new one for every cut of an armoured corpse piled up unused.
            if (steel && caps.mats.Exists(x => ReferenceEquals(x, steel)))
            {
                state.steel = HotSteel.Create(state.fill, HotSteel.HeatOf(steel));   // as hot as the cut that made it is now
                if (state.steel) state.family.owned.Add(state.steel);
            }
            for (int s = 0; s < caps.mats.Count; s++)
            {
                if (ReferenceEquals(caps.mats[s], fill)) caps.mats[s] = state.fill;
                else if (steel && ReferenceEquals(caps.mats[s], steel)) caps.mats[s] = state.steel ? state.steel : state.fill;
                // Charred fill and burnt rim of a fire cut belong to the pieces' family, which is released with its last
                // piece: the corpse takes its own copies.
                else if (FireCut.IsFire(caps.mats[s])) caps.mats[s] = FireCut.Adopt(caps.mats[s], state.family);
            }

            Mesh mesh = caps.ToSkinnedMesh("Zandatsu_CreatureCapMesh", bindposes);
            var go = new GameObject("Zandatsu_CreatureCap");
            go.transform.SetParent(state.creature.transform, false);
            Renderer any = null;
            foreach (Creature.RendererData rd in state.creature.renderers)
                if (rd != null && rd.renderer) { any = rd.renderer; break; }
            go.layer = any ? any.gameObject.layer : state.creature.gameObject.layer;
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = bones;
            smr.rootBone = bones[0];
            smr.updateWhenOffscreen = true;   // small mesh; its bones move all over the body
            smr.sharedMaterials = caps.mats.ToArray();
            state.Own(go);
            state.Own(mesh);
            PieceLighting.Apply(smr, state.family, null, false);   // lit at its bounds' middle: a bone under the floor made black
        }

        /// <summary>Cap renderers mod-made on a creature by earlier hybrid cuts (baked like the creature's own meshes).</summary>
        public static bool IsCreatureCap(SkinnedMeshRenderer smr) => smr && smr.name == "Zandatsu_CreatureCap";
    }
}
