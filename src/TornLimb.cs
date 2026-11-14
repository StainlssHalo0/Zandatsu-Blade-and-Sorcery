using System.Collections.Generic;
using ThunderRoad;
using ThunderRoad.Skill.SpellPower;
using UnityEngine;

namespace Zandatsu
{
    /// <summary>Native handles for a detached piece, positioned using the enemy's original grips and the remaining mesh.</summary>
    public sealed class TornLimb : MonoBehaviour
    {
        private readonly List<Handle> handles = new List<Handle>();
        private readonly List<(Handle source, Handle target)> transfers = new List<(Handle, Handle)>();

        public void Build(SlicedPiece piece, PieceSpec spec, Skeleton skeleton)
        {
            for (int i = 0; i < piece.bodies.Count; i++)
            {
                SkelBody bone = skeleton.bodies[spec.bodies[i]];
                PieceBody body = piece.bodies[i];
                RagdollPart original = bone.live ? bone.live.GetComponent<RagdollPart>() : null;
                if (original)
                {
                    foreach (HandleRagdoll source in original.handles)
                        if (source && source.data != null) AddHandle(body, bone, spec.data, i, source);
                }
                else if (bone.live && bone.live.TryGetComponent(out PieceBody previous) && previous.nativeHandle)
                    AddHandle(body, bone, spec.data, i, previous.nativeHandle);
            }
            if (handles.Count == 0 && piece.bodies.Count > 0)
                AddHandle(piece.bodies[0], skeleton.bodies[spec.bodies[0]], spec.data, 0, null);
        }

        private void AddHandle(PieceBody body, SkelBody bone, MeshData mesh, int index, Handle source)
        {
            Quaternion inverse = bone.live ? Quaternion.Inverse(bone.live.rotation) : Quaternion.Inverse(bone.rot);
            Vector3 position = source ? inverse * (source.transform.position - bone.live.position) : Vector3.zero;
            Quaternion rotation = source ? inverse * source.transform.rotation : Quaternion.identity;
            Vector3 axis = rotation * Vector3.up;
            Quaternion snapshotInverse = Quaternion.Inverse(bone.rot);
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
            {
                if (mesh.w[vertex].boneIndex0 != index) continue;
                float along = Vector3.Dot(snapshotInverse * (mesh.v[vertex] - bone.pos) - position, axis);
                min = Mathf.Min(min, along);
                max = Mathf.Max(max, along);
            }
            if (min > max) return;
            float scale = source ? source.transform.TransformVector(Vector3.up).magnitude : 1f;
            float half = source ? source.axisLength * scale * 0.5f : 0.05f;
            float start = Mathf.Clamp(-half, min, max), end = Mathf.Clamp(half, min, max);
            position += axis * ((start + end) * 0.5f);

            var go = new GameObject("Zandatsu_LimbHandle");
            go.SetActive(false);
            go.transform.SetParent(body.transform, false);
            go.transform.localPosition = position;
            go.transform.localRotation = rotation;
            var handle = go.AddComponent<Handle>();
            handle.handOverlapColliders = new List<Collider>();
            foreach (Collider collider in body.GetComponentsInChildren<Collider>())
                if (!collider.isTrigger) handle.handOverlapColliders.Add(collider);
            handle.axisLength = end - start;
            handle.touchRadius = source ? source.touchRadius * scale : 0.06f;
            handle.orientationDefaultLeft = CopyPose(handle, source, Side.Left, scale);
            handle.orientationDefaultRight = CopyPose(handle, source, Side.Right, scale);
            go.SetActive(true);
            var data = source ? (HandleData)source.data.Clone() : new HandleData();
            data.disableOnStart = false;
            data.disableTouch = false;
            data.allowTelekinesis = true;
            data.forceClimbing = false;
            handle.Load(data);
            handles.Add(handle);
            if (!body.nativeHandle) body.nativeHandle = handle;
            if (source is HandleRagdoll) transfers.Add((source, handle));
        }

        private static HandlePose CopyPose(Handle target, Handle source, Side side, float scale)
        {
            HandlePose original = source ? (side == Side.Left ? source.orientationDefaultLeft : source.orientationDefaultRight) : null;
            HandlePose pose = target.AddOrientation(side,
                original ? source.transform.InverseTransformPoint(original.transform.position) * scale : Vector3.zero,
                original ? Quaternion.Inverse(source.transform.rotation) * original.transform.rotation : Quaternion.identity);
            if (original)
            {
                pose.defaultHandPoseId = original.defaultHandPoseId;
                pose.targetHandPoseId = original.targetHandPoseId;
                pose.targetWeight = original.targetWeight;
            }
            return pose;
        }

        public void TransferGrips()
        {
            foreach (var (source, target) in transfers)
            {
                if (!source || !target) continue;
                foreach (RagdollHand hand in source.handlers.ToArray())
                {
                    if (!hand || !hand.playerHand || hand.grabbedHandle != source) continue;
                    hand.UnGrab(false);
                    hand.Grab(target, target.GetDefaultOrientation(hand.side), target.GetNearestAxisPosition(hand.grip.position));
                }
                foreach (SpellCaster caster in source.telekinesisHandlers.ToArray())
                {
                    SpellTelekinesis tk = caster ? caster.telekinesis : null;
                    if (tk == null || tk.catchedHandle != source) continue;
                    if (!tk.TryRelease() && !tk.TryRelease()) continue;
                    tk.targetHandle = target;
                    if (!tk.TryCatch()) tk.targetHandle = null;
                }
            }
            transfers.Clear();
        }

        public void ReleaseGrips()
        {
            foreach (Handle handle in handles)
            {
                if (!handle) continue;
                handle.Release();
                handle.ReleaseAllTkHandlers();
            }
        }
    }
}
