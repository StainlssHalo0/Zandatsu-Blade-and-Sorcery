using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ThunderRoad;
using ThunderRoad.Manikin;
using UnityEngine;

namespace Zandatsu
{
    /// <summary>
    /// Free-angle slicing (Metal Gear Rising style). A clean, fast slash cuts the whole creature along the plane spanned
    /// by the blade axis and the swing direction; the halves become small ragdolls that can be cut again.
    /// </summary>
    public class ZandatsuScript : ThunderScript
    {
        private const string Cat = "Zandatsu";

        [ModOption("Enabled", "Free-angle slicing on/off", order = 0, category = Cat)]
        public static bool Enabled = true;

        [ModOption("Min Swing Speed", "Blade speed (m/s) needed to cut through", nameof(SpeedValues), order = 1, category = Cat, defaultValueIndex = 12)]
        public static float MinSwingSpeed = 7f;

        [ModOption("Clean Cuts Only", "Only edge hits that really bite in cut (no flat-of-blade taps, no slow pushes)", order = 2, category = Cat)]
        public static bool CleanCutsOnly = true;

        [ModOption("Cut Through Armour", "Any edge-first swing fast enough cuts, even where the game lets the blade glance off (metal armour); the cut armour edge glows hot", order = 3, category = Cat)]
        public static bool CutThroughArmour = true;

        [ModOption("Only Player Cuts", "Only your own blades cut (NPC swords do vanilla damage)", order = 4, category = Cat)]
        public static bool PlayerOnly = true;

        [ModOption("Cut At Health", "A living enemy can be cut only once his health is down to this share or lower, counting the blow itself (corpses always). Always = no limit", nameof(HealthValues), order = 5, category = Cat, defaultValueIndex = 0)]
        public static float CutAtHealth = 100f;

        [ModOption("Cut Pieces Again", "Pieces of enemies and the cut-off halves of weapons and objects can be cut again and again", order = 6, category = Cat)]
        public static bool RecutPieces = true;

        [ModOption("Recut Delay", "Seconds before the same blade can cut the same enemy, his fresh pieces or a weapon again. Lower = faster slicing (a fast swing may cut more than once); 0.5 = one swing, one cut", nameof(RecutDelayValues), order = 7, category = Cat, defaultValueIndex = 10)]
        public static float RecutDelay = SameSwingSeconds;

        [ModOption("Cut Weapons", "Fast edge swings also cut enemy weapons and shields (and dropped ones): the half with the grip stays a shorter weapon, the other half falls", order = 8, category = Cat)]
        public static bool CutWeapons = true;

        [ModOption("Cut Props", "Fast edge swings also cut loose objects: barrels, pottery, rocks, logs, benches, stools, carts, baskets and more. The bigger half stays the object, the other half falls; the cut face shows the object's own surface. Breakable ones are cut instead of shattering. Objects the game keeps compressed (crates, chairs, tables, fruit) are read through zandatsu.shaders: without that file they cannot be cut and break as usual", order = 9, category = Cat)]
        public static bool CutProps = true;

        [ModOption("Palm Chop Cuts", "With the Martial Protege skill: an open-palm chop that would take a limb off at the joint cuts along the palm instead, like a blade", order = 10, category = Cat)]
        public static bool PalmChopCuts = true;

        [ModOption("Fire Cuts Burn", "A blade imbued with fire chars the cut: a dark seared cut face with a ragged burnt rim that glows and dies down, smoke and sizzle", order = 11, category = Cat)]
        public static bool FireCutsBurn = true;

        [ModOption("Hot Blade Cuts Easier", "A blade imbued with fire cuts with a slower swing (full charge: half the Min Swing Speed)", order = 12, category = Cat)]
        public static bool HotBladeCutsEasier = true;

        [ModOption("Plain Blade Heats Steel", "A cut through metal (armour, weapons, shields) glows dull red even from a blade without fire; off = the cut metal stays cold grey unless the blade burns", order = 13, category = Cat)]
        public static bool PlainBladeHeatsSteel = true;

        [ModOption("Hybrid Cuts", "The half with the hips stays the enemy (a real corpse with its handles); off = both halves become loose pieces", order = 14, category = Cat)]
        public static bool HybridCuts = true;

        [ModOption("Limb Cuts Not Fatal", "With Hybrid Cuts: an enemy who only loses an arm or a leg lives on until he bleeds out. A cut-off hand drops its weapon; without a leg he goes down and stays down", order = 15, category = Cat)]
        public static bool LimbCutsNotFatal = true;

        [ModOption("Bleed Out Time", "Seconds an enemy lives after losing a limb (each further limb halves what is left)", nameof(BleedValues), order = 16, category = Cat, defaultValueIndex = 3)]
        public static float BleedOutTime = 20f;

        [ModOption("Vanilla Dismemberment", "Keep the game's joint-only dismemberment too: by weapons, tearing, spells and Shard Striker (off: the game splits no bodies at the joints)", order = 17, category = Cat, defaultValueIndex = 0)]
        public static bool VanillaDismemberment = false;

        /// <summary>
        /// The game's own Dismemberment content setting (its gore options; the game's slicing checks it in TrySlice).
        /// Off: Zandatsu cuts no bodies either. Weapons are still cut.
        /// </summary>
        public static bool GoreAllowed
        {
            get
            {
                try { return GameManager.CheckContentActive(BuildSettings.ContentFlag.Dismemberment); }
                catch { return true; }   // settings not loaded yet
            }
        }

        [ModOption("Slow Motion On Cut", "Short slow-motion on every body cut", order = 18, category = Cat, defaultValueIndex = 0)]
        public static bool SlowMotionOnCut = false;

        [ModOption("Cut Sound", "Play the flesh slice sound on every cut (blood stays when off)", order = 19, category = Cat)]
        public static bool CutSound = true;

        [ModOption("Separation Speed", "How hard the halves push apart (m/s)", nameof(SeparationValues), order = 20, category = Cat, defaultValueIndex = 2)]
        public static float SeparationSpeed = 1f;

        [ModOption("Max Pieces", "Oldest pieces are removed above this count", nameof(MaxPieceValues), order = 21, category = Cat, defaultValueIndex = 5)]
        public static int MaxPieces = 60;

        [ModOption("Piece Lifetime", "Seconds before a piece disappears (0 = never)", nameof(LifetimeValues), order = 22, category = Cat, defaultValueIndex = 4)]
        public static float PieceLifetime = 120f;

        [ModOption("Grab Pieces", "Grip near a piece (of an enemy, or a cut-off half of a weapon or object) to pick it up (release grip to drop or throw)", order = 23, category = Cat)]
        public static bool GrabPieces = true;

        [ModOption("Piece Stiffness", "How firmly pieces keep the pose they had when cut (0 = limp like a vanilla corpse)", nameof(StiffnessValues), order = 24, category = Cat, defaultValueIndex = 4)]
        public static float PieceStiffness = 1f;

        [ModOption("Debug Key F9", "F9 (desktop keyboard) cuts the nearest enemy with a random plane", order = 25, category = Cat, defaultValueIndex = 0)]
        public static bool DebugKey = false;

        public static ModOptionFloat[] SpeedValues() => ModOptionFloat.CreateArray(1f, 40f, 0.5f);   // index 12 = 7 m/s default; damage curves top out at 20, fast wrist flicks reach 30+
        public static ModOptionFloat[] SeparationValues() => ModOptionFloat.CreateArray(0f, 5f, 0.5f);
        public static ModOptionInt[] MaxPieceValues() => ModOptionInt.CreateArray(10, 200, 10);
        public static ModOptionFloat[] LifetimeValues() => ModOptionFloat.CreateArray(0f, 600f, 30f);
        public static ModOptionFloat[] StiffnessValues() => ModOptionFloat.CreateArray(0f, 3f, 0.25f);
        public static ModOptionFloat[] BleedValues() => ModOptionFloat.CreateArray(5f, 120f, 5f);   // index 3 = 20 s

