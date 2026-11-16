using System;
using System.Collections.Generic;
using ThunderRoad;
using UnityEngine;

namespace Zandatsu
{
    // The original wound is gone when its cap is cut off or rebuilt.
    internal sealed class CapBlood : MonoBehaviour
    {
        private readonly List<EffectInstance> effects = new List<EffectInstance>();
        private SkinnedMeshRenderer cap;
        private Mesh mesh;
        private HybridState state;
        private Transform anchor;
        private Vector3 localPosition;
        private Quaternion localRotation;

        internal RagdollPart Part { get; private set; }

        internal void Setup(SkinnedMeshRenderer renderer, HybridState creatureState, RagdollPart part,
                            SkelBody body, Vector3 position, Vector3 direction)
        {
            cap = renderer;
            mesh = cap.sharedMesh;
            state = creatureState;
            Part = part;
            anchor = body.live;
            // The cut was computed in the snapshot pose, even if the ragdoll has already moved.
            localPosition = body.bind.MultiplyPoint3x4(position);
            localRotation = Quaternion.Inverse(body.rot) * Quaternion.LookRotation(direction);
        }

        private bool WoundExists => cap && mesh && cap.sharedMesh == mesh && !cap.forceRenderingOff && anchor &&
                                    (!Part || !state.IsSevered(Part));

        internal bool Play(EffectData effect, float intensity)
        {
            if (!WoundExists) return false;
            if (!isActiveAndEnabled) return true;
            try
            {
                EffectInstance instance = effect.Spawn(anchor.TransformPoint(localPosition), anchor.rotation * localRotation,
                                                       anchor, null, true, null, false, intensity, 1f);
                if (instance.effects != null && instance.effects.Count > 0) effects.Add(instance);
                instance.SetIntensity(intensity);
                instance.Play();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Zandatsu] cut blood: " + e.Message);
                return false;
            }
        }

        private void LateUpdate()
        {
            if (!WoundExists)
            {
                StopEffects();
                Destroy(this);
                return;
            }
            effects.RemoveAll(effect => effect.effects == null || effect.effects.Count == 0);
        }

        private void OnDisable() => StopEffects();

        private void StopEffects()
        {
            foreach (EffectInstance effect in effects) effect.Despawn();
            effects.Clear();
        }
    }
}
