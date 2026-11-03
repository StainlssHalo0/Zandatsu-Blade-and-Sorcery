using System;
using System.Collections.Generic;
using System.IO;
using ThunderRoad;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zandatsu
{
    /// <summary>
    /// The inside of a cut object (Cut Props): our own textures per kind of material (caps\*.png in the mod folder). The
    /// object's own texture was no good for it: most props share one atlas of many objects, and the cut face showed some
    /// other object's part of it, or a see-through one. Drawn with a copy of the object's own material (the game's
    /// LitMoss shader, lit like the object; the URP Lit shader in the game draws magenta) with our textures in it and the
    /// atlas's metal/smoothness map replaced by a plain one. Metal keeps the hot steel cut face of weapons.
    /// </summary>
    public static class CapTextures
    {
        public enum Kind { None, Metal, Wood, Stone, Ceramic, Bone, Fabric, Paper, Wicker, Straw, Fruit }

        /// <summary>Metres one repeat of a tiling texture covers (the generated images' scale). Wood is laid per cut face. Wicker (a coil with a middle) is mirrored at its edges: no seams where it repeats.</summary>
        public static float TileSize(Kind k) =>
            k == Kind.Stone ? 0.4f : k == Kind.Bone ? 0.08f : k == Kind.Paper ? 0.06f : k == Kind.Wicker ? 0.12f : k == Kind.Straw ? 0.2f : 0.15f;

        private static TextureWrapMode Wrap(Kind k) => k == Kind.Wicker ? TextureWrapMode.Mirror : TextureWrapMode.Repeat;

        private sealed class Set { public Texture2D albedo, normal, moes; }

        private static readonly Dictionary<Kind, Set> sets = new Dictionary<Kind, Set>();
        private static readonly Dictionary<(Kind, Material), Material> materials = new Dictionary<(Kind, Material), Material>();
        private static Material fallbackTemplate;
        private static bool loaded;

        /// <summary>Reads the textures ahead (the first cut would read them from disk).</summary>
        public static void Preload()
        {
            if (loaded) return;
            loaded = true;
            var found = new List<string>();
            foreach (Kind k in new[] { Kind.Wood, Kind.Stone, Kind.Ceramic, Kind.Bone, Kind.Fabric, Kind.Paper, Kind.Wicker, Kind.Straw, Kind.Fruit })
            {
                try
                {
                    string path = Path.Combine(Application.streamingAssetsPath, "Mods", "Zandatsu", "caps", k.ToString().ToLowerInvariant() + ".png");
                    if (!File.Exists(path)) continue;
                    var albedo = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = "Zandatsu_Cap_" + k };
                    if (!albedo.LoadImage(File.ReadAllBytes(path))) { Object.Destroy(albedo); continue; }
                    Texture2D normal = NormalFrom(albedo, k);
                    albedo.wrapMode = Wrap(k);
                    albedo.anisoLevel = 4;
                    albedo.Compress(true);
                    albedo.Apply(false, true);   // on the graphics card only
                    sets[k] = new Set { albedo = albedo, normal = normal, moes = Moes(k) };
                    found.Add($"{k} {albedo.width}x{albedo.height}");
                }
                catch (Exception e) { Debug.LogWarning($"[Zandatsu] cut face {k}: {e.Message}"); }
            }
            Debug.Log("[Zandatsu] cut faces: " + (found.Count > 0 ? string.Join(", ", found) : "no caps\\*.png, objects show their own texture"));
        }

        /// <summary>
        /// The kind whose texture is drawn: paper, wicker and straw as wood until their own textures are there; fruit its
        /// own surface until then (not the stuffing of the "Fabric" the game calls an apple).
        /// </summary>
        public static Kind Drawn(Kind k)
        {
            Preload();
            if (sets.ContainsKey(k)) return k;
            return k == Kind.Paper || k == Kind.Wicker || k == Kind.Straw ? Kind.Wood : k == Kind.Fruit ? Kind.None : k;
        }

        /// <summary>
        /// The cut face material of a kind for an object drawn with that material (its copy with our textures), null
        /// without the kind's texture or a material of the game's shader to copy (then the object's own surface is used).
        /// </summary>
        public static Material For(Kind k, Material objectMaterial)
        {
            Preload();
            k = Drawn(k);
            if (!sets.TryGetValue(k, out Set set)) return null;
            Material template = IsLitMoss(objectMaterial) ? objectMaterial : FallbackTemplate();
            if (!template) return null;
            if (materials.TryGetValue((k, template), out Material m) && m) return m;
            if (materials.Count > 64)
            {
                foreach (Material old in materials.Values) if (old) Object.Destroy(old);
                materials.Clear();
                foreach (Material old in insides.Values) if (old) Object.Destroy(old);   // copies of those cut faces
                insides.Clear();
            }
            m = new Material(template) { name = "Zandatsu_Cap_" + k };
            void Tex(string p, Texture t) { if (m.HasProperty(p)) { m.SetTexture(p, t); m.SetTextureScale(p, Vector2.one); m.SetTextureOffset(p, Vector2.zero); } }
            void Num(string p, float v) { if (m.HasProperty(p)) m.SetFloat(p, v); }
            void Vec(string p, Vector4 v) { if (m.HasProperty(p)) m.SetVector(p, v); }
            Tex("_BaseMap", set.albedo);
            Tex("_MainTex", set.albedo);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
            Tex("_BumpMap", set.normal);
            Num("_NormalStrength", 1f);
            Tex("_MetallicGlossMap", set.moes);
            Tex("_MossMetalMap", set.moes);
            Num("_Metallic", 0f);
            Num("_Smoothness", Smoothness(k));
            var whole = new Vector4(0f, 1f, 0f, 1f);
            Vec("_MetallicRemap", whole);
            Vec("_SmoothnessRemap", whole);
            Vec("_OcclusionRemap", whole);
            Vec("_EmissionRemap", whole);
            Num("_UseEmission", 0f);
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);
            materials[(k, template)] = m;
            return m;
        }

        private static readonly Dictionary<Material, Material> insides = new Dictionary<Material, Material>();

        /// <summary>The inside of a vessel the game models closed: its cut face material in shadow.</summary>
        public static Material Inside(Material cap)
        {
            if (!cap) return null;
            if (insides.TryGetValue(cap, out Material m) && m) return m;
            m = new Material(cap) { name = cap.name + "_Inside" };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", new Color(0.16f, 0.13f, 0.11f));
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0f);
            insides[cap] = m;
            return m;
        }

        /// <summary>
        /// The wall of a vessel the game models closed, without an inside (lidded pots, vases and jugs, barrels): its cut
        /// face is that wall round a dark inside. 0: not a vessel (or one with a real inner wall, which the cut finds).
        /// </summary>
        public static float HollowWall(Item item, Kind kind)
        {
            if (kind != Kind.Ceramic && kind != Kind.Wood && kind != Kind.Wicker) return 0f;
            string id = item.data != null ? item.data.id ?? "" : "";
            bool Has(params string[] words)
            {
                foreach (string w in words) if (id.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                return false;
            }
            if (Has("Lid", "Top", "Plate", "Part", "Ring", "Cork", "Board")) return 0f;
            if (Has("Barrel", "Bucket")) return 0.025f;
            if (Has("Crate", "Chest", "Box")) return 0.02f;
            if (Has("Pottery", "Jug", "Vase", "Amphora", "Urn", "Canope", "Cup", "Bowl", "Mug", "Goblet")) return 0.012f;
            return 0f;
        }

        private static bool IsLitMoss(Material m) => m && m.shader && m.shader.name.StartsWith("ThunderRoad/LitMoss") && m.HasProperty("_BaseMap");

        /// <summary>Any loaded material of the game's shader, for an object drawn with something else.</summary>
        private static Material FallbackTemplate()
        {
            if (fallbackTemplate) return fallbackTemplate;
            foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
                if (IsLitMoss(m) && m.IsKeywordEnabled("_NORMALMAP") && !m.IsKeywordEnabled("_ALPHATEST_ON") && !m.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"))
                {
                    fallbackTemplate = new Material(m) { name = "Zandatsu_CapTemplate" };
                    break;
                }
            return fallbackTemplate;
        }

        private static float Smoothness(Kind k) => k == Kind.Fruit ? 0.4f : k == Kind.Bone || k == Kind.Ceramic ? 0.25f : 0.1f;

        /// <summary>The game's packed map: metallic (R), occlusion (G), emission (B), smoothness (A). Not metal, no glow.</summary>
        private static Texture2D Moes(Kind k)
        {
            var t = new Texture2D(4, 4, TextureFormat.RGBA32, false, true) { name = "Zandatsu_Cap_MOES_" + k, wrapMode = TextureWrapMode.Repeat };
            var px = new Color32[16];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(0, 255, 0, (byte)(Smoothness(k) * 255f));
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        /// <summary>
        /// Relief from brightness (dark pores and cracks lie deeper), half the albedo's size. Packed as the game's shaders
        /// read an uncompressed normal map: x in red, y in green.
        /// </summary>
        private static Texture2D NormalFrom(Texture2D albedo, Kind k)
        {
            int mip = albedo.mipmapCount > 1 ? 1 : 0;
            int w = Mathf.Max(1, albedo.width >> mip), h = Mathf.Max(1, albedo.height >> mip);
            Color32[] px = albedo.GetPixels32(mip);
            if (px.Length != w * h) return null;
            var height = new float[w * h];
            for (int i = 0; i < px.Length; i++) height[i] = (px[i].r * 0.299f + px[i].g * 0.587f + px[i].b * 0.114f) / 255f;
            float strength = k == Kind.Bone ? 6f : k == Kind.Fabric ? 4f : k == Kind.Stone ? 3f : 2f;
            var outPx = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float H(int dx, int dy) => height[((y + dy + h) % h) * w + (x + dx + w) % w];
                    float gx = (H(1, -1) + 2f * H(1, 0) + H(1, 1)) - (H(-1, -1) + 2f * H(-1, 0) + H(-1, 1));
                    float gy = (H(-1, 1) + 2f * H(0, 1) + H(1, 1)) - (H(-1, -1) + 2f * H(0, -1) + H(1, -1));
                    Vector3 n = new Vector3(-gx * strength, -gy * strength, 1f).normalized;
                    outPx[y * w + x] = new Color32((byte)((n.x * 0.5f + 0.5f) * 255f), (byte)((n.y * 0.5f + 0.5f) * 255f),
                        (byte)((n.z * 0.5f + 0.5f) * 255f), 255);
                }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, true) { name = albedo.name + "_Normal", wrapMode = Wrap(k), anisoLevel = 4 };
            tex.SetPixels32(outPx);
            tex.Apply(true, true);
            return tex;
        }

        /// <summary>
        /// What the object is made of where the plane cuts it: the game's material of its colliders across the plane (all
        /// of them if none is), most of them; by name where the game's material says otherwise (bones and skulls are
        /// "Stone", so is some pottery). None: eggs, potions and the like keep their own surface.
        /// </summary>
        public static Kind Of(Item item, Vector3 point, Vector3 normal)
        {
            string id = item.data != null ? item.data.id ?? "" : "";
            bool Has(params string[] words)
            {
                foreach (string w in words) if (id.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                return false;
            }
            if (Has("Skeleton", "Skull", "Bone", "Jaw")) return Kind.Bone;
            if (Has("Book", "Journal", "Scroll")) return Kind.Paper;
            if (Has("Wicker", "Basket")) return Kind.Wicker;
            if (Has("ArcheryTarget") && !Has("Support")) return Kind.Straw;
            if (Has("Egg", "Potion", "Poo")) return Kind.None;
            if (Has("Apple", "Fruit", "Meiloorun", "Pear", "Melon", "Bread", "Cheese") || item.data?.type == ItemData.Type.Food) return Kind.Fruit;

            // By volume: a cart's many small iron fittings outnumbered its few big planks, and its cut face was steel.
            var across = new Dictionary<Kind, float>();
            var all = new Dictionary<Kind, float>();
            foreach (Collider c in item.GetComponentsInChildren<Collider>())
            {
                if (!c || !c.enabled || c.isTrigger || !c.sharedMaterial) continue;
                Kind k = KindOf(c.sharedMaterial.name.Replace(" (Instance)", ""));
                if (k == Kind.None) continue;   // foliage, dirt and the like: no say
                Bounds b = c.bounds;
                // Its own room, not its bounds' (a round shield's metal boss, a mesh, outweighed the wooden boards cut).
                float volume = Mathf.Max(ItemCut.Room(c), 1e-6f);
                all[k] = (all.TryGetValue(k, out float a) ? a : 0f) + volume;
                float reach = Mathf.Abs(normal.x) * b.extents.x + Mathf.Abs(normal.y) * b.extents.y + Mathf.Abs(normal.z) * b.extents.z;
                if (Mathf.Abs(Vector3.Dot(b.center - point, normal)) <= reach) across[k] = (across.TryGetValue(k, out float n) ? n : 0f) + volume;
            }
            Kind main = Kind.None;
            float most = 0f;
            foreach (KeyValuePair<Kind, float> kv in across.Count > 0 ? across : all)
                if (kv.Value > most) { most = kv.Value; main = kv.Key; }
            if (main == Kind.Stone && Has("Pottery", "Jug", "Cup", "Bowl", "Amphora", "Canope")) return Kind.Ceramic;
            return main;
        }

        /// <summary>The kind of cut face for one of the game's materials (a collider's physic material name).</summary>
        private static Kind KindOf(string material)
        {
            switch (material)
            {
                case "Wood":
                case "Rope": return Kind.Wood;
                case "Ceramic": return Kind.Ceramic;
                case "Stone": return Kind.Stone;
                case "Fabric":
                case "Leather": return Kind.Fabric;
                case "Metal":
                case "Blade":
                case "Plate":
                case "Chainmail": return Kind.Metal;
                default: return Kind.None;
            }
        }
    }
}
