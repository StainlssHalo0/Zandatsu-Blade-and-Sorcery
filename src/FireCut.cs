using System;
using System.Collections.Generic;
using System.IO;
using ThunderRoad;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zandatsu
{
    /// <summary>
    /// Cuts made by a blade imbued with fire. The flesh cap is charred (a darkened copy of the game's slice material), a
    /// band of burnt crust with a ragged inner edge runs along the cut where the flesh meets the skin (BurnEdge.png: a
    /// smouldering rim that glows at first and dies down to black), and the cut smokes and sizzles for a few seconds
    /// (the game's own fire-imbue hit effect). Stronger charge, darker char.
    /// </summary>
    public static class FireCut
    {
        public const float GlowSeconds = 5f;
        public const float SmokeSeconds = 4f;
        public const float EasierCut = 0.5f;   // full charge: half the swing speed cuts
        private const string TextureFile = "BurnEdge.png";
        private const string SmokeEffectId = "HitImbueFire";

        // Seared meat, browner and darker than raw but still meat: the black burnt rim around it carries the char. Darker
        // (×0.3) made the middle look as black as the crust, and the ragged edge read as a black fill.
        private static readonly Color Charred = new Color(0.62f, 0.4f, 0.3f);
        private static readonly Color GlowHot = new Color(1.6f, 1.25f, 1.1f);    // above 1: the rim blooms at first
        private static readonly Color GlowCold = new Color(0.5f, 0.42f, 0.4f);   // burnt out: only the crust is left

        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly string[] TintProps = { "_BaseColor", "_Color", "_MainColor", "_Tint", "_TintColor" };

        private const string BundleFile = "zandatsu.shaders";
        private const string RimShaderAsset = "Assets/Zandatsu/BurnEdge.shader";
        private static readonly int GlowId = Shader.PropertyToID("_Glow");
        private static readonly int BurnFieldId = Shader.PropertyToID("_BurnField");
        private const string FieldName = "Zandatsu_BurnField";
        private static readonly Dictionary<Material, BurnField> fields = new Dictionary<Material, BurnField>();
        private static readonly int EmberColorId = Shader.PropertyToID("_EmberColor");
        private static readonly Color Ember = new Color(4f, 1.5f, 0.35f);   // HDR: embers bloom while hot

        private static Shader unlit, rimShader;
        private static bool bundleTried;
        private static AssetBundle bundle;
        private static Texture2D texture;
        private static bool textureTried, charLogged;
        private static EffectData smoke;
        private static bool smokeTried;

        private class Glow { public Material m; public float start; public Color hot; public bool own; }
        private static readonly List<Glow> glowing = new List<Glow>();
        private static readonly Dictionary<Material, float> charOf = new Dictionary<Material, float>();   // char material -> charge

        // ---------------- the blade ----------------

        /// <summary>Fire charge 0..1 of an imbue (0: none, or another spell).</summary>
        public static float Charge(Imbue imbue)
        {
            if (!imbue || imbue.spellCastBase == null || imbue.energy <= 0f) return 0f;
            string id = imbue.spellCastBase.id;
            if (string.IsNullOrEmpty(id) || id.IndexOf("Fire", StringComparison.OrdinalIgnoreCase) < 0) return 0f;
            return Mathf.Clamp01(imbue.EnergyRatio);
        }

        public static float Charge(ColliderGroup group) => group ? Charge(group.imbue) : 0f;

        /// <summary>The swing speed needed to cut with this much fire on the blade.</summary>
        public static float MinSpeed(float minSwingSpeed, float fire) =>
            ZandatsuScript.HotBladeCutsEasier && fire > 0f ? minSwingSpeed * (1f - EasierCut * fire) : minSwingSpeed;

        // ---------------- materials ----------------

        /// <summary>A charred copy of the flesh material, owned by the family. Null if the shader has no colour to darken.</summary>
        public static Material Char(Material fill, float fire, PieceFamily family)
        {
            if (!fill) return null;
            var m = new Material(fill) { name = fill.name + " (Zandatsu charred)" };
            Color tint = Color.Lerp(Color.white, Charred, 0.6f + 0.4f * Mathf.Clamp01(fire));
            var used = new List<string>();
            foreach (string p in TintProps)
            {
                if (!m.HasProperty(p)) continue;
                Color c = m.GetColor(p);
                m.SetColor(p, new Color(c.r * tint.r, c.g * tint.g, c.b * tint.b, c.a));
                used.Add(p);
            }
            string layer = CharLayer(m, family);
            if (!charLogged)
            {
                charLogged = true;
                Debug.Log($"[Zandatsu] fire cut: flesh shader {(m.shader ? m.shader.name : "none")}, darkened [{string.Join(",", used)}], {layer}");
            }
            if (used.Count == 0 && !m.IsKeywordEnabled(RevealKeyword))
            {
                Object.Destroy(m);
                return null;
            }
            family.owned.Add(m);
            charOf[m] = fire;
            return m;
        }

        // The game chars a burning corpse with a reveal layer of its body shader (LitMoss): the green layer (_Layer1, a
        // burnt skin texture) shows where the creature's reveal mask is green, and CharBehaviour paints the mask green as
        // it burns. The flesh cap uses the same shader: it gets the body's green layer and a mask that is green all over.
        private const string RevealKeyword = "_REVEALLAYERS";
        private static readonly string[] CharTextures = { "_Layer1", "_Layer1NormalMap" };
        private static readonly string[] CharFloats = { "_Layer1Metallic", "_Layer1Smoothness", "_Layer1NormalStrength" };
        private static Texture2D charMask;
        private static Material charSource;   // a copy owned here: the creature's own materials go with it

        /// <summary>Gives the flesh copy the game's burnt layer, fully shown. What happened, for the log.</summary>
        private static string CharLayer(Material m, PieceFamily family)
        {
            if (!m.HasProperty("_Layer1") || !m.HasProperty("_RevealMask")) return "no burnt layer in the flesh shader";
            Material src = null;
            foreach (Object o in family.owned)
                if (o is Material s && s && s != m && s.HasProperty("_Layer1") && s.GetTexture("_Layer1")) { src = s; break; }   // s: a destroyed one throws
            if (src && (!charSource || charSource.GetTexture("_Layer1") != src.GetTexture("_Layer1")))
            {
                if (charSource) Object.Destroy(charSource);
                charSource = new Material(src) { name = "Zandatsu_CharSource" };
            }
            if (!charSource || !charSource.GetTexture("_Layer1")) return "no burnt layer on the creature's materials";

            foreach (string p in CharTextures)
                if (charSource.HasProperty(p)) m.SetTexture(p, charSource.GetTexture(p));
            foreach (string p in CharFloats)
                if (charSource.HasProperty(p) && m.HasProperty(p)) m.SetFloat(p, charSource.GetFloat(p));
            if (charSource.HasProperty("_LayerSurfaceExp") && m.HasProperty("_LayerSurfaceExp"))
                m.SetVector("_LayerSurfaceExp", charSource.GetVector("_LayerSurfaceExp"));
            m.SetTextureScale("_Layer1", charSource.GetTextureScale("_Layer1"));
            m.SetTextureOffset("_Layer1", charSource.GetTextureOffset("_Layer1"));
            if (!charMask)
            {
                charMask = new Texture2D(4, 4, TextureFormat.RGBA32, false, true) { name = "Zandatsu_CharMask" };
                var px = new Color32[16];
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(0, 255, 0, 0);   // green: burnt; no blood, height or glow
                charMask.SetPixels32(px);
                charMask.Apply(false, true);
            }
            m.SetTexture("_RevealMask", charMask);
            if (m.HasProperty("_LayerMask")) m.SetTexture("_LayerMask", Texture2D.whiteTexture);
            if (m.HasProperty("_UseReveal")) m.SetFloat("_UseReveal", 1f);
            m.EnableKeyword(RevealKeyword);
            Texture t = charSource.GetTexture("_Layer1");
            return $"burnt layer {(t ? t.name : "?")} x{charSource.GetTextureScale("_Layer1")}";
        }

        /// <summary>The burnt rim along the cut, glowing and dying down. Null if BurnEdge.png or an unlit shader is missing.</summary>
        private static bool shaderLogged;

        public static Material Burn(float fire, PieceFamily family)
        {
            Texture2D tex = Texture();
            if (!tex) return null;
            Shader own = RimShader();
            if (own)
            {
                // Our own shader (zandatsu.shaders): lit like the game's materials, embers glowing in the cracks, the rim
                // drawn on the cut face by a distance field the slice fills in (FieldOf, Ready).
                var r = new Material(own) { name = "Zandatsu_BurnEdge" };
                r.SetTexture(BaseMapId, tex);
                fields[r] = new BurnField();
                r.SetColor(EmberColorId, Ember * (0.35f + 0.65f * Mathf.Clamp01(fire)));
                var og = new Glow { m = r, start = Time.time, own = true };
                SetGlow(og, 0f);
                glowing.Add(og);
                family.owned.Add(r);
                return r;
            }
            // The game's build has no "Universal Render Pipeline/Unlit" (the hot steel edge fell back to the particles
            // one too): the rim never got a shader and silently never appeared.
            if (!unlit)
                foreach (string name in new[] { "Universal Render Pipeline/Unlit", "Universal Render Pipeline/Particles/Unlit", "Unlit/Transparent Cutout" })
                {
                    unlit = Shader.Find(name);
                    if (unlit && unlit.isSupported) break;
                    unlit = null;
                }
            if (!shaderLogged)
            {
                shaderLogged = true;
                Debug.Log("[Zandatsu] burnt rim shader: " + (unlit ? unlit.name : "none found, no burnt rim"));
            }
            if (!unlit) return null;
            var m = new Material(unlit) { name = "Zandatsu_BurnEdge" };
            if (m.HasProperty(BaseMapId)) m.SetTexture(BaseMapId, tex);
            if (m.HasProperty(MainTexId)) m.SetTexture(MainTexId, tex);
            // Blended by the texture's alpha (the flesh shows through between the teeth). Alpha clipping drew the clear
            // part black: the build lacks that variant of the particles shader; alpha blending is what every particle uses.
            m.SetFloat("_Surface", 1f);   // transparent
            m.SetFloat("_Blend", 0f);     // alpha
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_AlphaClip", 0f);
            m.DisableKeyword("_ALPHATEST_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            Color hot = Color.Lerp(GlowCold, GlowHot, 0.35f + 0.65f * Mathf.Clamp01(fire));
            var g = new Glow { m = m, start = Time.time, hot = hot };
            SetGlow(g, 0f);
            glowing.Add(g);
            family.owned.Add(m);
            return m;
        }

        /// <summary>The distance field the slice fills for this rim material; null: the rim is the old strip.</summary>
        public static BurnField FieldOf(Material burn) =>
            !ReferenceEquals(burn, null) && fields.TryGetValue(burn, out BurnField f) ? f : null;

        /// <summary>Main thread, the slice done: its distance field becomes the rim material's texture.</summary>
        public static void Ready(Material burn, PieceFamily family)
        {
            if (!burn || !fields.TryGetValue(burn, out BurnField f)) return;
            fields.Remove(burn);
            if (f.texels == null || f.size <= 0) return;
            var tex = new Texture2D(f.size, f.size, TextureFormat.RGBA32, false, true)
            {
                name = FieldName, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
            };
            tex.LoadRawTextureData(f.texels);
            tex.Apply(false, false);
            burn.SetTexture(BurnFieldId, tex);
            family.owned.Add(tex);
        }

        public static bool IsFire(Material m) => m && (charOf.ContainsKey(m) || glowing.Exists(g => g.m == m) || m.name == "Zandatsu_BurnEdge");

        /// <summary>A copy for another owner (the corpse keeps its cut face after the pieces are gone), glowing in step.</summary>
        public static Material Adopt(Material m, PieceFamily family)
        {
            var copy = new Material(m) { name = m.name };
            family.owned.Add(copy);
            // The distance field belongs to the pieces' family too: the corpse gets its own copy.
            if (copy.HasProperty(BurnFieldId) && copy.GetTexture(BurnFieldId) is Texture2D field && field.name == FieldName)
            {
                Texture2D own = Object.Instantiate(field);
                own.name = FieldName;
                copy.SetTexture(BurnFieldId, own);
                family.owned.Add(own);
            }
            Glow src = glowing.Find(g => g.m == m);
            if (src != null) glowing.Add(new Glow { m = copy, start = src.start, hot = src.hot, own = src.own });
            if (charOf.TryGetValue(m, out float fire)) charOf[copy] = fire;
            return copy;
        }

        /// <summary>Every frame: the rims die down from glowing to burnt out.</summary>
        public static void Update()
        {
            for (int i = glowing.Count - 1; i >= 0; i--)
            {
                Glow g = glowing[i];
                if (!g.m) { glowing.RemoveAt(i); continue; }
                float t = (Time.time - g.start) / GlowSeconds;
                SetGlow(g, t);
                if (t >= 1f) glowing.RemoveAt(i);
            }
            if (charOf.Count > 64)
            {
                var dead = new List<Material>();
                foreach (Material m in charOf.Keys) if (!m) dead.Add(m);
                foreach (Material m in dead) charOf.Remove(m);
            }
            if (fields.Count > 16)   // fields of cuts that never finished
            {
                var dead = new List<Material>();
                foreach (Material m in fields.Keys) if (!m) dead.Add(m);
                foreach (Material m in dead) fields.Remove(m);
            }
        }

        private static void SetGlow(Glow g, float t)
        {
            if (g.own)
            {
                g.m.SetFloat(GlowId, 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t)));
                return;
            }
            Color c = Color.Lerp(g.hot, GlowCold, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t)));
            if (g.m.HasProperty(BaseColorId)) g.m.SetColor(BaseColorId, c);
            if (g.m.HasProperty(ColorId)) g.m.SetColor(ColorId, c);
        }

        private static Texture2D Texture()
        {
            if (textureTried) return texture;
            textureTried = true;
            try
            {
                string path = Path.Combine(Application.streamingAssetsPath, "Mods", "Zandatsu", TextureFile);
                if (!File.Exists(path))
                {
                    Debug.LogWarning("[Zandatsu] fire cut: " + TextureFile + " not found, no burnt rim");
                    return null;
                }
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = "Zandatsu_BurnEdge", wrapMode = TextureWrapMode.Repeat };
                if (tex.LoadImage(File.ReadAllBytes(path)))
                {
                    tex.wrapModeV = TextureWrapMode.Clamp;
                    tex.anisoLevel = 4;
                    texture = tex;
                    Debug.Log($"[Zandatsu] burn edge texture {tex.width}x{tex.height}");
                }
                else Object.Destroy(tex);
            }
            catch (Exception e) { Debug.LogWarning("[Zandatsu] burn edge texture: " + e.Message); }
            return texture;
        }

        /// <summary>
        /// The burnt rim shader from zandatsu.shaders (an asset bundle built with the B&S SDK), null without the file:
        /// then the rim falls back to the game's unlit particle shader.
        /// </summary>
        private static Shader RimShader()
        {
            if (bundleTried) return rimShader;
            bundleTried = true;
            try
            {
                string path = Path.Combine(Application.streamingAssetsPath, "Mods", "Zandatsu", BundleFile);
                if (!File.Exists(path)) return null;
                bundle = AssetBundle.LoadFromFile(path);   // stays loaded: its shaders are used all session
                Shader s = bundle ? bundle.LoadAsset<Shader>(RimShaderAsset) : null;
                if (s && s.isSupported) rimShader = s;
                Debug.Log("[Zandatsu] " + BundleFile + ": " + (!bundle ? "could not be loaded"
                    : !s ? "no " + RimShaderAsset
                    : s.isSupported ? "burnt rim shader " + s.name
                    : s.name + " is not supported here, the rim uses the unlit particle shader"));
            }
            catch (Exception e) { Debug.LogWarning("[Zandatsu] " + BundleFile + ": " + e.Message); }
            return rimShader;
        }

        /// <summary>Another shader of zandatsu.shaders (null without the file, or where it cannot run).</summary>
        internal static Shader BundleShader(string asset)
        {
            RimShader();
            if (!bundle) return null;
            try
            {
                Shader s = bundle.LoadAsset<Shader>(asset);
                return s && s.isSupported ? s : null;
            }
            catch (Exception e) { Debug.LogWarning("[Zandatsu] " + BundleFile + ": " + e.Message); return null; }
        }

        /// <summary>Loads the texture and the shader ahead (the first fire cut would read them from disk).</summary>
        public static void Preload()
        {
            Texture();
            RimShader();
        }

        // ---------------- smoke ----------------

        /// <summary>Sparks, scorch, then smoke and sizzle from the cut for a few seconds (the game's fire-imbue hit).</summary>
        public static void Smoke(Transform parent, Vector3 point, Vector3 normal)
        {
            if (!parent) return;
            if (!smokeTried)
            {
                smokeTried = true;
                smoke = Catalog.GetData<EffectData>(SmokeEffectId, false);
                if (smoke == null) Debug.LogWarning("[Zandatsu] fire cut: no effect " + SmokeEffectId);
            }
            if (smoke == null) return;
            try
            {
                EffectInstance ei = smoke.Spawn(point, Quaternion.LookRotation(normal), parent, null, true, null, false, 1f, 1f);
                ei.SetIntensity(1f);
                ei.Play();
                ZandatsuRunner.Instance.RunAfterRealtime(SmokeSeconds, () =>
                {
                    try { ei.End(); } catch { }
                });
            }
            catch (Exception e) { Debug.LogWarning("[Zandatsu] fire cut smoke: " + e.Message); }
        }
    }
}
