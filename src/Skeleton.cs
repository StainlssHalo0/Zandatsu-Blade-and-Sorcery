using System.Collections.Generic;
using ThunderRoad;
using UnityEngine;

namespace Zandatsu
{
    public enum ShapeKind { Sphere, Capsule, Box, ConvexMesh }

    /// <summary>A collider described relative to its body (body-local pose + collider params).</summary>
    public class ShapeSpec
    {
        public ShapeKind kind;
        public Vector3 localPos = Vector3.zero;
        public Quaternion localRot = Quaternion.identity;
        public Vector3 scale = Vector3.one;
        public Vector3 center;
        public Vector3 size;      // box
        public float radius;      // sphere / capsule
        public float height;      // capsule
        public int direction;     // capsule axis
        public Mesh mesh;         // convex mesh
        public PhysicMaterial material;

        public static ShapeSpec From(Collider c, Transform body)
        {
            var s = new ShapeSpec
            {
                // Piece bodies have unit scale: keep offsets in metres and give the collider its absolute scale.
                localPos = Quaternion.Inverse(body.rotation) * (c.transform.position - body.position),
                localRot = Quaternion.Inverse(body.rotation) * c.transform.rotation,
                scale = c.transform.lossyScale,
                material = c.sharedMaterial
            };
            switch (c)
            {
                case SphereCollider sc:
                    s.kind = ShapeKind.Sphere; s.center = sc.center; s.radius = sc.radius;
                    break;
                case CapsuleCollider cc:
                    s.kind = ShapeKind.Capsule; s.center = cc.center; s.radius = cc.radius; s.height = cc.height; s.direction = cc.direction;
                    break;
                case BoxCollider bc:
                    s.kind = ShapeKind.Box; s.center = bc.center; s.size = bc.size;
                    break;
                case MeshCollider mc when mc.sharedMesh && mc.convex:
                    s.kind = ShapeKind.ConvexMesh; s.mesh = mc.sharedMesh;
                    break;
                default:
                    Bounds b = c.bounds; // world AABB fallback
                    s.kind = ShapeKind.Box;
                    s.localPos = Quaternion.Inverse(body.rotation) * (b.center - body.position);
                    s.localRot = Quaternion.identity;
                    s.scale = Vector3.one;
                    s.center = Vector3.zero;
                    s.size = b.size;
                    break;
            }
            return s;
        }

    }

    /// <summary>CharacterJoint settings plus the child's rest pose relative to its parent body.</summary>
    public class JointSpec
    {
        public Vector3 restLocalPos;
        public Quaternion restLocalRot = Quaternion.identity;
        public Vector3 anchor, axis, swingAxis;
        public SoftJointLimitSpring twistLimitSpring, swingLimitSpring;
        public SoftJointLimit lowTwistLimit, highTwistLimit, swing1Limit, swing2Limit;
        public bool enableProjection, enablePreprocessing;
        public float projectionDistance, projectionAngle;
        public float massScale = 1f, connectedMassScale = 1f;

        /// <summary>
        /// The game's joint data lives in scaled transform space (part localScale = mesh bone scale); piece bodies are
        /// unit-scale, so the anchor is converted to metres in the child's frame.
        /// </summary>
        public static JointSpec From(RagdollPart.CharacterJointData d, Transform child)
        {
            Vector3 cs = child.lossyScale;
            Transform parent = d.connectedBody.transform;
            return new JointSpec
            {
                restLocalPos = Quaternion.Inverse(parent.rotation) * parent.TransformVector(d.localPosition),
                restLocalRot = d.localRotation,
                anchor = Vector3.Scale(cs, d.anchor),
                axis = Dir(Vector3.Scale(cs, d.axis), Vector3.right),
                swingAxis = Dir(Vector3.Scale(cs, d.swingAxis), Vector3.up),
                twistLimitSpring = d.twistLimitSpring,
                swingLimitSpring = d.swingLimitSpring,
                lowTwistLimit = d.lowTwistLimit,
                highTwistLimit = d.highTwistLimit,
                swing1Limit = d.swing1Limit,
                swing2Limit = d.swing2Limit,
                enableProjection = d.enableProjection,
                enablePreprocessing = d.enablePreprocessing,
                projectionDistance = d.projectionDistance,
                projectionAngle = d.projectionAngle,
                massScale = d.massScale,
                connectedMassScale = d.connectedMassScale
            };
        }

