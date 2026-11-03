using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Zandatsu
{
    /// <summary>
    /// Material for the cut edge of metal (armour, weapons): glowing hot steel that cools down over a few seconds. Unlit,
    /// so it glows in the dark. A blade burning with fire leaves it white-hot; a plain blade only dull red.
    /// An optional greyscale texture "HotSteel.png" in the mod folder adds surface detail; the heat colour tints it.
    /// </summary>
    public static class HotSteel
    {
        public const float CoolSeconds = 12f;
        public const float PlainBladeHeat = 0.5f;   // on the heat scale below: dull red, no bloom (fire: up to 1, white-hot)
        private const string TextureFile = "HotSteel.png";
        private const string Name = "Zandatsu_HotSteel";

        // Heat gradient: white-orange glow -> dull red -> cold steel. Values above 1 bloom where HDR is on.
        private static readonly Color Hot = new Color(2.6f, 1.05f, 0.3f);
        private static readonly Color Warm = new Color(0.95f, 0.16f, 0.03f);
        private static readonly Color Cold = new Color(0.2f, 0.2f, 0.22f);

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");

        private static Shader shader;
        private static bool shaderLogged;
        private static Texture2D texture;
        private static bool textureTried;
        private struct Cooling { public Material m; public float start, peak; }
        private static readonly List<Cooling> cooling = new List<Cooling>();

        /// <summary>
        /// How hot the cut edge of a blade with this much fire on it starts. Without fire and with Plain Blade Heats Steel
        /// off: cold grey steel from the start.
        /// </summary>
        public static float PeakFor(float fire) =>
            fire <= 0f && !ZandatsuScript.PlainBladeHeatsSteel ? 0f : PlainBladeHeat + (1f - PlainBladeHeat) * Mathf.Clamp01(fire);

        public static bool IsHotSteel(Material m) => m && m.name == Name;

        /// <summary>
        /// How hot a hot steel material is now, for a copy that glows on from there (the corpse's cut face). 0 once it has
        /// cooled: it leaves the cooling list then, and cold steel (Plain Blade Heats Steel off) leaves it at once; the
        /// corpse copy of such steel was made white-hot.
        /// </summary>
        public static float HeatOf(Material m)
        {
            foreach (Cooling c in cooling)
                if (c.m == m) return Mathf.Max(0f, c.peak - (Time.time - c.start) / CoolSeconds);
            return 0f;
        }

        /// <summary>
        /// A fresh glowing material for one cut (each cut cools on its own), starting at peak heat (PeakFor). Null if no
        /// usable shader.
        /// </summary>
        public static Material Create(Material fallback, float peak = 1f)
        {
            if (!shader)
            {
                foreach (string name in new[] { "Universal Render Pipeline/Unlit", "Universal Render Pipeline/Particles/Unlit", "Unlit/Texture", "Unlit/Color" })
                {
                    shader = Shader.Find(name);
                    if (shader && shader.isSupported) break;
                    shader = null;
                }
                if (!shaderLogged)
                {
                    shaderLogged = true;
                    Debug.Log("[Zandatsu] hot steel shader: " + (shader ? shader.name : "none, using the flesh material"));
                }
            }
            Material m;
            if (shader) m = new Material(shader);
            else if (fallback) m = new Material(fallback);
            else return null;
            m.name = Name;

            Texture2D tex = Texture();
            if (tex)
            {
                if (m.HasProperty(BaseMapId)) m.SetTexture(BaseMapId, tex);
                if (m.HasProperty(MainTexId)) m.SetTexture(MainTexId, tex);
            }
            SetHeat(m, peak);
            cooling.Add(new Cooling { m = m, start = Time.time, peak = peak });
            return m;
        }

        /// <summary>Called every frame: fades every live hot steel material towards cold steel.</summary>
        public static void Update()
        {
            for (int i = cooling.Count - 1; i >= 0; i--)
            {
                Material m = cooling[i].m;
                if (!m)
                {
                    cooling.RemoveAt(i);
                    continue;
                }
                // The same cooling rate from any start: a dull red edge goes grey sooner.
                float age = Time.time - cooling[i].start;
                float heat = cooling[i].peak - age / CoolSeconds;
                SetHeat(m, heat);
                if (heat <= 0f) cooling.RemoveAt(i);
            }
        }

        private static void SetHeat(Material m, float heat)
        {
            heat = Mathf.Clamp01(heat);
            // Glows near-white for the first moment, then reddens; the last third fades to grey steel.
            Color c = heat > 0.35f
                ? Color.Lerp(Warm, Hot, Mathf.Pow((heat - 0.35f) / 0.65f, 1.5f))
                : Color.Lerp(Cold, Warm, heat / 0.35f);
            if (m.HasProperty(BaseColorId)) m.SetColor(BaseColorId, c);
            if (m.HasProperty(ColorId)) m.SetColor(ColorId, c);
        }

        private static Texture2D Texture()
        {
            if (textureTried) return texture;
            textureTried = true;
            try
            {
                string path = Path.Combine(Application.streamingAssetsPath, "Mods", "Zandatsu", TextureFile);
                if (!File.Exists(path)) return null;
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = "Zandatsu_HotSteel", wrapMode = TextureWrapMode.Repeat };
                if (tex.LoadImage(File.ReadAllBytes(path)))
                {
                    tex.anisoLevel = 4;
                    texture = tex;
                    Debug.Log($"[Zandatsu] hot steel texture {tex.width}x{tex.height}");
                }
                else Object.Destroy(tex);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Zandatsu] hot steel texture: " + e.Message);
            }
            return texture;
        }
    }
}
