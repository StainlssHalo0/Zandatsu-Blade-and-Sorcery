using ThunderRoad;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zandatsu
{
    /// <summary>Tissue strands attached to both sides of a torn joint, breaking at limb-specific distances.</summary>
    internal sealed class TearStrands : MonoBehaviour
    {
        private const int StrandCount = 9;
        private const int Rings = 12;
        private const int Sides = 7;
        private readonly Transform[] limbAnchors = new Transform[StrandCount];
        private readonly Vector3[] limbPoints = new Vector3[StrandCount];
        private readonly Vector3[] stumpPoints = new Vector3[StrandCount];
        private readonly float[] breakLengths = new float[StrandCount];
        private readonly float[] snappedAt = new float[StrandCount];
        private float thickness;
        private Transform stump;
        private CapBlood wound;
        private SlicedPiece piece;
        private Mesh mesh;
        private Vector3[] vertices;
        private Vector3[] normals;

        internal static Vector2 ReachFor(RagdollPart.Type type)
        {
            switch (type)
            {
                case RagdollPart.Type.LeftHand:
                case RagdollPart.Type.RightHand:
                case RagdollPart.Type.LeftFoot:
                case RagdollPart.Type.RightFoot: return new Vector2(0.45f, 0.65f);
                case RagdollPart.Type.LeftLeg:
                case RagdollPart.Type.RightLeg: return new Vector2(1.05f, 1.45f);
                case RagdollPart.Type.Neck:
                case RagdollPart.Type.Head: return new Vector2(0.55f, 0.80f);
                default: return new Vector2(0.75f, 1.05f);
            }
        }

        internal static void Create(SlicedPiece piece, PieceSpec spec, Skeleton skeleton, CreatureRecipe recipe)
        {
            if (!piece.fill || !recipe.capBlood || recipe.capKeptBody < 0 || !spec.hasCap) return;
            int submesh = spec.data.mats.IndexOf(piece.fill);
            if (submesh < 0 || spec.data.subs[submesh].Count < 3) return;
            var go = new GameObject("Zandatsu_TearStrands");
            go.layer = piece.gameObject.layer;
            go.transform.SetParent(piece.transform, false);
            var strands = go.AddComponent<TearStrands>();
            strands.piece = piece;
            strands.wound = recipe.capBlood;
            SkelBody kept = skeleton.bodies[recipe.capKeptBody];
            strands.stump = kept.live;
            var tornBody = skeleton.bodies[spec.bodies[spec.capBody]].live;
            var tornPart = tornBody ? tornBody.GetComponent<RagdollPart>() : null;
            Vector2 reach = ReachFor(tornPart ? tornPart.type : RagdollPart.Type.LeftArm);
            var triangles = spec.data.subs[submesh];
            int count = triangles.Count / 3;
            var attachmentPoints = new Vector3[StrandCount];
            var capBounds = new Bounds(spec.capCenter, Vector3.zero);
            foreach (int vertex in triangles) capBounds.Encapsulate(spec.data.v[vertex]);
            strands.thickness = Mathf.Clamp(capBounds.size.magnitude * 0.035f, 0.003f, 0.011f);
            for (int i = 0; i < StrandCount; i++)
            {
                // Spread attachments over the cap. Consecutive triangle indices often all lie on the same small patch.
                int index = 0;
                float best = -1f;
                for (int triangle = 0; triangle < count; triangle++)
                {
                    int at = triangle * 3;
                    Vector3 x = spec.data.v[triangles[at]], y = spec.data.v[triangles[at + 1]], z = spec.data.v[triangles[at + 2]];
                    float area = Vector3.Cross(y - x, z - x).sqrMagnitude;
                    if (area < 1e-12f) continue;
                    Vector3 candidate = (x + y + z) / 3f;
                    float score = area;
                    if (i > 0)
                    {
                        score = float.PositiveInfinity;
                        for (int j = 0; j < i; j++) score = Mathf.Min(score, (candidate - attachmentPoints[j]).sqrMagnitude);
                    }
                    if (score > best) { best = score; index = at; }
                }
                int a = triangles[index], b = triangles[index + 1], c = triangles[index + 2];
                Vector3 p = (spec.data.v[a] + spec.data.v[b] + spec.data.v[c]) / 3f;
                attachmentPoints[i] = p;
                strands.snappedAt[i] = -1f;
                strands.breakLengths[i] = Mathf.Lerp(reach.x, reach.y, (i * 0.618034f) % 1f);
                int body = spec.data.w[a].boneIndex0;
                SkelBody bone = skeleton.bodies[spec.bodies[body]];
                strands.limbAnchors[i] = piece.bodies[body].transform;
                strands.limbPoints[i] = Quaternion.Inverse(bone.rot) * (p - bone.pos);
                strands.stumpPoints[i] = kept.bind.MultiplyPoint3x4(p);
            }
            strands.BuildMesh();
            go.AddComponent<MeshFilter>().sharedMesh = strands.mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = piece.fill;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            strands.LateUpdate();
        }

        private void BuildMesh()
        {
            vertices = new Vector3[StrandCount * Rings * Sides];
            normals = new Vector3[vertices.Length];
            var uv = new Vector2[vertices.Length];
            var triangles = new int[StrandCount * (Rings - 2) * Sides * 6];
            int at = 0;
            for (int strand = 0; strand < StrandCount; strand++)
                for (int ring = 0; ring < Rings; ring++)
                    for (int side = 0; side < Sides; side++)
                    {
                        int a = (strand * Rings + ring) * Sides + side;
                        float along = (float)(ring < Rings / 2 ? ring : ring - 1) / (Rings - 2);
                        uv[a] = new Vector2((float)side / Sides, along * 2f);
                        // Two tubes meet at the middle while intact, then their free ends recoil independently.
                        if (ring == Rings - 1 || ring == Rings / 2 - 1) continue;
                        int b = (strand * Rings + ring) * Sides + (side + 1) % Sides;
                        triangles[at++] = a;
                        triangles[at++] = b;
                        triangles[at++] = a + Sides;
                        triangles[at++] = b;
                        triangles[at++] = b + Sides;
                        triangles[at++] = a + Sides;
                    }
            mesh = new Mesh { name = "Zandatsu_TissueStrands" };
            mesh.MarkDynamic();
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = triangles;
        }

        private void LateUpdate()
        {
            if (!ZandatsuScript.CheesePull || !piece || piece.IsDead || piece.busy || !stump || !stump.gameObject.activeInHierarchy ||
                !wound || !wound.isActiveAndEnabled)
            {
                Destroy(gameObject);
                return;
            }
            int remaining = 0;
            for (int i = 0; i < StrandCount; i++)
            {
                Vector3 a = stump.TransformPoint(stumpPoints[i]);
                Vector3 b = limbAnchors[i] ? limbAnchors[i].TransformPoint(limbPoints[i]) : a;
                float length = Vector3.Distance(a, b);
                // Measure the actual gap between wound attachments. Holding still cannot break intact fibres.
                float reach = breakLengths[i];
                if (snappedAt[i] < 0f && (!limbAnchors[i] || length >= reach)) snappedAt[i] = Time.time;
                float recoil = snappedAt[i] < 0f ? 0f : Mathf.Clamp01((Time.time - snappedAt[i]) / 0.25f);
                if (recoil < 1f) remaining++;
                Vector3 axis = length > 0.0001f ? (b - a) / length : Vector3.up;
                Vector3 u = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                Vector3 v = Vector3.Cross(axis, u);
                float stretch = Mathf.Clamp01(length / reach);
                float width = i % 3 == 0 ? 1.25f : i % 3 == 1 ? 0.45f : 0.8f;
                float radius = thickness * width / Mathf.Sqrt(1f + stretch * 3f);
                for (int ring = 0; ring < Rings; ring++)
                {
                    float rest = (float)(ring < Rings / 2 ? ring : ring - 1) / (Rings - 2);
                    float retreat = (1f - recoil) * (1f - recoil);
                    float t = ring < Rings / 2 ? rest * retreat : 1f - (1f - rest) * retreat;
                    float waist = 1f - 0.55f * Mathf.Sin(rest * Mathf.PI);
                    float tip = snappedAt[i] < 0f ? 1f : Mathf.Abs(rest * 2f - 1f);
                    float ripple = Mathf.Sin(rest * Mathf.PI * 3f + i * 2.4f) * 0.008f * (1f - stretch);
                    Vector3 center = Vector3.Lerp(a, b, t) +
                        (Vector3.down * (0.022f * (1f - stretch)) + u * ripple) * Mathf.Sin(t * Mathf.PI);
                    for (int side = 0; side < Sides; side++)
                    {
                        float angle = side * Mathf.PI * 2f / Sides;
                        Vector3 outward = u * Mathf.Cos(angle) + v * Mathf.Sin(angle);
                        int index = (i * Rings + ring) * Sides + side;
                        vertices[index] = transform.InverseTransformPoint(center + outward * (radius * waist * tip * (1f - recoil)));
                        normals[index] = transform.InverseTransformDirection(outward);
                    }
                }
            }
            if (remaining == 0) { Destroy(gameObject); return; }
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.RecalculateBounds();
        }

        private void OnDestroy()
        {
            if (mesh) Destroy(mesh);
        }
    }
}