        private static Vector3 Dir(Vector3 v, Vector3 fallback) => v.sqrMagnitude > 1e-12f ? v.normalized : fallback;

        /// <summary>A stiff joint that keeps the child where it is now relative to the parent (bridges missing bodies).</summary>
        public static JointSpec Stiff(Quaternion restLocalRot)
        {
            return new JointSpec
            {
                restLocalRot = restLocalRot,
                axis = Vector3.right,
                swingAxis = Vector3.up,
                lowTwistLimit = new SoftJointLimit { limit = -10f },
                highTwistLimit = new SoftJointLimit { limit = 10f },
                swing1Limit = new SoftJointLimit { limit = 10f },
                swing2Limit = new SoftJointLimit { limit = 10f }
            };
        }

        /// <param name="connectedAnchor">The anchor point in the parent body's frame (metres).</param>
        public CharacterJoint Create(GameObject go, Rigidbody connected, Vector3 connectedAnchor)
        {
            var j = go.AddComponent<CharacterJoint>();
            j.anchor = anchor;
            j.axis = axis;
            j.swingAxis = swingAxis;
            j.twistLimitSpring = twistLimitSpring;
            j.swingLimitSpring = swingLimitSpring;
            j.lowTwistLimit = lowTwistLimit;
            j.highTwistLimit = highTwistLimit;
            j.swing1Limit = swing1Limit;
            j.swing2Limit = swing2Limit;
            // Projection keeps long chains from stretching apart when a light half-limb hangs off a heavy torso.
            j.enableProjection = true;
            j.projectionDistance = projectionDistance > 0f ? projectionDistance : 0.05f;
            j.projectionAngle = projectionAngle > 0f ? projectionAngle : 15f;
            j.enablePreprocessing = enablePreprocessing;
            j.massScale = massScale > 0f ? massScale : 1f;
            j.connectedMassScale = connectedMassScale > 0f ? connectedMassScale : 1f;
            j.enableCollision = false;
            j.breakForce = float.PositiveInfinity;
            j.breakTorque = float.PositiveInfinity;
            j.autoConfigureConnectedAnchor = false;
            j.connectedBody = connected;
            j.connectedAnchor = connectedAnchor;
            return j;
        }
    }

    /// <summary>One rigid body of a ragdoll (creature part or piece body) at snapshot time.</summary>
    public class SkelBody
    {
        public string name;
        public Vector3 pos;
        public Quaternion rot;
        public int parent = -1;
        public JointSpec joint;
        public float mass, drag, angularDrag;
        public Vector3 scale = Vector3.one;   // lossy scale of the source transform (diagnostics)
        public int solverIterations, solverVelocityIterations;
        public List<ShapeSpec> shapes = new List<ShapeSpec>();
        // Live objects, read again on the main thread when the pieces spawn (they kept moving while the worker ran).
        public Transform live;
        public Rigidbody liveRb;
        public Matrix4x4 bind = Matrix4x4.identity;   // live.worldToLocalMatrix at snapshot time (hybrid cut caps)
        public Vector3 vel, angVel;
    }

    /// <summary>Bodies of whatever is being cut. Mesh weights index into bodies.</summary>
    public class Skeleton
    {
        public readonly List<SkelBody> bodies = new List<SkelBody>();

