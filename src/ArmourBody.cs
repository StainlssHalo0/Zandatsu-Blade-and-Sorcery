using System;
using System.Collections.Generic;
using ThunderRoad;
using ThunderRoad.Manikin;
using UnityEngine;

namespace Zandatsu
{
    // Keep the wearer's original body parts in the native wardrobe pipeline, including the pelvis and legs.
    internal sealed class ArmourBody : MonoBehaviour
    {
        private readonly Dictionary<ManikinWardrobeData, ManikinWardrobeData> originals = new Dictionary<ManikinWardrobeData, ManikinWardrobeData>();
        private Creature creature;
        private float nextCheck;
        private bool dirty = true;
        private readonly List<ManikinWardrobeData> baseParts = new List<ManikinWardrobeData>();
        private readonly HashSet<SkinnedMeshRenderer> skin = new HashSet<SkinnedMeshRenderer>();
        private bool ready;
        private bool skinCached;

        internal static void Attach(Creature creature)
        {
            if (!creature || creature.isPlayer || !creature.manikinLocations || creature.GetComponent<ArmourBody>()) return;
            var body = creature.gameObject.AddComponent<ArmourBody>();
            body.creature = creature;
            if (creature.manikinParts) creature.manikinParts.UpdateParts_Completed += body.WardrobeChanged;
        }

        private void WardrobeChanged(ManikinPart[] parts)
        {
            dirty = true;
            ready = false;
            skinCached = false;
            if (!creature || !creature.manikinParts || !creature.manikinLocations || originals.Count == 0) return;
            // Native fit shapes refer to the original wardrobe assets by identity.
            var equipped = new HashSet<ManikinWardrobeData>();
            foreach (var w in creature.manikinLocations.partLocations.Values)
                equipped.Add(originals.TryGetValue(w, out var original) ? original : w);
            var wardrobes = new ManikinWardrobeData[equipped.Count];
            equipped.CopyTo(wardrobes);
            // The game's array overload undercounts LOD group parents; the list overload does not.
            var allParts = new List<ManikinPart>();
            creature.manikinParts.GetAllParts(allParts, true);
            foreach (var part in allParts)
                if (part && part.blendShapes != null) part.UpdateBlendShapes(wardrobes);
            // Loading a hidden part is necessary for cutting, but must not expose skin through clothes.
            // Partial masks already remain native. Fully hidden layers need the equivalent shader mask
            // now that their geometry is loaded. Keep the original coverage per body location.
            if (creature.manikinProperties)
            {
                foreach (var body in baseParts)
                {
                    if (body.occlusionIDHash == 0) continue;
                    int hidden = 0;
                    for (int location = 0; location < body.channels.Length; location++)
                        foreach (var wardrobe in wardrobes)
                            for (int channel = 0; channel < wardrobe.channels.Length; channel++)
                            {
                                if (wardrobe.channels[channel] != body.channels[location]) continue;
                                int layer = 1 << body.layers[location];
                                if ((wardrobe.fullyOccludedLayers[channel] & layer) != 0) hidden = -1;
                                if ((wardrobe.partialOccludedLayers[channel] & layer) != 0)
                                    hidden |= wardrobe.partialOccludedMasks[channel];
                            }
                    creature.manikinProperties.occlusionBitmasks[body.occlusionIDHash] = hidden;
                }
                creature.manikinProperties.UpdateProperties();
            }
        }

        internal static bool HasBody(Creature creature)
        {
            var body = creature ? creature.GetComponent<ArmourBody>() : null;
            if (!body || !creature.manikinParts || !creature.manikinParts.IsDoneLoading() || body.baseParts.Count == 0) return false;
            body.CacheSkin();
            return body.ready;
        }

        internal static bool ExpectsNativeSkin(Creature creature)
        {
            var parts = creature.orgWardrobeLocations.wardrobe;
            if (parts != null)
                foreach (var part in parts)
                    if (part && part.name.EndsWith("_Base_Torso", StringComparison.Ordinal)) return true;
            return false;
        }

        private void CacheSkin()
        {
            if (skinCached || baseParts.Count == 0 || !creature.manikinParts || !creature.manikinParts.IsDoneLoading()) return;
            skin.Clear();
            ready = baseParts.Count > 0;
            foreach (var wardrobe in baseParts)
            {
                var part = creature.manikinParts.GetPartMatchingAssetGUID(wardrobe.assetPrefab.AssetGUID);
                if (!part) { ready = false; continue; }
                foreach (var renderer in part.GetComponentsInChildren<SkinnedMeshRenderer>(true)) skin.Add(renderer);
            }
            skinCached = true;
        }

