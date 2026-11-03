using System;
using System.Collections.Generic;
using System.Reflection;
using ThunderRoad;
using ThunderRoad.AI;
using ThunderRoad.AI.Get;
using UnityEngine;

namespace Zandatsu
{
    /// <summary>
    /// Hybrid cuts that only take limbs leave the enemy alive (the game itself kills on every dismemberment). A severed
    /// hand lets go of what it held, and of anything the AI later puts in it; without a leg he goes down and stays down;
    /// he bleeds from the stump, collapses shortly before the end and dies when the bleed-out time is up.
    /// </summary>
    public static class Survival
    {
        /// <summary>Damage left to a blow that cuts off a limb: the blow alone used to kill, the bleed-out decides now.</summary>
        public const float BlowDamage = 0.01f;

        private const RagdollPart.Type Limbs = RagdollPart.Type.LeftArm | RagdollPart.Type.RightArm |
                                               RagdollPart.Type.LeftHand | RagdollPart.Type.RightHand |
                                               RagdollPart.Type.LeftLeg | RagdollPart.Type.RightLeg |
                                               RagdollPart.Type.LeftFoot | RagdollPart.Type.RightFoot;
        private const RagdollPart.Type Legs = RagdollPart.Type.LeftLeg | RagdollPart.Type.RightLeg |
                                              RagdollPart.Type.LeftFoot | RagdollPart.Type.RightFoot;
        private const RagdollPart.Type RightArm = RagdollPart.Type.RightArm | RagdollPart.Type.RightHand;
        private const RagdollPart.Type LeftArm = RagdollPart.Type.LeftArm | RagdollPart.Type.LeftHand;
        private const float CollapseBefore = 3f;   // s before death the enemy falls from blood loss
        private const float BleedAfterDeath = 4f;  // s the stump keeps bleeding on the corpse
        private const float PulseInterval = 0.5f;  // s between gushes: each lasts longer, so they run into one stream
        private const string FallbackBleedId = "SliceFleshChild";

        /// <summary>
        /// A bleeding stump. The game's blood is a gush of finite length (the slice effect, or the one a blade pulled out of
        /// a stab wound leaves, which only plays when it ends: that one bled in fits). A new gush every half second keeps
        /// it flowing; weaker as time runs out.
        /// </summary>
        private sealed class Stump
        {
            public Transform parent;
            public Vector3 localPos;
            public Quaternion localRot;
            public EffectData effect;
        }

        private sealed class Bleeder
        {
            public Creature creature;
            public float startTime, deathTime, stopTime, nextPulse;
            public bool down, dead;
            public float nextPickupCheck;
            public readonly List<Stump> stumps = new List<Stump>();
            public readonly List<RagdollHand> lostHands = new List<RagdollHand>();
            public readonly Dictionary<GetItem, float> pickupsOff = new Dictionary<GetItem, float>();   // node -> its search radius
        }

        private static readonly Dictionary<Creature, Bleeder> bleeders = new Dictionary<Creature, Bleeder>();
        private static readonly object NoStandUp = new object();   // our handler in Brain.noStandupModifiers
        private static EffectData fallbackBleed;
        private static bool fallbackLooked;

        public static bool IsLimb(RagdollPart.Type type) => (type & Limbs) != 0 && (type & ~Limbs) == 0;

        public static bool IsBleeding(Creature creature) => creature && bleeders.TryGetValue(creature, out Bleeder b) && !b.dead;

        /// <summary>
        /// Did this cut take only limbs? A few vertices of the trunk or head in a piece (a shoulder seam weighted to the
        /// chest) do not count; more than that is a cut through the body, which kills as before.
        /// </summary>
        public static bool OnlyLimbs(Skeleton skel, CreatureRecipe rec, int[] pieceBodyVerts)
        {
            if (pieceBodyVerts == null) return false;
            bool lost = false;
            for (int b = 0; b < skel.bodies.Count && b < pieceBodyVerts.Length; b++)
            {
                if (pieceBodyVerts[b] == 0) continue;
                RagdollPart part = skel.bodies[b].live ? skel.bodies[b].live.GetComponent<RagdollPart>() : null;
                if (!part) continue;
                if (IsLimb(part.type))
                {
                    lost = true;
                    continue;
                }
                int total = rec.bodyVerts[b] + pieceBodyVerts[b];
                if (pieceBodyVerts[b] > Mathf.Max(40, total / 10)) return false;
            }
            return lost;
        }