        public static ModOptionFloat[] RecutDelayValues()   // 0, 0.05 ... 0.5 s (index 10 = 0.5 default)
        {
            var values = new ModOptionFloat[11];
            for (int i = 0; i <= 10; i++) values[i] = new ModOptionFloat((i * 0.05f).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s", i * 0.05f);
            return values;
        }

        public static ModOptionFloat[] HealthValues()   // Always, 90% ... 10%, Corpses only
        {
            var values = new ModOptionFloat[11];
            values[0] = new ModOptionFloat("Always", 100f);
            for (int i = 1; i <= 9; i++) values[i] = new ModOptionFloat((100 - 10 * i) + "%", 100f - 10f * i);
            values[10] = new ModOptionFloat("Corpses only", 0f);
            return values;
        }

        /// <summary>
        /// Cut At Health: a living creature is cut only once its health is down to the set share of its maximum. damage: what
        /// the blow being judged will take off (the hit event comes before the game applies it), so the blow that brings it
        /// low, or kills it, cuts.
        /// </summary>
        private static bool HealthAllows(Creature c, float damage = 0f) =>
            CutAtHealth >= 100f || c.isKilled || c.maxHealth <= 0f ||
            c.currentHealth - Mathf.Max(0f, damage) <= c.maxHealth * CutAtHealth * 0.01f;
        private struct PendingCut
        {
            public Creature creature;
            public Vector3 point;
            public Vector3 normal;
            public Vector3 bladeVelocity;
            public RagdollPart hitPart;
            public Item item;
            public float fire;   // fire charge on the blade, 0..1
        }

        private readonly List<PendingCut> pending = new List<PendingCut>();
        private readonly HashSet<Creature> claimed = new HashSet<Creature>();
        // A second blade striking a creature that is still being cut (dual wielding) waits here instead of being
        // dropped, and is applied to the creature and its fresh pieces as soon as the first cut is done.
        private readonly Dictionary<Creature, Item> claimedBy = new Dictionary<Creature, Item>();
        private readonly Dictionary<Creature, List<PendingCut>> deferred = new Dictionary<Creature, List<PendingCut>>();
        // Hybrid cuts leave the creature in the world, so the blade still moving through it (or bouncing off) right after
        // a cut started the next one at once: one swing split a body into three or four. One swing, one cut.
        private struct LastCut { public Item item; public float time, touch; }   // touch: last time a lightsaber that cut it touched it
        private readonly Dictionary<Creature, LastCut> lastCutBy = new Dictionary<Creature, LastCut>();
        public const float SameSwingSeconds = 0.5f;
        private readonly Dictionary<Damager, Vector3[]> lastBlade = new Dictionary<Damager, Vector3[]>();
        private readonly Collider[] overlap = new Collider[512];   // room for a pile of pieces around what the blade hits
        private readonly PieceGrab grab = new PieceGrab();

        public override void ScriptEnable()
        {
            base.ScriptEnable();
            EventManager.onCreatureHit += OnCreatureHit;
            EventManager.onLevelUnload += OnLevelUnload;
            EventManager.onCreatureDespawn += OnCreatureDespawn;
            EventManager.onCreatureSpawn += OnCreatureSpawn;
            EventManager.onRagdollSliced += OnRagdollSliced;
            if (Array.Exists(Environment.GetCommandLineArgs(), a => a.Equals("-zandatsutest", StringComparison.OrdinalIgnoreCase)))
                EventManager.onLevelLoad += OnLevelLoadSelfTest;
            Debug.Log("[Zandatsu] enabled - free-angle slicing active (ragdoll pieces)");
            Warmup.Run();
            // The hot steel texture is read from disk on first use: the first weapon cut of a session took 8 ms more.
            Material preload = HotSteel.Create(null);
            if (preload) UnityEngine.Object.Destroy(preload);
            FireCut.Preload();
            CapTextures.Preload();
            LogFilter.Install();
        }

        public override void ScriptDisable()
        {
            EventManager.onCreatureHit -= OnCreatureHit;
            EventManager.onLevelUnload -= OnLevelUnload;
            EventManager.onCreatureDespawn -= OnCreatureDespawn;
            EventManager.onCreatureSpawn -= OnCreatureSpawn;
            EventManager.onRagdollSliced -= OnRagdollSliced;
            EventManager.onLevelLoad -= OnLevelLoadSelfTest;   // subscribed only with -zandatsutest; harmless otherwise
            LogFilter.Uninstall();
            Damager.dismembermentEnabled = GoreAllowed;
            ClearPieces();
            HybridState.ClearAll();
            Survival.ClearAll();
            ItemCut.ClearAll();
            MeshCache.Clear();
            waiting.Clear();
            waitingSince.Clear();
            base.ScriptDisable();
        }

        private void OnLevelUnload(LevelData levelData, LevelData.Mode mode, EventTime eventTime)
        {
            if (eventTime != EventTime.OnStart) return;
            ClearPieces();
            HybridState.ClearAll();
            Survival.ClearAll();
            ItemCut.ClearAll();
            MeshCache.Clear();
            waiting.Clear();
            waitingSince.Clear();
            prewarmQueue.Clear();
            prewarmRenderers.Clear();
        }

        // Creatures are pooled: whatever a hybrid cut changed on one is put back before it can be reused.
        private void OnCreatureDespawn(Creature creature, EventTime eventTime)
        {
            if (eventTime != EventTime.OnStart) return;
            try { HybridState.OnDespawn(creature); }
            catch (Exception e) { Debug.LogError("[Zandatsu] restoring hybrid-cut creature: " + e); }
            try { Survival.OnDespawn(creature); }
            catch (Exception e) { Debug.LogError("[Zandatsu] ending bleed-out: " + e); }
        }

        private void ClearPieces()
        {
            grab.ReleaseAll();
            foreach (SlicedPiece p in SlicedPiece.all.ToArray())
                if (p) p.Kill();
        }

        private void OnCreatureHit(Creature creature, CollisionInstance ci, EventTime eventTime)
        {
            try
            {
                if (!Enabled || creature == null || creature.isPlayer || !GoreAllowed) return;
                Damager damager = ci?.damageStruct.damager;
                if (eventTime == EventTime.OnStart && PalmChopCuts) NotePalmHit(creature, ci);
                if (damager == null || damager.type != Damager.Type.Slash) return;
                if (ItemCut.IsBlasterBolt(damager.collisionHandler ? damager.collisionHandler.item : null)) return;   // a shot, not a blade
                if (StillIn(creature, damager.collisionHandler ? damager.collisionHandler.item : null)) return;
                if (PlayerOnly && !ci.IsDoneByPlayer()) return;
                float speed = ci.impactVelocity.magnitude;
                float fire = FireCut.Charge(ci.sourceColliderGroup);
                if (speed < FireCut.MinSpeed(MinSwingSpeed, fire)) return;
                // Same conditions the game uses for its own slicing: edge at a good angle that actually bit in.
                // Metal armour never lets the blade bite in (its material modifier forbids penetration), so with
                // Cut Through Armour a good edge angle is enough.
                if (CleanCutsOnly && (ci.damageStruct.badAngle ||
                    (!CutThroughArmour && ci.damageStruct.penetration != DamageStruct.Penetration.Hit))) return;
                if (!HealthAllows(creature, eventTime == EventTime.OnStart ? ci.damageStruct.damage : 0f)) return;
                // A blow that cuts into a limb must not kill by its damage (the game applies it after this event): if the cut
                // takes only the limb, the bleed-out decides; if it goes through the body after all, FinishJob kills.
                if (eventTime == EventTime.OnStart && LimbCutsNotFatal && HybridCuts && !creature.isKilled &&
                    ci.damageStruct.hitRagdollPart && Survival.IsLimb(ci.damageStruct.hitRagdollPart.type))
                    ci.damageStruct.damage = Mathf.Min(ci.damageStruct.damage, Survival.BlowDamage);

                Vector3 bladeAxis = damager.transform.up;
                Vector3 normal = Vector3.Cross(bladeAxis, ci.impactVelocity);
                if (normal.sqrMagnitude < 1e-4f * speed * speed) normal = damager.transform.right;

                Claim(new PendingCut
                {
                    creature = creature,
                    point = ci.contactPoint,
                    normal = normal.normalized,
                    bladeVelocity = ci.impactVelocity,
                    hitPart = ci.damageStruct.hitRagdollPart,
                    item = damager.collisionHandler ? damager.collisionHandler.item : null,
                    fire = fire
                });
            }
            catch (Exception e)
            {
                Debug.LogError("[Zandatsu] OnCreatureHit: " + e);
            }
        }

        public override void ScriptUpdate()
        {
            // The game's joint slicing: off while Zandatsu cuts (Options.Apply re-enables it), else as the game's own gore
            // setting says (a value saved when the mod started went stale when the player changed that setting later).
            Damager.dismembermentEnabled = GoreAllowed && (VanillaDismemberment || !Enabled);

            if (DebugKey && Input.GetKeyDown(KeyCode.F9)) DebugCutNearest();
            HotSteel.Update();
            ItemCut.Update();
            FireCut.Update();
            LogFilter.Update();
            HybridState.KeepSeveredStill();
            Survival.Update();
            EndVetoes();
            if (Enabled) Prewarm();
            if (Enabled && GrabPieces) grab.Update();
            else grab.ReleaseAll();
        }

        public override void ScriptLateUpdate()
        {
            if (pending.Count > 0)
            {
                PendingCut[] cuts = pending.ToArray();
                pending.Clear();
                foreach (PendingCut cut in cuts)
                {
                    try { StartCreatureCut(cut); }
                    catch (Exception e)
                    {
                        claimed.Remove(cut.creature);
                        Debug.LogError("[Zandatsu] CutCreature: " + e);
                    }
                }
                pending.AddRange(waiting);
                waiting.Clear();
            }
            if (jobs.Count > 0) FinishJobs();
            // The blade sweep cuts weapons too: without CutWeapons here, Cut Weapons did nothing while Cut Through Armour was off.
            if (Enabled && (CutThroughArmour || CutWeapons || CutProps || (RecutPieces && (SlicedPiece.all.Count > 0 || ItemDebris.Count > 0)))) SweepBlades();
            else lastBlade.Clear();
        }

        // ---------------- cut jobs ----------------
        // Main thread: snapshot skeleton, bake skinned meshes, clone materials.
        // Worker: slice, split into chunks, assign bodies, fit colliders. Main thread again: spawn ragdoll pieces.

        private class CutJob
        {
            public Creature creature;      // creature being cut, or null
            public SlicedPiece source;     // piece being re-cut, or null
            public Skeleton skeleton;
            public Vector3 point, normal;  // world space, at snapshot time
            public Vector3 bladeVelocity;
            public Material fill;
            public Material steel;
            public EffectData blood;
            public EffectData sound;
            public PieceFamily family;
            public int generation;
            public Item cutter;
            public float fire;             // fire charge on the cutting blade (smoke after the cut)
            public Material burn;          // burnt rim of a fire cut (its distance field is made a texture when done)
            public List<Item> dropped;    // weapons the cut creature was holding
            public List<SkinnedMeshRenderer> sources;   // hybrid cut: the creature renderers the snapshot was baked from
            public List<Mesh> sourceMeshes;             // hybrid cut: their meshes at snapshot time
            public Dictionary<Transform, int> meshBoneToBody;   // hybrid cut: mesh bone -> skeleton body
            public string prepDetail = "";                     // where the main-thread prep time went
            public float prepMs;
            public int gcStart = GC.CollectionCount(0);   // garbage collections while the cut ran (they stop every thread)
            public int vertexCount;
            public Task<CutResult> task;
        }

        private readonly List<CutJob> jobs = new List<CutJob>();

        private static readonly int RevealMaskId = Shader.PropertyToID("_RevealMask");

        // Cuts waiting for the game's own slicing of their creature to finish (put back in the queue next frame).
        private readonly List<PendingCut> waiting = new List<PendingCut>();
        private readonly Dictionary<Creature, float> waitingSince = new Dictionary<Creature, float>();
        private const float SliceWaitSeconds = 1.5f;

        private void StartCreatureCut(PendingCut cut)
        {
            Creature creature = cut.creature;
            if (!creature || !creature.gameObject.activeInHierarchy || !GoreAllowed)
            {
                if (creature) waitingSince.Remove(creature);
                return;
            }
            // The game is ripping this creature's mesh at a joint right now (a skill sliced it, often in the same swing):
            // a snapshot taken mid-rip met meshes that changed under it. Wait for the rip to finish; then the creature
            // counts as sliced by the game and is cut into loose pieces.
            if (creature.ragdoll && creature.ragdoll.sliceRunning)
            {
                if (!waitingSince.TryGetValue(creature, out float since))
                {
                    waitingSince[creature] = since = Time.time;
                    Debug.Log($"[Zandatsu] {creature.name}: the game is slicing it at a joint, the cut waits for it to finish");
                }
                if (Time.time - since < SliceWaitSeconds)
                {
                    waiting.Add(cut);
                    return;
                }
            }
            waitingSince.Remove(creature);
            float t0 = Time.realtimeSinceStartup;

            var meshBoneToBody = new Dictionary<Transform, int>();
            Skeleton skel = Skeleton.FromCreature(creature, meshBoneToBody);
            float tSkel = Time.realtimeSinceStartup;
            if (skel.bodies.Count == 0)
            {
                Debug.LogWarning("[Zandatsu] no ragdoll on " + creature.name);
                claimed.Remove(creature);
                return;
            }

            Material fill = null;
            EffectData blood = null;
            EffectData sound = null;
            PhysicMaterial physicMaterial = null;
            List<RagdollPart> parts = creature.ragdoll.parts;
            RagdollPart pref = cut.hitPart ? cut.hitPart : creature.ragdoll.rootPart;
            if (pref)
            {
                fill = pref.sliceFillMaterial;
                blood = pref.data?.sliceChildEffectData ?? pref.data?.sliceParentEffectData;
                sound = pref.data?.sliceParentEffectData;
            }
            foreach (RagdollPart p in parts)
            {
                if (!p) continue;
                if (!fill) fill = p.sliceFillMaterial;
                if (blood == null && p.data != null) blood = p.data.sliceChildEffectData ?? p.data.sliceParentEffectData;
                if (sound == null && p.data != null) sound = p.data.sliceParentEffectData;
            }
            if (sound == null) sound = Catalog.GetData<EffectData>(SliceSoundId, false);
            foreach (SkelBody b in skel.bodies)
                foreach (ShapeSpec s in b.shapes)
                    if (!physicMaterial && s.material) physicMaterial = s.material;

            // Hybrid: the chunk holding the root part stays the creature, so the snapshot remembers where vertices came from.
            int rootBody = -1;
            if (HybridCuts && creature.ragdoll && creature.ragdoll.rootPart && !VanillaSliced(creature))
                for (int i = 0; i < skel.bodies.Count; i++)
                    if (skel.bodies[i].live == creature.ragdoll.rootPart.transform) { rootBody = i; break; }
            CreatureSnapshot snap = SnapCreature(creature, meshBoneToBody, skel, rootBody >= 0, out HashSet<Material> metalSrc,
                                                 out List<SkinnedMeshRenderer> sources, out (int verts, int tris)[] sourceCounts);
            float tBake = Time.realtimeSinceStartup;
            if (snap.vertexCount == 0)
            {
                Debug.LogWarning("[Zandatsu] no visible skinned meshes on " + creature.name);
                claimed.Remove(creature);
                return;
            }

            var family = new PieceFamily { defaultPhysicMaterial = physicMaterial };
            family.HoldAssets(creature);
            foreach (Creature.RendererData rd in creature.renderers)
                if (rd != null && rd.renderer && rd.renderer.enabled) { family.sourceProbeUsage = rd.renderer.lightProbeUsage; break; }
            fill = family.OwnFill(fill);
            OwnMaterials(snap, fill, family, metalSrc);
            Material steel = NewSteel(family, fill, cut.fire);
            var metal = new HashSet<Material>(family.metal);
            FireMaterials(cut.fire, fill, family, out Material capFill, out Material burn);
            family.pendingCuts++;

            var job = new CutJob
            {
                fire = cut.fire,
                burn = burn,
                creature = creature,
                skeleton = skel,
                point = cut.point,
                normal = cut.normal,
                bladeVelocity = cut.bladeVelocity,
                fill = fill,
                steel = steel,
                blood = blood,
                sound = sound,
                family = family,
                generation = 0,
                cutter = cut.item,
                dropped = HeldItems(creature),
                sourceMeshes = sources != null ? sources.ConvertAll(s => s ? s.sharedMesh : null) : null,
                sources = sources,
                meshBoneToBody = meshBoneToBody,
                prepDetail = $"skeleton {(tSkel - t0) * 1000f:0.0}, snapshot {(tBake - tSkel) * 1000f:0.0}, materials {(Time.realtimeSinceStartup - tBake) * 1000f:0.0}",
                vertexCount = snap.vertexCount,
                prepMs = (Time.realtimeSinceStartup - t0) * 1000f
            };
            Vector3 point = cut.point, normal = cut.normal;
            BurnField burnField = FireCut.FieldOf(burn);
            int hitBody = -1;
            if (cut.hitPart)
                for (int i = 0; i < skel.bodies.Count; i++)
                    if (skel.bodies[i].live == cut.hitPart.transform) { hitBody = i; break; }
            job.task = Task.Run(() =>
            {
                // The vertices are skinned here, off the main thread, from the bone matrices frozen above.
                var clock = System.Diagnostics.Stopwatch.StartNew();
                MeshData world = snap.Build();
                float skinMs = (float)clock.Elapsed.TotalMilliseconds;
                CutResult r = CutWork.SliceAndPrepare(world, skel, point, normal, capFill, hitBody, metal, steel,
                                                      rootBody, rootBody >= 0 ? sourceCounts : null, burn, burnField);
                MeshData.Return(world);   // sliced: nothing refers to the snapshot any more
                if (r != null)
                {
                    float tRebuild = (float)clock.Elapsed.TotalMilliseconds;
                    if (r.creature != null) snap.PrepareRebuild(r.creature);
                    float rebuildMs = (float)clock.Elapsed.TotalMilliseconds - tRebuild;
                    r.ms += skinMs + rebuildMs;
                    r.msDetail = $"skin {skinMs:0.0}, " + r.msDetail + (r.creature != null ? $", creature meshes {rebuildMs:0.0}" : "");
                }
                return r;
            });
            jobs.Add(job);
        }

        /// <summary>
        /// A blade burning with fire: this cut's flesh caps get a charred copy of the fill and a burnt rim (capFill = fill,
        /// burn = null otherwise). The family's own fill stays as it was, so a later cut with a cold blade looks normal.
        /// </summary>
        private static void FireMaterials(float fire, Material fill, PieceFamily family, out Material capFill, out Material burn)
        {
            capFill = fill;
            burn = null;
            if (!FireCutsBurn || fire <= 0f || !fill) return;
            capFill = FireCut.Char(fill, fire, family) ?? fill;
            burn = FireCut.Burn(fire, family);
        }

        /// <summary>A cut by a hot blade with Fire Cuts Burn on: the wound is seared, it does not bleed.</summary>
        private static bool Seared(CutJob job) => FireCutsBurn && job.fire > 0f;

        /// <summary>Smoke and sizzle on both new cut faces: the corpse's (hybrid) and the nearest piece's.</summary>
        private static void FireSmoke(CutJob job, CutResult r)
        {
            if (!FireCutsBurn || job.fire <= 0f) return;
            Transform nearest = null;
            float best = float.MaxValue;
            foreach (SlicedPiece p in SlicedPiece.all)
            {
                if (!p || p.family != job.family || p.generation != job.generation) continue;
                foreach (PieceBody b in p.bodies)
                {
                    if (!b) continue;
                    float d = (b.transform.position - job.point).sqrMagnitude;
                    if (d < best) { best = d; nearest = b.transform; }
                }
            }
            FireCut.Smoke(nearest, job.point, job.normal);
            if (r.creature != null && r.creature.capKeptBody >= 0 && r.creature.capKeptBody < job.skeleton.bodies.Count)
                FireCut.Smoke(job.skeleton.bodies[r.creature.capKeptBody].live, r.creature.capCenter, -job.normal);
        }

        /// <summary>A hot steel material for this cut's armour edges (none if the family has no metal armour).</summary>
        private static Material NewSteel(PieceFamily family, Material fill, float fire)
        {
            if (family.metal.Count == 0) return null;
            Material steel = HotSteel.Create(fill, HotSteel.PeakFor(fire));   // white-hot only with fire on the blade
            if (!steel) return null;
            family.owned.Add(steel);
            family.metal.Add(steel);   // its band is steel too when cut again
            return steel;
        }

        // The creature's materials are per-creature instances that get destroyed / reset (blood mask released) when it
        // despawns, which left pieces with missing (magenta) materials. Pieces get their own copies.
        private static void OwnMaterials(CreatureSnapshot snap, Material fill, PieceFamily family, HashSet<Material> metalSrc)
        {
            var map = new Dictionary<Material, Material>();
            foreach (RendererSnap r in snap.renderers)
                for (int i = 0; i < r.mats.Length; i++)
                    r.mats[i] = OwnMaterial(r, i, map, fill, family, metalSrc);
        }

        /// <summary>The piece's own copy of material i of a renderer (a material that cannot be drawn: the fill, or dropped).</summary>
        private static Material OwnMaterial(RendererSnap r, int i, Dictionary<Material, Material> map, Material fill, PieceFamily family,
                                            HashSet<Material> metalSrc)
        {
            {
                Material m = r.mats[i];
                if (!m || !m.shader || !m.shader.isSupported)
                {
                    if (fill) return fill;
                    if (r.dropMat == null) r.dropMat = new bool[r.mats.Length];
                    r.dropMat[i] = true;
                    return m;
                }
                if (!map.TryGetValue(m, out Material copy))
                {
                    copy = new Material(m) { name = m.name + " (Zandatsu)" };
                    family.owned.Add(copy);
                    if (copy.HasProperty(RevealMaskId) && m.GetTexture(RevealMaskId) is RenderTexture mask && mask.IsCreated())
                    {
                        // Only mip 0 is copied and the mips are rebuilt from it: the game's mask keeps stale lower mips
                        // (auto-generated only when it paints), so seen from afar a copied piece turned blood red.
                        RenderTextureDescriptor desc = mask.descriptor;
                        desc.autoGenerateMips = false;
                        var maskCopy = new RenderTexture(desc) { name = "Zandatsu_RevealMask", filterMode = mask.filterMode, wrapMode = mask.wrapMode };
                        maskCopy.Create();
                        Graphics.CopyTexture(mask, 0, 0, maskCopy, 0, 0);
                        if (desc.useMipMap) maskCopy.GenerateMips();
                        copy.SetTexture(RevealMaskId, maskCopy);
                        family.owned.Add(maskCopy);
                    }
                    else if (copy.HasProperty(RevealMaskId) && m.GetTexture(RevealMaskId) is RenderTexture stale)
                    {
                        // A mask that is not created cannot be copied, and the game hands the original back to its pool on
                        // despawn: the piece then showed another creature's blood. No blood is better than someone else's.
                        copy.SetTexture(RevealMaskId, Texture2D.blackTexture);
                        Debug.LogWarning("[Zandatsu] reveal mask not created on " + m.name + " (" + stale.name + "): piece starts clean");
                    }
                    map[m] = copy;
                    if (metalSrc != null && metalSrc.Contains(m)) family.metal.Add(copy);
                }
                return copy;
            }
        }

        private void FinishJobs()
        {
            for (int j = 0; j < jobs.Count; j++)
            {
                CutJob job = jobs[j];
                if (!job.task.IsCompleted) continue;
                jobs.RemoveAt(j--);
                job.family.pendingCuts--;
                try { FinishJob(job); }
                catch (Exception e) { Debug.LogError("[Zandatsu] FinishJob: " + e); }
                finally
                {
                    if (job.source) job.source.busy = false;
                    if (job.creature)
                    {
                        claimed.Remove(job.creature);
                        claimedBy.Remove(job.creature);
                        if (job.cutter) lastCutBy[job.creature] = new LastCut { item = job.cutter, time = Time.time, touch = Time.time };
                        try { RunDeferred(job); }
                        catch (Exception e) { Debug.LogError("[Zandatsu] deferred cut: " + e); }
                    }
                    job.family.TryRelease();
                }
            }
            claimed.RemoveWhere(c => c == null || !c.gameObject.activeInHierarchy);
            if (lastCutBy.Count > 0)
                foreach (Creature c in new List<Creature>(lastCutBy.Keys))
                    if (!c || Time.time - Mathf.Max(lastCutBy[c].time, lastCutBy[c].touch) > SameSwingSeconds) lastCutBy.Remove(c);
            if (deferred.Count > 0)
                foreach (Creature c in new List<Creature>(deferred.Keys))
                    if (!c || !c.gameObject.activeInHierarchy || !claimed.Contains(c)) deferred.Remove(c);
        }

        /// <summary>Queues a cut on a free creature (true); a creature already being cut by another blade gets it deferred.</summary>
        private bool Claim(PendingCut cut)
        {
            Creature c = cut.creature;
            if (cut.item && lastCutBy.TryGetValue(c, out LastCut last) && last.item == cut.item &&
                Time.time - last.time < RecutDelay) return false;
            if (!claimed.Contains(c))
            {
                claimed.Add(c);
                claimedBy[c] = cut.item;
                pending.Add(cut);
                return true;
            }
            // The same blade keeps overlapping the body for a few frames of the same swing: that is not a second cut.
            if (!cut.item || (claimedBy.TryGetValue(c, out Item first) && first == cut.item)) return false;
            if (!deferred.TryGetValue(c, out List<PendingCut> list)) deferred[c] = list = new List<PendingCut>();
            if (list.Count >= 3 || list.Exists(d => d.item == cut.item)) return false;
            list.Add(cut);
            Debug.Log($"[Zandatsu] second blade on {c.name} deferred until the first cut is done");
            return false;
        }

        /// <summary>Cuts that arrived while the creature was busy: on what is left of it (hybrid) and on its new pieces near the hit.</summary>
        private void RunDeferred(CutJob job)
        {
            if (!deferred.TryGetValue(job.creature, out List<PendingCut> cuts)) return;
            deferred.Remove(job.creature);
            foreach (PendingCut cut in cuts)
            {
                if (job.creature && job.creature.gameObject.activeInHierarchy) Claim(cut);
                if (!RecutPieces) continue;
                foreach (SlicedPiece p in SlicedPiece.all.ToArray())
                {
                    if (!p || p.IsDead || p.busy || p.family != job.family) continue;
                    if (p.WorldBounds().SqrDistance(cut.point) > 0.09f) continue;   // only pieces the blade was at (30 cm)
                    CutPiece(p, cut.point, cut.normal, cut.bladeVelocity, cut.item, -1, cut.fire);
                }
            }
        }

        private void FinishJob(CutJob job)
        {
            // Before anything else: it turns the burn field into the rim's texture before the corpse copies the material,
            // and on a failed slice it still drops the field (it was left in FireCut's list).
            FireCut.Ready(job.burn, job.family);
            if (job.task.IsFaulted)
            {
                Debug.LogError("[Zandatsu] slice failed: " + job.task.Exception);
                return;
            }
            CutResult r = job.task.Result;
            float t0 = Time.realtimeSinceStartup;

            if (job.creature && !job.creature.gameObject.activeInHierarchy) return;
            if (!job.creature && (!job.source || job.source.IsDead)) return;
            if (r == null || r.pieces.Count == 0)
            {
                if (job.creature) Debug.Log("[Zandatsu] plane missed " + job.creature.name);
                return;
            }
            // A hybrid cut keeps the creature's own meshes. If the game split them while the cut was being worked out,
            // rebuilding failed on every mesh while the severed parts still froze where they were cut: necks and arms
            // stretched to them. Drop the cut (no pieces either); the game's slicing stands, a next cut makes pieces.
            if (job.creature && r.creature != null)
            {
                string why = null;
                if (VanillaSliced(job.creature)) why = "the game sliced it at a joint meanwhile";
                else if (job.sourceMeshes != null)
                    for (int i = 0; i < job.sources.Count && i < job.sourceMeshes.Count; i++)
                        if (job.sources[i] && job.sources[i].sharedMesh != job.sourceMeshes[i])
                        {
                            why = "its mesh " + job.sources[i].name + " changed meanwhile";
                            break;
                        }
                if (why != null)
                {
                    Debug.Log($"[Zandatsu] hybrid cut of {job.creature.name} dropped: {why}");
                    return;
                }
            }

            var ctx = new PieceContext
            {
                fill = job.fill,
                blood = job.blood,
                sound = job.sound,
                family = job.family,
                generation = job.generation,
                normal = job.normal,
                bladeVelocity = job.bladeVelocity,
                separationSpeed = SeparationSpeed,
                cutter = job.cutter,
                seared = Seared(job)
            };
            int bodies = 0, colliders = 0;
            SlicedPiece first = null;
            foreach (PieceSpec spec in r.pieces)
            {
                SlicedPiece piece = SlicedPiece.Create(spec, job.skeleton, ctx);
                if (!first) first = piece;
                bodies += piece.bodies.Count;
                colliders += piece.colliders.Count;
                if (job.cutter) piece.IgnoreTemporarily(ItemColliders(job.cutter), 0.5f);
                // The dead creature's weapons drop right inside its hand pieces.
                if (job.dropped != null)
                    foreach (Item held in job.dropped)
                        if (held) piece.IgnoreTemporarily(ItemColliders(held), 1.5f);
            }
            if (!ctx.seared && CutSound && !ctx.soundPlayed && ctx.sound != null && first && first.bodies.Count > 0)
                first.AttachBlood(ctx.sound, first.bodies[0].transform, job.point, job.normal);
            float t1 = Time.realtimeSinceStartup;

            string what;
            if (job.creature && r.creature != null)
            {
                // Hybrid: the chunk with the hips stays this creature (a real corpse); only the rest flew off as pieces.
                Creature creature = job.creature;
                bool survives = LimbCutsNotFatal && !creature.isKilled && Survival.OnlyLimbs(job.skeleton, r.creature, r.pieceBodyVerts);
                List<RagdollHand> hurtHands = survives ? Survival.DisarmCutArms(creature, job.skeleton, r.pieceBodyVerts) : null;
                float killMs = 0f;
                if (!survives)
                {
                    // The game's own death (and every other mod's death handlers) runs here: timed apart in the log, it is
                    // most of the first cut's cost in a session.
                    float tk = Time.realtimeSinceStartup;
                    try { if (!creature.isKilled) creature.Kill(); }
                    catch (Exception e) { Debug.LogWarning("[Zandatsu] Kill: " + e.Message); }
                    killMs = (Time.realtimeSinceStartup - tk) * 1000f;
                    try
                    {
                        creature.handLeft?.TryRelease();
                        creature.handRight?.TryRelease();
                    }
                    catch { }
                }
                EffectData capEffect = CutSound && !ctx.soundPlayed && ctx.sound != null ? ctx.sound : ctx.blood;
                if (CutSound && ReferenceEquals(capEffect, ctx.sound)) ctx.soundPlayed = true;
                if (!CutSound && ReferenceEquals(capEffect, ctx.sound)) capEffect = null;
                if (ctx.seared) capEffect = null;   // the wound is seared shut: smoke and sizzle instead of blood
                string kept = Hybrid.Apply(creature, r.creature, job.sources, job.skeleton, job.meshBoneToBody, r.pieceBodyVerts, job.fill, job.steel,
                                           capEffect, job.point, job.normal);
                var creatureColliders = new List<Collider>();
                foreach (RagdollPart part in creature.ragdoll.parts)
                    if (part && part.colliderGroup && part.colliderGroup.colliders != null)
                        foreach (Collider c in part.colliderGroup.colliders) if (c) creatureColliders.Add(c);
                // Parts split between the corpse and a piece keep their whole collider on the corpse: never collide with it
                // (lifting a piece lifted the corpse hooked on it). The rest of the corpse only for a moment: pieces start
                // inside it. The temporary ignore must not cover the permanent ones, or it switches them back on.
                for (int b = 0; b < job.skeleton.bodies.Count; b++)
                {
                    if (r.creature.bodyVerts[b] < 12 || r.pieceBodyVerts[b] == 0 || !job.skeleton.bodies[b].live) continue;
                    RagdollPart cutPart = job.skeleton.bodies[b].live.GetComponent<RagdollPart>();
                    if (cutPart && cutPart.colliderGroup && cutPart.colliderGroup.colliders != null)
                        foreach (Collider c in cutPart.colliderGroup.colliders)
                            if (c && !job.family.neverCollide.Contains(c)) job.family.neverCollide.Add(c);
                    // The fitted replacement is a box or capsule around a wedge: on a slanted cut it still pokes across
                    // the plane into the piece, which then spawned inside it and was flung off spinning.
                    Collider fit = HybridState.Get(creature, job.fill).RefitCollider(cutPart);
                    if (fit && !job.family.neverCollide.Contains(fit)) job.family.neverCollide.Add(fit);
                }
                creatureColliders.RemoveAll(c => job.family.neverCollide.Contains(c));
                foreach (SlicedPiece p in SlicedPiece.all)
                {
                    if (!p || p.family != job.family || p.generation != job.generation) continue;
                    p.IgnoreForever(job.family.neverCollide);
                    p.IgnoreTemporarily(creatureColliders, 1.5f);
                }
                string fate = "";
                if (survives)
                {
                    float left = Survival.LimbLost(creature, HybridState.Get(creature, job.fill), job.skeleton, r.creature, job.normal, BleedOutTime,
                                                   hurtHands, ctx.seared);
                    fate = $"; lives, bleeds out in {left:0} s";
                }
                if (!survives) fate = $"; game death {killMs:0.0} ms";
                what = "hybrid cut " + creature.name + " (assets held " + job.family.HeldAssetCount + "; " + kept + fate + ")";
                if (SlowMotionOnCut) TriggerSlowMo();
            }
            else if (job.creature)
            {
                what = "cut " + job.creature.name + " (assets held " + job.family.HeldAssetCount + ")";
                RemoveCreature(job.creature);
                if (SlowMotionOnCut) TriggerSlowMo();
            }
            else
            {
                what = "re-cut piece gen " + job.generation;
                job.source.Kill();
            }
            FireSmoke(job, r);
            EnforceLimit();

            float smin = float.MaxValue, smax = 0f;
            foreach (SkelBody sb in job.skeleton.bodies)
            {
                smin = Mathf.Min(smin, sb.scale.y);
                smax = Mathf.Max(smax, sb.scale.y);
            }
            Debug.Log($"[Zandatsu] {what}: {job.vertexCount} verts -> {r.posVerts}/{r.negVerts}, {r.pieces.Count} pieces, " +
                      $"{bodies} bodies, {colliders} colliders, cut [{r.cutBodies}]{(job.fire > 0f ? $", fire {job.fire:0.00}{(FireCutsBurn ? " (burnt)" : "")}" : "")}, sound {(!CutSound ? "off" : ctx.sound != null ? ctx.sound.id : "none")}, part scale {smin:0.###}-{smax:0.###}; main prep {job.prepMs:0.0} ms ({job.prepDetail}), worker {r.ms:0.0} ms ({r.msDetail}), " +
                      $"spawn {(t1 - t0) * 1000f:0.0} ms, remove {(Time.realtimeSinceStartup - t1) * 1000f:0.0} ms, gc {GC.CollectionCount(0) - job.gcStart}; " +
                      $"fixed verts: source n{r.fixedSource.normals}/t{r.fixedSource.tangents}/o{r.fixedSource.other}, " +
                      $"pieces n{r.fixedPieces.normals}/t{r.fixedPieces.tangents}/o{r.fixedPieces.other}");
        }

        private static readonly HashSet<string> outfitLogged = new HashSet<string>();

        /// <summary>Renderers of worn items the game counts as metal armour (ItemModuleWardrobe.isMetal).</summary>
        private static HashSet<Renderer> MetalRenderers(Creature creature)
        {
            var set = new HashSet<Renderer>();
            try
            {
                if (!creature.equipment || !creature.manikinLocations) return set;
                foreach (ItemContent content in creature.equipment.GetWornContents())
                {
                    ItemModuleWardrobe module = content?.data?.GetModule<ItemModuleWardrobe>();
                    if (module == null || !module.isMetal) continue;
                    ManikinWardrobeData wd = module.GetWardrobe(creature)?.manikinWardrobeData;
                    if (wd == null || wd.channels == null || wd.layers == null) continue;
                    for (int i = 0; i < wd.channels.Length && i < wd.layers.Length; i++)
                    {
                        ManikinPart part = creature.manikinLocations.GetPartAtLocation(wd.channels[i], wd.layers[i]);
                        if (!part) continue;
                        foreach (Renderer r in part.GetComponentsInChildren<Renderer>(true)) if (r) set.Add(r);
                        Renderer[] own = part.GetRenderers();
                        if (own != null) foreach (Renderer r in own) if (r) set.Add(r);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Zandatsu] metal armour lookup: " + e.Message);
            }
            return set;
        }

        /// <param name="track">Hybrid cut: remember the source renderer of every vertex (sources/sourceCounts).</param>
        private static CreatureSnapshot SnapCreature(Creature creature, Dictionary<Transform, int> meshBoneToBody, Skeleton skel, bool track,
                                                     out HashSet<Material> metalMats, out List<SkinnedMeshRenderer> sources,
                                                     out (int verts, int tris)[] sourceCounts)
        {
            metalMats = new HashSet<Material>();
            sources = new List<SkinnedMeshRenderer>();
            var counts = new List<(int verts, int tris)>();
            HashSet<Renderer> metal = MetalRenderers(creature);
            var hidden = new HashSet<Renderer>();
            if (creature.lodGroup)
            {
                LOD[] lods = creature.lodGroup.GetLODs();
                for (int l = 1; l < lods.Length; l++)
                    foreach (Renderer r in lods[l].renderers)
                        if (r) hidden.Add(r);
                // A renderer shared by several LODs (eyes, teeth) is still LOD0.
                if (lods.Length > 0)
                    foreach (Renderer r in lods[0].renderers)
                        if (r) hidden.Remove(r);
            }

            // The VFX renderer is a coarse invisible hull of the body that status effects (gravity Floating, burning,
            // electrocution) draw on. Baked into pieces it gave oversized flesh caps sticking out of the limbs, and
            // magenta slabs whenever an effect had changed its material.
            // Creatures without such a hull (the chicken) name their visible body as the VFX renderer, so only the name
            // or the invisible VFX shader counts, not GetRendererForVFX() alone.
            bool IsVfx(SkinnedMeshRenderer smr)
            {
                if (smr.name.IndexOf("vfx", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                foreach (Material mat in smr.sharedMaterials)
                    if (mat && mat.shader && mat.shader.name.IndexOf("/VFX/", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                return false;
            }

            // Effects parent their own skinned meshes to the creature: the gravity Floating effect adds "DummyQuad"
            // (4 vertices, URP Lit), which came out as big magenta triangles on the pieces. With a Manikin, only
            // renderers of Manikin parts (the game's own Creature.renderers list) are body or clothing.
            var own = new HashSet<SkinnedMeshRenderer>();
            if (creature.manikinParts && creature.renderers != null)
                foreach (Creature.RendererData rd in creature.renderers)
                    if (rd != null && rd.renderer) own.Add(rd.renderer);
            bool IsExtra(SkinnedMeshRenderer smr) =>
                !Hybrid.IsCreatureCap(smr) &&   // cut faces left on the creature by an earlier hybrid cut
                (smr.sharedMesh.vertexCount < 16 ||
                 (own.Count > 0 && !own.Contains(smr) && !smr.GetComponentInParent<ManikinPart>()));

            // The game's own dismemberment moves the cut-off share of each mesh into a split renderer (RendererData
            // .splitRenderer, under the part's slicedMeshRoot, skinned to "_Split" bones that Skeleton maps to that part).
            // It is not a Manikin renderer, but it is the body: baked at the LOD of its source.
            var splitSource = new Dictionary<SkinnedMeshRenderer, SkinnedMeshRenderer>();
            if (creature.renderers != null)
                foreach (Creature.RendererData rd in creature.renderers)
                    if (rd != null && rd.renderer && rd.splitRenderer) splitSource[rd.splitRenderer] = rd.renderer;

            var all = new List<SkinnedMeshRenderer>();
            var skipped = new List<bool>();
            foreach (SkinnedMeshRenderer smr in creature.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!smr || !smr.sharedMesh) continue;
                bool split = splitSource.TryGetValue(smr, out SkinnedMeshRenderer splitFrom);
                all.Add(smr);
                skipped.Add(!smr.enabled || smr.forceRenderingOff || !smr.gameObject.activeInHierarchy ||
                            hidden.Contains(split ? splitFrom : smr) || smr.GetComponentInParent<Item>() ||
                            IsVfx(smr) || (!split && IsExtra(smr)));
            }

            // Diagnostics once per outfit (creature type + the meshes it wears): which bones skin to which body.
            string typeId = creature.data != null ? creature.data.id : creature.name;
            var outfit = new System.Text.StringBuilder(typeId);
            for (int i = 0; i < all.Count; i++) if (!skipped[i]) outfit.Append('|').Append(all[i].sharedMesh.name);
            bool logMap = outfitLogged.Add(outfit.ToString());

            // Where each body is (part origin + collider centres): vertices whose bones map to no body take the nearest.
            var centres = new List<KeyValuePair<int, Vector3>>();
            for (int b = 0; b < skel.bodies.Count; b++)
            {
                SkelBody sb = skel.bodies[b];
                centres.Add(new KeyValuePair<int, Vector3>(b, sb.pos));
                foreach (ShapeSpec sh in sb.shapes)
                    centres.Add(new KeyValuePair<int, Vector3>(b, sb.pos + sb.rot * (sh.localPos + sh.localRot * Vector3.Scale(sh.scale, sh.center))));
            }
            var bodyNames = new string[skel.bodies.Count];
            for (int b = 0; b < bodyNames.Length; b++) bodyNames[b] = skel.bodies[b].name;
            var snap = new CreatureSnapshot { centres = centres, bodyNames = bodyNames, track = track };
            var unmapped = new List<string>();
            var bonesPerBody = logMap ? new SortedDictionary<int, SortedSet<string>>() : null;
            Mesh baked = null;   // BakeMesh: only to check the hand skinning once per outfit, and for meshes it failed on
            var block = new MaterialPropertyBlock();
            if (logMap)
            {
                snap.logHead = "[Zandatsu] renderers " + typeId + ":";
                var statics = new System.Text.StringBuilder();
                foreach (Renderer r in creature.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(r is MeshRenderer) || r.GetComponentInParent<Item>()) continue;
                    // Static meshes on the creature are not baked: worth knowing if something goes missing on the pieces.
                    statics.Append(" | static ").Append(r.name).Append(r.enabled && r.gameObject.activeInHierarchy ? "" : " (off)");
                }
                snap.logParts.Add(statics);
            }
            for (int ri = 0; ri < all.Count; ri++)
            {
                SkinnedMeshRenderer smr = all[ri];
                bool skip = skipped[ri];
                bool isSplit = splitSource.TryGetValue(smr, out SkinnedMeshRenderer splitOf);
                bool isMetal = metal.Contains(isSplit ? splitOf : smr);
                System.Text.StringBuilder rendererLog = null;
                if (logMap)
                {
                    rendererLog = new System.Text.StringBuilder();
                    snap.logParts.Add(rendererLog);
                    rendererLog.Append(" | ").Append(smr.name).Append(' ').Append(smr.sharedMesh.vertexCount).Append('v');
                    if (skip) rendererLog.Append(IsVfx(smr) ? " (vfx)" : hidden.Contains(smr) ? " (lod)" : smr.GetComponentInParent<Item>() ? " (item)" :
                                                 IsExtra(smr) ? " (extra)" : " (off)");
                    if (isMetal) rendererLog.Append(" (metal)");
                    if (isSplit) rendererLog.Append(" (cut off by the game)");
                    var shaders = new List<string>();
                    foreach (Material mat in smr.sharedMaterials) shaders.Add(mat ? mat.name + "/" + (mat.shader ? mat.shader.name : "-") : "null");
                    rendererLog.Append(" [").Append(string.Join(", ", shaders)).Append(']');
                }
                if (skip) continue;

                Mesh shared = smr.sharedMesh;
                Transform[] bones = smr.bones;
                MeshCache mc = MeshCache.Get(shared);   // null: not readable (usually read already when the creature spawned)
                RendererSnap rs;
                bool useBake;
                if (mc != null)
                {
                    rs = SkinSnap(smr, mc, bones);
                    useBake = !CheckSkin(smr, rs, ref baked);
                }
                else
                {
                    rs = new RendererSnap { rigid = smr.transform.localToWorldMatrix, rootBone = smr.rootBone ? smr.rootBone.name : null };
                    useBake = true;
                }
                rs.log = rendererLog;
                if (rendererLog != null && skinChecked.TryGetValue(shared, out string skinNote)) rendererLog.Append(skinNote);
                if (useBake)
                {
                    if (!baked) baked = new Mesh();
                    smr.BakeMesh(baked, true);
                    if (rs.mesh == null) rs.mesh = MeshCache.Read(baked);   // unreadable source: no weights, follows one body
                    if (rs.mesh == null) continue;
                    rs.bakedV = baked.vertices;
                    rs.bakedN = baked.normals;
                    rs.bakedT = baked.tangents;
                    if (rs.bakedV.Length != rs.mesh.vertexCount) continue;
                }

                // Skin weights re-targeted from mesh bones to ragdoll bodies (a bone counts for the nearest ancestor body).
                int nullBones = 0, lostBones = 0;
                if (bones != null && bones.Length > 0 && rs.mesh.w.Length == rs.mesh.vertexCount)
                {
                    var boneBody = new int[bones.Length];
                    for (int bi = 0; bi < bones.Length; bi++)
                    {
                        boneBody[bi] = -1;
                        if (!bones[bi]) { nullBones++; continue; }
                        Transform cur = bones[bi];
                        for (int depth = 0; cur && depth < 32; depth++, cur = cur.parent)
                        {
                            if (meshBoneToBody.TryGetValue(cur, out int body)) { boneBody[bi] = body; break; }
                        }
                        if (boneBody[bi] < 0)
                        {
                            lostBones++;
                            if (unmapped.Count < 12) unmapped.Add(bones[bi].name);
                        }
                        if (logMap && boneBody[bi] >= 0)
                        {
                            if (!bonesPerBody.TryGetValue(boneBody[bi], out SortedSet<string> names))
                                bonesPerBody[boneBody[bi]] = names = new SortedSet<string>();
                            names.Add(bones[bi].name);
                        }
                    }
                    rs.boneBody = boneBody;
                }
                else
                {
                    // Unskinned or rigid: follow the body its root bone (or the renderer) hangs under, else the nearest.
                    int body = -1;
                    for (Transform cur = smr.rootBone ? smr.rootBone : smr.transform; cur && body < 0; cur = cur.parent)
                        if (meshBoneToBody.TryGetValue(cur, out int b)) body = b;
                    rs.rigidBody = body;
                }
                rs.nullBones = nullBones;
                rs.lostBones = lostBones;

                Material[] mats = smr.sharedMaterials;
                var blocks = new MaterialPropertyBlock[mats.Length];
                smr.GetPropertyBlock(block);
                bool rendererBlock = !block.isEmpty;
                for (int mi = 0; mi < mats.Length; mi++)
                {
                    var perMat = new MaterialPropertyBlock();
                    smr.GetPropertyBlock(perMat, mi);
                    if (!perMat.isEmpty) blocks[mi] = perMat;
                    else if (rendererBlock)
                    {
                        var copy = new MaterialPropertyBlock();
                        smr.GetPropertyBlock(copy);
                        blocks[mi] = copy;
                    }
                    if (isMetal && mats[mi]) metalMats.Add(mats[mi]);
                }
                rs.mats = mats;
                rs.blocks = blocks;
                rs.source = track ? sources.Count : -1;
                sources.Add(smr);
                counts.Add((rs.mesh.vertexCount, rs.mesh.triCount));
                snap.renderers.Add(rs);
                snap.vertexCount += rs.mesh.vertexCount;
            }
            if (baked) UnityEngine.Object.Destroy(baked);
            if (unmapped.Count > 0)
                Debug.LogWarning("[Zandatsu] bones without a body (vertices go to the nearest body): " + string.Join(", ", unmapped));
            if (logMap)
            {
                var sb = new System.Text.StringBuilder("[Zandatsu] bone map " + typeId + ":");
                foreach (KeyValuePair<int, SortedSet<string>> kv in bonesPerBody)
                    sb.Append(" | ").Append(kv.Key < skel.bodies.Count ? skel.bodies[kv.Key].name : kv.Key.ToString())
                      .Append(": ").Append(string.Join(",", kv.Value));
                Debug.Log(sb.ToString());
                Debug.Log("[Zandatsu] parts " + typeId + ":" + skel.partInfo);
                // The renderers line is logged by the worker once the vertices are skinned (CreatureSnapshot.Build).
            }
            sourceCounts = counts.ToArray();
            return snap;
        }

        private const float SkinTolerance = 0.005f;   // m, median distance from BakeMesh above which a mesh is baked
        private static readonly HashSet<Mesh> bakeOnly = new HashSet<Mesh>();
        private static readonly Dictionary<Mesh, string> skinChecked = new Dictionary<Mesh, string>();   // mesh -> log note

        /// <summary>A renderer's skinning frozen now (main thread): bone matrices, blend shapes in use, bones per vertex.</summary>
        private static RendererSnap SkinSnap(SkinnedMeshRenderer smr, MeshCache mc, Transform[] bones)
        {
            var rs = new RendererSnap
            {
                mesh = mc,
                restPose = true,
                rigid = smr.transform.localToWorldMatrix,
                rootBone = smr.rootBone ? smr.rootBone.name : null
            };
            int quality = smr.quality == SkinQuality.Auto ? (int)QualitySettings.skinWeights : (int)smr.quality;
            rs.influences = quality >= 1 && quality < 4 ? quality : 4;
            if (bones != null && bones.Length > 0 && mc.bind.Length == bones.Length)
            {
                rs.skin = new Matrix4x4[bones.Length];
                for (int bi = 0; bi < bones.Length; bi++)
                    rs.skin[bi] = bones[bi] ? bones[bi].localToWorldMatrix * mc.bind[bi] : rs.rigid;
            }
            if (mc.shapeCount > 0)
            {
                var shapes = new List<RendererSnap.Shape>();
                for (int k = 0; k < mc.shapeCount; k++)
                    if (RendererSnap.MakeShape(mc, k, smr.GetBlendShapeWeight(k), out RendererSnap.Shape sh)) shapes.Add(sh);
                if (shapes.Count > 0) rs.shapes = shapes.ToArray();
            }
            return rs;
        }

        /// <summary>
        /// Once per mesh: the hand skinning against the engine's own BakeMesh. A mesh it misses by more than a few
        /// millimetres is baked by the engine from then on. False: bake this one. Meshes a hybrid cut rebuilt are made from
        /// checked ones and are not baked again for it.
        /// </summary>
        private static bool CheckSkin(SkinnedMeshRenderer smr, RendererSnap rs, ref Mesh baked)
        {
            Mesh shared = smr.sharedMesh;
            if (bakeOnly.Contains(shared)) return false;
            if (skinChecked.ContainsKey(shared) || shared.name.EndsWith(Hybrid.RebuiltSuffix)) return true;
            if (!baked) baked = new Mesh();
            smr.BakeMesh(baked, true);
            float median = SkinError(rs, baked, out float max);
            string note = $" skin {median * 1000f:0.#}/{max * 1000f:0.#}mm";
            if (median > SkinTolerance)
            {
                bakeOnly.Add(shared);
                note += " (baked)";
                Debug.LogWarning($"[Zandatsu] skinning of {shared.name} is {median * 1000f:0.#} mm off the engine's: baked from now on");
            }
            skinChecked[shared] = note;
            return median <= SkinTolerance;
        }

        // ---------- prewarm ----------
        // The first cut of each outfit read all its meshes and checked their skinning in the cut's frame (27 ms). That
        // work now happens in the frames after a creature spawns: one step (read one mesh, or check one) per frame.

        private const float PrewarmDelay = 1.5f;   // s after spawning: the Manikin outfit is in place by then
        private readonly List<KeyValuePair<Creature, float>> prewarmQueue = new List<KeyValuePair<Creature, float>>();
        private readonly List<SkinnedMeshRenderer> prewarmRenderers = new List<SkinnedMeshRenderer>();
        private Mesh prewarmBaked;

        // ---------- the game's own dismemberment ----------
        // Several skills slice by the joint without asking Damager.dismembermentEnabled (Inner Flame, the lightning trail,
        // gravity decapitation, Shard Striker shots). On a creature a hybrid cut had rebuilt, the game's split mangled the
        // meshes into long stretched limbs and necks; the other way round, our hybrid rebuild of meshes the game had split
        // did the same. So: no joint slicing on our creatures, and creatures the game sliced are cut into loose pieces.

        private readonly List<RagdollPart> vetoed = new List<RagdollPart>();
        private bool vetoLogged;

        private void OnRagdollSliced(RagdollPart part, EventTime eventTime)
        {
            if (eventTime != EventTime.OnStart || !part || part.isSliced || !part.ragdoll) return;
            Creature creature = part.ragdoll.creature;
            bool palm = PalmChop(creature);
            // Also while our cut of it is still being worked out: a skill slicing it in those few frames (the lightning
            // trail of the very swing that cut) split its meshes under the snapshot. Without Vanilla Dismemberment, on any
            // creature: spells and Shard Striker split at the joints past Damager.dismembermentEnabled, and a head split off
            // that way left every later creature from the pool with only a stump of the head's skin (the game's own error).
            bool ours = HybridState.Has(creature) || claimed.Contains(creature);
            if (!palm && !ours && (VanillaDismemberment || !Enabled)) return;
            // Ragdoll.TrySlice gives up on a part already sliced, checked right after this event; set back next frame.
            part.isSliced = true;
            vetoed.Add(part);
            if (palm || vetoLogged) return;
            vetoLogged = true;
            Debug.Log("[Zandatsu] the game's joint slicing on " + creature.name + " (" + part.name + ") skipped: " +
                      (ours ? "it was cut by Zandatsu" : "Vanilla Dismemberment is off"));
        }

        // Martial Protege (SkillBruteForce): an open-palm chop that passes its checks slices the hit limb off at the joint
        // (Damager.TryPerformSlice, past Damager.dismembermentEnabled) on the creature-hit event's end, then kills. Cutting
        // that body later stretched it. The chop is taken over: the game still decides when it cuts, we cut along the palm.
        private struct PalmHit { public int frame; public RagdollHand hand; public RagdollPart part; public Vector3 point, velocity; }
        private readonly Dictionary<Creature, PalmHit> palmHits = new Dictionary<Creature, PalmHit>();

        /// <summary>A hit by the player's bare hand, remembered for this frame (the skill slices on the same hit's end).</summary>
        private void NotePalmHit(Creature creature, CollisionInstance ci)
        {
            Creature player = Player.local ? Player.local.creature : null;
            Damager damager = ci.damageStruct.damager;
            if (!player || !damager) return;
            RagdollHand hand = player.handRight && damager == player.handRight.bodyDamager ? player.handRight
                             : player.handLeft && damager == player.handLeft.bodyDamager ? player.handLeft : null;
            if (!hand) return;
            if (palmHits.Count > 16) palmHits.Clear();
            palmHits[creature] = new PalmHit
            {
                frame = Time.frameCount,
                hand = hand,
                part = ci.damageStruct.hitRagdollPart,
                point = ci.contactPoint,
                velocity = ci.impactVelocity
            };
        }

        /// <summary>
        /// True when the game's joint slicing now comes from a palm chop of this frame: it is vetoed, and our cut takes its
        /// place. The plane holds the fingers and the swing (the palm's edge leads, as the skill checks).
        /// </summary>
        private bool PalmChop(Creature creature)
        {
            if (!PalmChopCuts || !Enabled || !GoreAllowed || !creature || creature.isPlayer ||
                !palmHits.TryGetValue(creature, out PalmHit hit) || hit.frame != Time.frameCount || !hit.hand) return false;
            Vector3 axis = hit.hand.PointDir;
            Vector3 normal = Vector3.Cross(axis, hit.velocity);
            if (normal.sqrMagnitude < 1e-4f * hit.velocity.sqrMagnitude) normal = Vector3.Cross(axis, hit.hand.ThumbDir);
            if (normal.sqrMagnitude < 1e-8f) return true;
            // The skill may slice a second part for the same chop: one cut (Claim refuses a creature being cut).
            if (Claim(new PendingCut
                {
                    creature = creature,
                    point = hit.point,
                    normal = normal.normalized,
                    bladeVelocity = hit.velocity,
                    hitPart = hit.part,
                    item = null,
                    fire = 0f
                }))
                Debug.Log($"[Zandatsu] palm chop cut {creature.name} at {(hit.part ? hit.part.name : "?")} ({hit.velocity.magnitude:0.0} m/s)");
            return true;
        }

        private void EndVetoes()
        {
            if (vetoed.Count == 0) return;
            foreach (RagdollPart part in vetoed)
                if (part && !HybridState.IsSevered(part.ragdoll ? part.ragdoll.creature : null, part)) part.isSliced = false;
            vetoed.Clear();
        }

        /// <summary>
        /// The game's own dismemberment split this creature's meshes (a part has its split mesh), or is splitting them now:
        /// Ragdoll.SliceCoroutine raises sliceRunning at once but rips the mesh over several frames and only then marks
        /// the bones, and a cut snapshotted in between rebuilt nothing yet froze the severed head where it was cut.
        /// </summary>
        private static bool VanillaSliced(Creature creature)
        {
            if (creature.ragdoll.sliceRunning) return true;
            foreach (RagdollPart part in creature.ragdoll.parts)
                if (part && (part.slicedMeshRoot || part.bone != null && part.bone.meshSplit)) return true;
            return false;
        }

        private void OnCreatureSpawn(Creature creature)
        {
            if (creature && !creature.isPlayer) prewarmQueue.Add(new KeyValuePair<Creature, float>(creature, Time.time + PrewarmDelay));
        }

        private void Prewarm()
        {
            if (prewarmRenderers.Count == 0)
            {
                for (int i = 0; i < prewarmQueue.Count; i++)
                {
                    Creature c = prewarmQueue[i].Key;
                    if (c && Time.time < prewarmQueue[i].Value) continue;
                    prewarmQueue.RemoveAt(i);
                    if (!c || !c.gameObject.activeInHierarchy) return;
                    var lodSkip = new HashSet<Renderer>();
                    if (c.lodGroup)
                    {
                        LOD[] lods = c.lodGroup.GetLODs();
                        for (int l = 1; l < lods.Length; l++) foreach (Renderer r in lods[l].renderers) if (r) lodSkip.Add(r);
                        if (lods.Length > 0) foreach (Renderer r in lods[0].renderers) if (r) lodSkip.Remove(r);
                    }
                    foreach (SkinnedMeshRenderer smr in c.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                        if (smr && smr.enabled && smr.sharedMesh && !lodSkip.Contains(smr) && !smr.GetComponentInParent<Item>())
                            prewarmRenderers.Add(smr);
                    return;
                }
                return;
            }
            SkinnedMeshRenderer next = prewarmRenderers[prewarmRenderers.Count - 1];
            if (!next || !next.sharedMesh || !next.gameObject.activeInHierarchy)
            {
                prewarmRenderers.RemoveAt(prewarmRenderers.Count - 1);
                return;
            }
            Mesh mesh = next.sharedMesh;
            if (!MeshCache.Has(mesh))
            {
                if (MeshCache.Get(mesh) == null) prewarmRenderers.RemoveAt(prewarmRenderers.Count - 1);
                return;   // the check next frame
            }
            prewarmRenderers.RemoveAt(prewarmRenderers.Count - 1);
            if (!skinChecked.ContainsKey(mesh) && !bakeOnly.Contains(mesh))
                CheckSkin(next, SkinSnap(next, MeshCache.Get(mesh), next.bones), ref prewarmBaked);
        }
        private static readonly List<Vector3> bakeVerts = new List<Vector3>();
        private static readonly List<float> skinErrors = new List<float>();

        /// <summary>Median (and max) distance between the hand skinning and BakeMesh, over up to 64 sampled vertices.</summary>
        private static float SkinError(RendererSnap rs, Mesh baked, out float max)
        {
            max = 0f;
            baked.GetVertices(bakeVerts);
            int n = Mathf.Min(bakeVerts.Count, rs.mesh.vertexCount);
            skinErrors.Clear();
            for (int vi = 0, step = Mathf.Max(1, n / 64); vi < n; vi += step)
            {
                rs.Vertex(vi, out Vector3 p, out _, out _);
                float e = (rs.rigid.MultiplyPoint3x4(bakeVerts[vi]) - p).magnitude;
                skinErrors.Add(e);
                if (e > max) max = e;
            }
            if (skinErrors.Count == 0) return 0f;
            skinErrors.Sort();
            return skinErrors[skinErrors.Count / 2];
        }

        private const string SliceSoundId = "SliceFleshParent";

        private static List<Item> HeldItems(Creature creature)
        {
            var items = new List<Item>();
            foreach (RagdollHand h in new[] { creature.handLeft, creature.handRight })
            {
                Item it = h && h.grabbedHandle ? h.grabbedHandle.item : null;
                if (it && !items.Contains(it)) items.Add(it);
            }
            return items;
        }

        private static IEnumerable<Collider> ItemColliders(Item item)
        {
            foreach (ColliderGroup cg in item.colliderGroups)
            {
                if (!cg || cg.colliders == null) continue;
                foreach (Collider c in cg.colliders)
                    if (c) yield return c;
            }
        }

        private static void RemoveCreature(Creature creature)
        {
            try
            {
                if (!creature.isKilled) creature.Kill();
            }
            catch (Exception e) { Debug.LogWarning("[Zandatsu] Kill: " + e.Message); }
            try
            {
                creature.handLeft?.TryRelease();
                creature.handRight?.TryRelease();
            }
            catch { }
            creature.Despawn();
        }

        private static void EnforceLimit()
        {
            // Kill() takes the piece out of the list; a destroyed or already dead entry is dropped by hand. The guard
            // keeps a bookkeeping slip from ever freezing the game again.
            for (int guard = 0; SlicedPiece.all.Count > MaxPieces && guard < 1000; guard++)
            {
                SlicedPiece oldest = SlicedPiece.all[0];
                if (oldest && !oldest.IsDead) oldest.Kill();
                else SlicedPiece.all.RemoveAt(0);
            }
        }

        private static void TriggerSlowMo()
        {
            try
            {
                if (TimeManager.slowMotionState != TimeManager.SlowMotionState.Disabled) return;
                var curve = AnimationCurve.EaseInOut(0f, 0f, 0.1f, 1f);
                TimeManager.SetSlowMotion(true, 0.3f, curve, null, true, false);
                ZandatsuRunner.Instance.RunAfterRealtime(0.6f, () =>
                {
                    if (TimeManager.slowMotionState != TimeManager.SlowMotionState.Disabled)
                        TimeManager.SetSlowMotion(false, 1f, curve, null, true, false);
                });
            }
            catch (Exception e) { Debug.LogWarning("[Zandatsu] slowmo: " + e.Message); }
        }

        // ---------------- re-cutting pieces ----------------

        private void SweepBlades()
        {
            Creature player = Player.local ? Player.local.creature : null;
            if (!player) return;
            var seen = new HashSet<Damager>();
            SweepItem(player.handLeft && player.handLeft.grabbedHandle ? player.handLeft.grabbedHandle.item : null, seen);
            SweepItem(player.handRight && player.handRight.grabbedHandle ? player.handRight.grabbedHandle.item : null, seen);
            if (SweptItems.Count > 0)
            {
                SweptItems.RemoveWhere(i => !i);
                foreach (Item extra in SweptItems) SweepItem(extra, seen);
            }
            if (lastBlade.Count > seen.Count)
            {
                var stale = new List<Damager>();
                foreach (Damager d in lastBlade.Keys) if (!d || !seen.Contains(d)) stale.Add(d);
                foreach (Damager d in stale) lastBlade.Remove(d);
            }
        }

        /// <summary>
        /// Blades not in the player's hands that still sweep like a held one: through armour, weapons and pieces (another
        /// mod's flying or remote blade, the test bench). The player's own hands are swept anyway.
        /// </summary>
        public static readonly HashSet<Item> SweptItems = new HashSet<Item>();

        private void SweepItem(Item item, HashSet<Damager> seen)
        {
            if (!item) return;
            int mask = 1 << GameManager.GetLayer(LayerName.DroppedItem);
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);

            foreach (CollisionHandler ch in item.collisionHandlers)
            {
                if (!ch) continue;
                // The blade's own physics speed. From its drawn positions frame to frame, a blade drawn without
                // interpolation, or moved in a frame without a physics step after frames with none, jumped: 20-57 m/s
                // read from a 4 m/s swing (the test bench), and a flat swing read as edge first.
                Rigidbody body = ch.physicBody != null ? ch.physicBody.rigidBody : null;
                if (body && body.isKinematic) body = null;
                foreach (Damager d in ch.damagers)
                {
                    if (!d || d.type != Damager.Type.Slash || !seen.Add(d)) continue;
                    // The game measures the slash zone in the damager's own space (its local y within ±length/2), so a
                    // scaled damager reaches further: The Outer Rim stretches a lightsaber's blade, damagers included, by
                    // scale, and in metres only part of the blade cut.
                    Vector3 axis = d.transform.up;
                    float half = d.penetrationLength * 0.5f;
                    Vector3 a = d.transform.TransformPoint(new Vector3(0f, -half, 0f));
                    Vector3 b = d.transform.TransformPoint(new Vector3(0f, half, 0f));
                    if ((b - a).sqrMagnitude < 0.01f)   // shorter than 10 cm: at least that much
                    {
                        Vector3 c = (a + b) * 0.5f;
                        a = c - axis * 0.05f;
                        b = c + axis * 0.05f;
                    }
                    Vector3 edge = d.transform.forward;
                    // Where the physics has the blade: drawn with interpolation it lags a physics step behind, and what it
                    // hits is found where the physics has it. A heavy object (a barrel, a crate) stops the blade at its
                    // surface within the step of the blow; the drawn blade got there a step later, already slow, and it was
                    // never cut.
                    if (body && body.interpolation != RigidbodyInterpolation.None)
                    {
                        Transform bt = body.transform;
                        Quaternion turn = body.rotation * Quaternion.Inverse(bt.rotation);
                        a = body.position + turn * (a - bt.position);
                        b = body.position + turn * (b - bt.position);
                        axis = turn * axis;
                        edge = turn * edge;
                    }
                    // A breakable object shatters on the blow itself (its collision, inside the physics step), before this
                    // sweep sees the blade in it: a blade fast enough to cut switches breaking off ahead of it. Only edge
                    // first, as below: a stab or the flat of a blade switched it off and then did not cut (a pot was
                    // neither broken nor cut by them).
                    if (CutProps && body)
                    {
                        Vector3 ahead = body.GetPointVelocity((a + b) * 0.5f);
                        if (ahead.sqrMagnitude >= MinSwingSpeed * MinSwingSpeed * 0.25f &&
                            (!CleanCutsOnly || Mathf.Abs(Vector3.Dot(ahead.normalized, edge)) >= 0.5f))
                            ItemCut.Disarm(a, b, ahead, item);
                    }
                    Vector3 mid = (a + b) * 0.5f;
                    Vector3 bodyVel = body ? body.GetPointVelocity(mid) : Vector3.zero;
                    Vector3 bodyTip = body ? body.GetPointVelocity(b) : Vector3.zero;
                    if (!lastBlade.TryGetValue(d, out Vector3[] prev))
                    {
                        lastBlade[d] = new[] { a, b, bodyVel, bodyTip };   // ends, then the blade's speed (middle, tip)
                        continue;
                    }
                    Vector3 vel = body ? bodyVel : (mid - (prev[0] + prev[1]) * 0.5f) / dt;
                    Vector3 tipVel = body ? bodyTip : (b - prev[1]) / dt;
                    if (body)
                    {
                        // The speed it came in with: a heavy object (a barrel, a crate) stops the blade within the physics step
                        // of the blow, before this sweep, and its speed after the blow was too low to cut what it hit.
                        if (prev[2].sqrMagnitude > vel.sqrMagnitude) vel = prev[2];
                        if (prev[3].sqrMagnitude > tipVel.sqrMagnitude) tipVel = prev[3];
                    }
                    Vector3 pa = prev[0], pb = prev[1];
                    prev[0] = a;
                    prev[1] = b;
                    prev[2] = bodyVel;
                    prev[3] = bodyTip;
                    Vector3 swing = tipVel.sqrMagnitude > vel.sqrMagnitude ? tipVel : vel;
                    float speed = swing.magnitude;
                    float fire = FireCut.Charge(d.colliderGroup);
                    if (speed < FireCut.MinSpeed(MinSwingSpeed, fire)) continue;
                    // Edge first: the swing must go along the edge direction, not the flat of the blade.
                    if (CleanCutsOnly && Mathf.Abs(Vector3.Dot(swing / speed, edge)) < 0.5f) continue;
                    Vector3 normal = Vector3.Cross(axis, swing);
                    if (normal.sqrMagnitude < 1e-6f) continue;
                    normal.Normalize();

                    if (CutWeapons || CutProps) ItemCut.Sweep(pa, pb, a, b, mid, normal, swing, item, fire);
                    if (CutThroughArmour) SweepCreatures(a, b, mid, normal, swing, item, fire);
                    if (!RecutPieces || (SlicedPiece.all.Count == 0 && ItemDebris.Count == 0)) continue;

                    int hits = Physics.OverlapCapsuleNonAlloc(a, b, 0.03f, overlap, mask, QueryTriggerInteraction.Ignore);
                    for (int i = 0; i < hits; i++)
                    {
                        // A cut-off half of a weapon or an object: cut again too (a hand holding it keeps its side).
                        ItemDebris debris = overlap[i] ? overlap[i].GetComponentInParent<ItemDebris>() : null;
                        if (debris)
                        {
                            if (!debris.CanBeCut(item, RecutDelay)) continue;
                            try { debris.CutAgain(mid, normal, swing, fire, item, grab.HoldPoint(debris)); }
                            catch (Exception e) { Debug.LogError("[Zandatsu] debris cut: " + e); }
                            continue;
                        }
                        SlicedPiece piece = overlap[i] ? overlap[i].GetComponentInParent<SlicedPiece>() : null;
                        if (!piece || !piece.CanBeCut(Mathf.Min(0.15f, RecutDelay))) continue;
                        if (piece.cutter == item && Time.time - piece.spawnTime < RecutDelay) continue;   // same swing
                        PieceBody hit = overlap[i].GetComponentInParent<PieceBody>();
                        int hitBody = hit ? piece.bodies.IndexOf(hit) : -1;
                        try { CutPiece(piece, mid, normal, swing, item, hitBody, fire); }
                        catch (Exception e) { Debug.LogError("[Zandatsu] CutPiece: " + e); }
                    }
                }
            }
        }

        private static int creatureMask;

        /// <summary>
        /// A blade swung fast through a creature cuts it even when the game never reports a biting hit: on metal armour
        /// the hit glances off (no penetration, sometimes below the material's damage threshold, then no hit event at all).
        /// The plane contains the blade, so any point on it will do.
        /// </summary>
        private void SweepCreatures(Vector3 a, Vector3 b, Vector3 mid, Vector3 normal, Vector3 swing, Item item, float fire)
        {
            if (!GoreAllowed) return;
            if (creatureMask == 0)
                creatureMask = (1 << GameManager.GetLayer(LayerName.NPC)) | (1 << GameManager.GetLayer(LayerName.Ragdoll)) |
                               (1 << GameManager.GetLayer(LayerName.ItemAndRagdollOnly)) | (1 << GameManager.GetLayer(LayerName.LocomotionOnly));
            int hits = Physics.OverlapCapsuleNonAlloc(a, b, 0.03f, overlap, creatureMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits; i++)
            {
                RagdollPart part = overlap[i] ? overlap[i].GetComponentInParent<RagdollPart>() : null;
                Creature creature = part && part.ragdoll ? part.ragdoll.creature : null;
                if (!creature || creature.isPlayer || !creature.gameObject.activeInHierarchy) continue;
                if (StillIn(creature, item)) continue;
                if (!HealthAllows(creature)) continue;
                if (Claim(new PendingCut
                    {
                        creature = creature,
                        point = mid,
                        normal = normal,
                        bladeVelocity = swing,
                        hitPart = part,
                        item = item,
                        fire = fire
                    }))
                    Debug.Log($"[Zandatsu] blade sweep cut {creature.name} at {part.name} ({swing.magnitude:0.0} m/s)");
            }
        }

        private const float SaberLeaveSeconds = 0.2f;   // no touch for this long: the lightsaber has left the body

        /// <summary>
        /// A lightsaber passes into a body and stays there, and the game reports a hit every frame it touches it: the same
        /// saber cut the same enemy again every half second (each cut rebuilds the body; the game stuttered until the saber
        /// was switched off). It cuts it again only after leaving it. True while this saber is still in what it last cut;
        /// every touch counts, so call it on every hit.
        /// </summary>
        private bool StillIn(Creature c, Item item)
        {
            if (!item || !ItemCut.IsLightsaber(item) || !lastCutBy.TryGetValue(c, out LastCut last) || last.item != item)
                return false;
            bool still = Time.time - last.touch < SaberLeaveSeconds;
            last.touch = Time.time;
            lastCutBy[c] = last;
            return still;
        }

        private void CutPiece(SlicedPiece piece, Vector3 point, Vector3 normal, Vector3 bladeVelocity, Item cutter, int hitBody,
            float fire = 0f)
        {
            if (!piece || piece.IsDead || piece.busy || !GoreAllowed) return;
            float t0 = Time.realtimeSinceStartup;
            Skeleton skel = Skeleton.FromPiece(piece);
            MeshData data = piece.BakeCurrent();
            Material fill = piece.fill;
            Material steel = NewSteel(piece.family, fill, fire);
            var metal = new HashSet<Material>(piece.family.metal);
            FireMaterials(fire, fill, piece.family, out Material capFill, out Material burn);

            piece.busy = true;
            piece.family.pendingCuts++;
            var job = new CutJob
            {
                fire = fire,
                burn = burn,
                source = piece,
                skeleton = skel,
                point = point,
                normal = normal,
                bladeVelocity = bladeVelocity,
                fill = fill,
                blood = piece.blood,
                sound = piece.sound,
                family = piece.family,
                generation = piece.generation + 1,
                cutter = cutter,
                vertexCount = data.VertexCount,
                prepMs = (Time.realtimeSinceStartup - t0) * 1000f
            };
            BurnField burnField = FireCut.FieldOf(burn);
            job.task = Task.Run(() => CutWork.SliceAndPrepare(data, skel, point, normal, capFill, hitBody, metal, steel, -1, null, burn,
                                                                burnField));
            jobs.Add(job);
        }

        // ---------------- debug ----------------

        // Launch with -zandatsutest: spawn an enemy in front of the player, cut it, then re-cut the larger piece.
        private bool selfTestDone;

        private void OnLevelLoadSelfTest(LevelData levelData, LevelData.Mode mode, EventTime eventTime)
        {
            if (eventTime != EventTime.OnEnd || selfTestDone || levelData == null) return;
            if (levelData.id == "MainMenu" || levelData.id == "CharacterSelection") return;
            selfTestDone = true;
            Debug.Log("[Zandatsu] self-test scheduled in level " + levelData.id);
            ZandatsuRunner.Instance.RunAfterRealtime(6f, SelfTestSpawn);
        }

        private void SelfTestSpawn()
        {
            Creature player = Player.local ? Player.local.creature : null;
            Transform head = player ? player.transform : null;
            Vector3 fwd = head ? Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized : Vector3.forward;
            Vector3 pos = (head ? head.position : Vector3.zero) + fwd * 2.5f;
            CreatureData data = Catalog.GetData<CreatureData>("HumanMale");
            if (data == null) { Debug.LogError("[Zandatsu] self-test: no HumanMale data"); return; }
            data.SpawnAsync(pos, Quaternion.LookRotation(-fwd).eulerAngles.y, null, true, null, creature =>
            {
                Debug.Log("[Zandatsu] self-test: spawned " + creature.name);
                ZandatsuRunner.Instance.RunAfterRealtime(3f, () =>
                {
                    Vector3 center = creature.ragdoll.rootPart.transform.position;
                    // Diagonal cut through the chest, like a rising kesagiri.
                    Vector3 normal = (Vector3.up + creature.transform.right * 0.8f).normalized;
                    claimed.Add(creature);
                    pending.Add(new PendingCut { creature = creature, point = center + Vector3.up * 0.25f, normal = normal });
                    ZandatsuRunner.Instance.RunAfterRealtime(2f, SelfTestRecut);
                });
            });
        }

        private void SelfTestRecut()
        {
            SlicedPiece biggest = null;
            foreach (SlicedPiece p in SlicedPiece.all)
                if (p && (!biggest || p.data.VertexCount > biggest.data.VertexCount)) biggest = p;
            if (!biggest) { Debug.LogError("[Zandatsu] self-test: no pieces after cut"); return; }
            CutPiece(biggest, biggest.Center, (Vector3.right + Vector3.forward * 0.3f).normalized, Vector3.zero, null, -1);
            ZandatsuRunner.Instance.RunAfterRealtime(1f, () => Debug.Log($"[Zandatsu] self-test done: {SlicedPiece.all.Count} pieces alive"));
        }

        private void DebugCutNearest()
        {
            Vector3 from = Player.local && Player.local.creature ? Player.local.creature.transform.position : Vector3.zero;
            Creature best = null;
            float bestDist = float.MaxValue;
            foreach (Creature c in Creature.allActive)
            {
                if (!c || c.isPlayer || claimed.Contains(c)) continue;
                float dist = (c.transform.position - from).sqrMagnitude;
                if (dist < bestDist) { bestDist = dist; best = c; }
            }
            if (!best)
            {
                Debug.Log("[Zandatsu] F9: no creature to cut");
                return;
            }
            Vector3 center = best.ragdoll && best.ragdoll.rootPart ? best.ragdoll.rootPart.transform.position : best.transform.position + Vector3.up;
            claimed.Add(best);
            pending.Add(new PendingCut
            {
                creature = best,
                point = center + UnityEngine.Random.insideUnitSphere * 0.15f,
                normal = UnityEngine.Random.onUnitSphere,
                bladeVelocity = Vector3.zero,
                hitPart = null,
                item = null
            });
        }
    }
}
