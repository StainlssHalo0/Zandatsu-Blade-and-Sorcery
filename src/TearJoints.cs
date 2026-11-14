using System.Collections.Generic;
using ThunderRoad;
using ThunderRoad.Skill;
using UnityEngine;

namespace Zandatsu
{
    /// <summary>Arms only the joints under the player's two grips, without enabling the game's blade dismemberment.</summary>
    internal sealed class TearJoints
    {
        private struct ArmedJoint
        {
            public RagdollPart part;
            public CharacterJoint joint;
            public float originalForce;
            public float appliedForce;

            public void Restore()
            {
                if (joint && joint.breakForce == appliedForce) joint.breakForce = originalForce;
            }
        }

        private readonly List<ArmedJoint> armed = new List<ArmedJoint>(4);
        private readonly Dictionary<RagdollPart, float> desired = new Dictionary<RagdollPart, float>(4);

        public void Update(Creature player, HashSet<Creature> cutting)
        {
            desired.Clear();
            if (player)
            {
                var left = player.handLeft;
                var right = player.handRight;
                if (left && right && left.grabbedHandle is HandleRagdoll leftGrip && right.grabbedHandle is HandleRagdoll rightGrip &&
                    player.TryGetSkill<SkillBruteForce>("BruteForce", out var skill))
                    AddPair(leftGrip, rightGrip, skill.minBreakForceMultiplier, cutting);

                var leftTk = player.mana?.casterLeft?.telekinesis;
                var rightTk = player.mana?.casterRight?.telekinesis;
                if (leftTk != null && rightTk != null && (leftTk.allowDismemberment || rightTk.allowDismemberment))
                    AddPair(leftTk.catchedHandle as HandleRagdoll, rightTk.catchedHandle as HandleRagdoll,
                            rightTk.allowDismemberment ? rightTk.dismembermentBreakForceMultiplier : leftTk.dismembermentBreakForceMultiplier,
                            cutting);
            }

            for (int i = armed.Count - 1; i >= 0; i--)
            {
                ArmedJoint entry = armed[i];
                if (!entry.part || !entry.joint || entry.part.characterJoint != entry.joint ||
                    !desired.TryGetValue(entry.part, out float force))
                {
                    entry.Restore();
                    armed.RemoveAt(i);
                    continue;
                }
                if (force != entry.appliedForce)
                {
                    entry.joint.breakForce = force;
                    entry.appliedForce = force;
                    armed[i] = entry;
                }
                desired.Remove(entry.part);
            }

            foreach (var pair in desired)
            {
                CharacterJoint joint = pair.Key.characterJoint;
                armed.Add(new ArmedJoint { part = pair.Key, joint = joint, originalForce = joint.breakForce, appliedForce = pair.Value });
                joint.breakForce = pair.Value;
            }
        }

        private void AddPair(HandleRagdoll left, HandleRagdoll right, float multiplier, HashSet<Creature> cutting)
        {
            if (!left || !right) return;
            RagdollPart a = left.ragdollPart, b = right.ragdollPart;
            if (!a || !b || a == b || !a.ragdoll || a.ragdoll != b.ragdoll) return;
            Creature creature = a.ragdoll.creature;
            if (!creature || creature.isPlayer || !creature.gameObject.activeInHierarchy || cutting.Contains(creature)) return;
            AddPart(a, multiplier);
            AddPart(b, multiplier);
            if (a.parentPart && a.parentPart.type != RagdollPart.Type.Torso) AddPart(a.parentPart, multiplier);
            if (b.parentPart && b.parentPart.type != RagdollPart.Type.Torso) AddPart(b.parentPart, multiplier);
        }

        private void AddPart(RagdollPart part, float multiplier)
        {
            if (!part.characterJoint || part.characterJointLocked || !part.ripBreak || part.isSliced) return;
            float force = part.ripBreakForce * multiplier / Mathf.Max(0.01f, Time.timeScale);
            if (!desired.TryGetValue(part, out float previous) || force < previous) desired[part] = force;
        }

        public void Clear(Creature creature = null)
        {
            desired.Clear();
            for (int i = armed.Count - 1; i >= 0; i--)
            {
                ArmedJoint entry = armed[i];
                if (creature && entry.part && entry.part.ragdoll && entry.part.ragdoll.creature != creature) continue;
                entry.Restore();
                armed.RemoveAt(i);
            }
        }
    }
}