        /// <summary>
        /// Before the creature's meshes change: a hand whose arm lost anything (hand, forearm or upper arm) lets go at once.
        /// A half-cut hand kept its weapon hanging in the stump, and a weapon held with both hands stayed in the other one,
        /// too heavy to lift. Returns the hands that hold nothing from now on.
        /// </summary>
        public static List<RagdollHand> DisarmCutArms(Creature creature, Skeleton skel, int[] pieceBodyVerts)
        {
            var hurt = new List<RagdollHand>();
            if (pieceBodyVerts == null) return hurt;
            bool right = false, left = false;
            for (int b = 0; b < skel.bodies.Count && b < pieceBodyVerts.Length; b++)
            {
                if (pieceBodyVerts[b] == 0) continue;
                RagdollPart part = skel.bodies[b].live ? skel.bodies[b].live.GetComponent<RagdollPart>() : null;
                if (!part) continue;
                if ((part.type & RightArm) != 0) right = true;
                if ((part.type & LeftArm) != 0) left = true;
            }
            if (right) Disarm(creature.handRight, creature.handLeft, hurt);
            if (left) Disarm(creature.handLeft, creature.handRight, hurt);
            return hurt;
        }

        private static void Disarm(RagdollHand hand, RagdollHand other, List<RagdollHand> hurt)
        {
            if (!hand) return;
            hurt.Add(hand);
            Item item = hand.grabbedHandle ? hand.grabbedHandle.item : null;
            try { hand.TryRelease(); } catch { }
            if (item && other && other.grabbedHandle && other.grabbedHandle.item == item)
                try { other.TryRelease(); } catch { }
        }

        /// <summary>After Hybrid.Apply severed the limb: start (or speed up) the bleed-out, drop, fall, bleed.</summary>
        /// <param name="hurtHands">Hands that hold nothing from now on (DisarmCutArms).</param>
        /// <returns>Seconds left to live (diagnostics).</returns>
        /// <param name="seared">Cut by a hot blade: the stump is seared shut and does not spurt (the clock still runs).</param>
        public static float LimbLost(Creature creature, HybridState state, Skeleton skel, CreatureRecipe rec, Vector3 normal, float bleedOutTime,
                                     List<RagdollHand> hurtHands, bool seared = false)
        {
            if (!bleeders.TryGetValue(creature, out Bleeder b))
            {
                b = new Bleeder { creature = creature, startTime = Time.time, deathTime = Time.time + bleedOutTime };
                bleeders[creature] = b;
            }
            else if (!b.dead) b.deathTime = Time.time + (b.deathTime - Time.time) * 0.5f;   // another limb: half of what was left

            bool legLost = false;
            foreach (RagdollPart part in creature.ragdoll.parts)
            {
                if (!part || !state.IsSevered(part)) continue;
                if ((part.type & Legs) != 0) legLost = true;
                if (part is RagdollHand hand && !b.lostHands.Contains(hand)) b.lostHands.Add(hand);
            }
            if (hurtHands != null)
                foreach (RagdollHand hand in hurtHands)
                    if (hand && !b.lostHands.Contains(hand)) b.lostHands.Add(hand);
            if (legLost) Down(b);

            int body = rec.capKeptBody;   // never a severed part: those hang where they were cut
            if (!seared && body >= 0 && body < skel.bodies.Count && skel.bodies[body].live)
            {
                Transform parent = skel.bodies[body].live;
                RagdollPart part = parent.GetComponent<RagdollPart>();
                EffectData effect = part && part.data != null ? part.data.sliceChildEffectData : null;
                if (effect == null)
                {
                    if (!fallbackLooked)
                    {
                        fallbackLooked = true;
                        fallbackBleed = Catalog.GetData<EffectData>(FallbackBleedId, false);
                    }
                    effect = fallbackBleed;
                }
                if (effect != null)
                    b.stumps.Add(new Stump
                    {
                        parent = parent,
                        localPos = parent.InverseTransformPoint(rec.capCenter),
                        localRot = Quaternion.Inverse(parent.rotation) * Quaternion.LookRotation(-normal * rec.side),
                        effect = effect
                    });
                else Debug.LogWarning("[Zandatsu] no blood effect for the stump");
            }
            b.nextPulse = Time.time;
            return b.deathTime - Time.time;
        }

        private static void Pulse(Bleeder b)
        {
            float span = Mathf.Max(0.01f, b.deathTime - b.startTime);
            float intensity = b.dead
                ? Mathf.Lerp(0.5f, 0.1f, (Time.time - b.deathTime) / BleedAfterDeath)
                : Mathf.Lerp(1f, 0.5f, (Time.time - b.startTime) / span);   // the bleed weakens as he runs out
            foreach (Stump st in b.stumps)
            {
                if (!st.parent) continue;
                try
                {
                    EffectInstance ei = st.effect.Spawn(st.parent.TransformPoint(st.localPos), st.parent.rotation * st.localRot, st.parent,
                                                        null, true, null, false, intensity, 1f);
                    ei.SetIntensity(intensity);
                    ei.Play();
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Zandatsu] bleed: " + e.Message);
                    st.parent = null;   // don't retry every half second
                }
            }
        }

