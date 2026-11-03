using System.Collections.Generic;
using ThunderRoad;
using ThunderRoad.Manikin;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Zandatsu
{
    /// <summary>
    /// Pieces cut from the same creature. Owns the cloned materials / reveal masks (the creature's own are destroyed
    /// or reset when it despawns) and frees them once the last piece and pending cut are gone.
    /// </summary>
    public class PieceFamily
    {
        public readonly List<SlicedPiece> pieces = new List<SlicedPiece>();
        public readonly List<Object> owned = new List<Object>();
        public readonly HashSet<Material> metal = new HashSet<Material>();   // metal armour materials + hot steel
        public PhysicMaterial defaultPhysicMaterial;
        public int pendingCuts;
        // Colliders of creature parts this family was cut out of (hybrid cut): still full size on the corpse, so they
        // reach into the pieces. Pieces of the family never collide with them, or lifting a piece lifts the corpse.
        public readonly List<Collider> neverCollide = new List<Collider>();
        public UnityEngine.Rendering.LightProbeUsage sourceProbeUsage = UnityEngine.Rendering.LightProbeUsage.BlendProbes;   // diagnostics
        public LightProbeVolume lightVolume;            // volume the family's materials are registered with (see PieceLighting)
        private FamilyLightKey lightKey;
        private readonly HashSet<Material> lightRegistered = new HashSet<Material>();

        /// <summary>A family copy of the game's shared slice-fill material, so lighting changes never touch the original.</summary>
        public Material OwnFill(Material fill)
        {
            if (!fill) return null;
            var copy = new Material(fill) { name = fill.name + " (Zandatsu)" };
            owned.Add(copy);
            return copy;
        }

        /// <summary>Registers materials with a LightProbeVolume like LightVolumeReceiver does for creatures (once per material).</summary>
        public void RegisterLighting(LightProbeVolume volume, List<Material> mats)
        {
            if (!volume) return;
            if (!lightKey)
            {
                var go = new GameObject("Zandatsu_LightKey") { hideFlags = HideFlags.HideAndDontSave };
                lightKey = go.AddComponent<FamilyLightKey>();
            }
            lightVolume = volume;
            var fresh = new List<Material>();
            foreach (Material m in mats) if (m && lightRegistered.Add(m)) fresh.Add(m);
            if (fresh.Count > 0) volume.RegisterMaterials(lightKey, fresh.ToArray());
        }
        private readonly Dictionary<PhysicMaterial, PhysicMaterial> physicCopies = new Dictionary<PhysicMaterial, PhysicMaterial>();

        /// <summary>
        /// The game finds a collider's MaterialData by hashing collider.material.name, expecting "&lt;id&gt; (Instance)".
        /// Ragdoll materials are already instances, so copies get the bare id back as their name.
        /// </summary>
        public PhysicMaterial PhysicCopy(PhysicMaterial src)
        {
            if (!src) src = defaultPhysicMaterial;
            if (!src) return null;
            if (physicCopies.TryGetValue(src, out PhysicMaterial copy) && copy) return copy;
            copy = new PhysicMaterial(BaseName(src.name))
            {
                dynamicFriction = src.dynamicFriction,
                staticFriction = src.staticFriction,
                bounciness = src.bounciness,
                frictionCombine = src.frictionCombine,
                bounceCombine = src.bounceCombine
            };
            physicCopies[src] = copy;
            owned.Add(copy);
            return copy;
        }

        public static string BaseName(string name)
        {
            const string suffix = " (Instance)";
            while (name.EndsWith(suffix)) name = name.Substring(0, name.Length - suffix.Length);
            return name;
        }

        // Manikin loads every body part and worn item through Addressables and releases it when the creature despawns:
        // the asset bundle is unloaded and its textures destroyed, while the pieces' material copies still point at
        // them. Unity then hands the freed texture slots to whatever loads next, so armour showed the next enemy's
        // teeth. One more reference per part keeps the bundles loaded until the last piece is gone.
        private readonly List<AsyncOperationHandle<GameObject>> heldAssets = new List<AsyncOperationHandle<GameObject>>();
        private static readonly System.Reflection.FieldInfo partEntriesField =
            typeof(ManikinPartList).GetField("_partEntries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        public int HeldAssetCount => heldAssets.Count;

        public void HoldAssets(Creature creature)
        {
            try
            {
                ManikinPartList list = creature ? creature.manikinParts : null;
                if (!list || partEntriesField == null) return;
                if (!(partEntriesField.GetValue(list) is List<ManikinPartList.PartEntry> entries)) return;
                foreach (ManikinPartList.PartEntry e in entries)
                {
                    if (!e.IsAddressablesLoaded || string.IsNullOrEmpty(e.AssetGUID)) continue;
                    heldAssets.Add(Addressables.LoadAssetAsync<GameObject>(e.AssetGUID));
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[Zandatsu] holding creature assets: " + ex.Message);
            }
        }

        public void TryRelease()
        {
            if (pieces.Count > 0 || pendingCuts > 0) return;
            foreach (AsyncOperationHandle<GameObject> h in heldAssets)
            {
                try { if (h.IsValid()) Addressables.Release(h); }
                catch (System.Exception ex) { Debug.LogWarning("[Zandatsu] releasing creature assets: " + ex.Message); }
            }
            heldAssets.Clear();
            if (lightKey)
            {
                try { if (lightVolume) lightVolume.UnregisterMaterials(lightKey); }
                catch (System.Exception ex) { Debug.LogWarning("[Zandatsu] unregistering piece lighting: " + ex.Message); }
                Object.Destroy(lightKey.gameObject);
            }
            lightKey = null;
            lightVolume = null;
            lightRegistered.Clear();
            foreach (Object o in owned)
            {
                if (!o) continue;
                if (o is RenderTexture rt) rt.Release();
                Object.Destroy(o);
            }
            owned.Clear();
            metal.Clear();
            physicCopies.Clear();
        }
    }

    /// <summary>One rigid body of a piece's little ragdoll.</summary>
    public class PieceBody : MonoBehaviour
    {
        public SlicedPiece piece;
        public string bodyName;
        public int parentIndex = -1;
        public JointSpec joint;
        public List<ShapeSpec> shapes;
        public Rigidbody rb;
    }

    /// <summary>Everything a piece needs from the cut that made it.</summary>
    public class PieceContext
    {
        public Material fill;
        public EffectData blood;
        public EffectData sound;        // slice sound (+ blood); played once per cut on the first capped piece
        public bool soundPlayed;
        public PieceFamily family;
        public int generation;
        public Vector3 normal;          // cut plane normal (world, at snapshot time)
        public Vector3 bladeVelocity;
        public float separationSpeed;
        public bool lightingLogged;     // lighting diagnostics once per cut
        public Item cutter;             // blade that made this cut (no second cut by it in the same swing)
        public bool seared;             // a hot blade (fire, lightsaber) cut: no blood, the fire effect sizzles instead
    }

    /// <summary>
    /// A re-cuttable chunk of a sliced creature: a skinned mesh driven by its own small ragdoll (one rigidbody per
    /// creature part it contains, joined with the creature's own joint limits), so limbs still bend after the cut.
    /// </summary>
    public class SlicedPiece : MonoBehaviour
    {
        public static readonly List<SlicedPiece> all = new List<SlicedPiece>();
        private const float MaxDepenetration = 1.5f;   // m/s
        private const float MaxPieceSpeed = 6f;        // m/s, whole piece
        private const float MaxBodySpread = 1.5f;      // m/s, one body relative to its piece
        private const float MaxAngularSpeed = 6f;      // rad/s
        private const float MaxJointMassRatio = 4f;    // heaviest / lightest body across one joint, as the solver sees it
        private const float ToneSag = 0.35f;           // rad a level limb sags at Piece Stiffness 1
        private const float ToneDamping = 0.6f;        // share of critical damping

        public MeshData data;          // bind pose (world space at the time of the cut), weights = body indices
        public Material fill;
        public EffectData blood;
        public EffectData sound;
        public PieceFamily family;
        public SkinnedMeshRenderer smr;
        public readonly List<PieceBody> bodies = new List<PieceBody>();
        public readonly List<Collider> colliders = new List<Collider>();
        public readonly List<CharacterJoint> joints = new List<CharacterJoint>();
        private int jointChecks;
        public float spawnTime;
        public Item cutter;            // blade that cut this piece out
        public int generation;
        public bool busy;              // a re-cut of this piece is being computed
        private readonly List<EffectInstance> effects = new List<EffectInstance>();
        private readonly List<KeyValuePair<Collider, float>> ignored = new List<KeyValuePair<Collider, float>>();
        private readonly List<PhysicMaterial> physicInstances = new List<PhysicMaterial>();
        private Mesh mesh;
        private float size;
        private bool dead;

        public bool IsDead => dead;

        public float Mass
        {
            get
            {
                float m = 0f;
                foreach (PieceBody b in bodies) if (b && b.rb) m += b.rb.mass;
                return m;
            }
        }

        public Vector3 Center
        {
            get
            {
                Vector3 c = Vector3.zero;
                float m = 0f;
                foreach (PieceBody b in bodies)
                {
                    if (!b || !b.rb) continue;
                    c += b.rb.worldCenterOfMass * b.rb.mass;
                    m += b.rb.mass;
                }
                return m > 0f ? c / m : transform.position;
            }
        }

        public static SlicedPiece Create(PieceSpec spec, Skeleton skel, PieceContext ctx)
        {
            int layer = GameManager.GetLayer(LayerName.DroppedItem);
            int n = spec.bodies.Length;

            var go = new GameObject("Zandatsu_Piece") { layer = layer };
            var piece = go.AddComponent<SlicedPiece>();
            piece.data = spec.data;
            piece.fill = ctx.fill;
            piece.blood = ctx.blood;
            piece.sound = ctx.sound;
            piece.family = ctx.family;
            piece.generation = ctx.generation;
            piece.cutter = ctx.cutter;
            piece.spawnTime = Time.time;
            piece.size = spec.bounds.size.magnitude;

            // Poses: snapshot (= bind pose of the mesh) and current (the source kept moving while the worker ran).
            var snapP = new Vector3[n];
            var snapR = new Quaternion[n];
            var curP = new Vector3[n];
            var curR = new Quaternion[n];
            var vel = new Vector3[n];
            var ang = new Vector3[n];
            var localParent = new int[n];
            var joints = new JointSpec[n];
            for (int k = 0; k < n; k++)
            {
                SkelBody sb = skel.bodies[spec.bodies[k]];
                snapP[k] = sb.pos;
                snapR[k] = sb.rot;
                bool live = sb.live;
                curP[k] = live ? sb.live.position : sb.pos;
                curR[k] = live ? sb.live.rotation : sb.rot;
                vel[k] = sb.liveRb ? sb.liveRb.velocity : sb.vel;
                ang[k] = sb.liveRb ? sb.liveRb.angularVelocity : sb.angVel;
                localParent[k] = -1;
                if (sb.joint != null)
                {
                    localParent[k] = System.Array.IndexOf(spec.bodies, sb.parent);
                    joints[k] = sb.joint;
                }
            }

            // Every body must hang off the piece's tree, or it falls on its own and drags its vertices along
            // (e.g. both thighs of a skirt piece without the pelvis). Missing links are bridged with stiff joints.
            int main = 0;
            {
                var vc = new int[n];
                foreach (BoneWeight bw in spec.data.w) vc[bw.boneIndex0]++;
                for (int k = 1; k < n; k++) if (vc[k] > vc[main]) main = k;
            }
            // First the nearest ancestor that is in the piece (acyclic: follows the creature hierarchy)...
            for (int k = 0; k < n; k++)
            {
                if (localParent[k] >= 0) continue;
                int a = skel.bodies[spec.bodies[k]].parent, guard = 0;
                while (a >= 0 && guard++ < 32)
                {
                    int li = System.Array.IndexOf(spec.bodies, a);
                    if (li >= 0)
                    {
                        localParent[k] = li;
                        joints[k] = JointSpec.Stiff(Quaternion.Inverse(curR[li]) * curR[k]);
                        break;
                    }
                    a = skel.bodies[a].parent;
                }
            }
            // ...then every other tree root hangs off the main body.
            int mainRoot = main;
            for (int g = 0; localParent[mainRoot] >= 0 && g < 64; g++) mainRoot = localParent[mainRoot];
            for (int k = 0; k < n; k++)
            {
                if (localParent[k] >= 0 || k == mainRoot) continue;
                localParent[k] = main;
                joints[k] = JointSpec.Stiff(Quaternion.Inverse(curR[main]) * curR[k]);
            }

            // Joints take their limits relative to the pose they are created in, so build them in the rest pose
            // (the creature's bind rotation), then move every body to where it really is. Positions come from the
            // current pose with the joint anchor kept glued to the parent, so the joints never pull limbs apart.
            var anchorInParent = new Vector3[n];
            var restP = new Vector3[n];
            var restR = new Quaternion[n];
            var restDone = new bool[n];
            for (int k = 0; k < n; k++) Rest(k, 0);

            void Rest(int k, int depth)
            {
                if (restDone[k]) return;
                int p = localParent[k];
                if (p < 0 || depth > 32)
                {
                    restP[k] = curP[k];
                    restR[k] = curR[k];
                }
                else
                {
                    Rest(p, depth + 1);
                    JointSpec j = joints[k];
                    Vector3 anchorNow = curP[k] + curR[k] * j.anchor;
                    anchorInParent[k] = Quaternion.Inverse(curR[p]) * (anchorNow - curP[p]);
                    restR[k] = restR[p] * j.restLocalRot;
                    restP[k] = restP[p] + restR[p] * anchorInParent[k] - restR[k] * j.anchor;
                }
                restDone[k] = true;
            }

            var bones = new Transform[n];
            var vertsPerBody = new int[n];
            foreach (BoneWeight bw in spec.data.w) vertsPerBody[bw.boneIndex0]++;
            for (int k = 0; k < n; k++)
            {
                SkelBody sb = skel.bodies[spec.bodies[k]];
                var bgo = new GameObject("Zandatsu_Body_" + sb.name) { layer = layer };
                bgo.transform.SetParent(go.transform, false);
                bgo.transform.SetPositionAndRotation(restP[k], restR[k]);

                List<ShapeSpec> shapes;
                if (spec.cut[k] && spec.fitted[k] != null)
                {
                    ShapeSpec f = spec.fitted[k];
                    f.material = sb.shapes.Count > 0 ? sb.shapes[0].material : null;
                    shapes = new List<ShapeSpec> { f };
                }
                else shapes = sb.shapes;
                if (shapes.Count == 0)
                    shapes = new List<ShapeSpec> { new ShapeSpec { kind = ShapeKind.Sphere, radius = 0.04f } };
                foreach (ShapeSpec s in shapes) piece.AddShape(bgo.transform, s, layer);

                Rigidbody rb = bgo.AddComponent<Rigidbody>();
                rb.mass = Mathf.Max(0.1f, sb.mass * spec.massFraction[k]);
                rb.drag = sb.drag;
                rb.angularDrag = Mathf.Max(sb.angularDrag, 0.2f);
                if (sb.solverIterations > 0) rb.solverIterations = Mathf.Max(sb.solverIterations, 8);
                if (sb.solverVelocityIterations > 0) rb.solverVelocityIterations = sb.solverVelocityIterations;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                // PhysX pushes overlapping colliders apart at unlimited speed by default: a piece spawned slightly
                // inside the floor, a weapon or the player flew off. Overlaps now resolve gently.
                rb.maxDepenetrationVelocity = MaxDepenetration;

                var pb = bgo.AddComponent<PieceBody>();
                pb.piece = piece;
                pb.bodyName = sb.name;
                pb.shapes = shapes;
                pb.rb = rb;
                piece.bodies.Add(pb);
                bones[k] = bgo.transform;
            }

            var charJoints = new CharacterJoint[n];
            for (int k = 0; k < n; k++)
            {
                int p = localParent[k];
                if (p < 0) continue;
                PieceBody pb = piece.bodies[k];
                pb.parentIndex = p;
                pb.joint = joints[k];
                charJoints[k] = pb.joint.Create(pb.gameObject, piece.bodies[p].rb, anchorInParent[k]);
                piece.joints.Add(charJoints[k]);

                // The solver cannot hold a limit between bodies of very different mass (a split forearm stub under
                // a heavy hand): the wrist folded past its limit. Bound the ratio the solver sees.
                CharacterJoint cj = charJoints[k];
                float own = pb.rb.mass * cj.massScale, other = piece.bodies[p].rb.mass * cj.connectedMassScale;
                if (own * MaxJointMassRatio < other) cj.massScale *= other / (own * MaxJointMassRatio);
                else if (other * MaxJointMassRatio < own) cj.connectedMassScale *= own / (other * MaxJointMassRatio);
            }

            // The struck ragdoll part often carries the blade's impulse; copied as is it flung single bodies away
            // and the joints yanked the rest after them. Keep the piece's overall motion, damp the spread.
            Vector3 mean = Vector3.zero;
            float massSum = 0f;
            for (int k = 0; k < n; k++)
            {
                float m = piece.bodies[k].rb.mass;
                mean += vel[k] * m;
                massSum += m;
            }
            mean = Vector3.ClampMagnitude(massSum > 0f ? mean / massSum : Vector3.zero, MaxPieceSpeed);
            Vector3 push = ctx.normal * (spec.side * ctx.separationSpeed) + Vector3.ClampMagnitude(ctx.bladeVelocity * 0.05f, 0.75f);
            for (int k = 0; k < n; k++)
            {
                PieceBody pb = piece.bodies[k];
                pb.transform.SetPositionAndRotation(curP[k], curR[k]);
                pb.rb.position = curP[k];
                pb.rb.rotation = curR[k];
                pb.rb.velocity = mean + Vector3.ClampMagnitude(vel[k] - mean, MaxBodySpread) + push;
                pb.rb.angularVelocity = Vector3.ClampMagnitude(ang[k], MaxAngularSpeed);
            }
            AddTone(piece, localParent, joints, anchorInParent, charJoints, curP, curR);

            // Skinned mesh: vertices are in snapshot world space, the piece root sits at the origin.
            var bindposes = new Matrix4x4[n];
            for (int k = 0; k < n; k++) bindposes[k] = Matrix4x4.TRS(snapP[k], snapR[k], Vector3.one).inverse;
            piece.mesh = spec.data.ToSkinnedMesh("Zandatsu_PieceMesh", bindposes);

            int rootBone = 0;
            for (int k = 1; k < n; k++) if (vertsPerBody[k] > vertsPerBody[rootBone]) rootBone = k;
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            piece.smr = smr;
            smr.sharedMesh = piece.mesh;
            smr.bones = bones;
            smr.rootBone = bones[rootBone];
            smr.updateWhenOffscreen = false;
            float reach = spec.bounds.extents.magnitude + 0.5f;
            smr.localBounds = new Bounds(Quaternion.Inverse(snapR[rootBone]) * (spec.bounds.center - snapP[rootBone]), Vector3.one * (reach * 2f));
            smr.sharedMaterials = spec.data.mats.ToArray();
            for (int i = 0; i < spec.data.blocks.Count; i++)
                if (spec.data.blocks[i] != null) smr.SetPropertyBlock(spec.data.blocks[i], i);
            PieceLighting.Apply(piece, !ctx.lightingLogged);
            ctx.lightingLogged = true;

            // Bodies of one piece never collide with each other (like vanilla adjacent ragdoll parts),
            // nor with nearby siblings that were cut out of the same spot.
            IgnorePairs(piece.colliders, piece.colliders);
            ctx.family.neverCollide.RemoveAll(c => !c);
            IgnorePairs(ctx.family.neverCollide, piece.colliders);
            foreach (SlicedPiece sibling in ctx.family.pieces)
            {
                if (!sibling || sibling.dead) continue;
                Bounds a = sibling.WorldBounds();
                a.Expand(0.3f);
                if (a.Intersects(piece.WorldBounds())) IgnorePairs(sibling.colliders, piece.colliders);
            }

            if (spec.hasCap && !ctx.seared)
            {
                int k = spec.capBody;
                Quaternion delta = curR[k] * Quaternion.Inverse(snapR[k]);
                Vector3 capNow = curP[k] + delta * (spec.capCenter - snapP[k]);
                // The parent slice effect has the flesh-cut sound as well as blood; one per cut is enough.
                EffectData effect = ctx.blood;
                if (!ZandatsuScript.CutSound)
                {
                    // Blood only. If the creature has no blood-only effect, its "blood" is the parent one with the sound.
                    if (ReferenceEquals(effect, ctx.sound)) effect = null;
                }
                else if (!ctx.soundPlayed && ctx.sound != null)
                {
                    effect = ctx.sound;
                    ctx.soundPlayed = true;
                }
                piece.AttachBlood(effect, bones[k], capNow, delta * (-ctx.normal * spec.side));
            }

            ctx.family.pieces.Add(piece);
            all.Add(piece);
            return piece;
        }

        /// <summary>
        /// Muscle tone: a soft slerp drive per joint holds the pose the piece had when it was cut, so limbs keep their
        /// shape instead of folding like wet cloth. The spring scales with the load the joint carries (everything that
        /// hangs below it), so a hand and a leg sag about the same angle: ToneSag radians at stiffness 1 when held level.
        /// </summary>
        private static void AddTone(SlicedPiece piece, int[] localParent, JointSpec[] joints, Vector3[] anchorInParent,
                                    CharacterJoint[] charJoints, Vector3[] curP, Quaternion[] curR)
        {
            float tone = ZandatsuScript.PieceStiffness;
            int n = piece.bodies.Count;
            if (tone <= 0f || n < 2) return;

            // Mass and centre of mass of each body plus everything hanging below it, deepest bodies first.
            var depth = new int[n];
            for (int k = 0; k < n; k++)
                for (int a = localParent[k], g = 0; a >= 0 && g < 64; a = localParent[a], g++) depth[k]++;
            var order = new int[n];
            for (int k = 0; k < n; k++) order[k] = k;
            System.Array.Sort(order, (x, y) => depth[y].CompareTo(depth[x]));
            var subMass = new float[n];
            var subMoment = new Vector3[n];
            for (int k = 0; k < n; k++)
            {
                Rigidbody rb = piece.bodies[k].rb;
                subMass[k] = rb.mass;
                subMoment[k] = (curP[k] + curR[k] * rb.centerOfMass) * rb.mass;
            }
            foreach (int k in order)
            {
                int p = localParent[k];
                if (p < 0) continue;
                subMass[p] += subMass[k];
                subMoment[p] += subMoment[k];
            }

            foreach (int k in order)
            {
                int p = localParent[k];
                if (p < 0 || charJoints[k] == null) continue;
                PieceBody pb = piece.bodies[k];
                Vector3 anchorWorld = curP[k] + curR[k] * joints[k].anchor;
                float lever = Mathf.Max(0.05f, Vector3.Distance(subMoment[k] / subMass[k], anchorWorld));
                float spring = tone * subMass[k] * Physics.gravity.magnitude * lever / ToneSag;
                float inertia = subMass[k] * lever * lever;
                float damper = 2f * ToneDamping * Mathf.Sqrt(spring * inertia);

                // Created in the current pose: the drive's target (identity) is the pose of the moment of the cut.
                var cj = pb.gameObject.AddComponent<ConfigurableJoint>();
                cj.autoConfigureConnectedAnchor = false;
                cj.anchor = joints[k].anchor;
                cj.connectedBody = piece.bodies[p].rb;
                cj.connectedAnchor = anchorInParent[k];
                cj.xMotion = cj.yMotion = cj.zMotion = ConfigurableJointMotion.Free;
                cj.angularXMotion = cj.angularYMotion = cj.angularZMotion = ConfigurableJointMotion.Free;
                cj.rotationDriveMode = RotationDriveMode.Slerp;
                cj.slerpDrive = new JointDrive { positionSpring = spring, positionDamper = damper, maximumForce = float.MaxValue };
                cj.enableCollision = false;
                cj.massScale = charJoints[k].massScale;
                cj.connectedMassScale = charJoints[k].connectedMassScale;
            }
        }

        private static void IgnorePairs(List<Collider> a, List<Collider> b)
        {
            foreach (Collider x in a)
            foreach (Collider y in b)
                if (x && y && x != y) Physics.IgnoreCollision(x, y, true);
        }

        private void AddShape(Transform body, ShapeSpec s, int layer)
        {
            Transform host = body;
            if (s.localPos != Vector3.zero || s.localRot != Quaternion.identity || s.scale != Vector3.one)
            {
                var child = new GameObject("Zandatsu_Collider") { layer = layer };
                child.transform.SetParent(body, false);
                child.transform.localPosition = s.localPos;
                child.transform.localRotation = s.localRot;
                child.transform.localScale = s.scale;
                host = child.transform;
            }
            Collider c;
            switch (s.kind)
            {
                case ShapeKind.Sphere:
                    var sc = host.gameObject.AddComponent<SphereCollider>();
                    sc.center = s.center; sc.radius = s.radius;
                    c = sc;
                    break;
                case ShapeKind.Capsule:
                    var cc = host.gameObject.AddComponent<CapsuleCollider>();
                    cc.center = s.center; cc.radius = s.radius; cc.height = s.height; cc.direction = s.direction;
                    c = cc;
                    break;
                case ShapeKind.ConvexMesh:
                    var mc = host.gameObject.AddComponent<MeshCollider>();
                    mc.convex = true;
                    mc.sharedMesh = s.mesh;
                    c = mc;
                    break;
                default:
                    var bc = host.gameObject.AddComponent<BoxCollider>();
                    bc.center = s.center; bc.size = s.size;
                    c = bc;
                    break;
            }
            PhysicMaterial pm = family.PhysicCopy(s.material);
            if (pm)
            {
                c.sharedMaterial = pm;
                // Force the per-collider instance now and give it the name the game's lookup expects.
                PhysicMaterial inst = c.material;
                inst.name = pm.name + " (Instance)";
                if (inst != pm) physicInstances.Add(inst);
            }
            colliders.Add(c);
        }

        /// <summary>Diagnostics: how far apart the two sides of the worst joint are (should stay at ~0).</summary>
        private void LogJointGap(string when)
        {
            float worst = 0f;
            string which = null;
            foreach (CharacterJoint j in joints)
            {
                if (!j || !j.connectedBody) continue;
                Vector3 a = j.transform.TransformPoint(j.anchor);
                Vector3 b = j.connectedBody.transform.TransformPoint(j.connectedAnchor);
                float d = Vector3.Distance(a, b);
                if (d > worst) { worst = d; which = j.name; }
            }
            if (worst > 0.03f)
                Debug.LogWarning($"[Zandatsu] joint gap {worst:0.000} m at {which} after {when} (gen {generation}, {bodies.Count} bodies)");
        }

        public Bounds WorldBounds()
        {
            if (smr) return smr.bounds;
            return new Bounds(transform.position, Vector3.one);
        }

        /// <summary>Current skinned pose as mesh data (same layout as data), for cutting this piece again.</summary>
        public MeshData BakeCurrent()
        {
            var baked = new Mesh();
            smr.BakeMesh(baked, true);
            var v = new List<Vector3>(data.VertexCount);
            var n = new List<Vector3>(data.VertexCount);
            var t = new List<Vector4>(data.VertexCount);
            baked.GetVertices(v);
            baked.GetNormals(n);
            baked.GetTangents(t);
            Destroy(baked);
            // The piece root never moves (identity), so its local space is world space.
            return data.WithPose(v, n, t);
        }

        public void AttachBlood(EffectData effect, Transform parent, Vector3 worldPos, Vector3 worldDir)
        {
            if (effect == null) return;
            try
            {
                EffectInstance ei = effect.Spawn(worldPos, Quaternion.LookRotation(worldDir), parent, null, true, null, false, 1f, 1f);
                ei.SetIntensity(1f);
                ei.Play();
                effects.Add(ei);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Zandatsu] blood effect failed: " + e.Message);
            }
        }

        public void IgnoreForever(IEnumerable<Collider> others)
        {
            foreach (Collider o in others)
            {
                if (!o) continue;
                foreach (Collider c in colliders)
                    if (c) Physics.IgnoreCollision(c, o, true);
            }
        }

        public void IgnoreTemporarily(IEnumerable<Collider> others, float seconds)
        {
            float until = Time.time + seconds;
            foreach (Collider o in others)
            {
                if (!o) continue;
                foreach (Collider c in colliders)
                    if (c) Physics.IgnoreCollision(c, o, true);
                ignored.Add(new KeyValuePair<Collider, float>(o, until));
            }
        }

        public bool CanBeCut(float cooldown)
        {
            return !dead && !busy && Time.time - spawnTime > cooldown && data.TriangleCount > 40 && size > 0.08f;
        }

        private void Update()
        {
            // Twice: right after the spawn and once the piece has settled (a neck pulled apart later showed as a
            // helmet sliding off the face).
            if (jointChecks == 0 && Time.time - spawnTime > 0.5f) { jointChecks = 1; LogJointGap("0.5 s"); }
            else if (jointChecks == 1 && Time.time - spawnTime > 5f) { jointChecks = 2; LogJointGap("5 s"); }
            for (int i = ignored.Count - 1; i >= 0; i--)
            {
                if (Time.time < ignored[i].Value) continue;
                Collider o = ignored[i].Key;
                if (o)
                    foreach (Collider c in colliders)
                        if (c) Physics.IgnoreCollision(c, o, false);
                ignored.RemoveAt(i);
            }
            if (ZandatsuScript.PieceLifetime > 0f && Time.time - spawnTime > ZandatsuScript.PieceLifetime) Kill();
        }

        /// <summary>Always destroy through here so pooled blood effects go back to their pool first.</summary>
        public void Kill()
        {
            if (dead) return;
            dead = true;
            // Out of the live list now, not in OnDestroy at the end of the frame: the piece limit counts this list and
            // looped forever on a piece that was already dying (the game froze while slicing pieces fast).
            all.Remove(this);
            foreach (EffectInstance ei in effects)
            {
                try { ei?.Despawn(); } catch { }
            }
            effects.Clear();
            // The game parents its own hit effects (sounds, blood, reveal) to whatever collider was hit;
            // hand them back to their pools or they get destroyed with the piece.
            foreach (Effect e in GetComponentsInChildren<Effect>(true))
            {
                if (!e) continue;
                try
                {
                    if (e.containingInstance != null) e.containingInstance.Despawn();
                    else e.Despawn();
                }
                catch { }
                if (e && e.transform.IsChildOf(transform)) e.transform.SetParent(null, true);
            }
            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            dead = true;
            all.Remove(this);
            if (mesh) Destroy(mesh);
            foreach (PhysicMaterial pm in physicInstances) if (pm) Destroy(pm);
            if (family != null)
            {
                family.pieces.Remove(this);
                family.TryRelease();
            }
        }
    }
}