        public static Skeleton FromCreature(Creature creature, Dictionary<Transform, int> meshBoneToBody)
        {
            var sk = new Skeleton();
            List<RagdollPart> parts = creature.ragdoll ? creature.ragdoll.parts : null;
            if (parts == null) return sk;

            var rbIndex = new Dictionary<Rigidbody, int>();
            var partIndex = new Dictionary<RagdollPart, int>();
            var valid = new List<RagdollPart>();
            foreach (RagdollPart p in parts)
            {
                Rigidbody rb = p && p.physicBody != null ? p.physicBody.rigidBody : null;
                if (!rb) continue;
                rbIndex[rb] = valid.Count;
                partIndex[p] = valid.Count;
                valid.Add(p);
            }
            var severed = new bool[valid.Count];
            for (int i = 0; i < valid.Count; i++) severed[i] = GameSevered(valid[i]);

            for (int i = 0; i < valid.Count; i++)
            {
                RagdollPart p = valid[i];
                Rigidbody rb = p.physicBody.rigidBody;
                Transform tr = p.transform;
                var b = new SkelBody
                {
                    name = p.type.ToString(),
                    pos = tr.position,
                    rot = tr.rotation,
                    scale = tr.lossyScale,
                    // A live creature runs on its standing masses (arms and hands made heavy to hold weapons, a hand
                    // can weigh 20 kg); a dead body uses the ragdolled ones. Pieces are dead.
                    mass = p.ragdolledMass > 0f ? p.ragdolledMass : rb.mass,
                    drag = rb.drag,
                    angularDrag = rb.angularDrag,
                    solverIterations = rb.solverIterations,
                    solverVelocityIterations = rb.solverVelocityIterations,
                    live = tr,
                    liveRb = rb,
                    bind = tr.worldToLocalMatrix,
                    vel = rb.velocity,
                    angVel = rb.angularVelocity
                };

                // A part the game cut off keeps its original joint data (orgCharacterJointData): joined to the body again,
                // the pieces pulled it back from wherever it lay and the skin between them stretched across the level.
                RagdollPart.CharacterJointData jd = p.orgCharacterJointData;
                if (jd == null && p.characterJoint) jd = new RagdollPart.CharacterJointData(p.characterJoint);
                if (jd != null && jd.connectedBody && rbIndex.TryGetValue(jd.connectedBody, out int parentIdx) && parentIdx != i &&
                    severed[parentIdx] == severed[i])
                {
                    b.parent = parentIdx;
                    b.joint = JointSpec.From(jd, tr);
                }
                else if (p.parentPart && partIndex.TryGetValue(p.parentPart, out int pp) && pp != i && severed[pp] == severed[i])
                {
                    // No joint data: keep the current offset as a stiff default joint.
                    b.parent = pp;
                    b.joint = JointSpec.Stiff(Quaternion.Inverse(p.parentPart.transform.rotation) * tr.rotation);
                }

                if (p.colliderGroup && p.colliderGroup.colliders != null)
                    foreach (Collider c in p.colliderGroup.colliders)
                        if (c && !c.isTrigger) b.shapes.Add(ShapeSpec.From(c, tr));

                sk.bodies.Add(b);
            }

            // A part's own mesh bone beats another part's linked bone. Claimed first-come in part order, Head_Mesh
            // landed on the neck: the face followed the neck body, the head body kept only the eyeballs, and they slid
            // out of their sockets. A linked bone claimed twice goes to the nearer part.
            // The mesh bones of a part the game cut off went back onto the body at their rest pose (Ragdoll.OnSlice): only
            // the seam left on the body still skins to them, so they belong to the body, not to the part far away.
            for (int i = 0; i < valid.Count; i++)
                if (valid[i].meshBone && !severed[i] && !meshBoneToBody.ContainsKey(valid[i].meshBone)) meshBoneToBody[valid[i].meshBone] = i;
            var ownBones = new HashSet<Transform>(meshBoneToBody.Keys);
            for (int i = 0; i < valid.Count; i++)
            {
                Transform[] linked = valid[i].linkedMeshBones;
                if (linked == null || severed[i]) continue;
                foreach (Transform lb in linked)
                {
                    if (!lb || ownBones.Contains(lb)) continue;
                    if (meshBoneToBody.TryGetValue(lb, out int other) &&
                        (valid[other].transform.position - lb.position).sqrMagnitude <= (valid[i].transform.position - lb.position).sqrMagnitude)
                        continue;
                    meshBoneToBody[lb] = i;
                }
            }

            // Bones without a part of their own (fingers, jaw, toes, face) are reparented by the ragdoll under the
            // animation rig, so walking their transform parents never reaches a part. Use the game's bone tree instead:
            // every mesh/animation bone belongs to its nearest ancestor bone that has a part. Bone.part only knows a
            // part's meshBone, not its linkedMeshBones, so those are checked too: otherwise a face hanging from a linked
            // head bone skips the head and lands on the neck.
            var partBones = new Dictionary<Transform, int>(meshBoneToBody);
            if (creature.ragdoll.bones != null)
                foreach (Ragdoll.Bone bone in creature.ragdoll.bones)
                {
                    if (bone == null) continue;
                    int body = -1;
                    Ragdoll.Bone cur = bone;
                    for (int depth = 0; cur != null && depth < 64; depth++, cur = cur.parent)
                    {
                        if (cur.mesh && partBones.TryGetValue(cur.mesh, out body)) break;
                        if (cur.part && partIndex.TryGetValue(cur.part, out body) && !severed[body]) break;
                        body = -1;
                    }
                    if (body >= 0)
                    {
                        if (bone.mesh && !meshBoneToBody.ContainsKey(bone.mesh)) meshBoneToBody[bone.mesh] = body;
                        if (bone.animation && !meshBoneToBody.ContainsKey(bone.animation)) meshBoneToBody[bone.animation] = body;
                    }
                    // The cut-off share of the meshes (the game's split renderers) skins to the "_Split" copies of the
                    // bones, which follow the part that was cut off.
                    if (!bone.meshSplit || meshBoneToBody.ContainsKey(bone.meshSplit)) continue;
                    cur = bone;
                    for (int depth = 0; cur != null && depth < 64; depth++, cur = cur.parent)
                        if (cur.part && partIndex.TryGetValue(cur.part, out int cutOff) && severed[cutOff])
                        {
                            meshBoneToBody[bone.meshSplit] = cutOff;
                            break;
                        }
                }
            // Part transforms themselves, for anything parented under the ragdoll hierarchy.
            for (int i = 0; i < valid.Count; i++)
                if (!meshBoneToBody.ContainsKey(valid[i].transform)) meshBoneToBody[valid[i].transform] = i;

            var info = new System.Text.StringBuilder();
            foreach (RagdollPart p in valid)
            {
                info.Append(" | ").Append(p.type).Append(' ').Append(p.meshBone ? p.meshBone.name : "-");
                if (p.linkedMeshBones != null && p.linkedMeshBones.Length > 0)
                {
                    info.Append(" +[");
                    for (int k = 0; k < p.linkedMeshBones.Length; k++)
                        info.Append(k > 0 ? "," : "").Append(p.linkedMeshBones[k] ? p.linkedMeshBones[k].name : "null");
                    info.Append(']');
                }
                info.Append($" {p.ragdolledMass:0.##}/{p.standingMass:0.##}/{p.physicBody.mass:0.##}kg");
            }
            sk.partInfo = info.ToString();
            return sk;
        }