        private static void Down(Bleeder b)
        {
            if (b.down) return;
            b.down = true;
            Creature c = b.creature;
            try
            {
                if (c.brain) c.brain.AddNoStandUpModifier(NoStandUp);
                if (c.ragdoll.state != Ragdoll.State.Destabilized && c.ragdoll.state != Ragdoll.State.Inert)
                    c.ragdoll.SetState(Ragdoll.State.Destabilized);
            }
            catch (Exception e) { Debug.LogWarning("[Zandatsu] knock down: " + e.Message); }
        }

        /// <summary>Every frame (main thread).</summary>
        public static void Update()
        {
            if (bleeders.Count == 0) return;
            List<Creature> done = null;
            foreach (Bleeder b in bleeders.Values)
            {
                Creature c = b.creature;
                if (!b.dead && (!c || c.isKilled || Time.time >= b.deathTime))
                {
                    if (c && !c.isKilled)
                    {
                        try { c.Kill(); }
                        catch (Exception e) { Debug.LogWarning("[Zandatsu] bleed-out kill: " + e.Message); }
                        if (c.isKilled) Debug.Log("[Zandatsu] " + c.name + " bled out");
                    }
                    // Killed by the bleed-out or anything else: the stump bleeds on a little, weaker, then stops.
                    b.dead = true;
                    b.deathTime = Time.time;
                    b.stopTime = Time.time + BleedAfterDeath;
                    if (c && c.brain) c.brain.RemoveNoStandUpModifier(NoStandUp);
                }
                if (b.dead && (!c || Time.time >= b.stopTime))
                {
                    (done ?? (done = new List<Creature>())).Add(c);
                    continue;
                }
                if (!b.dead)
                {
                    // The AI draws weapons into a hand it no longer has; it falls out at once.
                    foreach (RagdollHand hand in b.lostHands)
                        if (hand && hand.grabbedHandle)
                            try { hand.TryRelease(); } catch { }
                    if (!b.down && Time.time >= b.deathTime - CollapseBefore) Down(b);
                    if (Time.time >= b.nextPickupCheck)
                    {
                        b.nextPickupCheck = Time.time + PickupCheckInterval;
                        NoPickups(b);
                    }
                }
                if (Time.time >= b.nextPulse)
                {
                    b.nextPulse = Time.time + PulseInterval;
                    Pulse(b);
                }
            }
            if (done != null) foreach (Creature c in done) Forget(c);
        }

        private static void Forget(Creature creature)
        {
            if ((object)creature == null || !bleeders.TryGetValue(creature, out Bleeder b)) return;
            bleeders.Remove(creature);
            if (b.down && creature && creature.brain) creature.brain.RemoveNoStandUpModifier(NoStandUp);
            foreach (KeyValuePair<GetItem, float> kv in b.pickupsOff)
                if (kv.Key != null) kv.Key.searchRadius = kv.Value;
            b.pickupsOff.Clear();
        }

        // ---------- no weapon pickups without the right hand ----------
        // The game's AI always picks weapons up from the ground with its right hand (PickupItem never sets another side).
        // Without it, an enemy bent down, the weapon fell out of the missing hand, and again, and again. The item search of
        // his own behaviour tree (GetItem, in the unarmed subtree) gets a zero radius, which makes it find nothing. That
        // subtree is loaded when he loses his weapon, so the tree is walked again now and then.

        private const float PickupCheckInterval = 0.5f;
        private static readonly FieldInfo childTreeField =
            typeof(ChildTreeNode).GetField("childTree", BindingFlags.NonPublic | BindingFlags.Instance);

        private static void NoPickups(Bleeder b)
        {
            Creature c = b.creature;
            if (!c || !c.handRight || !b.lostHands.Contains(c.handRight)) return;
            BehaviorTreeData tree = c.brain ? c.brain.instance?.tree : null;
            if (tree == null) return;
            try { DisablePickups(tree.rootNode, b.pickupsOff, 0); }
            catch (Exception e) { Debug.LogWarning("[Zandatsu] weapon pickup off: " + e.Message); }
        }

        private static void DisablePickups(Node node, Dictionary<GetItem, float> off, int depth)
        {
            if (node == null || depth > 64) return;
            switch (node)
            {
                case GetItem search:
                    if (!off.ContainsKey(search))
                    {
                        off[search] = search.searchRadius;
                        search.searchRadius = 0f;   // GetItem fails at once on a zero radius
                    }
                    break;
                case ControlNode control:
                    if (control.childs != null)
                        foreach (Node child in control.childs) DisablePickups(child, off, depth + 1);
                    break;
                case DecoratorNode decorator:
                    DisablePickups(decorator.child, off, depth + 1);
                    break;
                case ChildTreeNode childTree:
                    if (childTreeField?.GetValue(childTree) is BehaviorTreeData sub) DisablePickups(sub.rootNode, off, depth + 1);
                    break;
            }
        }

        /// <summary>Creatures are pooled: nothing of ours may stay on one put back.</summary>
        public static void OnDespawn(Creature creature) => Forget(creature);

        public static void ClearAll()
        {
            foreach (Creature c in new List<Creature>(bleeders.Keys)) Forget(c);
        }
    }
}
