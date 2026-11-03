using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using ThunderRoad;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Zandatsu
{
    /// <summary>
    /// Cut Weapons: an enemy's (or a dropped) weapon or shield is cut along the blade's plane, like Blade Mode cutting a
    /// sword in Metal Gear Rising. The half with the grip stays the same Item, shorter: its model is cut (red-hot cut face),
    /// colliders beyond the cut are switched off or shortened, cutting and piercing zones shortened or switched off, grips
    /// beyond the cut let go. The other half falls as loose debris. Items are pooled: everything is undone on despawn.
    /// </summary>
    public static class ItemCut
    {
        private const float SameSwingSeconds = 0.3f;
        private const int MaxVerts = 60000;

        private class Job
        {
            public Item item;
            public CutState state;
            public List<Renderer> targets;
            public List<Mesh> meshes;
            public List<Matrix4x4> toItem;
            public List<int> subCounts;
            public Vector3 pointW, normalW;
            public int keptSign;          // +1: the item keeps the side the normal points to
            public Vector3 bladeVelocity;
            public Material steelKeep, steelDebris;
            public Material insideMark;    // stand-in for the dark inside of a vessel modelled closed
            public ItemDebris.Faces faces; // what a later cut of the debris shows
            public Item cutter;
            public float prepMs;
            public int verts;
            public Task<bool> task;
            public MeshData pos, neg;
            // Imbue shapes (the hidden meshes spell effects take their shape from): cut without cut faces.
            public List<Renderer> imbueTargets;
            public List<Mesh> imbueMeshes;
            public List<Matrix4x4> imbueToItem;
            public List<int> imbueSubCounts;
            public MeshData imbuePos, imbueNeg;
            public bool imbueCut;
            public string detail;
            public string miss;
            public int lodLevel;
            public double workerMs;
            public bool prop;              // Cut Props: cut faces from the object's own surface, not hot steel
            public string capInfo;
        }

        private class ColliderRecord
        {
            public Collider c;
            public bool enabled;
            public Vector3 center, size;
            public float height;
        }

        private class DamagerRecord
        {
            public Damager d;
            public CollisionHandler ch;
            public Vector3 localPosition;
            public float length, depth;
            public bool removed;
        }

        /// <summary>Everything cutting changed on one item (kept from its first cut until it despawns).</summary>
        private class CutState
        {
            public Item item;
            public bool busy;
            public int cuts;
            public readonly Dictionary<MeshFilter, Mesh> originalMeshes = new Dictionary<MeshFilter, Mesh>();
            public readonly List<Renderer> hidden = new List<Renderer>();
            public LODGroup lodGroup;
            public int lodLevel;           // the level of detail cut (0, or a lower one when LOD0 cannot be read)
            public bool lodWasEnabled;
            public readonly Dictionary<Collider, ColliderRecord> colliders = new Dictionary<Collider, ColliderRecord>();
            public readonly List<MeshCollider> hulls = new List<MeshCollider>();   // ours, standing in for its mesh colliders (HullCollider)
            // Baked stand-ins for its skinned parts (a bow's limbs and string), cut in their place; what worked them, off once cut.
            public readonly List<KeyValuePair<SkinnedMeshRenderer, MeshRenderer>> standIns = new List<KeyValuePair<SkinnedMeshRenderer, MeshRenderer>>();
            public readonly List<Action> undo = new List<Action>();   // StopWorkings, undone on despawn
            public readonly Dictionary<Damager, DamagerRecord> damagers = new Dictionary<Damager, DamagerRecord>();
            public readonly List<Handle> handlesOff = new List<Handle>();
            public float mass = -1f;
            public readonly Dictionary<CollisionHandler, float> ownMass = new Dictionary<CollisionHandler, float>();   // see ScaleOwnMass
            public bool useCustomCom, customInertia;
            public Vector3 customCom;
            public readonly List<MeshRenderer> caps = new List<MeshRenderer>();
            public Renderer capFollow;
            public readonly List<Object> owned = new List<Object>();
            public Item.SpawnEvent onDespawn;
            public Breakable breakable;    // a cut breakable object never shatters: its pre-made pieces were of the whole
            public bool breakableWas;
            public readonly List<KeyValuePair<Renderer, ShadowCastingMode>> shadows = new List<KeyValuePair<Renderer, ShadowCastingMode>>();
        }

        private static readonly Dictionary<Item, CutState> states = new Dictionary<Item, CutState>();
        private static readonly Dictionary<Item, float> lastCut = new Dictionary<Item, float>();
        private static readonly List<Job> jobs = new List<Job>();
        // Room for a pile of cut-off halves (a dozen colliders each) around what the blade hits: with 64 they filled it and
        // the object itself was not among the hits.
        private static readonly Collider[] overlap = new Collider[512];
        private static int itemMask;

        // ---------------- finding a weapon under the blade ----------------

        private const float SweepStep = 0.02f;   // m between blade positions tested along one frame's swing
        private const float EdgeLead = 0.04f;    // m ahead of the blade's middle line, along the swing, also tested
        private static readonly HashSet<Item> swept = new HashSet<Item>();

        /// <summary>
        /// The player's blade moved from pa-pb to a-b this frame, fast, along swing; cutting plane (point, normal). Every
        /// weapon the blade passed through on the way is cut. Checking only where the blade is now missed thin blades:
        /// at 7 m/s and 90 frames a second the blade jumps ~8 cm per frame, right over a 5 mm sword.
        /// </summary>
        public static void Sweep(Vector3 pa, Vector3 pb, Vector3 a, Vector3 b, Vector3 point, Vector3 normal, Vector3 swing, Item cutter,
            float fire = 0f)
        {
            EnsureMask();
            float travel = Mathf.Max((a - pa).magnitude, (b - pb).magnitude);
            int steps = Mathf.Clamp(Mathf.CeilToInt(travel / SweepStep), 1, 12);
            // Then once more just ahead: the edge leads the blade's middle line, and a heavy object (a crate) stops the
            // blade with its edge on the surface and the line 5-7 cm short of it.
            Vector3 lead = swing.normalized * EdgeLead;
            swept.Clear();
            for (int s = 1; s <= steps + 1; s++)
            {
                float t = Mathf.Min(1f, (float)s / steps);
                Vector3 ahead = s > steps ? lead : Vector3.zero;
                int hits = Physics.OverlapCapsuleNonAlloc(Vector3.Lerp(pa, a, t) + ahead, Vector3.Lerp(pb, b, t) + ahead, 0.03f, overlap,
                    itemMask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < hits; i++)
                {
                    Item item = overlap[i] ? overlap[i].GetComponentInParent<Item>() : null;
                    if (item && item != cutter) swept.Add(item);
                }
            }
            foreach (Item item in swept)
            {
                if (!CanCut(item)) continue;
                lastCut[item] = Time.time;
                try { Start(item, point, normal, swing, fire, cutter); }
                catch (Exception e) { Debug.LogError("[Zandatsu] weapon cut: " + e); }
            }
        }

        private static void EnsureMask()
        {
            // Big objects (barrels, crates, chairs) are on the layer the player can stand on; some (a ship's wheel, a shoe
            // bench) on one that only meets moving objects.
            if (itemMask == 0)
                itemMask = (1 << GameManager.GetLayer(LayerName.DroppedItem)) | (1 << GameManager.GetLayer(LayerName.MovingItem)) |
                           (1 << GameManager.GetLayer(LayerName.ItemAndRagdollOnly)) |
                           (1 << GameManager.GetLayer(LayerName.PlayerLocomotionObject)) |
                           (1 << GameManager.GetLayer(LayerName.MovingObjectOnly));
        }

        private static bool IsWeapon(Item item) => item.data.type == ItemData.Type.Weapon || item.data.type == ItemData.Type.Shield;

        /// <summary>
        /// Cut Props: loose objects (barrels, crates, furniture, pottery, logs, rocks, food, potions, tools) and anything
        /// breakable. Not what holds a level together or works it: doors, levers, bridges, ropes, key orbs, zipline wheels
        /// (or an object hinged to the world), and never skill crystals, spells, clothing or notes.
        /// </summary>
        private static bool IsProp(Item item)
        {
            ItemData.Type t = item.data.type;
            bool kind = t == ItemData.Type.Prop || t == ItemData.Type.Food || t == ItemData.Type.Potion || t == ItemData.Type.Tool ||
                        item.GetComponentInChildren<Breakable>();
            if (!kind) return false;
            string id = item.data.id ?? "";
            foreach (string no in NotProps)
                if (id.IndexOf(no, StringComparison.OrdinalIgnoreCase) >= 0) { NotCut(item, "works the level (" + no + ")"); return false; }
            foreach (Joint j in item.GetComponentsInChildren<Joint>())
                if (j && (!j.connectedBody || !j.connectedBody.transform.IsChildOf(item.transform)))   // hinged to the world
                {
                    NotCut(item, $"hinged to the world ({j.GetType().Name} on {j.name})");
                    return false;
                }
            return true;
        }

        private static readonly string[] NotProps = { "Door", "Lever", "Bridge", "Rope", "Hinge", "Key", "Zipline" };   // Key: a Dalgarian door's key orb

        private static bool CanCut(Item item)
        {
            if (!item.gameObject.activeInHierarchy || item.data == null) return false;
            if (IsWeapon(item) ? !ZandatsuScript.CutWeapons : !ZandatsuScript.CutProps || !IsProp(item)) return false;
            if (item.holder) { NotCut(item, "in a holder"); return false; }   // holstered or stored: the game keeps it by its data, it would come back whole
            if (lastCut.TryGetValue(item, out float t) && Time.time - t < Mathf.Min(SameSwingSeconds, ZandatsuScript.RecutDelay)) return false;
            if (states.TryGetValue(item, out CutState s) && s.busy) return false;
            foreach (RagdollHand h in item.handlers)
                if (h && h.creature && h.creature.isPlayer) return false;   // never the player's own weapons
            return !Uncuttable(item);
        }

        // ---------------- breakable objects ----------------

        private struct Disarmed { public Breakable b; public bool was; public float time; }
        private static readonly Dictionary<Breakable, Disarmed> disarmed = new Dictionary<Breakable, Disarmed>();
        private static readonly Collider[] ahead = new Collider[512];
        private const float DisarmLook = 0.12f, DisarmHold = 0.6f;

        /// <summary>
        /// A blade (a-b) moving fast along velocity: breakable objects in its way for the next DisarmLook seconds stop
        /// breaking, so its blow cuts them instead (Breakable.Hit runs inside the physics step, before any sweep). Blunt
        /// blows, throws and falls still break them; one the blade misses can break again after DisarmHold.
        /// </summary>
        public static void Disarm(Vector3 a, Vector3 b, Vector3 velocity, Item cutter)
        {
            EnsureMask();
            Vector3 step = velocity * DisarmLook;
            int hits = Physics.OverlapCapsuleNonAlloc((a + b) * 0.5f + step * 0.5f - (b - a) * 0.5f, (a + b) * 0.5f + step * 0.5f + (b - a) * 0.5f,
                0.05f + step.magnitude * 0.5f, ahead, itemMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits; i++)
            {
                Breakable br = ahead[i] ? ahead[i].GetComponentInParent<Breakable>() : null;
                if (!br || br.IsBroken) continue;
                Item item = br.LinkedItem ? br.LinkedItem : br.GetComponentInParent<Item>();
                if (!item || item == cutter || item.data == null || !IsProp(item)) continue;
                if (!MeshesReadable(item)) continue;   // it could not be cut: it breaks as it always did
                if (disarmed.TryGetValue(br, out Disarmed d)) { d.time = Time.time; disarmed[br] = d; continue; }
                disarmed[br] = new Disarmed { b = br, was = br.canBreak, time = Time.time };
                br.SetBreakable(false);
            }
        }

        /// <summary>Breakables the blade missed break again; cut ones stay unbreakable (their CutState restores them).</summary>
        private static void Rearm()
        {
            if (disarmed.Count == 0) return;
            var done = new List<Breakable>();
            foreach (KeyValuePair<Breakable, Disarmed> kv in disarmed)
            {
                if (!kv.Key) { done.Add(kv.Key); continue; }
                if (Time.time - kv.Value.time < DisarmHold) continue;
                Item item = kv.Key.LinkedItem ? kv.Key.LinkedItem : kv.Key.GetComponentInParent<Item>();
                if (!item || !states.ContainsKey(item)) kv.Key.SetBreakable(kv.Value.was);
                done.Add(kv.Key);
            }
            foreach (Breakable b in done) disarmed.Remove(b);
        }

        /// <summary>What a breakable's canBreak was before a blade came near (its own value if never disarmed).</summary>
        private static bool OriginalCanBreak(Breakable b) => disarmed.TryGetValue(b, out Disarmed d) ? d.was : b.canBreak;

        private const int Lightsaber = 1, BlasterBolt = 2;
        private static readonly Dictionary<Item, int> energy = new Dictionary<Item, int>();

        /// <summary>Which energy weapon of another mod an item is (The Outer Rim), known by its scripts' type names.</summary>
        private static int Energy(Item item)
        {
            if (energy.TryGetValue(item, out int kind)) return kind;
            kind = 0;
            foreach (MonoBehaviour mb in item.GetComponentsInChildren<MonoBehaviour>(true))
            {
                string n = mb ? mb.GetType().Name : null;
                if (n == null) continue;
                if (n.IndexOf("Lightsaber", StringComparison.OrdinalIgnoreCase) >= 0) kind |= Lightsaber;
                if (n.IndexOf("BlasterBolt", StringComparison.OrdinalIgnoreCase) >= 0) kind |= BlasterBolt;
            }
            if (energy.Count > 256) energy.Clear();
            energy[item] = kind;
            return kind;
        }

        /// <summary>
        /// Energy weapons (lightsabers, blaster bolts): the blade is a glowing beam its own script stretches and switches
        /// on and off, so cutting it would chop a light.
        /// </summary>
        private static bool Uncuttable(Item item) => Energy(item) != 0;

        /// <summary>
        /// A blaster bolt: a shot, not a blade. Its hit comes in as a slash (the game's own dismemberment is allowed for
        /// some bolts), and it sliced enemies apart.
        /// </summary>
        public static bool IsBlasterBolt(Item item) => item && (Energy(item) & BlasterBolt) != 0;

        public static bool IsLightsaber(Item item) => item && (Energy(item) & Lightsaber) != 0;

        private static readonly HashSet<string> notCutLogged = new HashSet<string>();

        /// <summary>Why an item under the blade was left whole: once per kind of item.</summary>
        private static void NotCut(Item item, string why)
        {
            string id = item.data != null ? item.data.id : item.name;
            if (notCutLogged.Add(id)) Debug.Log($"[Zandatsu] {id} not cut: {why}");
        }

        /// <summary>
        /// The material most of these renderers' parts are drawn with, of the game's lit shader if any is (the many ivy
        /// leaves on a forsaken cart outnumbered its wood, and a leaf shader cannot carry the cut face).
        /// </summary>
        private static Material MainMaterial(List<Renderer> renderers)
        {
            var count = new Dictionary<Material, int>();
            foreach (Renderer r in renderers)
                if (r) foreach (Material m in r.sharedMaterials) if (m) count[m] = (count.TryGetValue(m, out int n) ? n : 0) + 1;
            bool any = false;
            foreach (Material m in count.Keys) if (m.shader && m.shader.name.StartsWith("ThunderRoad/LitMoss")) { any = true; break; }
            if (any)
                foreach (Material m in new List<Material>(count.Keys))
                    if (!m.shader || !m.shader.name.StartsWith("ThunderRoad/LitMoss")) count.Remove(m);
            Material best = null;
            int most = 0;
            foreach (KeyValuePair<Material, int> kv in count) if (kv.Value > most) { most = kv.Value; best = kv.Key; }
            return best;
        }

        /// <summary>The renderers of an item's LOD group not in that level of detail.</summary>
        private static HashSet<Renderer> OtherLods(Item item, int level)
        {
            var other = new HashSet<Renderer>();
            LODGroup lod = item.GetComponentInChildren<LODGroup>();
            if (!lod) return other;
            LOD[] lods = lod.GetLODs();
            if (level >= lods.Length) return other;
            var keep = new HashSet<Renderer>();
            foreach (Renderer r in lods[level].renderers) if (r) keep.Add(r);
            for (int l = 0; l < lods.Length; l++)
                foreach (Renderer r in lods[l].renderers) if (r && !keep.Contains(r)) other.Add(r);
            return other;
        }

        /// <summary>
        /// The level of detail a cut works on: LOD0, else (its meshes compressed, which cannot be read) the first lower
        /// level whose meshes all can: the object is cut in that level's simpler shape (a crate's LOD2) and keeps it.
        /// -1: none can be cut.
        /// </summary>
        private static int CutLevel(Item item)
        {
            LODGroup lod = item.GetComponentInChildren<LODGroup>();
            int levels = lod ? Mathf.Max(1, lod.lodCount) : 1;
            for (int level = 0; level < levels; level++)
                if (MeshesReadable(item, OtherLods(item, level))) return level;
            return -1;
        }

        /// <summary>Whether the object can be cut (some level of detail readable).</summary>
        private static bool MeshesReadable(Item item) => CutLevel(item) >= 0;

        /// <summary>Whether every mesh a cut of this object would split can be read (Start's choice of renderers).</summary>
        private static bool MeshesReadable(Item item, HashSet<Renderer> skip)
        {
            foreach (Renderer r in item.renderers)
            {
                if (!r || skip.Contains(r)) continue;
                if (!(r is MeshRenderer) || !r.enabled || r.forceRenderingOff || !r.gameObject.activeInHierarchy) continue;   // skinned: baked (StandIn)
                if (r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (!mf || !mf.sharedMesh || !MeshReader.Readable(mf.sharedMesh, out _)) return false;
            }
            return true;
        }

        private static void Start(Item item, Vector3 point, Vector3 normal, Vector3 swing, float fire, Item cutter)
        {
            float t0 = Time.realtimeSinceStartup;
            if (!states.TryGetValue(item, out CutState state)) state = new CutState { item = item };

            // What to cut: the item's visible mesh renderers (and the cut faces of earlier cuts), of one level of detail:
            // LOD0, or the one an earlier cut chose, or the first that can be read.
            int lodLevel = state.cuts > 0 ? state.lodLevel : Mathf.Max(0, CutLevel(item));
            HashSet<Renderer> lowerLods = OtherLods(item, lodLevel);
            var candidates = new List<Renderer>(item.renderers);
            foreach (MeshRenderer c in state.caps) if (c) candidates.Add(c);
            foreach (KeyValuePair<SkinnedMeshRenderer, MeshRenderer> kv in state.standIns) if (kv.Value) candidates.Add(kv.Value);

            var targets = new List<Renderer>();
            var meshes = new List<Mesh>();
            var toItem = new List<Matrix4x4>();
            var subCounts = new List<int>();
            var data = new MeshData(2048, true);
            string unreadable = null;
            Matrix4x4 w2i = item.transform.worldToLocalMatrix;
            bool prop = !IsWeapon(item);
            foreach (Renderer candidate in candidates)
            {
                if (!candidate || lowerLods.Contains(candidate)) continue;
                if (!candidate.enabled || candidate.forceRenderingOff || !candidate.gameObject.activeInHierarchy) continue;
                Renderer r = candidate;
                if (candidate is SkinnedMeshRenderer smr)
                {
                    // A bow's limbs and string, a crossbow's arms: cut as they are now, baked into a stand-in.
                    r = StandIn(state, smr);
                    if (!r) { NotCut(item, "skinned mesh " + smr.name + " could not be baked"); return; }
                }
                else if (!(candidate is MeshRenderer)) continue;
                // A shadow-only stand-in (a ShadowProxy, on objects and weapons such as arrows): not cut (its cut faces lay
                // over the real ones, and on the cut-off half, drawn like the rest, its material showed pink); it is
                // switched off once the item is cut and the item casts its own shadow.
                if (r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;
                MeshFilter mf = r.GetComponent<MeshFilter>();
                Mesh mesh = mf ? mf.sharedMesh : null;
                // Many objects ship their meshes without CPU access: read back from the graphics card once (weapons too).
                string why = null;
                Mesh readable = mesh ? MeshReader.Readable(mesh, out why) : null;
                if (!readable) { unreadable = (mesh ? mesh.name : r.name) + (why != null ? ": " + why : ""); continue; }
                if (data.VertexCount + readable.vertexCount > MaxVerts) { NotCut(item, $"over {MaxVerts} vertices"); return; }
                Matrix4x4 m = w2i * r.transform.localToWorldMatrix;
                data.Append(readable, m, r.sharedMaterials, null, null, targets.Count);
                targets.Add(r);
                meshes.Add(mesh);
                toItem.Add(m);
                subCounts.Add(readable.subMeshCount);
            }
            if (targets.Count == 0 || data.VertexCount == 0) { NotCut(item, unreadable != null ? "mesh not readable (" + unreadable + ")" : "no visible mesh"); return; }

            // Imbue shapes: spell effects (fire, lightning...) spread over the colliderGroup's imbueEffectRenderer (a hidden
            // mesh of the blade, or one the game builds from the colliders on the first imbue) and glow on its
            // imbueEmissionRenderer. Uncut, the flames ran along the whole old blade. Built now if missing (from the
            // colliders still whole), then cut with the rest, without cut faces.
            var imbueTargets = new List<Renderer>();
            var imbueMeshes = new List<Mesh>();
            var imbueToItem = new List<Matrix4x4>();
            var imbueSubCounts = new List<int>();
            var imbueData = new MeshData(512, true);
            foreach (Imbue imb in item.imbues)
            {
                ColliderGroup cg = imb ? imb.colliderGroup : null;
                if (!cg || cg.imbueEffectRenderer || cg.colliders == null || cg.colliders.Count == 0) continue;
                try
                {
                    cg.GenerateImbueMesh();
                    if (cg.imbueEffectRenderer) cg.imbueEffectRenderer.enabled = false;
                }
                catch (Exception e) { Debug.LogWarning("[Zandatsu] imbue mesh: " + e.Message); }
            }
            foreach (ColliderGroup cg in item.colliderGroups)
            {
                if (!cg) continue;
                foreach (Renderer r in new[] { cg.imbueEffectRenderer, cg.imbueEmissionRenderer })
                {
                    if (!(r is MeshRenderer) || targets.Contains(r) || imbueTargets.Contains(r)) continue;
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    Mesh mesh = mf ? mf.sharedMesh : null;
                    if (!mesh || !mesh.isReadable || mesh.vertexCount > MaxVerts) continue;
                    Matrix4x4 m = w2i * r.transform.localToWorldMatrix;
                    imbueData.Append(mesh, m, r.sharedMaterials, null, null, imbueTargets.Count);
                    imbueTargets.Add(r);
                    imbueMeshes.Add(mesh);
                    imbueToItem.Add(m);
                    imbueSubCounts.Add(mesh.subMeshCount);
                }
            }

            // The grip that stays the weapon: the one a hand holds, else the main grip, else the centre of mass. An object
            // (Cut Props) keeps the half a hand holds, else the side of its centre of mass (usually the bigger one).
            Vector3 keep = item.physicBody != null ? item.physicBody.worldCenterOfMass : item.transform.position;
            RagdollHand holder = item.mainHandler;
            if (!holder) foreach (RagdollHand h in item.handlers) if (h) { holder = h; break; }
            if (holder && holder.grabbedHandle) keep = holder.grabbedHandle.transform.position;
            else if (!prop && item.mainHandleRight) keep = item.mainHandleRight.transform.position;
            else if (!prop && item.mainHandleLeft) keep = item.mainHandleLeft.transform.position;
            int keptSign = Vector3.Dot(keep - point, normal) >= 0f ? 1 : -1;

            // Plane in item space (normals transform with the inverse transpose).
            Vector3 pointL = w2i.MultiplyPoint3x4(point);
            Vector3 normalL = item.transform.localToWorldMatrix.transpose.MultiplyVector(normal).normalized;

            // White-hot only from a blade burning with fire; a plain blade leaves a dull red edge. Objects get the cut face of
            // what they are made of, and so do shields (a wooden one showed hot steel); other weapons are hot steel.
            bool shield = item.data != null && item.data.type == ItemData.Type.Shield;
            CapTextures.Kind capKind = prop || shield ? CapTextures.Drawn(CapTextures.Of(item, point, normal)) : CapTextures.Kind.None;
            Material capOwn = capKind > CapTextures.Kind.Metal ? CapTextures.For(capKind, MainMaterial(targets)) : null;
            bool ownFace = prop ? capKind != CapTextures.Kind.Metal : capOwn;   // a shield of no known material: hot steel
            Material steelKeep = HotSteel.Create(null, HotSteel.PeakFor(fire));
            Material steelDebris = HotSteel.Create(null, HotSteel.PeakFor(fire));
            if (!steelKeep || !steelDebris) return;
            // A vessel modelled closed: a wall round a dark inside (a stand-in marks the inside until it gets its material).
            float hollow = capOwn ? CapTextures.HollowWall(item, capKind) : 0f;
            Material insideOwn = hollow > 0f ? CapTextures.Inside(capOwn) : null;
            Material insideMark = insideOwn ? new Material(steelKeep) { name = "Zandatsu_InsideMark" } : null;

            state.busy = true;
            states[item] = state;
            var job = new Job
            {
                item = item,
                state = state,
                targets = targets,
                meshes = meshes,
                toItem = toItem,
                subCounts = subCounts,
                pointW = point,
                normalW = normal,
                keptSign = keptSign,
                bladeVelocity = swing,
                steelKeep = steelKeep,
                steelDebris = steelDebris,
                verts = data.VertexCount,
                imbueTargets = imbueTargets,
                imbueMeshes = imbueMeshes,
                imbueToItem = imbueToItem,
                imbueSubCounts = imbueSubCounts,
                prop = prop,
                lodLevel = lodLevel,
                faces = new ItemDebris.Faces { kind = capKind, cap = capOwn, inside = insideOwn, hollow = hollow },
                cutter = cutter,
                prepMs = (Time.realtimeSinceStartup - t0) * 1000f
            };
            job.task = Task.Run(() =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                // An object's cut face is first made of hot steel too (a stand-in), then given the inside of what it is made
                // of (our texture), else its own surface; metal stays hot steel, as on weapons.
                CapSurface surface = ownFace ? CapSurface.Of(data, pointL, normalL, capKind, capOwn) : null;
                bool ok = MeshSlicer.Slice(data, pointL, normalL, steelKeep, out job.pos, out job.neg, holes: true,
                    hollow: hollow, inside: insideMark);
                job.detail = MeshSlicer.LastDetail;
                if (ok)
                {
                    if (surface != null && !ReferenceEquals(surface.material, null))   // no Unity calls off the main thread
                    {
                        surface.Apply(job.pos, steelKeep, normalL, insideMark, insideOwn);
                        surface.Apply(job.neg, steelKeep, normalL, insideMark, insideOwn);
                    }
                    job.capInfo = surface?.info;
                    job.pos.Sanitize();
                    job.neg.Sanitize();
                    // No fill material: no cut faces on the hidden imbue shapes. A miss leaves them whole (all on one side).
                    if (imbueData.VertexCount > 0)
                        job.imbueCut = MeshSlicer.Slice(imbueData, pointL, normalL, null, out job.imbuePos, out job.imbueNeg);
                }
                else
                {
                    float lo = float.MaxValue, hi = float.MinValue;
                    foreach (Vector3 v in data.v) { float s = Vector3.Dot(v - pointL, normalL); lo = Mathf.Min(lo, s); hi = Mathf.Max(hi, s); }
                    if (data.v.Count > 0) job.miss = $", it lies {lo * 100f:0} to {hi * 100f:0} cm from the plane";
                }
                job.workerMs = clock.Elapsed.TotalMilliseconds;
                return ok;
            });
            job.insideMark = insideMark;
            jobs.Add(job);
        }

        // ---------------- every frame ----------------

        public static void Update()
        {
            for (int i = jobs.Count - 1; i >= 0; i--)
            {
                Job job = jobs[i];
                if (!job.task.IsCompleted) continue;
                jobs.RemoveAt(i);
                job.state.busy = false;
                bool ok = !job.task.IsFaulted && job.task.Result;
                if (job.task.IsFaulted) Debug.LogError("[Zandatsu] weapon cut: " + job.task.Exception);
                else if (!ok && job.item)
                {
                    NotCut(job.item, $"the plane missed its mesh ({job.verts} verts{job.miss})");
                    lastCut.Remove(job.item);   // a blade just beside it: the same swing can still cut it a moment later
                }
                else if (ok && job.item && !Unchanged(job)) NotCut(job.item, "it changed while it was being cut");
                if (ok && Unchanged(job))
                {
                    try { Finish(job); }
                    catch (Exception e)
                    {
                        Debug.LogError("[Zandatsu] weapon cut: " + e);
                        ok = false;
                    }
                }
                else ok = false;
                if (job.insideMark) Object.Destroy(job.insideMark);   // replaced by the inside's material
                if (!ok)
                {
                    Object.Destroy(job.steelKeep);
                    Object.Destroy(job.steelDebris);
                    if (job.state.cuts == 0)
                    {
                        foreach (Object o in job.state.owned) if (o) Object.Destroy(o);   // baked stand-ins never shown
                        Forget(job.state);
                    }
                }
            }

            // Cut faces follow their weapon: hidden with it (held in a holder, culled), on its layer (first-person hand).
            foreach (CutState s in states.Values)
            {
                // Baked stand-ins as their skinned parts: hidden with them (in a holder), on their layer (in the hand).
                foreach (KeyValuePair<SkinnedMeshRenderer, MeshRenderer> kv in s.standIns)
                {
                    if (!kv.Key || !kv.Value || kv.Value.forceRenderingOff) continue;
                    if (kv.Value.enabled != kv.Key.enabled) kv.Value.enabled = kv.Key.enabled;
                    if (kv.Value.gameObject.layer != kv.Key.gameObject.layer) kv.Value.gameObject.layer = kv.Key.gameObject.layer;
                }
                if (!s.capFollow) continue;
                bool on = s.capFollow.enabled && s.capFollow.gameObject.activeInHierarchy;
                int layer = s.capFollow.gameObject.layer;
                foreach (MeshRenderer c in s.caps)
                {
                    if (!c) continue;
                    if (c.enabled != on) c.enabled = on;
                    if (c.gameObject.layer != layer) c.gameObject.layer = layer;
                }
            }
            ItemDebris.Update();
            Rearm();
        }

        /// <summary>The item still looks the way it was snapshotted (not despawned, not cut by something else meanwhile).</summary>
        private static bool Unchanged(Job job)
        {
            if (!job.item || !job.item.gameObject.activeInHierarchy) return false;
            for (int r = 0; r < job.targets.Count; r++)
            {
                if (!job.targets[r]) return false;
                MeshFilter mf = job.targets[r].GetComponent<MeshFilter>();
                if (!mf || mf.sharedMesh != job.meshes[r]) return false;
            }
            return true;
        }

        private static void Finish(Job job)
        {
            float t0 = Time.realtimeSinceStartup;
            Item item = job.item;
            CutState state = job.state;
            MeshData kept = job.keptSign > 0 ? job.pos : job.neg;
            MeshData off = job.keptSign > 0 ? job.neg : job.pos;
            Vector3 p = job.pointW, n = job.normalW;
            int side = job.keptSign;
            float Dist(Vector3 w) => Vector3.Dot(w - p, n) * side;   // >= 0: on the kept side

            if (state.cuts == 0)
            {
                state.onDespawn = time => { if (time == EventTime.OnStart) Restore(state); };
                item.OnDespawnEvent += state.onDespawn;
                state.mass = item.physicBody != null ? item.physicBody.mass : -1f;
                state.useCustomCom = item.useCustomCenterOfMass;
                state.customCom = item.customCenterOfMass;
                state.customInertia = item.customInertiaTensor;
                state.lodGroup = item.GetComponentInChildren<LODGroup>();
                state.lodLevel = job.lodLevel;
                if (state.lodGroup)
                {
                    // The level cut stays on at every distance, the others (with the whole shape) off.
                    state.lodWasEnabled = state.lodGroup.enabled;
                    foreach (Renderer r in OtherLods(item, job.lodLevel))
                        if (!r.forceRenderingOff)
                        {
                            r.forceRenderingOff = true;
                            state.hidden.Add(r);
                        }
                    state.lodGroup.ForceLOD(job.lodLevel);
                    state.lodGroup.enabled = false;
                }
            }
            state.cuts++;

            // 0. Skinned parts cut: their baked stand-ins from now on, the skinned ones and what bent them off.
            bool baked = false;
            foreach (KeyValuePair<SkinnedMeshRenderer, MeshRenderer> kv in state.standIns)
            {
                if (!kv.Value || !kv.Value.forceRenderingOff || !job.targets.Contains(kv.Value)) continue;
                kv.Value.forceRenderingOff = false;
                if (kv.Key && !kv.Key.forceRenderingOff)
                {
                    kv.Key.forceRenderingOff = true;
                    state.hidden.Add(kv.Key);
                }
                baked = true;
            }
            if (baked) StopWorkings(item, state);

            // 1. The weapon's own renderers get what is left of their mesh (in their own space, same submeshes).
            int keptVerts = 0;
            for (int r = 0; r < job.targets.Count; r++)
            {
                Renderer rend = job.targets[r];
                MeshFilter mf = rend.GetComponent<MeshFilter>();
                Mesh mesh = Extract(kept, sub => sub >= 0 && VertexOrigin.TagRenderer(sub) == r, sub => VertexOrigin.TagSubmesh(sub),
                    job.subCounts[r], job.toItem[r].inverse, rend.name + " (Zandatsu)", out int vc);
                keptVerts += vc;
                if (!state.originalMeshes.ContainsKey(mf) && !IsCap(state, rend)) state.originalMeshes[mf] = mf.sharedMesh;
                if (mesh)
                {
                    state.owned.Add(mesh);
                    mf.sharedMesh = mesh;
                }
                else if (!rend.forceRenderingOff)
                {
                    rend.forceRenderingOff = true;
                    state.hidden.Add(rend);
                }
            }

            // 1a. The shadow-only stand-in still had the whole item's shape: off, and the item casts its own.
            foreach (Renderer r in item.renderers)
            {
                if (!r) continue;
                if (r.shadowCastingMode == ShadowCastingMode.ShadowsOnly && !r.forceRenderingOff)
                {
                    r.forceRenderingOff = true;
                    state.hidden.Add(r);
                }
                else if (job.targets.Contains(r) && r.shadowCastingMode == ShadowCastingMode.Off)
                {
                    state.shadows.Add(new KeyValuePair<Renderer, ShadowCastingMode>(r, r.shadowCastingMode));
                    r.shadowCastingMode = ShadowCastingMode.On;
                }
            }

            // 1b. Imbue shapes: what is left of them, so spell effects only cover the blade that is still there.
            if (job.imbueCut)
            {
                MeshData imbueKept = job.keptSign > 0 ? job.imbuePos : job.imbueNeg;
                for (int r = 0; r < job.imbueTargets.Count; r++)
                {
                    Renderer rend = job.imbueTargets[r];
                    MeshFilter mf = rend ? rend.GetComponent<MeshFilter>() : null;
                    if (!mf || mf.sharedMesh != job.imbueMeshes[r]) continue;
                    Mesh mesh = Extract(imbueKept, sub => sub >= 0 && VertexOrigin.TagRenderer(sub) == r, sub => VertexOrigin.TagSubmesh(sub),
                        job.imbueSubCounts[r], job.imbueToItem[r].inverse, rend.name + " (Zandatsu)", out _);
                    if (!mesh) continue;
                    if (!state.originalMeshes.ContainsKey(mf)) state.originalMeshes[mf] = mf.sharedMesh;
                    state.owned.Add(mesh);
                    mf.sharedMesh = mesh;
                }
            }

            // 2. The kept cut face, on the item, red-hot and cooling.
            Mesh capMesh = Extract(kept, sub => sub < 0, null, 0, Matrix4x4.identity, "Zandatsu_WeaponCut", out _, out Material[] capMats);
            if (capMesh)
            {
                var go = new GameObject("Zandatsu_WeaponCut") { layer = job.targets[0].gameObject.layer };
                go.transform.SetParent(item.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = capMesh;
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = capMats;
                mr.shadowCastingMode = ShadowCastingMode.On;
                ItemCapLighting.Attach(item, mr);
                state.caps.Add(mr);
                state.owned.Add(capMesh);
                state.owned.Add(go);
                if (!state.capFollow) state.capFollow = job.targets[0];
            }
            state.owned.Add(job.steelKeep);

            // 3a. The cut-off half's colliders, taken before this half's are cut back: the item's own beyond the cut. With
            // only a hull of its shape, half a blade (1.5 cm thick) sank through the floor now and then.
            var itemColliders = new List<Collider>();
            foreach (ColliderGroup g in item.colliderGroups) if (g && g.colliders != null) itemColliders.AddRange(g.colliders);
            foreach (MeshCollider h in state.hulls) if (h && h.enabled) itemColliders.Add(h);
            List<Collider> offColliders = OffColliders(itemColliders, Dist);
            bool meshReaches = MeshReaches(itemColliders, Dist);

            // 3. Colliders, damage zones, grips beyond the cut.
            int collOff = 0, collFit = 0;
            var hulls = new List<Collider>();
            foreach (ColliderGroup g in item.colliderGroups)
            {
                if (!g || g.colliders == null) continue;
                foreach (Collider c in g.colliders)
                {
                    if (!c || !c.enabled || c.isTrigger) continue;
                    // An object's mesh collider through the cut (one hull for a whole barrel): swapped for the hull of what
                    // is left, or the half stayed as big as the whole object and stood on the cut-off half's place.
                    if (job.prop && c is MeshCollider mc && Spans(mc, Dist) && HullCollider(state, mc, kept, item, hulls))
                    {
                        collFit++;
                        continue;
                    }
                    int r = ClipCollider(state, c, Dist);
                    if (r < 0) collOff++;
                    else if (r > 0) collFit++;
                }
            }
            // Hulls of earlier cuts are not in its collider groups: made again of what is left (an anvil stand cut nine
            // times kept the hull of its first cut, and collided where nothing was to be seen).
            foreach (MeshCollider h in state.hulls)
            {
                if (!h || !h.enabled || hulls.Contains(h)) continue;
                int keptN = KeptCorners(h, Dist);
                if (keptN == 8) continue;
                if (keptN == 0) { h.enabled = false; collOff++; }
                else if (RefitHull(state, h, kept, item)) collFit++;
            }
            int dmgShort = 0, dmgOff = 0;
            foreach (CollisionHandler ch in item.collisionHandlers)
            {
                if (!ch) continue;
                foreach (Damager d in new List<Damager>(ch.damagers))
                {
                    if (!d) continue;
                    int r = ClipDamager(state, ch, d, Dist);
                    if (r < 0) dmgOff++;
                    else if (r > 0) dmgShort++;
                }
            }
            int gripsOff = 0;
            foreach (Handle h in item.handles)
            {
                if (!h || state.handlesOff.Contains(h) || Dist(h.transform.position) >= 0f) continue;
                if (h.handlers != null)
                    foreach (RagdollHand hand in new List<RagdollHand>(h.handlers))
                        try { hand.TryRelease(); } catch { }
                h.SetTouch(false);
                state.handlesOff.Add(h);
                gripsOff++;
            }
            // The mass by the colliders' room on each side (a sword's many hilt vertices gave its half blade 5 % of it),
            // else by vertices.
            float keptRoom = 0f, offRoom = 0f;
            Vector3 keptCentre = Vector3.zero;
            foreach (Collider c in itemColliders)
                if (c && c.enabled && !c.isTrigger)
                {
                    float r = Room(c);
                    keptRoom += r;
                    keptCentre += Centre(c) * r;
                }
            keptCentre = keptRoom > 0f ? keptCentre / keptRoom : item.transform.TransformPoint(ItemDebris.Centroid(kept));
            foreach (Collider c in offColliders) offRoom += Room(c);
            float keptShare = keptRoom > 0f && offRoom > 0f ? Mathf.Clamp(keptRoom / (keptRoom + offRoom), 0.05f, 0.95f)
                                                             : Mathf.Clamp((float)keptVerts / Mathf.Max(1, job.verts), 0.15f, 1f);
            float debrisMass = 0.3f, offOverKept = 0f;
            if (item.physicBody != null && state.mass > 0f)
            {
                float before = item.physicBody.mass;
                item.physicBody.mass = Mathf.Max(0.1f, before * keptShare);
                debrisMass = Mathf.Max(0.1f, before - item.physicBody.mass);
                offOverKept = (before - item.physicBody.mass) / item.physicBody.mass;
                ScaleOwnMass(item, state, item.physicBody.mass / before);
            }
            // Where the cut-off part's mass was: its colliders' middle, by room, else its shape's.
            Vector3 offCentre = Vector3.zero;
            if (offRoom > 0f)
            {
                foreach (Collider c in offColliders) offCentre += Centre(c) * Room(c);
                offCentre /= offRoom;
            }
            else offCentre = item.transform.TransformPoint(ItemDebris.Centroid(off));
            string com = Rebalance(item, state, Dist, offCentre, keptCentre, offOverKept, kept);
            int effects = RefreshImbueEffects(item);

            // 4. The other half falls as debris.
            Vector3 vel = item.physicBody != null ? item.physicBody.GetPointVelocity(p) : Vector3.zero;
            Vector3 push = -n * side * ZandatsuScript.SeparationSpeed * 0.5f + job.bladeVelocity * 0.15f;
            ReplaceMaterial(off, job.steelKeep, job.steelDebris);
            // The weapon's materials as they are now, as the kept part has them: the snapshot's could be gone. The game
            // swaps a blade's material for a new instance (blood on it) and destroys the old one, and a destroyed
            // material renders magenta (a katana's debris showed a magenta stretch of blade).
            for (int i = 0; i < off.mats.Count; i++)
            {
                int tag = off.subTag[i];
                if (tag < 0) continue;
                int r = VertexOrigin.TagRenderer(tag), s = VertexOrigin.TagSubmesh(tag);
                if (r >= job.targets.Count || !job.targets[r]) continue;
                Material[] now = job.targets[r].sharedMaterials;
                if (s < now.Length && now[s]) off.mats[i] = now[s];
            }
            var colliders = new List<Collider>(hulls);
            foreach (MeshCollider h in state.hulls) if (h && !colliders.Contains(h)) colliders.Add(h);
            foreach (ColliderGroup g in item.colliderGroups)
                if (g && g.colliders != null) foreach (Collider c in g.colliders) if (c) colliders.Add(c);
            bool debris = ItemDebris.Spawn(item, off, job.steelDebris, debrisMass, vel + push, colliders, job.faces, job.cutter, offColliders,
                meshReaches);
            if (!debris) Object.Destroy(job.steelDebris);

            // A breakable object, once cut, never shatters: its pre-made pieces were pieces of the whole object.
            string broke = "";
            if (job.prop)
            {
                Breakable br = item.GetComponentInChildren<Breakable>(true);
                if (br && !state.breakable)
                {
                    state.breakable = br;
                    state.breakableWas = OriginalCanBreak(br);
                    br.SetBreakable(false);
                    disarmed.Remove(br);
                    broke = ", breakable: shattering off";
                }
            }
            string capMat = capMesh && capMats != null && capMats.Length > 0 && capMats[0] ? capMats[0].name : "none";

            Debug.Log($"[Zandatsu] {(job.prop ? "prop" : "weapon")} cut {item.data.id} ({item.name}, cut {state.cuts}{(job.lodLevel > 0 ? $", at LOD{job.lodLevel}: LOD0 compressed" : "")}): " +
                      $"{job.verts} verts -> kept {keptVerts}" +
                      (job.prop || job.faces.cap ? $", cut face {capMat} {job.capInfo}{broke}" : "") +
                      $"{(debris ? "" : ", no debris")}, colliders off {collOff} / shortened {collFit}, damage zones off {dmgOff} / shortened {dmgShort}, " +
                      $"grips off {gripsOff}, mass x{keptShare:0.00}{com}, imbue shapes {(job.imbueCut ? job.imbueTargets.Count : 0)}, " +
                      $"spell effects reshaped {effects}; main prep {job.prepMs:0.0} ms, worker {job.workerMs:0.0} ms ({job.detail}), " +
                      $"apply {(Time.realtimeSinceStartup - t0) * 1000f:0.0} ms");
        }

        private static bool IsCap(CutState s, Renderer r) => r is MeshRenderer mr && s.caps.Contains(mr);

        /// <summary>
        /// A skinned part (a bow's limbs and string, a crossbow's arms) as it is now, baked into a plain mesh renderer on it
        /// that is cut in its place: hidden until the cut is made (Finish shows it and hides the skinned one).
        /// </summary>
        private static MeshRenderer StandIn(CutState s, SkinnedMeshRenderer smr)
        {
            foreach (KeyValuePair<SkinnedMeshRenderer, MeshRenderer> kv in s.standIns) if (kv.Key == smr && kv.Value) return kv.Value;
            if (!smr.sharedMesh) return null;
            var baked = new Mesh { name = smr.sharedMesh.name + " (Zandatsu baked)" };
            try { smr.BakeMesh(baked, false); }   // in the renderer's space, its scale left to the stand-in's transform
            catch (Exception e)
            {
                Debug.LogWarning("[Zandatsu] baking " + smr.name + ": " + e.Message);
                Object.Destroy(baked);
                return null;
            }
            if (baked.vertexCount == 0) { Object.Destroy(baked); return null; }
            var go = new GameObject("Zandatsu_Baked_" + smr.name) { layer = smr.gameObject.layer };
            go.transform.SetParent(smr.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = baked;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = smr.sharedMaterials;
            mr.shadowCastingMode = smr.shadowCastingMode;
            mr.receiveShadows = smr.receiveShadows;
            mr.lightProbeUsage = smr.lightProbeUsage;
            mr.reflectionProbeUsage = smr.reflectionProbeUsage;
            mr.probeAnchor = smr.probeAnchor;
            mr.renderingLayerMask = smr.renderingLayerMask;
            var block = new MaterialPropertyBlock();
            smr.GetPropertyBlock(block);
            if (!block.isEmpty) mr.SetPropertyBlock(block);
            mr.forceRenderingOff = true;
            s.standIns.Add(new KeyValuePair<SkinnedMeshRenderer, MeshRenderer>(smr, mr));
            s.owned.Add(baked);
            s.owned.Add(go);
            return mr;
        }

        private static readonly EventInfo heldAction = typeof(Item).GetEvent("OnHeldActionEvent");
        private static readonly MethodInfo crossbowAction = typeof(Crossbow).GetMethod("ItemActionEvent", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// A bow or crossbow cut: its limbs no longer bend (a baked stand-in), so it no longer draws or shoots: a cut bow is
        /// broken. Its string object goes off (switching off the BowString alone was not enough: it listens to the bow
        /// from Awake on, and grabbing the bow made the string grabbable again), and so does the crossbow's trigger (wired
        /// to the item from Awake on: a loaded one could still shoot). All back on despawn.
        /// </summary>
        private static void StopWorkings(Item item, CutState s)
        {
            foreach (BowString b in item.GetComponentsInChildren<BowString>(true))
            {
                if (!b) continue;
                Handle h = b.stringHandle;
                if (h && h.handlers != null)
                    foreach (RagdollHand hand in new List<RagdollHand>(h.handlers))
                        try { hand.TryRelease(); } catch { }
                GameObject g = b.gameObject;
                if (g != item.gameObject && g.activeSelf)
                {
                    g.SetActive(false);
                    s.undo.Add(() => { if (g) g.SetActive(true); });
                }
                else if (b.enabled)
                {
                    b.enabled = false;
                    s.undo.Add(() => { if (b) b.enabled = true; });
                }
            }
            foreach (Crossbow c in item.GetComponentsInChildren<Crossbow>(true))
            {
                if (!c || heldAction == null || crossbowAction == null) continue;
                try
                {
                    Delegate trigger = Delegate.CreateDelegate(heldAction.EventHandlerType, c, crossbowAction);
                    heldAction.RemoveEventHandler(item, trigger);
                    s.undo.Add(() => { if (item && c) heldAction.AddEventHandler(item, trigger); });
                }
                catch (Exception e) { Debug.LogWarning("[Zandatsu] crossbow trigger: " + e.Message); }
            }
        }

        private static readonly FieldInfo orgMassField =
            typeof(CollisionHandler).GetField("orgMass", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// The game keeps its own copy of an item's mass in each collision handler and sets the body back to it whenever a
        /// physics modifier comes or goes (Time Stop puts one on every item and takes it off; telekinesis and water too):
        /// an axe without its head weighed as the whole axe again (1.2 kg instead of 0.51). Scaled as the body was.
        /// </summary>
        private static void ScaleOwnMass(Item item, CutState s, float factor)
        {
            if (orgMassField == null) return;
            foreach (CollisionHandler ch in item.collisionHandlers)
            {
                if (!ch || ch.physicBody == null || ch.physicBody.rigidBody != item.physicBody.rigidBody) continue;
                float own = (float)orgMassField.GetValue(ch);
                if (!s.ownMass.ContainsKey(ch)) s.ownMass[ch] = own;
                orgMassField.SetValue(ch, Mathf.Max(0.1f, own * factor));
            }
        }

        /// <summary>
        /// Many weapons carry a hand-set centre of mass (a mace's sits in its head). Cut off with the head it stayed there,
        /// in the air: the handle balanced on its end and stood upright. If the hand-set point is gone, Unity works the
        /// centre out from the colliders that are left; the custom inertia (also from the whole weapon) goes the same way.
        /// The game re-applies the item's centre of mass on releases, so the item's own settings are changed, not just the body.
        /// A hand-set point on the kept side was still the whole weapon's: an axe with its head cut off, or a sword without
        /// its tip, balanced as before (the log said "hand-set kept, moved 0 cm" at a third of the mass). It moves away from
        /// the cut-off part by the mass that went: whole = (kept·m + off·m') / (m + m'), so kept = whole + (whole − off)·m'/m.
        /// The masses are guesses by the colliders' room, and a blade's room is more than its share of the weight: a long
        /// sword cut past its centre had it go to the pommel. So it goes no further than the middle of what is left (by its
        /// colliders), unless the hand-set point was already past that (a hilt-heavy weapon losing its tip).
        /// </summary>
        private static string Rebalance(Item item, CutState s, Func<Vector3, float> dist, Vector3 offCentre, Vector3 keptCentre,
                                        float offOverKept, MeshData kept)
        {
            if (item.physicBody == null || !item.physicBody.rigidBody) return "";
            Rigidbody rb = item.physicBody.rigidBody;
            Vector3 before = rb.worldCenterOfMass;
            string how;
            if (item.useCustomCenterOfMass && dist(item.transform.TransformPoint(item.customCenterOfMass)) >= 0f)
            {
                how = "hand-set kept";
                Vector3 whole = item.customCenterOfMass;
                Vector3 away = whole - item.transform.InverseTransformPoint(offCentre);
                float shift = away.magnitude * offOverKept;
                if (away.sqrMagnitude > 1e-8f)
                {
                    away.Normalize();
                    float toMiddle = Vector3.Dot(item.transform.InverseTransformPoint(keptCentre) - whole, away);
                    if (toMiddle > 0f) shift = Mathf.Min(shift, toMiddle);
                }
                Vector3 moved = whole + away * shift;
                // Within what is left.
                if (kept != null && kept.v.Count > 0)
                {
                    var b = new Bounds(kept.v[0], Vector3.zero);
                    foreach (Vector3 v in kept.v) b.Encapsulate(v);
                    moved = b.ClosestPoint(moved);
                }
                if (offOverKept > 0f && dist(item.transform.TransformPoint(moved)) >= 0f)
                {
                    item.customCenterOfMass = moved;
                    how = "hand-set, moved off the cut-off part";
                }
            }
            else
            {
                item.useCustomCenterOfMass = false;
                how = "from colliders";
            }
            item.customInertiaTensor = false;
            try
            {
                item.ResetCenterOfMass();
                rb.ResetInertiaTensor();
            }
            catch (Exception e) { Debug.LogWarning("[Zandatsu] weapon centre of mass: " + e.Message); }
            // Collider changes reach the physics engine with its next step: worked out right away, the centre of mass came
            // from the old colliders (the log said "moved 0 cm" after colliders went). Again once physics has them.
            Vector3 beforeLocal = item.transform.InverseTransformPoint(before);
            string name = item.data != null ? item.data.id : item.name;
            ZandatsuRunner.Instance.RunAfterRealtime(0.15f, () =>
            {
                if (!item || !rb || !states.TryGetValue(item, out CutState st) || st != s) return;
                try
                {
                    item.ResetCenterOfMass();
                    rb.ResetInertiaTensor();
                }
                catch { return; }
                float moved = (rb.centerOfMass - beforeLocal).magnitude * 100f;
                Debug.Log($"[Zandatsu] weapon cut {name}: centre of mass {how}, moved {moved:0} cm");
            });
            return $", centre of mass {how}";
        }

        /// <summary>A spell already burning on the weapon took its shape when it started: give it the cut shape.</summary>
        private static int RefreshImbueEffects(Item item)
        {
            int n = 0;
            foreach (Imbue imb in item.imbues)
            {
                ColliderGroup cg = imb ? imb.colliderGroup : null;
                EffectInstance fx = cg && imb.spellCastBase != null ? imb.spellCastBase.imbueEffect : null;
                if (fx == null) continue;
                try
                {
                    if (cg.imbueEffectRenderer) fx.SetRenderer(cg.imbueEffectRenderer, false);
                    if (cg.imbueEmissionRenderer) fx.SetRenderer(cg.imbueEmissionRenderer, true);
                    n++;
                }
                catch (Exception e) { Debug.LogWarning("[Zandatsu] imbue effect: " + e.Message); }
            }
            return n;
        }

        internal static void ReplaceMaterial(MeshData m, Material from, Material to)
        {
            for (int i = 0; i < m.mats.Count; i++) if (ReferenceEquals(m.mats[i], from)) m.mats[i] = to;
        }

        /// <summary>
        /// A mesh of the submeshes of m that take() accepts, vertices mapped by back. slot(): submesh index in the new mesh
        /// (null: one submesh per accepted one, materials returned in mats).
        /// </summary>
        public static Mesh Extract(MeshData m, Func<int, bool> take, Func<int, int> slot, int slots, Matrix4x4 back, string name,
            out int vertexCount)
        {
            return Extract(m, take, slot, slots, back, name, out vertexCount, out _);
        }

        public static Mesh Extract(MeshData m, Func<int, bool> take, Func<int, int> slot, int slots, Matrix4x4 back, string name,
            out int vertexCount, out Material[] mats)
        {
            vertexCount = 0;
            mats = null;
            var chosen = new List<int>();
            for (int s = 0; s < m.subs.Count; s++)
                if (m.subs[s].Count > 0 && take(m.subTag[s])) chosen.Add(s);
            if (chosen.Count == 0) return null;

            var map = new Dictionary<int, int>();
            var v = new List<Vector3>();
            var nn = new List<Vector3>();
            var tt = new List<Vector4>();
            var uv = new List<Vector2>();
            var uv1 = new List<Vector2>();
            var col = new List<Color32>();
            int Map(int i)
            {
                if (map.TryGetValue(i, out int k)) return k;
                k = v.Count;
                map[i] = k;
                v.Add(back.MultiplyPoint3x4(m.v[i]));
                Vector3 nv = back.MultiplyVector(m.n[i]);
                nn.Add(nv.sqrMagnitude > 1e-12f ? nv.normalized : Vector3.up);
                Vector4 t4 = m.t[i];
                Vector3 td = back.MultiplyVector(new Vector3(t4.x, t4.y, t4.z));
                td = td.sqrMagnitude > 1e-12f ? td.normalized : Vector3.right;
                tt.Add(new Vector4(td.x, td.y, td.z, t4.w < 0f ? -1f : 1f));
                uv.Add(m.uv[i]);
                if (m.hasUv1) uv1.Add(m.uv1[i]);
                if (m.hasCol) col.Add(m.col[i]);
                return k;
            }

            int subCount = slot != null ? slots : chosen.Count;
            var tris = new List<int>[subCount];
            for (int k = 0; k < subCount; k++) tris[k] = new List<int>();
            if (slot == null) mats = new Material[subCount];
            for (int c = 0; c < chosen.Count; c++)
            {
                int s = chosen[c];
                int k = slot != null ? slot(m.subTag[s]) : c;
                if (k < 0 || k >= subCount) continue;
                foreach (int i in m.subs[s]) tris[k].Add(Map(i));
                if (mats != null) mats[k] = m.mats[s];
            }
            if (v.Count == 0) return null;
            vertexCount = v.Count;
            var mesh = new Mesh { name = name };
            if (v.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(v);
            mesh.SetNormals(nn);
            mesh.SetTangents(tt);
            mesh.SetUVs(0, uv);
            if (m.hasUv1) mesh.SetUVs(1, uv1);
            if (m.hasCol) mesh.SetColors(col);
            mesh.subMeshCount = subCount;
            for (int k = 0; k < subCount; k++) mesh.SetTriangles(tris[k], k, false);
            mesh.RecalculateBounds();
            return mesh;
        }

        // ---------------- colliders and damage zones ----------------

        /// <summary>
        /// The colliders of the side dist() is negative on, for the half cut off there: copies of the boxes, capsules and
        /// spheres (each on an object of its own, where the original is), cut back to that side. Mesh colliders are left
        /// out (the half's own shape stands in for them).
        /// </summary>
        internal static List<Collider> OffColliders(IEnumerable<Collider> colliders, Func<Vector3, float> dist)
        {
            var copies = new List<Collider>();
            foreach (Collider c in colliders)
            {
                if (!c || !c.enabled || c.isTrigger) continue;
                if (!(c is BoxCollider || c is CapsuleCollider || c is SphereCollider)) continue;
                var go = new GameObject("Zandatsu_DebrisCollider");
                go.transform.SetPositionAndRotation(c.transform.position, c.transform.rotation);
                go.transform.localScale = c.transform.lossyScale;
                Collider copy;
                switch (c)
                {
                    case BoxCollider b:
                        var nb = go.AddComponent<BoxCollider>();
                        nb.center = b.center;
                        nb.size = b.size;
                        copy = nb;
                        break;
                    case CapsuleCollider k:
                        var nk = go.AddComponent<CapsuleCollider>();
                        nk.center = k.center;
                        nk.radius = k.radius;
                        nk.height = k.height;
                        nk.direction = k.direction;
                        copy = nk;
                        break;
                    default:
                        var s = (SphereCollider)c;
                        var ns = go.AddComponent<SphereCollider>();
                        ns.center = s.center;
                        ns.radius = s.radius;
                        copy = ns;
                        break;
                }
                if (ClipCollider(null, copy, w => -dist(w)) < 0) Object.Destroy(go);
                else copies.Add(copy);
            }
            return copies;
        }

        /// <summary>The room a collider takes (m³), for sharing an item's mass between its halves.</summary>
        internal static float Room(Collider c)
        {
            Vector3 s = c.transform.lossyScale;
            float k = Mathf.Abs(s.x * s.y * s.z);
            switch (c)
            {
                case BoxCollider b:
                    return Mathf.Abs(b.size.x * b.size.y * b.size.z) * k;
                case CapsuleCollider cc:
                {
                    float r = cc.radius, h = Mathf.Max(0f, cc.height - 2f * r);
                    return (Mathf.PI * r * r * h + 4f / 3f * Mathf.PI * r * r * r) * k;
                }
                case SphereCollider sc:
                    return 4f / 3f * Mathf.PI * sc.radius * sc.radius * sc.radius * k;
                default:
                    Bounds wb = c.bounds;
                    return wb.size.x * wb.size.y * wb.size.z * 0.5f;
            }
        }

        /// <summary>A collider's middle in the world (its shape's, not its bounds': fresh copies may not have bounds yet).</summary>
        internal static Vector3 Centre(Collider c)
        {
            switch (c)
            {
                case BoxCollider b: return c.transform.TransformPoint(b.center);
                case CapsuleCollider cc: return c.transform.TransformPoint(cc.center);
                case SphereCollider sc: return c.transform.TransformPoint(sc.center);
                default: return c.bounds.center;
            }
        }

        /// <summary>-1: switched off (all beyond the cut), 1: shortened, 0: untouched.</summary>
        internal static int ClipCollider(Collider c, Func<Vector3, float> dist) => ClipCollider(null, c, dist);   // a copy of ours

        private static int ClipCollider(CutState s, Collider c, Func<Vector3, float> dist)
        {
            Transform t = c.transform;
            switch (c)
            {
                case BoxCollider bc:
                {
                    var corners = new Vector3[8];
                    Vector3 h = bc.size * 0.5f;
                    for (int i = 0; i < 8; i++)
                        corners[i] = bc.center + new Vector3((i & 1) != 0 ? h.x : -h.x, (i & 2) != 0 ? h.y : -h.y, (i & 4) != 0 ? h.z : -h.z);
                    var d = new float[8];
                    int keptN = 0;
                    for (int i = 0; i < 8; i++) { d[i] = dist(t.TransformPoint(corners[i])); if (d[i] >= 0f) keptN++; }
                    if (keptN == 8) return 0;
                    Record(s, c);
                    if (keptN == 0) { c.enabled = false; return -1; }
                    var b = new Bounds();
                    bool any = false;
                    void Add(Vector3 q) { if (!any) { b = new Bounds(q, Vector3.zero); any = true; } else b.Encapsulate(q); }
                    for (int i = 0; i < 8; i++)
                    {
                        if (d[i] >= 0f) Add(corners[i]);
                        for (int bit = 1; bit < 8; bit <<= 1)
                        {
                            int j = i | bit;
                            if (j == i || (d[i] >= 0f) == (d[j] >= 0f)) continue;
                            Add(Vector3.Lerp(corners[i], corners[j], d[i] / (d[i] - d[j])));
                        }
                    }
                    bc.center = b.center;
                    bc.size = Vector3.Max(b.size, Vector3.one * 0.002f);
                    return 1;
                }
                case CapsuleCollider cc:
                {
                    Vector3 axis = cc.direction == 0 ? Vector3.right : cc.direction == 1 ? Vector3.up : Vector3.forward;
                    Vector3 e0 = cc.center - axis * cc.height * 0.5f, e1 = cc.center + axis * cc.height * 0.5f;
                    float d0 = dist(t.TransformPoint(e0)), d1 = dist(t.TransformPoint(e1));
                    if (d0 >= 0f && d1 >= 0f) return 0;
                    Record(s, c);
                    if (d0 < 0f && d1 < 0f) { c.enabled = false; return -1; }
                    Vector3 cut = Vector3.Lerp(e0, e1, d0 / (d0 - d1));
                    Vector3 keptEnd = d0 >= 0f ? e0 : e1;
                    cc.center = (keptEnd + cut) * 0.5f;
                    cc.height = Mathf.Max((keptEnd - cut).magnitude, cc.radius * 2f);
                    return 1;
                }
                case SphereCollider sc:
                    if (dist(t.TransformPoint(sc.center)) >= 0f) return 0;
                    Record(s, c);
                    c.enabled = false;
                    return -1;
                default:
                    if (KeptCorners(c, dist) > 0) return 0;
                    Record(s, c);
                    c.enabled = false;
                    return -1;
            }
        }

        /// <summary>How many of a collider's bounds' corners are on the kept side.</summary>
        private static int KeptCorners(Collider c, Func<Vector3, float> dist)
        {
            Bounds wb = c.bounds;
            int keptN = 0;
            for (int i = 0; i < 8; i++)
            {
                Vector3 q = wb.center + Vector3.Scale(wb.extents, new Vector3((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1));
                if (dist(q) >= 0f) keptN++;
            }
            return keptN;
        }

        /// <summary>A collider partly on each side of the cut (its bounds' corners).</summary>
        private static bool Spans(Collider c, Func<Vector3, float> dist)
        {
            int keptN = KeptCorners(c, dist);
            return keptN > 0 && keptN < 8;
        }

        /// <summary>A mesh collider of the item's reaching beyond the cut: the cut-off half gets none of it (see OffColliders).</summary>
        internal static bool MeshReaches(IEnumerable<Collider> colliders, Func<Vector3, float> dist)
        {
            foreach (Collider c in colliders)
                if (c is MeshCollider && c.enabled && !c.isTrigger && KeptCorners(c, dist) < 8) return true;
            return false;
        }

        /// <summary>
        /// Swaps a mesh collider for a convex one of the kept half's mesh, under it (so the game still finds its collider
        /// group and physics material). The original is switched off and comes back on despawn.
        /// </summary>
        private static bool HullCollider(CutState s, MeshCollider c, MeshData kept, Item item, List<Collider> made)
        {
            Matrix4x4 back = c.transform.worldToLocalMatrix * item.transform.localToWorldMatrix;
            Mesh hull = Extract(kept, _ => true, _ => 0, 1, back, "Zandatsu_PropHull", out int vc);
            if (!hull || vc < 4) { if (hull) Object.Destroy(hull); return false; }
            var go = new GameObject("Zandatsu_PropHull") { layer = c.gameObject.layer };
            go.transform.SetParent(c.transform, false);
            var nc = go.AddComponent<MeshCollider>();
            nc.convex = true;
            nc.sharedMesh = hull;
            PhysicMaterial pm = ItemDebris.CleanPhysic(item);
            if (pm) nc.sharedMaterial = pm;
            Record(s, c);
            c.enabled = false;
            s.owned.Add(hull);
            s.owned.Add(go);
            s.hulls.Add(nc);
            made.Add(nc);
            return true;
        }

        /// <summary>A hull of ours made again of what is left of the object (its mesh swapped, the collider kept).</summary>
        private static bool RefitHull(CutState s, MeshCollider hull, MeshData kept, Item item)
        {
            Matrix4x4 back = hull.transform.worldToLocalMatrix * item.transform.localToWorldMatrix;
            Mesh mesh = Extract(kept, _ => true, _ => 0, 1, back, "Zandatsu_PropHull", out int vc);
            if (!mesh || vc < 4) { if (mesh) Object.Destroy(mesh); return false; }
            Mesh old = hull.sharedMesh;
            hull.sharedMesh = mesh;
            s.owned.Add(mesh);
            if (old && s.owned.Remove(old)) Object.Destroy(old);
            return true;
        }

        private static void Record(CutState s, Collider c)
        {
            if (s == null || s.colliders.ContainsKey(c)) return;   // no state: a copy of ours (debris), nothing to restore
            var rec = new ColliderRecord { c = c, enabled = c.enabled };
            if (c is BoxCollider bc) { rec.center = bc.center; rec.size = bc.size; }
            else if (c is CapsuleCollider cc) { rec.center = cc.center; rec.height = cc.height; }
            s.colliders[c] = rec;
        }

        /// <summary>
        /// Slash zone: a segment along the blade (centre, up, penetrationLength) is cut back to the kept side; pierce zone
        /// (a point, the tip): off when the tip is gone. -1: off, 1: shortened, 0: untouched.
        /// </summary>
        private static int ClipDamager(CutState s, CollisionHandler ch, Damager d, Func<Vector3, float> dist)
        {
            Transform t = d.transform;
            if (d.penetrationLength > 0f)
            {
                // In the damager's own space, as the game measures it (local y within ±length/2).
                float half = d.penetrationLength * 0.5f;
                float unit = Mathf.Max(t.TransformVector(Vector3.up).magnitude, 1e-4f);   // metres per local unit along y
                Vector3 a = t.TransformPoint(new Vector3(0f, -half, 0f)), b = t.TransformPoint(new Vector3(0f, half, 0f));
                float da = dist(a), db = dist(b);
                if (da >= 0f && db >= 0f) return 0;
                RecordDamager(s, ch, d);
                if (da < 0f && db < 0f) { Remove(s, ch, d); return -1; }
                Vector3 cut = Vector3.Lerp(a, b, da / (da - db));
                Vector3 keptEnd = da >= 0f ? a : b;
                float len = (keptEnd - cut).magnitude;
                if (len < 0.02f) { Remove(s, ch, d); return -1; }
                // A zone sitting on the item's root (or on an object with colliders, grips, models) can't be moved
                // without moving those: it stays as it was while most of it is left, else it goes.
                if (t == s.item.transform || t.GetComponent<Collider>() || t.GetComponent<Renderer>() || t.GetComponent<Handle>())
                {
                    if (len < half * unit) { Remove(s, ch, d); return -1; }
                    return 0;
                }
                t.position = (keptEnd + cut) * 0.5f;
                d.penetrationLength = len / unit;
                return 1;
            }
            if (d.penetrationDepth > 0f)
            {
                if (dist(t.position) >= 0f) return 0;
                RecordDamager(s, ch, d);
                Remove(s, ch, d);
                return -1;
            }
            return 0;
        }

        private static void RecordDamager(CutState s, CollisionHandler ch, Damager d)
        {
            if (s.damagers.ContainsKey(d)) return;
            s.damagers[d] = new DamagerRecord
            {
                d = d, ch = ch, localPosition = d.transform.localPosition, length = d.penetrationLength, depth = d.penetrationDepth
            };
        }

        /// <summary>
        /// The blade or tip of this zone is gone. While its collider group still has colliders that can hit, the game wants
        /// a damager for it ("Tried to damage with ColliderGroup 'Spike' ... but no damagers were found!" on every hit of a
        /// buckler's shortened spike): the zone stays as a blunt one (no slash length, no pierce depth). With nothing left
        /// to hit with, it goes.
        /// </summary>
        private static void Remove(CutState s, CollisionHandler ch, Damager d)
        {
            bool groupHits = false;
            if (d.colliderGroup && d.colliderGroup.colliders != null)
                foreach (Collider c in d.colliderGroup.colliders)
                    if (c && c.enabled && !c.isTrigger) { groupHits = true; break; }
            if (groupHits)
            {
                d.penetrationLength = 0f;
                d.penetrationDepth = 0f;
                return;
            }
            ch.damagers.Remove(d);
            s.damagers[d].removed = true;
        }

        // ---------------- undo ----------------

        private static void Restore(CutState s)
        {
            Item item = s.item;
            if (item && s.onDespawn != null) item.OnDespawnEvent -= s.onDespawn;
            foreach (KeyValuePair<MeshFilter, Mesh> kv in s.originalMeshes) if (kv.Key && kv.Value) kv.Key.sharedMesh = kv.Value;
            foreach (Renderer r in s.hidden) if (r) r.forceRenderingOff = false;
            foreach (Action a in s.undo)
                try { a(); } catch (Exception e) { Debug.LogWarning("[Zandatsu] undo: " + e.Message); }
            if (s.lodGroup)
            {
                s.lodGroup.enabled = s.lodWasEnabled;
                s.lodGroup.ForceLOD(-1);
            }
            foreach (ColliderRecord rec in s.colliders.Values)
            {
                if (!rec.c) continue;
                rec.c.enabled = rec.enabled;
                if (rec.c is BoxCollider bc) { bc.center = rec.center; bc.size = rec.size; }
                else if (rec.c is CapsuleCollider cc) { cc.center = rec.center; cc.height = rec.height; }
            }
            var resort = new HashSet<CollisionHandler>();
            foreach (DamagerRecord rec in s.damagers.Values)
            {
                if (!rec.d) continue;
                rec.d.transform.localPosition = rec.localPosition;
                rec.d.penetrationLength = rec.length;
                rec.d.penetrationDepth = rec.depth;
                if (rec.removed && rec.ch && !rec.ch.damagers.Contains(rec.d))
                {
                    rec.ch.damagers.Add(rec.d);
                    resort.Add(rec.ch);
                }
            }
            foreach (CollisionHandler ch in resort)
                try { ch.SortDamagers(); } catch { }
            foreach (Handle h in s.handlesOff) if (h) h.SetTouch(true);
            if (s.breakable) s.breakable.SetBreakable(s.breakableWas);
            foreach (KeyValuePair<Renderer, ShadowCastingMode> kv in s.shadows) if (kv.Key) kv.Key.shadowCastingMode = kv.Value;
            if (item && item.physicBody != null && s.mass > 0f) item.physicBody.mass = s.mass;
            if (orgMassField != null)
                foreach (KeyValuePair<CollisionHandler, float> kv in s.ownMass) if (kv.Key) orgMassField.SetValue(kv.Key, kv.Value);
            if (item && s.cuts > 0)
            {
                item.useCustomCenterOfMass = s.useCustomCom;
                item.customCenterOfMass = s.customCom;
                item.customInertiaTensor = s.customInertia;
                try
                {
                    item.ResetCenterOfMass();
                    item.ResetInertiaTensor();
                }
                catch { }
            }
            foreach (MeshRenderer cap in s.caps)
                if (cap && cap.TryGetComponent(out ItemCapLighting lighting)) lighting.Release();
            foreach (Object o in s.owned)
            {
                if (!o) continue;
                if (o is GameObject go) ItemDebris.ReleaseEffects(go.transform);   // a hull of ours was hit: the game's effects on it
                Object.Destroy(o);
            }
            Forget(s);
        }

        private static void Forget(CutState s)
        {
            if (s.item) states.Remove(s.item);
            else
            {
                Item dead = null;
                foreach (KeyValuePair<Item, CutState> kv in states) if (kv.Value == s) { dead = kv.Key; break; }
                if (!ReferenceEquals(dead, null)) states.Remove(dead);
            }
        }

        /// <summary>Level unload / mod off: undo every cut weapon, drop all debris.</summary>
        public static void ClearAll()
        {
            foreach (CutState s in new List<CutState>(states.Values)) Restore(s);
            foreach (Disarmed d in disarmed.Values) if (d.b) d.b.SetBreakable(d.was);
            disarmed.Clear();
            states.Clear();
            lastCut.Clear();
            jobs.Clear();
            ItemDebris.ClearAll();
            MeshReader.ClearAll();
        }
    }

    /// <summary>
    /// The surface an object's cut face takes (Cut Props): the material of the outside the cut goes through most (a barrel
    /// cut through its iron hoops is still wood), mapped flat onto the cut at the same texture scale as outside, its
    /// texture's U direction (the grain of planks) as near as the plane allows. Worker thread: only MeshData and
    /// references, no Unity calls.
    /// </summary>
    public sealed class CapSurface
    {
        public Material material;
        public string info;
        private CapTextures.Kind kind;
        private Vector3 uDir, vDir;
        private float densU = 3f, densV = 3f;   // UV units per metre (the hot steel cut face's 3)

        /// <summary>Our own texture of that kind (CapTextures), or (own null) the object's own surface.</summary>
        public static CapSurface Of(MeshData data, Vector3 point, Vector3 normal, CapTextures.Kind kind, Material own)
        {
            if (!ReferenceEquals(own, null))
            {
                Vector3 u = Vector3.ProjectOnPlane(Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right, normal).normalized;
                if (kind == CapTextures.Kind.Paper && data.v.Count > 0)
                {
                    // The pages' edges run along the cover (the book's thinnest way across is through the pages).
                    Vector3 lo = data.v[0], hi = data.v[0];
                    foreach (Vector3 p in data.v) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
                    Vector3 size = hi - lo;
                    Vector3 thin = size.x <= size.y && size.x <= size.z ? Vector3.right : size.y <= size.z ? Vector3.up : Vector3.forward;
                    Vector3 along = Vector3.Cross(normal, thin);
                    if (along.sqrMagnitude > 0.01f) u = along.normalized;
                }
                return new CapSurface
                {
                    material = own, kind = kind, uDir = u, vDir = Vector3.Cross(normal, u).normalized,
                    densU = 1f / CapTextures.TileSize(kind), densV = 1f / CapTextures.TileSize(kind),
                    info = kind.ToString().ToLowerInvariant()
                };
            }
            return Own(data, point, normal);
        }

        private static CapSurface Own(MeshData data, Vector3 point, Vector3 normal)
        {
            var cut = new Dictionary<Material, float>();
            var all = new Dictionary<Material, float>();
            bool hasUv = data.uv.Count == data.v.Count;
            for (int s = 0; s < data.subs.Count; s++)
            {
                Material m = data.mats[s];
                if (ReferenceEquals(m, null) || data.subTag[s] < 0) continue;
                List<int> tris = data.subs[s];
                for (int k = 0; k + 2 < tris.Count; k += 3)
                {
                    Vector3 p0 = data.v[tris[k]], p1 = data.v[tris[k + 1]], p2 = data.v[tris[k + 2]];
                    float area = Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5f;
                    all[m] = (all.TryGetValue(m, out float a) ? a : 0f) + area;
                    float d0 = Vector3.Dot(p0 - point, normal), d1 = Vector3.Dot(p1 - point, normal), d2 = Vector3.Dot(p2 - point, normal);
                    if (Mathf.Min(d0, Mathf.Min(d1, d2)) < 0f && Mathf.Max(d0, Mathf.Max(d1, d2)) >= 0f)
                        cut[m] = (cut.TryGetValue(m, out float c) ? c : 0f) + area;
                }
            }
            Material best = null;
            float bestArea = 0f;
            foreach (KeyValuePair<Material, float> kv in cut.Count > 0 ? cut : all)
                if (kv.Value > bestArea) { bestArea = kv.Value; best = kv.Key; }
            var cs = new CapSurface { material = best };
            if (ReferenceEquals(best, null)) return cs;

            // Texture scale and the U direction, from that material's triangles near the cut (all of them as a fallback).
            Vector3 grain = Vector3.zero;
            double area0 = 0, lenU = 0, lenV = 0;
            for (int pass = 0; pass < 2 && area0 <= 0; pass++)
                for (int s = 0; s < data.subs.Count && hasUv; s++)
                {
                    if (!ReferenceEquals(data.mats[s], best) || data.subTag[s] < 0) continue;
                    List<int> tris = data.subs[s];
                    for (int k = 0; k + 2 < tris.Count; k += 3)
                    {
                        int i0 = tris[k], i1 = tris[k + 1], i2 = tris[k + 2];
                        Vector3 p0 = data.v[i0], e1 = data.v[i1] - p0, e2 = data.v[i2] - p0;
                        if (pass == 0)
                        {
                            float d0 = Vector3.Dot(p0 - point, normal), d1 = Vector3.Dot(data.v[i1] - point, normal), d2 = Vector3.Dot(data.v[i2] - point, normal);
                            if (Mathf.Abs(d0) > 0.15f && Mathf.Abs(d1) > 0.15f && Mathf.Abs(d2) > 0.15f) continue;   // near the cut only
                        }
                        Vector2 w0 = data.uv[i0], f1 = data.uv[i1] - w0, f2 = data.uv[i2] - w0;
                        float r = f1.x * f2.y - f2.x * f1.y;
                        float area = Vector3.Cross(e1, e2).magnitude * 0.5f;
                        if (Mathf.Abs(r) < 1e-10f || area < 1e-8f) continue;
                        Vector3 dPdu = (e1 * f2.y - e2 * f1.y) / r;   // world change per unit of U
                        Vector3 dPdv = (e2 * f1.x - e1 * f2.x) / r;
                        float lu = dPdu.magnitude, lv = dPdv.magnitude;
                        if (lu < 1e-6f || lv < 1e-6f || float.IsNaN(lu) || float.IsNaN(lv)) continue;
                        if (Vector3.Dot(dPdu, grain) < 0f) dPdu = -dPdu;
                        grain += dPdu / lu * area;
                        area0 += area;
                        lenU += lu * area;
                        lenV += lv * area;
                    }
                }
            if (area0 > 0)
            {
                cs.densU = Mathf.Clamp((float)(area0 / lenU), 0.05f, 200f);
                cs.densV = Mathf.Clamp((float)(area0 / lenV), 0.05f, 200f);
            }
            Vector3 along = Vector3.ProjectOnPlane(grain, normal);
            if (along.sqrMagnitude < 0.09f * grain.sqrMagnitude || along.sqrMagnitude < 1e-12f)
                along = Vector3.ProjectOnPlane(Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right, normal);   // end grain: any way
            cs.uDir = along.normalized;
            cs.vDir = Vector3.Cross(normal, cs.uDir).normalized;
            cs.info = $"{cs.densU:0.#}x{cs.densV:0.#} UV/m{(area0 > 0 ? "" : " (default scale)")}";
            return cs;
        }

        /// <summary>
        /// The cut faces of m (submeshes still of the stand-in material) get this surface; the dark inside of a vessel
        /// modelled closed (insideMark) gets insideMaterial, laid flat at the tiling scale.
        /// </summary>
        public void Apply(MeshData m, Material standIn, Vector3 normal, Material insideMark = null, Material insideMaterial = null)
        {
            for (int s = 0; s < m.subs.Count; s++)
            {
                bool inside = !ReferenceEquals(insideMark, null) && ReferenceEquals(m.mats[s], insideMark);
                if (!inside && !ReferenceEquals(m.mats[s], standIn)) continue;
                m.mats[s] = inside ? insideMaterial : material;
                if (kind == CapTextures.Kind.Wood && !inside)   // the log end has a middle: laid per face
                {
                    LayWood(m, m.subs[s], normal);
                    continue;
                }
                foreach (int i in m.subs[s])
                {
                    Vector3 p = m.v[i];
                    m.uv[i] = new Vector2(Vector3.Dot(p, uDir) * densU, Vector3.Dot(p, vDir) * densV);
                    m.t[i] = Tangent(m, i, uDir, normal);
                }
            }
        }

        /// <summary>Tangent along U; mirrored (w -1) on the half whose cut face looks the other way, for the relief.</summary>
        private static Vector4 Tangent(MeshData m, int i, Vector3 u, Vector3 normal)
        {
            float w = i < m.n.Count && Vector3.Dot(m.n[i], normal) < 0f ? -1f : 1f;
            return new Vector4(u.x, u.y, u.z, w);
        }

        /// <summary>
        /// Wood (a sawn log end, rings round its middle) laid on each cut face by its shape: a round one (a log, a branch, a
        /// leg cut across) gets the rings round its own middle; a long one (a plank, a board) a strip near the log's rim,
        /// where the rings run along it, as on a sawn board's end.
        /// </summary>
        private void LayWood(MeshData m, List<int> tris, Vector3 normal)
        {
            // Faces: triangles joined by shared corners.
            var parent = new Dictionary<int, int>();
            int Find(int x)
            {
                if (!parent.ContainsKey(x)) parent[x] = x;
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }
            for (int k = 0; k + 2 < tris.Count; k += 3)
            {
                int a = Find(tris[k]);
                parent[Find(tris[k + 1])] = a;
                parent[Find(tris[k + 2])] = a;
            }
            var faces = new Dictionary<int, List<int>>();
            foreach (int i in tris)
            {
                int r = Find(i);
                if (!faces.TryGetValue(r, out List<int> list)) faces[r] = list = new List<int>();
                if (!list.Contains(i)) list.Add(i);
            }
            foreach (List<int> face in faces.Values)
            {
                // Its own axes: the long way (principal axis) and across.
                double mx = 0, my = 0;
                foreach (int i in face) { mx += Vector3.Dot(m.v[i], uDir); my += Vector3.Dot(m.v[i], vDir); }
                mx /= face.Count;
                my /= face.Count;
                double sxx = 0, syy = 0, sxy = 0;
                foreach (int i in face)
                {
                    double x = Vector3.Dot(m.v[i], uDir) - mx, y = Vector3.Dot(m.v[i], vDir) - my;
                    sxx += x * x; syy += y * y; sxy += x * y;
                }
                float angle = 0.5f * Mathf.Atan2((float)(2 * sxy), (float)(sxx - syy));
                Vector3 e1 = Mathf.Cos(angle) * uDir + Mathf.Sin(angle) * vDir;
                Vector3 e2 = Vector3.Cross(normal, e1).normalized;
                float minA = float.MaxValue, maxA = float.MinValue, minB = float.MaxValue, maxB = float.MinValue;
                foreach (int i in face)
                {
                    float a = Vector3.Dot(m.v[i], e1), b = Vector3.Dot(m.v[i], e2);
                    minA = Mathf.Min(minA, a); maxA = Mathf.Max(maxA, a); minB = Mathf.Min(minB, b); maxB = Mathf.Max(maxB, b);
                }
                float lenA = maxA - minA, lenB = maxB - minB, cA = (minA + maxA) * 0.5f, cB = (minB + maxB) * 0.5f;
                bool round = lenA <= 1.6f * lenB;
                // Round: the whole log end over it (thin sticks: its middle only, fewer rings). Long: a third of a log's
                // width along it, near the rim.
                float size = round ? Mathf.Max(Mathf.Max(lenA, lenB) * 1.05f, 0.12f) : Mathf.Max(lenA * 2.5f, 0.5f);
                float centreV = round ? 0.5f : 0.86f;
                foreach (int i in face)
                {
                    Vector3 p = m.v[i];
                    m.uv[i] = new Vector2(0.5f + (Vector3.Dot(p, e1) - cA) / size, centreV + (Vector3.Dot(p, e2) - cB) / size);
                    m.t[i] = Tangent(m, i, e1, normal);
                }
            }
        }
    }

    /// <summary>
    /// The cut-off half of a weapon or an object: a plain physics object (no Item), removed by Piece Lifetime / Max Pieces.
    /// With Cut Pieces Again a blade cuts it again (CutAgain), with Grab Pieces a hand picks it up (PieceGrab).
    /// </summary>
    public class ItemDebris : MonoBehaviour
    {
        private static readonly List<ItemDebris> all = new List<ItemDebris>();
        public static int Count => all.Count;
        private readonly PieceFamily family = new PieceFamily();
        private readonly List<Object> owned = new List<Object>();
        private float spawnTime;

        // The cut faces it gets when cut again, as the first cut made them: the object's inside (our texture, a dark inside
        // for a vessel modelled closed) or, null, hot steel.
        private CapTextures.Kind capKind;
        private Material capMaterial, insideMaterial;
        private float hollow;
        private Item cutter;    // the blade that made it: the same swing does not cut it again at once
        private float lastCut = -1f;
        private int cuts;
        public bool held;       // in a hand (PieceGrab): it stays however old
        private struct Ignored { public Collider other; public float at, giveUp; }
        private readonly List<Ignored> ignoreUntil = new List<Ignored>();   // collisions off for a while (back on in Update)
        private const float SeparateSeconds = 0.3f, GiveUpSeconds = 3f;

        /// <summary>The cut faces a later cut of the debris gets (from the item's cut).</summary>
        public struct Faces
        {
            public CapTextures.Kind kind;
            public Material cap, inside;
            public float hollow;
        }

        public Rigidbody Body => GetComponent<Rigidbody>();

        /// <summary>The cut-off half of an item: parts, the item's own colliders beyond the cut (OffColliders), become its own.</summary>
        public static bool Spawn(Item item, MeshData off, Material steel, float mass, Vector3 velocity, List<Collider> ignore,
            Faces faces, Item blade, List<Collider> parts, bool meshReaches)
        {
            // As the weapon's own renderer: reflections and per-renderer properties (a metal buckler half came out black).
            Renderer like = null;
            // Not its shadow proxy (on and listed first on crates): a crate's half copied ShadowsOnly from it and was seen
            // only by its shadow.
            foreach (Renderer r in item.renderers)
                if (r is MeshRenderer && r.enabled && r.shadowCastingMode != ShadowCastingMode.ShadowsOnly) { like = r; break; }
            return Build(item.transform.position, item.transform.rotation, item.transform.lossyScale, like, DebrisPhysic(item), off, steel,
                mass, velocity, ignore, faces, blade, parts, meshReaches, out _);
        }

        private const float MinThickness = 0.03f;   // m: a thinner hull sank through the floor

        /// <summary>
        /// Whether a half (seen: its mesh's bounds in the world) needs its own shape besides the item's colliders it was
        /// given: none given, a mesh collider reached into it (an anvil stand's body is one: its half got only the small
        /// boxes under it, and blades and hands went through the rest), all it got is thinner than 3 cm (a mug's walls: its
        /// half sank through the floor), or together they span less than half of it along some axis (an arrow split along
        /// its length: the shaft's capsule stayed whole on the other side, and 82 cm of it had a 7 cm collider).
        /// </summary>
        private static bool NeedsShape(List<Collider> parts, bool meshReaches, Bounds seen)
        {
            if (meshReaches) return true;
            bool thick = false, any = false;
            var span = new Bounds();
            foreach (Collider p in parts)
            {
                if (!p) continue;
                if (Thickness(p) >= MinThickness) thick = true;
                Bounds b = WorldBounds(p);
                if (any) span.Encapsulate(b);
                else { span = b; any = true; }
            }
            if (!thick) return true;
            Vector3 s = span.size, need = seen.size * 0.5f;
            return s.x < need.x || s.y < need.y || s.z < need.z;
        }

        /// <summary>A collider's bounds in the world worked out from its shape (a fresh copy's own bounds may lag behind).</summary>
        private static Bounds WorldBounds(Collider c)
        {
            Transform t = c.transform;
            var b = new Bounds();
            bool any = false;
            void Add(Vector3 local)
            {
                Vector3 w = t.TransformPoint(local);
                if (any) b.Encapsulate(w);
                else { b = new Bounds(w, Vector3.zero); any = true; }
            }
            switch (c)
            {
                case BoxCollider bc:
                    Vector3 h = bc.size * 0.5f;
                    for (int i = 0; i < 8; i++)
                        Add(bc.center + new Vector3((i & 1) != 0 ? h.x : -h.x, (i & 2) != 0 ? h.y : -h.y, (i & 4) != 0 ? h.z : -h.z));
                    return b;
                case CapsuleCollider cc:
                {
                    Vector3 axis = cc.direction == 0 ? Vector3.right : cc.direction == 1 ? Vector3.up : Vector3.forward;
                    float r = cc.radius, half = Mathf.Max(0f, cc.height * 0.5f - r);
                    foreach (float side in new[] { -1f, 1f })
                        for (int i = 0; i < 8; i++)
                            Add(cc.center + axis * half * side + new Vector3((i & 1) != 0 ? r : -r, (i & 2) != 0 ? r : -r, (i & 4) != 0 ? r : -r));
                    return b;
                }
                case SphereCollider sc:
                    for (int i = 0; i < 8; i++)
                    {
                        float r = sc.radius;
                        Add(sc.center + new Vector3((i & 1) != 0 ? r : -r, (i & 2) != 0 ? r : -r, (i & 4) != 0 ? r : -r));
                    }
                    return b;
                default:
                    return c.bounds;
            }
        }

        /// <summary>A collider's least width in the world.</summary>
        private static float Thickness(Collider c)
        {
            Vector3 s = c.transform.lossyScale;
            s = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
            float least = Mathf.Min(s.x, Mathf.Min(s.y, s.z));
            switch (c)
            {
                case BoxCollider b:
                    Vector3 e = Vector3.Scale(b.size, s);
                    return Mathf.Min(e.x, Mathf.Min(e.y, e.z));
                case CapsuleCollider k: return 2f * k.radius * least;
                case SphereCollider sp: return 2f * sp.radius * least;
                default:
                    Bounds wb = c.bounds;
                    return Mathf.Min(wb.size.x, Mathf.Min(wb.size.y, wb.size.z));
            }
        }

        /// <summary>
        /// The half's own shape: its hull, or, where it is thinner than 3 cm across, a box of its size at least 3 cm thick
        /// (half a blade's 1.5 cm hull sank through the floor now and then).
        /// </summary>
        private static Collider ShapeCollider(GameObject go, Mesh mesh)
        {
            Vector3 s = go.transform.lossyScale;
            var scale = new Vector3(Mathf.Max(Mathf.Abs(s.x), 1e-4f), Mathf.Max(Mathf.Abs(s.y), 1e-4f), Mathf.Max(Mathf.Abs(s.z), 1e-4f));
            Bounds b = mesh.bounds;
            Vector3 size = Vector3.Scale(b.size, scale);
            if (Mathf.Min(size.x, Mathf.Min(size.y, size.z)) < MinThickness)
            {
                var box = go.AddComponent<BoxCollider>();
                box.center = b.center;
                box.size = new Vector3(Mathf.Max(b.size.x, MinThickness / scale.x), Mathf.Max(b.size.y, MinThickness / scale.y),
                    Mathf.Max(b.size.z, MinThickness / scale.z));
                return box;
            }
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.convex = true;
            return mc;
        }

        /// <summary>Its colliders: the item's own it was given (children), and its shape (on itself) where it needs one.</summary>
        public List<Collider> Colliders
        {
            get
            {
                var list = new List<Collider>();
                foreach (Collider c in GetComponentsInChildren<Collider>()) if (c && c.enabled) list.Add(c);
                return list;
            }
        }

        /// <summary>
        /// A debris object of off (in the space of the given pose) with copies of its materials (the source's own instances
        /// are reset or destroyed when it is pooled or removed), steel (the cut faces' hot steel) kept as it is.
        /// </summary>
        private static bool Build(Vector3 position, Quaternion rotation, Vector3 scale, Renderer like, PhysicMaterial physic, MeshData off,
            Material steel, float mass, Vector3 velocity, List<Collider> ignore, Faces faces, Item blade, List<Collider> parts,
            bool meshReaches, out ItemDebris built)
        {
            built = null;
            void DropParts()
            {
                if (parts != null) foreach (Collider p in parts) if (p) Destroy(p.gameObject);
            }
            var copies = new Dictionary<Material, Material>();
            for (int i = 0; i < off.mats.Count; i++)
            {
                Material m = off.mats[i];
                if (ReferenceEquals(m, steel)) continue;
                if (ReferenceEquals(m, null))
                {
                    // An empty slot draws nothing on the weapon (a katana's hidden second mesh, the shape spell effects
                    // spread over): nothing on the debris either. Drawn as steel it showed as a red stretch of blade.
                    off.subs[i].Clear();
                    off.mats[i] = steel;
                    continue;
                }
                if (!m || !m.shader || !m.shader.isSupported)
                {
                    // Destroyed or a shader that cannot draw: steel, not magenta.
                    off.mats[i] = steel;
                    continue;
                }
                // An earlier cut face (hot steel) as a fresh hot steel at the heat it has now: a plain copy left the cooling
                // list and stayed as bright as it was at this moment for as long as the debris lay there.
                if (!copies.TryGetValue(m, out Material c))
                    copies[m] = c = HotSteel.IsHotSteel(m) ? HotSteel.Create(null, HotSteel.HeatOf(m)) ?? new Material(m)
                                                           : new Material(m) { name = m.name.EndsWith(" (Zandatsu)") ? m.name : m.name + " (Zandatsu)" };
                off.mats[i] = c;
            }
            MergeSubmeshes(off);
            Mesh mesh = ItemCut.Extract(off, _ => true, null, 0, Matrix4x4.identity, "Zandatsu_WeaponDebris", out int vc, out Material[] mats);
            if (!mesh || vc < 4)
            {
                foreach (Material c in copies.Values) Destroy(c);
                if (mesh) Destroy(mesh);
                DropParts();
                return false;
            }

            int layer = GameManager.GetLayer(LayerName.DroppedItem);
            var go = new GameObject("Zandatsu_WeaponDebris") { layer = layer };
            go.transform.SetPositionAndRotation(position, rotation);
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = mats;
            if (like)
            {
                mr.reflectionProbeUsage = like.reflectionProbeUsage;
                mr.shadowCastingMode = like.shadowCastingMode == ShadowCastingMode.TwoSided ? ShadowCastingMode.TwoSided : ShadowCastingMode.On;
                var block = new MaterialPropertyBlock();
                like.GetPropertyBlock(block);
                if (!block.isEmpty) mr.SetPropertyBlock(block);
            }

            // The item's own colliders beyond the cut, and its shape where they fall short.
            var mine = new List<Collider>();
            if (parts != null)
                foreach (Collider p in parts)
                {
                    if (!p) continue;
                    p.transform.SetParent(go.transform, true);
                    p.gameObject.layer = layer;
                    mine.Add(p);
                }
            if (NeedsShape(mine, meshReaches, mr.bounds)) mine.Add(ShapeCollider(go, mesh));
            foreach (Collider m in mine) if (physic) m.sharedMaterial = physic;
            // Off only where the other half's colliders are sunk into these (one not cut back, a hull that is not exact):
            // met there, the halves were thrown apart. Elsewhere they only touch along the cut and meet at once: switched
            // off for every pair, a stump's top slid into what was left of it.
            var sunk = new List<Collider>();
            foreach (Collider c in ignore)
            {
                if (!c) continue;
                bool any = false;
                foreach (Collider m in mine)
                    if (Overlap(m, c))
                    {
                        Physics.IgnoreCollision(m, c, true);
                        any = true;
                    }
                if (any) sunk.Add(c);
            }

            var rb = go.AddComponent<Rigidbody>();
            rb.mass = Mathf.Max(0.02f, mass);
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.velocity = velocity;

            var debris = go.AddComponent<ItemDebris>();
            debris.spawnTime = Time.time;
            debris.capKind = faces.kind;
            debris.capMaterial = faces.cap;
            debris.insideMaterial = faces.inside;
            debris.hollow = faces.hollow;
            debris.cutter = blade;
            foreach (Collider c in sunk)
                debris.ignoreUntil.Add(new Ignored { other = c, at = Time.time + SeparateSeconds, giveUp = Time.time + SeparateSeconds + GiveUpSeconds });
            debris.owned.Add(mesh);
            debris.owned.Add(steel);
            foreach (Material c in copies.Values) debris.family.owned.Add(c);
            // Lit where it is (the middle of its bounds), not at its object's origin: that is the whole item's, often at its
            // foot, and under a stump's top lying on the floor it was under the ground, where the light probes are black.
            PieceLighting.Apply(mr, debris.family, null, false);
            all.Add(debris);
            built = debris;
            return true;
        }

        /// <summary>Submeshes of the same material joined: every cut added its cut faces as submeshes of their own.</summary>
        private static void MergeSubmeshes(MeshData m)
        {
            for (int i = 0; i < m.subs.Count; i++)
                for (int j = m.subs.Count - 1; j > i; j--)
                {
                    if (!ReferenceEquals(m.mats[i], m.mats[j])) continue;
                    m.subs[i].AddRange(m.subs[j]);
                    m.subs.RemoveAt(j);
                    m.mats.RemoveAt(j);
                    m.blocks.RemoveAt(j);
                    m.subTag.RemoveAt(j);
                }
        }

        /// <summary>Whether a blade may cut it now (Recut Delay since its last cut; not by the swing that made it).</summary>
        public bool CanBeCut(Item blade, float recutDelay)
        {
            if (lastCut >= 0f && Time.time - lastCut < Mathf.Min(0.15f, recutDelay)) return false;   // any blade, just after a cut
            if (blade == cutter && Time.time - Mathf.Max(spawnTime, lastCut) < recutDelay) return false;   // the same swing
            return true;
        }

        /// <summary>
        /// Cut again: split by the plane; this debris keeps the side held in a hand (keepPoint), else the side of its
        /// centre of mass; the other side falls as new debris. Cut faces as the first cut made them.
        /// </summary>
        public bool CutAgain(Vector3 pointW, Vector3 normalW, Vector3 swing, float fire, Item blade, Vector3? keepPoint)
        {
            float t0 = Time.realtimeSinceStartup;
            MeshFilter mf = GetComponent<MeshFilter>();
            MeshRenderer mr = GetComponent<MeshRenderer>();
            Rigidbody rb = GetComponent<Rigidbody>();
            Mesh mesh = mf ? mf.sharedMesh : null;
            if (!mesh || !mesh.isReadable || !mr || !rb || mesh.vertexCount > 60000) return false;

            var data = new MeshData(mesh.vertexCount + 256);
            data.Append(mesh, Matrix4x4.identity, mr.sharedMaterials, null, null);
            Matrix4x4 w2l = transform.worldToLocalMatrix;
            Vector3 pointL = w2l.MultiplyPoint3x4(pointW);
            Vector3 normalL = transform.localToWorldMatrix.transpose.MultiplyVector(normalW).normalized;
            Material steelKeep = HotSteel.Create(null, HotSteel.PeakFor(fire));
            Material steelOff = HotSteel.Create(null, HotSteel.PeakFor(fire));
            Material insideMark = insideMaterial && steelKeep ? new Material(steelKeep) { name = "Zandatsu_InsideMark" } : null;
            void Drop()
            {
                if (steelKeep) Destroy(steelKeep);
                if (steelOff) Destroy(steelOff);
                if (insideMark) Destroy(insideMark);
            }
            if (!steelKeep || !steelOff) { Drop(); return false; }

            CapSurface surface = capMaterial ? CapSurface.Of(data, pointL, normalL, capKind, capMaterial) : null;
            if (!MeshSlicer.Slice(data, pointL, normalL, steelKeep, out MeshData pos, out MeshData neg, holes: true,
                    hollow: insideMark ? hollow : 0f, inside: insideMark))
            {
                Drop();
                return false;
            }
            string detail = MeshSlicer.LastDetail;
            if (surface != null)
            {
                surface.Apply(pos, steelKeep, normalL, insideMark, insideMaterial);
                surface.Apply(neg, steelKeep, normalL, insideMark, insideMaterial);
            }
            if (insideMark) { Destroy(insideMark); insideMark = null; }
            pos.Sanitize();
            neg.Sanitize();

            Vector3 keepW = keepPoint ?? rb.worldCenterOfMass;
            bool keepPos = Vector3.Dot(w2l.MultiplyPoint3x4(keepW) - pointL, normalL) >= 0f;
            MeshData kept = keepPos ? pos : neg, off = keepPos ? neg : pos;
            ItemCut.ReplaceMaterial(off, steelKeep, steelOff);
            MergeSubmeshes(kept);
            float vKept = Volume(kept), vOff = Volume(off);
            Mesh keptMesh = ItemCut.Extract(kept, _ => true, null, 0, Matrix4x4.identity, "Zandatsu_WeaponDebris", out int keptVerts,
                out Material[] keptMats);
            if (!keptMesh || keptVerts < 4)
            {
                if (keptMesh) Destroy(keptMesh);
                Drop();
                return false;
            }

            // Colliders: the item's own it was given are split as the item's were (copies beyond the cut for the other side,
            // these cut back); a shape of its own is made again for each side that had or needs one.
            Func<Vector3, float> keptDist = w => Vector3.Dot(w - pointW, normalW) * (keepPos ? 1f : -1f);
            List<Collider> before = Colliders;
            PhysicMaterial physic = before.Count > 0 && before[0].sharedMaterial ? Clean(before[0].sharedMaterial) : null;
            var given = new List<Collider>();
            foreach (Collider c in before) if (c.gameObject != gameObject) given.Add(c);
            List<Collider> offParts = ItemCut.OffColliders(given, keptDist);
            float keptRoom = 0f, offRoom = 0f;
            bool hadShape = false;
            foreach (Collider c in before)
            {
                if (c.gameObject == gameObject) { hadShape = true; Destroy(c); continue; }   // its shape: made again below
                if (ItemCut.ClipCollider(c, keptDist) < 0)
                {
                    ReleaseEffects(c.transform);
                    Destroy(c.gameObject);
                }
                else keptRoom += ItemCut.Room(c);
            }
            foreach (Collider c in offParts) offRoom += ItemCut.Room(c);

            // This side.
            Mesh old = mf.sharedMesh;
            mf.sharedMesh = keptMesh;
            mr.sharedMaterials = keptMats;
            var mine = new List<Collider>();
            foreach (Collider c in GetComponentsInChildren<Collider>()) if (c && c.enabled && c.gameObject != gameObject) mine.Add(c);
            if (NeedsShape(mine, hadShape, mr.bounds))
            {
                Collider shape = ShapeCollider(gameObject, keptMesh);
                if (physic) shape.sharedMaterial = physic;
                mine.Add(shape);
            }

            // The other side, at this pose, pushed off this one along the cut. The mass by the colliders' room on each side,
            // else by the halves' volume.
            float total = rb.mass;
            float share = keptRoom > 0f && offRoom > 0f ? Mathf.Clamp(keptRoom / (keptRoom + offRoom), 0.05f, 0.95f)
                        : vKept + vOff > 1e-9f ? Mathf.Clamp(vKept / (vKept + vOff), 0.05f, 0.95f) : 0.5f;
            Vector3 vel = rb.GetPointVelocity(pointW);
            Vector3 push = normalW * (keepPos ? -1f : 1f) * ZandatsuScript.SeparationSpeed * 0.5f + swing * 0.15f;
            var faces = new Faces { kind = capKind, cap = capMaterial, inside = insideMaterial, hollow = hollow };
            bool split = Build(transform.position, transform.rotation, transform.lossyScale, mr, physic, off, steelOff,
                total * (1f - share), vel + push, mine, faces, blade, offParts, hadShape, out ItemDebris other);
            if (!split) Destroy(steelOff);

            owned.Remove(old);
            if (old) Destroy(old);
            owned.Add(keptMesh);
            if (Array.IndexOf(keptMats, steelKeep) >= 0) owned.Add(steelKeep);
            else Destroy(steelKeep);
            rb.mass = Mathf.Max(0.02f, total * share);
            rb.ResetCenterOfMass();
            rb.ResetInertiaTensor();
            lastCut = Time.time;
            cutter = blade;
            cuts++;
            Debug.Log($"[Zandatsu] debris cut ({cuts}): {mesh.vertexCount} verts -> kept {keptVerts}{(split ? "" : ", other side too small")}, " +
                      $"cut face {(capMaterial ? capMaterial.name : "hot steel")}, mass {total:0.00} -> {rb.mass:0.00} + {total - rb.mass:0.00} kg" +
                      $"{(keepPoint.HasValue ? ", the held side kept" : "")}; {(Time.realtimeSinceStartup - t0) * 1000f:0.0} ms ({detail})");
            return true;
        }

        /// <summary>Volume of a closed mesh (the cut faces close it).</summary>
        private static float Volume(MeshData m)
        {
            double v = 0;
            foreach (List<int> tris in m.subs)
                for (int k = 0; k + 2 < tris.Count; k += 3)
                    v += Vector3.Dot(m.v[tris[k]], Vector3.Cross(m.v[tris[k + 1]], m.v[tris[k + 2]]));
            return (float)Math.Abs(v / 6.0);
        }

        /// <summary>The middle of a closed mesh's volume (its points' average when it has none to speak of).</summary>
        internal static Vector3 Centroid(MeshData m)
        {
            double v = 0, x = 0, y = 0, z = 0;
            foreach (List<int> tris in m.subs)
                for (int k = 0; k + 2 < tris.Count; k += 3)
                {
                    Vector3 a = m.v[tris[k]], b = m.v[tris[k + 1]], c = m.v[tris[k + 2]];
                    double t = Vector3.Dot(a, Vector3.Cross(b, c));
                    Vector3 sum = a + b + c;
                    v += t;
                    x += t * sum.x;
                    y += t * sum.y;
                    z += t * sum.z;
                }
            if (Math.Abs(v) > 1e-12)
                return new Vector3((float)(x / (4.0 * v)), (float)(y / (4.0 * v)), (float)(z / (4.0 * v)));
            Vector3 mean = Vector3.zero;
            foreach (Vector3 p in m.v) mean += p;
            return m.v.Count > 0 ? mean / m.v.Count : Vector3.zero;
        }

        /// <summary>
        /// No collisions with these for a moment (a hand letting go, the other half of a cut), so it leaves them without a
        /// kick; back on once apart (Update).
        /// </summary>
        public void IgnoreTemporarily(List<Collider> others, float seconds)
        {
            List<Collider> mine = Colliders;
            foreach (Collider c in others)
            {
                if (!c) continue;
                foreach (Collider m in mine) Physics.IgnoreCollision(m, c, true);
                ignoreUntil.Add(new Ignored { other = c, at = Time.time + seconds, giveUp = Time.time + seconds + GiveUpSeconds });
            }
        }

        /// <summary>Two colliders sunk into each other (more than touching).</summary>
        private static bool Overlap(Collider a, Collider b)
        {
            if (!a || !b || !a.enabled || !b.enabled || !a.gameObject.activeInHierarchy || !b.gameObject.activeInHierarchy) return false;
            try
            {
                Transform ta = a.transform, tb = b.transform;
                return Physics.ComputePenetration(a, ta.position, ta.rotation, b, tb.position, tb.rotation, out _, out float depth) && depth > 0.005f;
            }
            catch { return false; }
        }

        /// <summary>
        /// The weapon's physics material under its catalog name. The game knows a collider's material by the name of
        /// collider.material, which is "<name> (Instance)"; the weapon's colliders already hold such an instance, and handed
        /// on as it is it became "<name> (Instance) (Instance)": the game warned "Target physics material not found" on
        /// every hit on the debris and gave it no hit sounds or effects.
        /// </summary>
        private static readonly Dictionary<string, PhysicMaterial> physics = new Dictionary<string, PhysicMaterial>();

        public static PhysicMaterial CleanPhysic(Item item) => DebrisPhysic(item);

        private static PhysicMaterial DebrisPhysic(Item item)
        {
            foreach (ColliderGroup g in item.colliderGroups)
            {
                if (!g || g.colliders == null) continue;
                foreach (Collider c in g.colliders)
                {
                    PhysicMaterial src = c ? c.sharedMaterial : null;
                    if (!src) continue;
                    return Clean(src);
                }
            }
            return null;
        }

        /// <summary>One per material name for all debris (a new one per debris piled up).</summary>
        private static PhysicMaterial Clean(PhysicMaterial src)
        {
            string name = src.name;
            while (name.EndsWith(" (Instance)", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 11);
            if (physics.TryGetValue(name, out PhysicMaterial shared) && shared) return shared;
            return physics[name] = new PhysicMaterial(name)
            {
                dynamicFriction = src.dynamicFriction,
                staticFriction = src.staticFriction,
                bounciness = src.bounciness,
                frictionCombine = src.frictionCombine,
                bounceCombine = src.bounceCombine
            };
        }

        public static void Update()
        {
            float life = ZandatsuScript.PieceLifetime;
            for (int i = all.Count - 1; i >= 0; i--)
            {
                ItemDebris d = all[i];
                if (!d) { all.RemoveAt(i); continue; }
                if (d.ignoreUntil.Count > 0)
                {
                    // Back on pair by pair once apart. Off for good, a stump's top fell into what was left of it; met while
                    // still sunk into each other, they would be thrown apart: such a pair waits (and stays off if it never parts).
                    List<Collider> mine = null;
                    for (int k = d.ignoreUntil.Count - 1; k >= 0; k--)
                    {
                        Ignored e = d.ignoreUntil[k];
                        if (Time.time < e.at) continue;
                        bool waiting = false;
                        if (e.other)
                            foreach (Collider m in mine ?? (mine = d.Colliders))
                            {
                                if (Overlap(m, e.other))
                                {
                                    if (Time.time < e.giveUp) waiting = true;
                                    continue;
                                }
                                Physics.IgnoreCollision(m, e.other, false);
                            }
                        if (waiting)
                        {
                            e.at = Time.time + 0.2f;
                            d.ignoreUntil[k] = e;
                        }
                        else d.ignoreUntil.RemoveAt(k);
                    }
                }
                if (life > 0f && !d.held && Time.time - d.spawnTime > life) d.Kill();
            }
            // Oldest first, never one in a hand.
            for (int i = 0; i < all.Count && all.Count > ZandatsuScript.MaxPieces; i++)
            {
                if (all[i] && all[i].held) continue;
                if (all[i]) all[i].Kill();
                all.RemoveAt(i);
                i--;
            }
        }

        public static void ClearAll()
        {
            foreach (ItemDebris d in all) if (d) d.Kill();
            all.Clear();
        }

        /// <summary>Always destroyed through here, as SlicedPiece.Kill: the game's effects on it go back to their pools first.</summary>
        private void Kill()
        {
            ReleaseEffects(transform);
            Destroy(gameObject);
        }

        /// <summary>
        /// The game parents its own hit effects (sparks, rubbing, sounds) to the collider hit: those under t go back to their
        /// pools before t is destroyed, or they go with it ("Effect Rubbing_Metal(Clone) has been destroyed but it should
        /// not! Hierarchy: Zandatsu_WeaponDebris", then "pool has been exhausted" on hits after).
        /// </summary>
        internal static void ReleaseEffects(Transform t)
        {
            foreach (Effect e in t.GetComponentsInChildren<Effect>(true))
            {
                if (!e) continue;
                try
                {
                    if (e.containingInstance != null) e.containingInstance.Despawn();
                    else e.Despawn();
                }
                catch { }
                if (e && e.transform.IsChildOf(t)) e.transform.SetParent(null, true);
            }
        }

        private void OnDestroy()
        {
            all.Remove(this);
            // The game reads collider.material on hits, which gives a collider its own instance of the shared material:
            // those copies are ours to free.
            foreach (Collider c in GetComponentsInChildren<Collider>(true))
            {
                PhysicMaterial pm = c ? c.sharedMaterial : null;
                if (pm && !physics.ContainsValue(pm)) Destroy(pm);
            }
            foreach (Object o in owned) if (o) Destroy(o);
            owned.Clear();
            family.TryRelease();
        }
    }
}