        internal static bool IsSkinRenderer(Creature creature, SkinnedMeshRenderer renderer)
        {
            var body = creature ? creature.GetComponent<ArmourBody>() : null;
            if (!body) return false;
            body.CacheSkin();
            return body.skin.Contains(renderer);
        }

        private void Update()
        {
            if (!dirty || !creature || !creature.gameObject.activeInHierarchy || Time.time < nextCheck || !ZandatsuScript.Enabled) return;
            nextCheck = Time.time + 0.25f;
            // Manikin silently ignores UpdatePartGameObjects while wardrobe assets are loading.
            // Wait before replacing wardrobe entries so the reload that restores skin is actually submitted.
            if (!creature.manikinParts || !creature.manikinParts.IsDoneLoading()) return;
            if (creature.orgWardrobeLocations.wardrobe == null) return;
            if (baseParts.Count == 0)
                foreach (var w in creature.orgWardrobeLocations.wardrobe)
                    if (w && (w.name.EndsWith("_Base_Torso", StringComparison.Ordinal) ||
                              w.name.EndsWith("_Base_Legs", StringComparison.Ordinal) ||
                              w.name.EndsWith("_Base_Feet", StringComparison.Ordinal) ||
                              w.name.EndsWith("_Base_HandLeft", StringComparison.Ordinal) ||
                              w.name.EndsWith("_Base_HandRight", StringComparison.Ordinal) ||
                              w.name.EndsWith("_Base_Head", StringComparison.Ordinal))) baseParts.Add(w);
            if (baseParts.Count == 0) return;
            dirty = false;
            var bodyLayers = new Dictionary<string, int>();
            foreach (var part in baseParts)
                for (int i = 0; i < part.channels.Length; i++)
                {
                    bodyLayers.TryGetValue(part.channels[i], out int layers);
                    bodyLayers[part.channels[i]] = layers | (1 << part.layers[i]);
                }
            bool changed = false;
            var locations = creature.manikinLocations.partLocations;
            var seen = new HashSet<ManikinWardrobeData>();
            foreach (var pair in new List<KeyValuePair<ManikinLocations.LocationKey, ManikinWardrobeData>>(locations))
            {
                var w = pair.Value;
                if (!w || originals.ContainsKey(w) || !seen.Add(w)) continue;
                ManikinWardrobeData copy = null;
                for (int channel = 0; channel < w.channels.Length; channel++)
                {
                    if (!bodyLayers.TryGetValue(w.channels[channel], out int mask) ||
                        (w.fullyOccludedLayers[channel] & mask) == 0) continue;
                    if (!copy)
                    {
                        copy = Instantiate(w);
                        copy.name = w.name + " (Zandatsu body)";
                        copy.fullyOccludedLayers = (int[])w.fullyOccludedLayers.Clone();
                    }
                    // Manikin handles -1 as a special case and takes Log2 of other nonzero masks.
                    // Preserve its supported layer range without creating a different negative mask.
                    copy.fullyOccludedLayers[channel] &= int.MaxValue & ~mask;
                }
                if (!copy) continue;
                originals.Add(copy, w);
                foreach (var key in new List<ManikinLocations.LocationKey>(locations.Keys))
                    if (locations[key] == w) locations[key] = copy;
                changed = true;
            }
            if (changed)
            {
                ready = false;
                skinCached = false;
                creature.manikinLocations.UpdateParts();
                Debug.Log("[Zandatsu] restoring complete native body under clothing on " + creature.name);
            }
        }

        internal void Restore(bool rebuild)
        {
            if (creature && creature.manikinParts) creature.manikinParts.UpdateParts_Completed -= WardrobeChanged;
            if (creature && creature.manikinLocations)
            {
                var locations = creature.manikinLocations.partLocations;
                foreach (var key in new List<ManikinLocations.LocationKey>(locations.Keys))
                    if (originals.TryGetValue(locations[key], out var original)) locations[key] = original;
                if (rebuild && originals.Count > 0) creature.manikinLocations.UpdateParts();
            }
            foreach (var copy in originals.Keys) if (copy) Destroy(copy);
            originals.Clear();
            Destroy(this);
        }

        private void OnDestroy()
        {
            if (creature && creature.manikinParts) creature.manikinParts.UpdateParts_Completed -= WardrobeChanged;
            foreach (var copy in originals.Keys) if (copy) Destroy(copy);
            originals.Clear();
        }
    }
}
