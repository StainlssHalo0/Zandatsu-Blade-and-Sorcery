using System.Collections.Generic;
using ThunderRoad;
using UnityEngine;

namespace Zandatsu
{
    /// <summary>
    /// Lets the player pick pieces up: pieces of enemies and the cut-off halves of weapons and objects (ItemDebris). They
    /// are not Items and have no Handles, so the game's own grab ignores them, and its climb grip only lets the player
    /// hang off them. Gripping near one joins its nearest body to the hand.
    /// </summary>
    public class PieceGrab
    {
        private class Hold
        {
            public RagdollHand hand;
            public PieceBody body;        // a piece of an enemy, or
            public ItemDebris debris;     // a cut-off half of a weapon or an object
            public ConfigurableJoint joint;
            public List<Collider> handColliders;
            public bool Alive => body ? body.piece && !body.piece.IsDead : debris;
        }

        private const float Reach = 0.09f;   // m around the grip point
        private readonly Hold[] holds = new Hold[2];
        private readonly bool[] wasPressed = new bool[2];
        private readonly Collider[] overlap = new Collider[32];

        public void Update()
        {
            Creature player = Player.local ? Player.local.creature : null;
            if (!player)
            {
                ReleaseAll();
                return;
            }
            for (int i = 0; i < 2; i++)
            {
                RagdollHand rh = player.GetHand(i == 0 ? Side.Left : Side.Right);
                PlayerControl.Hand ctrl = rh && rh.playerHand ? rh.playerHand.controlHand : null;
                if (ctrl == null) { Release(i); continue; }
                bool pressed = ctrl.gripPressed;
                Hold h = holds[i];
                if (h != null)
                {
                    if (!pressed || !h.joint || !h.Alive || rh.grabbedHandle) Release(i);
                }
                else if (pressed && !rh.grabbedHandle)
                {
                    // The climb grip may have closed on a piece first (it runs from physics contacts): take it over.
                    if (Climbed(rh, out PieceBody body, out ItemDebris debris))
                    {
                        rh.climb.UnGrip();
                        Grab(i, rh, body, debris);
                    }
                    else if (!wasPressed[i]) TryGrab(i, rh);
                }
                wasPressed[i] = pressed;
            }
        }

        /// <summary>Where a hand holds this debris (its grip), null when no hand does: a cut keeps that side.</summary>
        public Vector3? HoldPoint(ItemDebris debris)
        {
            foreach (Hold h in holds)
                if (h != null && h.debris == debris && h.hand && h.hand.grip) return h.hand.grip.position;
            return null;
        }

        private static bool Climbed(RagdollHand rh, out PieceBody body, out ItemDebris debris)
        {
            body = null;
            debris = null;
            if (rh.climb == null || !rh.climb.isGripping || rh.climb.gripPhysicBody == null || !rh.climb.gripPhysicBody.gameObject) return false;
            GameObject go = rh.climb.gripPhysicBody.gameObject;
            PieceBody pb = go.GetComponent<PieceBody>();
            if (pb && pb.piece && !pb.piece.IsDead) body = pb;
            else debris = go.GetComponent<ItemDebris>();
            return body || debris;
        }

        private void TryGrab(int i, RagdollHand rh)
        {
            if (!rh.grip) return;
            Vector3 p = rh.grip.position;
            int mask = 1 << GameManager.GetLayer(LayerName.DroppedItem);
            int n = Physics.OverlapSphereNonAlloc(p, Reach, overlap, mask, QueryTriggerInteraction.Ignore);
            PieceBody bestBody = null;
            ItemDebris bestDebris = null;
            float bestDist = float.MaxValue;
            for (int k = 0; k < n; k++)
            {
                Collider c = overlap[k];
                if (!c) continue;
                PieceBody pb = c.GetComponentInParent<PieceBody>();
                ItemDebris db = pb ? null : c.GetComponentInParent<ItemDebris>();
                if (pb && (!pb.piece || pb.piece.IsDead || !pb.rb)) continue;
                if (!pb && (!db || !db.Body)) continue;
                float d = (c.ClosestPoint(p) - p).sqrMagnitude;
                if (d < bestDist) { bestDist = d; bestBody = pb; bestDebris = db; }
            }
            if (bestBody || bestDebris) Grab(i, rh, bestBody, bestDebris);
        }

        private void Grab(int i, RagdollHand rh, PieceBody body, ItemDebris debris)
        {
            Rigidbody handRb = rh.physicBody != null ? rh.physicBody.rigidBody : null;
            Rigidbody rb = body ? body.rb : debris ? debris.Body : null;
            if (!handRb || !rb) return;
            Release(i);

            // Held where it is: anchors on both sides are the same world point, so nothing snaps.
            Vector3 at = rh.grip ? rh.grip.position : handRb.position;
            var j = rb.gameObject.AddComponent<ConfigurableJoint>();
            j.autoConfigureConnectedAnchor = false;
            j.connectedBody = handRb;
            j.anchor = rb.transform.InverseTransformPoint(at);
            j.connectedAnchor = handRb.transform.InverseTransformPoint(at);
            j.xMotion = j.yMotion = j.zMotion = ConfigurableJointMotion.Locked;
            j.angularXMotion = j.angularYMotion = j.angularZMotion = ConfigurableJointMotion.Locked;
            j.projectionMode = JointProjectionMode.PositionAndRotation;
            j.projectionDistance = 0.05f;
            j.projectionAngle = 10f;
            j.enablePreprocessing = false;
            // Scaling the piece's inverse mass up makes the solver treat it as about as light as the hand, so heavy
            // chunks follow the hand instead of jerking it; gravity still weighs on the hand through the joint.
            j.massScale = Mathf.Clamp(rb.mass / Mathf.Max(handRb.mass, 0.1f), 1f, 10f);

            var handColliders = new List<Collider>();
            if (rh.colliderGroup && rh.colliderGroup.colliders != null)
                foreach (Collider c in rh.colliderGroup.colliders)
                    if (c) handColliders.Add(c);
            IEnumerable<Collider> held = body ? (IEnumerable<Collider>)body.piece.colliders : debris.Colliders;
            foreach (Collider h in handColliders)
                foreach (Collider c in held)
                    if (c) Physics.IgnoreCollision(h, c, true);
            if (debris) debris.held = true;

            holds[i] = new Hold { hand = rh, body = body, debris = debris, joint = j, handColliders = handColliders };
            try { rh.HapticTick(0.8f); } catch { }
            Debug.Log(body ? $"[Zandatsu] grab {(i == 0 ? "left" : "right")}: {body.bodyName}, {rb.mass:0.0} kg (piece {body.piece.Mass:0.0} kg)"
                           : $"[Zandatsu] grab {(i == 0 ? "left" : "right")}: a cut-off half, {rb.mass:0.0} kg");
        }

        private void Release(int i)
        {
            Hold h = holds[i];
            if (h == null) return;
            holds[i] = null;
            if (h.joint) Object.Destroy(h.joint);
            // Collisions with the hand come back once the piece has left the fingers.
            if (h.body && h.body.piece && !h.body.piece.IsDead) h.body.piece.IgnoreTemporarily(h.handColliders, 0.3f);
            if (h.debris)
            {
                h.debris.held = false;
                h.debris.IgnoreTemporarily(h.handColliders, 0.3f);
            }
        }

        public void ReleaseAll()
        {
            Release(0);
            Release(1);
        }
    }
}
