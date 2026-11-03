using System.Collections.Generic;
using System.Text;
using ThunderRoad;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zandatsu
{
    /// <summary>
    /// Lights pieces the way the game lights creatures. On levels assembled from areas (Outpost, dungeons) creature
    /// renderers do not use Unity light probes: LightVolumeReceiver turns lightProbeUsage off and registers the
    /// materials with a LightProbeVolume, which enables _PROBEVOLUME_ON and feeds 3D SH textures. Pieces carry copies of
    /// those materials but were plain renderers with Unity probes and no registration; there they rendered as blinding
    /// bloom blobs (NaN or huge lighting). Now: the level's volume if there is one, else Unity probes (as creatures).
    /// </summary>
    public static class PieceLighting
    {
        private static readonly int UseProbeVolume = Shader.PropertyToID("_UseProbeVolume");
        private static readonly int UseProbeVolumeLux = Shader.PropertyToID("_PROBEVOLUME");
        private static readonly int ProbeVolumeAmbient = Shader.PropertyToID("_ProbeVolumeAmbient");
        private static readonly int ProbeVolumeShR = Shader.PropertyToID("_ProbeVolumeShR");
        private static readonly int ProbeVolumeShG = Shader.PropertyToID("_ProbeVolumeShG");
        private static readonly int ProbeVolumeShB = Shader.PropertyToID("_ProbeVolumeShB");
        private static readonly int ProbeVolumeOcc = Shader.PropertyToID("_ProbeVolumeOcc");
        private const string Keyword = "_PROBEVOLUME_ON";

        public static void Apply(SlicedPiece piece, bool log)
        {
            // No probe anchor: the probes are sampled at the middle of the piece's bounds. The anchor used to be the
            // first body, and a body that settled under the floor left the whole piece black — the probes down there
            // are dark (the cut-off top of an item went black the same way when its anchor was the item's origin).
            Apply(piece.smr, piece.family, null, log);
        }

        /// <summary>Any renderer whose family-owned materials need lighting like a creature's (pieces, creature caps).</summary>
        public static void Apply(Renderer smr, PieceFamily family, Transform anchor, bool log)
        {
            if (!smr || family == null) return;
            var mats = new List<Material>();
            foreach (Material m in smr.sharedMaterials)
                if (m && family.owned.Contains(m) && !mats.Contains(m)) mats.Add(m);

            int keywordBefore = 0;
            foreach (Material m in mats) if (m.IsKeywordEnabled(Keyword)) keywordBefore++;

            LightProbeVolume volume = family.lightVolume;
            string how;
            if (!volume)
            {
                Bounds b = smr.bounds;
                LightVolumeReceiver.GetVolumeFromBoundsOrPosition(b, b.center, out volume);
            }
            if (volume)
            {
                family.RegisterLighting(volume, mats);
                smr.lightProbeUsage = LightProbeUsage.Off;
                how = "volume " + volume.name;
            }
            else
            {
                foreach (Material m in mats) DisableVolume(m);
                smr.lightProbeUsage = LightProbeUsage.BlendProbes;
                how = "unity probes";
            }
            smr.probeAnchor = anchor;

            if (log) Debug.Log("[Zandatsu] lighting: " + how + Diagnose(smr, mats, keywordBefore, family.sourceProbeUsage));
        }

        public static void DisableVolume(Material m)
        {
            m.SetFloat(UseProbeVolume, 0f);
            m.SetFloat(UseProbeVolumeLux, 0f);
            m.DisableKeyword(Keyword);
            m.SetTexture(ProbeVolumeAmbient, null);
            m.SetTexture(ProbeVolumeShR, null);
            m.SetTexture(ProbeVolumeShG, null);
            m.SetTexture(ProbeVolumeShB, null);
            m.SetTexture(ProbeVolumeOcc, null);
        }

        /// <summary>What the piece is lit with: Unity SH at its position, material state, any NaN/huge material value.</summary>
        private static string Diagnose(Renderer smr, List<Material> mats, int keywordBefore, LightProbeUsage source)
        {
            var sb = new StringBuilder();
            sb.Append(", creature renderers used ").Append(source);
            sb.Append(", copies with ").Append(Keyword).Append(" at the cut ").Append(keywordBefore).Append('/').Append(mats.Count);
            try
            {
                Vector3 pos = smr.bounds.center;
                LightProbes.GetInterpolatedProbe(pos, smr, out SphericalHarmonicsL2 sh);
                float max = 0f;
                bool nan = false;
                for (int c = 0; c < 3; c++)
                    for (int i = 0; i < 9; i++)
                    {
                        float v = sh[c, i];
                        if (float.IsNaN(v) || float.IsInfinity(v)) nan = true;
                        else max = Mathf.Max(max, Mathf.Abs(v));
                    }
                sb.Append(", unity probe SH max ").Append(max.ToString("0.###")).Append(nan ? " NaN!" : "");
                sb.Append(", ambient ").Append(RenderSettings.ambientMode);
            }
            catch (System.Exception e)
            {
                sb.Append(", SH check failed: ").Append(e.Message);
            }

            var bad = new List<string>();
            foreach (Material m in mats)
            {
                Shader s = m.shader;
                if (!s) continue;
                int count = s.GetPropertyCount();
                for (int i = 0; i < count && bad.Count < 8; i++)
                {
                    string name = s.GetPropertyName(i);
                    switch (s.GetPropertyType(i))
                    {
                        case ShaderPropertyType.Float:
                        case ShaderPropertyType.Range:
                            if (Bad(m.GetFloat(name))) bad.Add(m.name + "." + name + "=" + m.GetFloat(name));
                            break;
                        case ShaderPropertyType.Color:
                            Color c = m.GetColor(name);
                            if (Bad(c.r) || Bad(c.g) || Bad(c.b) || Bad(c.a)) bad.Add(m.name + "." + name + "=" + c);
                            break;
                        case ShaderPropertyType.Vector:
                            Vector4 v = m.GetVector(name);
                            if (Bad(v.x) || Bad(v.y) || Bad(v.z) || Bad(v.w)) bad.Add(m.name + "." + name + "=" + v);
                            break;
                    }
                }
            }
            sb.Append(bad.Count > 0 ? ", suspicious material values: " + string.Join("; ", bad) : ", material values ok");
            return sb.ToString();
        }

        private static bool Bad(float f) => float.IsNaN(f) || float.IsInfinity(f) || Mathf.Abs(f) > 1e5f;
    }

    /// <summary>Key the family's materials are registered under with a LightProbeVolume (it wants a MonoBehaviour).</summary>
    public class FamilyLightKey : MonoBehaviour { }

    /// <summary>
    /// Lights an object's kept cut face as the game lights the object. Its LightVolumeReceiver registers the object's own
    /// materials with the volume it is in (the renderers' Unity probes off) and moves them on as the object goes from area
    /// to area. The cut face is a renderer of the mod's with copies of those materials, the volume's keyword on: left on
    /// Unity probes and unregistered, it went blinding white under bloom on Outpost, as pieces of enemies had. It follows
    /// the object's receiver; the registration is the cut face's own (this component is its key).
    /// </summary>
    public class ItemCapLighting : MonoBehaviour
    {
        private static readonly int ProbeVolumeShR = Shader.PropertyToID("_ProbeVolumeShR");
        private LightVolumeReceiver receiver;
        private LightProbeVolume registered;
        private Renderer cap;
        private Material[] mats;

        public static void Attach(Item item, Renderer cap)
        {
            var mats = new List<Material>();
            foreach (Material m in cap.sharedMaterials) if (m && m.HasProperty(ProbeVolumeShR)) mats.Add(m);
            if (mats.Count == 0) return;   // the hot steel of a weapon's: unlit
            var l = cap.gameObject.AddComponent<ItemCapLighting>();
            l.cap = cap;
            l.mats = mats.ToArray();
            l.receiver = item.lightVolumeReceiver;
            if (l.receiver) l.receiver.onVolumeChangeEvent += l.OnVolumeChange;
            LightProbeVolume volume = l.receiver ? l.receiver.currentLightProbeVolume : null;
            if (!volume && LightProbeVolume.Exists)
            {
                Bounds b = cap.bounds;
                LightVolumeReceiver.GetVolumeFromBoundsOrPosition(b, b.center, out volume);
            }
            l.Use(volume);
        }

        private void OnVolumeChange(LightProbeVolume current, List<LightProbeVolume> all) => Use(current);

        private void Use(LightProbeVolume volume)
        {
            if (!cap) return;
            if (registered && registered != volume) registered.UnregisterMaterials(this);
            if (volume)
            {
                if (registered != volume) volume.RegisterMaterials(this, mats);
                cap.lightProbeUsage = LightProbeUsage.Off;
            }
            else
            {
                foreach (Material m in mats) if (m) PieceLighting.DisableVolume(m);
                cap.lightProbeUsage = LightProbeUsage.BlendProbes;
            }
            registered = volume;
        }

        /// <summary>Off the volume while its materials are still alive (they go with the cut face on despawn).</summary>
        public void Release()
        {
            if (receiver) receiver.onVolumeChangeEvent -= OnVolumeChange;
            receiver = null;
            if (registered) registered.UnregisterMaterials(this);
            registered = null;
        }

        private void OnDestroy() => Release();
    }
}