        /// <summary>
        /// Cut off by the game's own dismemberment (Ragdoll.OnSlice sets slicedMeshRoot on the part it cut and isSliced on
        /// it and every part below). Zandatsu's own severed parts (hybrid cuts) have no slicedMeshRoot.
        /// </summary>
        public static bool GameSevered(RagdollPart p)
        {
            for (int depth = 0; p && depth < 32; depth++, p = p.parentPart)
                if (p.isSliced && p.slicedMeshRoot) return true;
            return false;
        }

        /// <summary>Diagnostics: part, mesh bone, linked bones, ragdolled/standing/current mass.</summary>
        public string partInfo;

        public static Skeleton FromPiece(SlicedPiece piece)
        {
            var sk = new Skeleton();
            foreach (PieceBody pb in piece.bodies)
            {
                Transform tr = pb.transform;
                sk.bodies.Add(new SkelBody
                {
                    name = pb.bodyName,
                    pos = tr.position,
                    rot = tr.rotation,
                    parent = pb.parentIndex,
                    joint = pb.joint,
                    mass = pb.rb.mass,
                    drag = pb.rb.drag,
                    angularDrag = pb.rb.angularDrag,
                    solverIterations = pb.rb.solverIterations,
                    solverVelocityIterations = pb.rb.solverVelocityIterations,
                    shapes = pb.shapes,
                    live = tr,
                    liveRb = pb.rb,
                    vel = pb.rb.velocity,
                    angVel = pb.rb.angularVelocity
                });
            }
            return sk;
        }
    }
}
