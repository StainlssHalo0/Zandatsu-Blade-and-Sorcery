using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using ThunderRoad;
using UnityEngine;

namespace ZandatsuLab
{
    /// <summary>
    /// Test bench for Zandatsu, never shipped: does nothing unless the game is started with "-zandatsulab &lt;list&gt;"
    /// ("all", or scenario names separated by commas). Each scenario runs in the real game with whatever other mods are
    /// installed: real blades are flown through real enemies, so the game's own hit events, Realistic Deaths, The Outer
    /// Rim and so on all take part. Results (PASS/FAIL per check, frame times, errors, screenshots) go to
    /// persistentDataPath/ZandatsuLab/&lt;run&gt;; the game quits at the end.
    /// </summary>
    public class LabScript : ThunderScript
    {
        private string list;
        private bool started;

        public override void ScriptEnable()
        {
            base.ScriptEnable();
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals("-zandatsulab", StringComparison.OrdinalIgnoreCase)) list = args[i + 1];
            if (list == null) return;
            Debug.Log("[ZandatsuLab] armed: " + list);
            EventManager.onLevelLoad += OnLevelLoad;
        }

        private void OnLevelLoad(LevelData levelData, LevelData.Mode mode, EventTime eventTime)
        {
            if (started || eventTime != EventTime.OnEnd || levelData == null) return;
            if (levelData.id == "MainMenu" || levelData.id == "CharacterSelection") return;
            started = true;
            var go = new GameObject("ZandatsuLab");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<LabRunner>().Run(list);
        }
    }

    public class LabRunner : MonoBehaviour
    {
        private const string Sword = "SwordLongCommon";
        private const string Saber = "Lightsaber_Anakin";
        private const string Bolt = "BlasterBolt";
        private const string MetalChest = "ApparelOfficerChest";

        private static readonly string[] All =
        {
            "swing_basic", "swing_slow", "swing_flat", "sweep_jitter", "armour", "fire", "limb", "recut_slow", "recut_fast",
            "cut_at_health", "pieces_again", "weapon_cut", "weapon_balance", "weapon_held", "saber", "blaster", "rd_arm",
            "prop_barrel", "prop_crate", "prop_chair", "prop_pottery", "prop_log", "prop_apple", "prop_stool", "prop_rock", "prop_skull",
            "prop_cushion", "prop_mug", "prop_cup", "prop_bowl", "prop_basket", "prop_mug2", "prop_book", "prop_target", "prop_light", "arrow",
            "debris_fall", "debris_recut", "debris_grab", "debris_props", "stab_pottery", "stab_stuck", "stab_stuck_prop", "stump_stack", "bow_cut", "shield_faces", "spell_off"
        };

        private string root, dir;
        private int shot;
        private readonly List<string> summary = new List<string>();
        private readonly List<string> entries = new List<string>();

        // Current scenario.
        private string current;
        private bool failed;
        private readonly List<float> frames = new List<float>();
        private readonly List<int> frameIds = new List<int>();
        private readonly List<int> logFrames = new List<int>();   // frame of each log line (log, same index)
        private volatile int frameNo;
        private readonly HashSet<int> shotFrames = new HashSet<int>();   // frames the bench rendered and saved a screenshot in
        private readonly List<GameObject> spawned = new List<GameObject>();

        // Every log line of the session (thread-safe: the game logs from worker threads too).
        private readonly List<(float t, LogType type, string msg)> log = new List<(float, LogType, string)>();
        private int logMark;
        private float baseline, hullBaseline;
        private readonly HashSet<int> seenEnemies = new HashSet<int>();   // instance ids of every enemy spawned so far
        private readonly Dictionary<int, string> lastUse = new Dictionary<int, string>();   // instance id -> last scenario it served in
        private bool reusedNow;
        private string reusedFrom;
        private Dictionary<FieldInfo, object> defaults;

        public void Run(string list)
        {
            root = Path.Combine(Application.persistentDataPath, "ZandatsuLab", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(root);
            Application.logMessageReceivedThreaded += OnLog;
            string[] names = list.Equals("all", StringComparison.OrdinalIgnoreCase) ? All : list.Split(',');
            StartCoroutine(Main(names));
        }

        private void OnLog(string msg, string stack, LogType type)
        {
            if (msg != null && msg.Contains("[ZandatsuLab]")) return;
            // Where it was thrown: in the mod (or the bench) if any frame is ours, else the game's (its first frame).
            if (type == LogType.Exception && !string.IsNullOrEmpty(stack))
                msg += " @ " + (stack.Split('\n').FirstOrDefault(l => l.Contains("Zandatsu")) ?? stack.Split('\n')[0]);
            lock (log)
            {
                log.Add((Time.realtimeSinceStartup, type, msg ?? ""));
                logFrames.Add(frameNo);
            }
        }

        private void Update()
        {
            frameNo = Time.frameCount;
            if (current == null) return;
            frames.Add(Time.unscaledDeltaTime);
            frameIds.Add(Time.frameCount);
        }

        // Where scenarios set things up: where the player stood when the bench began, facing the same way.
        private static bool staged;
        private static Vector3 stagePos, stageFwd;

        /// <summary>
        /// The game window shows the test: the player (body and hands hidden, they hung in front of the view) stands aside,
        /// facing the spot where objects and enemies are set up, which stays where the player stood. The window is the
        /// game's own headset mirror (a camera of the bench's own drawing to the window left it black).
        /// </summary>
        private void Stage()
        {
            HidePlayer();
            Creature me = Player.local ? Player.local.creature : null;
            if (!me) return;
            stagePos = me.transform.position;
            stageFwd = Vector3.ProjectOnPlane(me.transform.forward, Vector3.up).normalized;
            if (stageFwd.sqrMagnitude < 0.1f) stageFwd = Vector3.forward;
            staged = true;
            ClearStage();
            Vector3 right = Vector3.Cross(Vector3.up, stageFwd);
            Vector3 spot = stagePos + stageFwd * 2.1f;
            Vector3 eye = spot + right * 3f - stageFwd * 0.8f;
            Player.local.Teleport(eye, Quaternion.LookRotation(Vector3.ProjectOnPlane(spot - eye, Vector3.up)));
            Debug.Log($"[ZandatsuLab] stage: level {Level.current?.data?.id}, set up at {stagePos}, player watching from {eye}");
        }

        private static void HidePlayer()
        {
            Creature me = Player.local ? Player.local.creature : null;
            if (!me) return;
            foreach (Renderer r in me.GetComponentsInChildren<Renderer>(true)) if (r) r.forceRenderingOff = true;
        }

        /// <summary>What the game window shows, saved (to see the stage as a player watching would).</summary>
        private IEnumerator WindowShot(string name)
        {
            yield return new WaitForEndOfFrame();
            try { ScreenCapture.CaptureScreenshot(Path.Combine(dir, name + ".png")); }
            catch (Exception e) { Note("warning", "window shot: " + e.Message); }
        }

        private IEnumerator Main(string[] names)
        {
            yield return Wait(8f);
            Player.invincibility = true;
            Stage();
            Type zs = ZType("ZandatsuScript");
            // Every scenario starts from the mod's own defaults (src/ZandatsuScript.cs), not from the player's saved
            // options (a player's Recut Delay 0 made one swing cut three times). Only the running values change: the saved
            // options file is not touched.
            var declared = new Dictionary<string, object>
            {
                ["Enabled"] = true, ["MinSwingSpeed"] = 7f, ["CleanCutsOnly"] = true, ["CutThroughArmour"] = true,
                ["PlayerOnly"] = true, ["CutAtHealth"] = 100f, ["RecutPieces"] = true, ["RecutDelay"] = 0.5f,
                ["CutWeapons"] = true, ["CutProps"] = true, ["PalmChopCuts"] = true, ["FireCutsBurn"] = true, ["HotBladeCutsEasier"] = true,
                ["PlainBladeHeatsSteel"] = true, ["HybridCuts"] = true, ["LimbCutsNotFatal"] = true, ["BleedOutTime"] = 20f,
                ["VanillaDismemberment"] = false, ["SlowMotionOnCut"] = false, ["CutSound"] = true, ["SeparationSpeed"] = 1f,
                ["MaxPieces"] = 60, ["PieceLifetime"] = 120f, ["GrabPieces"] = true, ["PieceStiffness"] = 1f, ["DebugKey"] = false
            };
            defaults = new Dictionary<FieldInfo, object>();
            foreach (var kv in declared)
            {
                FieldInfo f = zs.GetField(kv.Key, BindingFlags.Public | BindingFlags.Static);
                if (f != null && f.FieldType == kv.Value.GetType()) defaults[f] = kv.Value;
                else Note("warning", "default for an unknown option " + kv.Key);
            }
            Note("player options", string.Join(", ", defaults.Where(kv => !Equals(kv.Key.GetValue(null), kv.Value)).Select(kv => $"{kv.Key.Name}={kv.Key.GetValue(null)}")));
            Note("mods", string.Join(", ", ModManager.loadedMods.Select(m => m.Name)));
            foreach (string raw in names)
            {
                string name = raw.Trim();
                MethodInfo m = GetType().GetMethod("S_" + name, BindingFlags.NonPublic | BindingFlags.Instance);
                if (m == null) { summary.Add($"SKIP {name}: no such scenario"); continue; }
                Begin(name);
                yield return Guard((IEnumerator)m.Invoke(this, null));
                yield return WindowShot("99_window");
                End();
                yield return Cleanup();
            }
            Finish();
        }

        // ---------- scenarios ----------

        /// <summary>A plain edge-first swing through the chest at 10 m/s: one hybrid cut, nothing stretched.</summary>
        private IEnumerator S_swing_basic()
        {
            Creature c = null;
            yield return Enemy(x => c = x);
            if (!c) yield break;
            yield return Shot("before", c);
            yield return SwingAt(c, RagdollPart.Type.Torso, 10f, false);
            yield return Wait(1.5f);
            Expect("cut", Cuts(c) >= 1, $"{Cuts(c)} cuts");
            Expect("one cut per swing", Cuts(c) <= 1, $"{Cuts(c)} cuts");
            ExpectNotStretched();
            yield return Shot("after", c);
        }

        /// <summary>Below Min Swing Speed (7 m/s): no cut.</summary>
        private IEnumerator S_swing_slow()
        {
            Creature c = null;
            yield return Enemy(x => c = x);
            if (!c) yield break;
            yield return SwingAt(c, RagdollPart.Type.Torso, 4f, false);
            yield return Wait(1f);
            Expect("no cut at 4 m/s", Cuts(c) == 0, $"{Cuts(c)} cuts");
        }

        /// <summary>Flat of the blade first, with Clean Cuts Only: no cut.</summary>
        private IEnumerator S_swing_flat()
        {
            Creature c = null;
            yield return Enemy(x => c = x);
            if (!c) yield break;
            yield return SwingAt(c, RagdollPart.Type.Torso, 10f, true);
            yield return Wait(1f);
            Expect("no cut with the flat", Cuts(c) == 0, $"{Cuts(c)} cuts");
        }

        /// <summary>
        /// A 4 m/s swing with a blade drawn without interpolation (the game renders more often than it steps physics, so
        /// the drawn blade jumps): the blade sweep must read the blade's real speed and not cut.
        /// </summary>
        private IEnumerator S_sweep_jitter()
        {
            Creature c = null;
            yield return Enemy(x => c = x);
            if (!c) yield break;
            Item blade = null;
            yield return SpawnItem(Sword, x => blade = x);
            if (!blade) yield break;
            yield return Swing(blade, Target(c, RagdollPart.Type.Torso), c.transform.right, c.transform.forward, 4f, false, interpolate: false);
            yield return Wait(1f);
            Expect("no cut at 4 m/s without interpolation", Cuts(c) == 0, $"{Cuts(c)} cuts; {LastLines("blade sweep", 2)}");
        }

        /// <summary>Metal chest armour: cut through (Cut Through Armour), the cut steel glows.</summary>
        private IEnumerator S_armour()
        {
            Creature c = null;
            yield return Enemy(x => c = x, MetalChest);
            if (!c) yield break;
            yield return Shot("before", c);
            yield return SwingAt(c, RagdollPart.Type.Torso, 10f, false);
            yield return Wait(1.5f);
            Expect("cut through armour", Cuts(c) >= 1, $"{Cuts(c)} cuts");
            Expect("hot steel on the cut", HasMaterial("Zandatsu_HotSteel"), MaterialsSeen());
            ExpectNotStretched();
            yield return Shot("after", c);
        }

        /// <summary>A fire-imbued blade: the cut is burnt (charred face, burnt rim).</summary>
        private IEnumerator S_fire()
        {
            Creature c = null;
            yield return Enemy(x => c = x);
            if (!c) yield break;
            yield return SwingAt(c, RagdollPart.Type.Torso, 10f, false, imbue: "Fire");
            yield return Wait(1.5f);
            Expect("cut", Cuts(c) >= 1, $"{Cuts(c)} cuts");
            Expect("burnt cut", LogHas("(burnt)") || HasMaterial("Zandatsu_BurnEdge"), MaterialsSeen());
            ExpectNotStretched();
            yield return Shot("after", c);
        }

        /// <summary>Upper arm only (Limb Cuts Not Fatal, bleed out 5 s): lives on, then bleeds out.</summary>
        private IEnumerator S_limb()
        {
            Set("BleedOutTime", 5f);
            Creature c = null;
            yield return Enemy(x => c = x);
            if (!c) yield break;
            yield return SwingAt(c, RagdollPart.Type.RightArm, 10f, false, vertical: true);
            yield return Wait(1.5f);
            Expect("arm cut", Cuts(c) >= 1, $"{Cuts(c)} cuts");
            Expect("alive after losing the arm", c && !c.isKilled, c ? $"health {c.currentHealth:0}" : "gone");
            yield return Shot("after", c);
            yield return Wait(7f);
            Expect("bled out", !c || c.isKilled || LogHas("bled out"), c ? $"alive {!c.isKilled}" : "gone");
            ExpectNotStretched();
        }

        /// <summary>Recut Delay 0.5 (default): two swings 0.25 s apart, same blade: the second is the same swing.</summary>
        private IEnumerator S_recut_slow() => Recut(0.5f, 1);

        /// <summary>Recut Delay 0: two swings 0.25 s apart both cut.</summary>
        private IEnumerator S_recut_fast() => Recut(0f, 2);

        private IEnumerator Recut(float delay, int expected)
        {
            Set("RecutDelay", delay);
            Creature c = null;
            yield return Enemy(x => c = x);
            if (!c) yield break;
            Item blade = null;
            yield return SpawnItem(Sword, x => blade = x);
            if (!blade) yield break;
            yield return Swing(blade, Target(c, RagdollPart.Type.Torso) + Vector3.up * 0.15f, c.transform.right, c.transform.forward, 10f, false);
            yield return Wait(0.1f);
            yield return Swing(blade, Target(c, RagdollPart.Type.Torso) - Vector3.up * 0.2f, c.transform.right, -c.transform.forward, 10f, false);
            yield return Wait(1.5f);
            Expect($"{expected}+ cut(s), at most {(expected == 1 ? 1 : 9)}, with Recut Delay {delay}", Cuts(c) >= expected && (expected > 1 || Cuts(c) == 1), $"{Cuts(c)} cuts");
            ExpectNotStretched();
        }

        /// <summary>Cut At Health 50%: no cut at full health; a cut once health is at 30%.</summary>
        private IEnumerator S_cut_at_health()
        {
            Set("CutAtHealth", 50f);
            Creature c = null;
            yield return Enemy(x => c = x);
            if (!c) yield break;
            yield return SwingAt(c, RagdollPart.Type.Torso, 10f, false);
            yield return Wait(1f);
            Expect("no cut at full health", Cuts(c) == 0, $"{Cuts(c)} cuts, health {c.currentHealth:0}/{c.maxHealth:0}");
            c.currentHealth = c.maxHealth * 0.3f;
            yield return Wait(1f);   // the first blow staggered him: the next swing waited for him to stand still
            Note("health", $"{c.currentHealth:0}/{c.maxHealth:0}, alive {!c.isKilled}, ragdoll {c.ragdoll.state}, chest at {Target(c, RagdollPart.Type.Torso)}");
            yield return Shot("before_second", c);
            // A staggered enemy now and then lets a flown blade pass without touching him (no damage, no hit): that is the
            // bench's aim, not the option, so such a swing is repeated (at most twice).
            for (int attempt = 0; attempt < 3 && Cuts(c) == 0; attempt++)
            {
                float hp = c.currentHealth;
                yield return SwingAt(c, RagdollPart.Type.Torso, 10f, false);
                yield return Wait(1.5f);
                if (Cuts(c) == 0 && Mathf.Approximately(hp, c.currentHealth)) Note("retry", "the swing did not touch him");
                else break;
            }
            Expect("cut at 30% health", Cuts(c) >= 1, $"{Cuts(c)} cuts, health now {c.currentHealth:0}, chest at {Target(c, RagdollPart.Type.Torso)}; {LastLines("[Zandatsu]", 3)}");
        }

        /// <summary>Hybrid Cuts off: two loose halves; a swing through the bigger one cuts it again.</summary>
        private IEnumerator S_pieces_again()
        {
            Set("HybridCuts", false);
            Creature c = null;
            yield return Enemy(x => c = x);
            if (!c) yield break;
            yield return SwingAt(c, RagdollPart.Type.Torso, 10f, false);
            yield return Wait(2f);
            Component big = Pieces().OrderByDescending(p => Size(p)).FirstOrDefault();
            Expect("halves are pieces", big, $"{Pieces().Count} pieces");
            if (!big) yield break;
            Vector3 at = Center(big);
            Item blade = null;
            yield return SpawnItem(Sword, x => blade = x);
            yield return Swing(blade, at, Vector3.Cross(Vector3.up, Vector3.forward), Vector3.up * -1f, 10f, false);
            yield return Wait(1.5f);
            Expect("piece cut again", LogHas("re-cut piece"), $"{Pieces().Count} pieces");
            ExpectNotStretched();
        }

        /// <summary>
        /// A torso cut whose piece settles on the floor, then probe lighting measured where the renderer actually
        /// samples it: its probeAnchor (the piece's first body) versus the piece's bounds centre. A piece black on a
        /// lit floor means the anchor samples dead probes (under the floor, or outside the baked tetrahedra), the same
        /// failure the cut-off top of an item had when its anchor was the item's origin. Diagnostic, not in "all".
        /// </summary>
        private IEnumerator S_piece_light()
        {
            Creature c = null;
            yield return Enemy(x => c = x);
            if (!c) yield break;
            yield return SwingAt(c, RagdollPart.Type.Torso, 10f, false);
            yield return Wait(3f);   // the pieces settle on the floor
            Expect("cut", Cuts(c) >= 1, $"{Cuts(c)} cuts");
            int dark = 0, total = 0;
            foreach (Component p in Pieces())
            {
                SkinnedMeshRenderer smr = p ? p.GetComponentInChildren<SkinnedMeshRenderer>() : null;
                if (!smr) continue;
                total++;
                var sb = new StringBuilder();
                sb.Append(p.name).Append(": usage ").Append(smr.lightProbeUsage)
                  .Append(", anchor ").Append(smr.probeAnchor ? smr.probeAnchor.name : "none");
                float atBounds = ShMax(smr.bounds.center, smr);
                Vector3 anchorPos = smr.probeAnchor ? smr.probeAnchor.position : smr.bounds.center;
                float atAnchor = ShMax(anchorPos, smr);
                sb.Append(", SH bounds centre ").Append(atBounds.ToString("0.###"))
                  .Append(", SH at anchor (y ").Append(anchorPos.y.ToString("0.00")).Append(") ").Append(atAnchor.ToString("0.###"))
                  .Append(", anchor ").Append(GroundNote(anchorPos, p.transform));
                foreach (object body in Bodies(p))
                {
                    Rigidbody rb = BodyRb(body);
                    if (!rb) continue;
                    sb.Append(" | ").Append(BodyName(body))
                      .Append(" SH ").Append(ShMax(rb.position, smr).ToString("0.###"))
                      .Append(' ').Append(GroundNote(rb.position, p.transform));
                }
                // A material still carrying the volume keyword while the renderer samples Unity probes would draw black.
                int kw = smr.sharedMaterials.Count(m => m && m.IsKeywordEnabled("_PROBEVOLUME_ON"));
                sb.Append(", _PROBEVOLUME_ON ").Append(kw).Append('/').Append(smr.sharedMaterials.Length);
                Note("piece light", sb.ToString());
                if (atBounds > 0.2f && atAnchor < 0.05f) dark++;
                if (kw > 0 && smr.lightProbeUsage == UnityEngine.Rendering.LightProbeUsage.BlendProbes) dark++;
            }
            Expect("pieces checked", total > 0, $"{total} pieces");
            Expect("no piece sampling dead probes", dark == 0, $"{dark} of {total} pieces dark at the anchor or with the volume keyword on Unity probes");
            Component big = Pieces().OrderByDescending(p => Size(p)).FirstOrDefault();
            if (big) yield return ShotNear("piece_settled", Center(big), 1.5f);
        }

        private static float ShMax(Vector3 pos, Renderer r)
        {
            try
            {
                LightProbes.GetInterpolatedProbe(pos, r, out UnityEngine.Rendering.SphericalHarmonicsL2 sh);
                float max = 0f;
                for (int ch = 0; ch < 3; ch++)
                    for (int i = 0; i < 9; i++)
                    {
                        float v = sh[ch, i];
                        if (!float.IsNaN(v) && !float.IsInfinity(v)) max = Mathf.Max(max, Mathf.Abs(v));
                    }
                return max;
            }
            catch { return float.NaN; }
        }

        /// <summary>Height above whatever is under the point (the piece's own colliders do not count).</summary>
        private static string GroundNote(Vector3 pos, Transform pieceRoot)
        {
            RaycastHit best = default;
            float dist = float.MaxValue;
            bool found = false;
            foreach (RaycastHit h in Physics.RaycastAll(pos + Vector3.up * 0.3f, Vector3.down, 4f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (h.transform.IsChildOf(pieceRoot)) continue;
                if (h.distance < dist) { dist = h.distance; best = h; found = true; }
            }
            if (!found) return "no ground under";
            float above = pos.y - best.point.y;
            return above < 0f ? $"UNDER the floor by {-above:0.000} m ({best.collider.name})" : $"{above:0.000} m above {best.collider.name}";
        }

        private static IEnumerable Bodies(Component piece) =>
            piece.GetType().GetField("bodies")?.GetValue(piece) as IEnumerable ?? new object[0];

        private static string BodyName(object body) =>
            body?.GetType().GetField("bodyName")?.GetValue(body) as string ?? "?";

        private static Rigidbody BodyRb(object body) =>
            body?.GetType().GetField("rb")?.GetValue(body) as Rigidbody;

        // ---------- Cut Props ----------

        private IEnumerator S_prop_barrel() => Prop("Barrel1", "Wood");
        private IEnumerator S_prop_crate() => Prop("Crate", "Wood");     // compressed: read through the capture shader
        private IEnumerator S_prop_chair() => Prop("Chair1", "Wood");    // compressed: read through the capture shader
        private IEnumerator S_prop_pottery() => Prop("Pottery_01", "Ceramic");
        private IEnumerator S_prop_log() => Prop("WoodLog1", "Wood");
        private IEnumerator S_prop_apple() => Prop("FoodApple", "Fruit");   // compressed: read through the capture shader
        private IEnumerator S_prop_stool() => Prop("Stool2", "Wood");
        private IEnumerator S_prop_rock() => Prop("Rock_Boulder_01", "Stone");
        private IEnumerator S_prop_skull() => Prop("SkeletonSkull", "Bone");
        private IEnumerator S_prop_cushion() => Prop("Stool_KharTib_Cushion", "Fabric");
        private IEnumerator S_prop_mug() => Prop("Mug1", "Metal");
        private IEnumerator S_prop_cup() => Prop("Pottery_05", "Ceramic");
        private IEnumerator S_prop_bowl() => Prop("Pottery_02", "Ceramic");
        private IEnumerator S_prop_basket() => Prop("WickerBasket_01", "Wicker");
        private IEnumerator S_prop_book() => Prop("Book_01", "Paper");
        private IEnumerator S_prop_target() => Prop("ArcheryTarget", "Straw");
        private IEnumerator S_prop_mug2() => Prop("Mug2", "Wood");

        /// <summary>
        /// Two arrows held still in the air, each cut near one end (one of them through the fletching): every material
        /// left on the pieces, its shader and whether that shader draws (magenta otherwise), and a close look.
        /// </summary>
        private IEnumerator S_arrow()
        {
            foreach (float at in new[] { 0.15f, 0.85f })
            {
                Item arrow = null;
                yield return SpawnItem("Arrow", x => arrow = x, PlayerFront(1.6f) + Vector3.up * 1.3f);
                if (!arrow) { Expect("arrow spawned", false, ""); yield break; }
                arrow.physicBody.rigidBody.isKinematic = true;
                arrow.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(PlayerPos() - arrow.transform.position, Vector3.up).normalized);
                yield return null;
                Bounds look = RenderBounds(arrow);
                Vector3 axis = look.size.x >= look.size.y && look.size.x >= look.size.z ? Vector3.right
                             : look.size.y >= look.size.z ? Vector3.up : Vector3.forward;
                Vector3 target = look.min + Vector3.Scale(look.size, axis) * at + Vector3.Scale(look.extents, Vector3.one - axis);
                Note("arrow", $"type {arrow.data.type}, size {look.size}, cut at {at:0.00} of its length; materials before: {Materials(arrow.gameObject)}");
                int debrisBefore = DebrisCount();
                Item blade = null;
                yield return SpawnItem(Sword, x => blade = x);
                if (!blade) yield break;
                Vector3 dir = Vector3.Cross(axis, Vector3.up).sqrMagnitude > 0.1f ? Vector3.up : Vector3.right;
                yield return Swing(blade, target, Vector3.Cross(dir, axis), -dir, 10f, false);
                yield return Wait(1f);
                Expect($"arrow cut at {at:0.00}", LogHas("cut Arrow ("), LastLines("cut Arrow (", 1) + " " + LastLines("Arrow not cut", 1));
                var pieces = new List<GameObject> { arrow ? arrow.gameObject : null };
                Type debris = ZType("ItemDebris");
                if (debris != null) foreach (UnityEngine.Object d in FindObjectsOfType(debris)) pieces.Add(((Component)d).gameObject);
                string mats = string.Join(" | ", pieces.Where(p => p).Select(p => p.name + ": " + Materials(p)));
                Expect("every piece's shader draws (no magenta)", !mats.Contains("NOT SUPPORTED") && !mats.Contains("Hidden/InternalErrorShader"), mats);
                Note("debris", $"{debrisBefore} -> {DebrisCount()}");
                yield return ShotAt("arrow_" + (int)(at * 100), look.center);
                yield return ShotCap("arrow_" + (int)(at * 100), arrow);
                ClearDebris();
                if (arrow) arrow.Despawn();
            }
        }

        private static string Materials(GameObject go) =>
            string.Join(", ", go.GetComponentsInChildren<Renderer>().Where(r => r.enabled && r.gameObject.activeInHierarchy).SelectMany(r =>
                r.sharedMaterials.Select(m => !m ? $"{r.name}=null" : $"{r.name}={m.name} [{(m.shader ? m.shader.name : "no shader")}{(m.shader && !m.shader.isSupported ? " NOT SUPPORTED" : "")}]")));

        private static void ClearDebris() =>
            ZType("ItemDebris")?.GetMethod("ClearAll", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);

        /// <summary>
        /// Every object Cut Props may cut, one after another on the floor, a level swing through its middle: cut (and with
        /// what cut face), left to the game (compressed mesh), not reached, or the mod's reason; a close look at each cut.
        /// </summary>
        private IEnumerator S_prop_all()
        {
            var types = new[] { ItemData.Type.Prop, ItemData.Type.Food, ItemData.Type.Potion, ItemData.Type.Tool };
            var t = new Tally();
            foreach (ItemData data in Catalog.GetDataList<ItemData>().Where(d => types.Contains(d.type)).OrderBy(d => d.id))
            {
                ClearDebris();
                Item prop = null;
                yield return SpawnItem(data.id, x => prop = x, PlayerFront(1.8f) + Vector3.up * 0.4f);
                if (!prop) { t.other.Add(data.id + " (no spawn)"); continue; }
                yield return Wait(1.5f);
                if (!prop) { t.other.Add(data.id + " (gone)"); continue; }
                Vector3 fwd = Vector3.ProjectOnPlane(RenderBounds(prop).center - PlayerPos(), Vector3.up).normalized;
                // At the middle of its biggest collider: the middle of its whole shape can be air (a battering ram, a branch).
                yield return CutAndCheck(prop, data.id, MainCollider(prop), Vector3.Cross(Vector3.up, fwd), fwd, ColliderBounds(prop).min.y, t);
                if (prop) { spawned.Remove(prop.gameObject); prop.Despawn(); }
                yield return null;
            }
            ClearDebris();
            t.Report(this, "every readable object cut and its half sound");
        }

        /// <summary>
        /// Every weapon and shield, one after another, held still in the air lying across, chopped from above through the
        /// middle of its biggest collider; checked as the objects are (CutAndCheck).
        /// </summary>
        private IEnumerator S_weapon_all()
        {
            var types = new[] { ItemData.Type.Weapon, ItemData.Type.Shield };
            var t = new Tally();
            foreach (ItemData data in Catalog.GetDataList<ItemData>().Where(d => types.Contains(d.type)).OrderBy(d => d.id))
            {
                ClearDebris();
                // The floor where it will hang, measured before it is there (a ray from under it met its own lower parts).
                float floor = Physics.Raycast(PlayerFront(1.8f) + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 5f, ~0, QueryTriggerInteraction.Ignore)
                    ? hit.point.y : PlayerPos().y;
                Item weapon = null;
                yield return SpawnItem(data.id, x => weapon = x, PlayerFront(1.8f) + Vector3.up * 1.2f);
                if (!weapon) { t.other.Add(data.id + " (no spawn)"); continue; }
                if (weapon.physicBody != null) weapon.physicBody.isKinematic = true;
                // Lying across the chop, its longest side along x (an arrow's is its z: turned as a sword is, it was split
                // along its length).
                Vector3 size = RenderBounds(weapon).size;
                weapon.transform.rotation = size.z >= size.x && size.z >= size.y ? Quaternion.Euler(0f, 90f, 0f)
                                          : size.y >= size.x ? Quaternion.Euler(0f, 0f, 90f) : Quaternion.identity;
                yield return Wait(0.5f);
                if (!weapon) { t.other.Add(data.id + " (gone)"); continue; }
                yield return CutAndCheck(weapon, data.id, MainCollider(weapon), Vector3.forward, Vector3.down, floor, t);
                if (weapon) { spawned.Remove(weapon.gameObject); weapon.Despawn(); }
                yield return null;
            }
            ClearDebris();
            t.Report(this, "every readable weapon cut and its half sound");
        }

        /// <summary>
        /// Bows and the crossbow (skinned limbs and string): cut as they are, baked into plain meshes. Checked as every
        /// weapon is (CutAndCheck), and: the baked parts stand where the bow was (with its half, its whole place), its
        /// string cannot be grabbed, not even once the bow is in a hand (the game makes it grabbable then), and on
        /// despawn it is the bow it was.
        /// </summary>
        private IEnumerator S_bow_cut()
        {
            var t = new Tally();
            foreach (string id in new[] { "BowCommon", "BowWar", "KharTibCrossbow" })
            {
                ClearDebris();
                Item bow = null;
                yield return SpawnItem(id, x => bow = x, PlayerFront(1.8f) + Vector3.up * 1.2f);
                if (!bow) { Note("warning", "no " + id); continue; }
                bow.physicBody.isKinematic = true;
                Vector3 size = SkinBounds(bow).size;
                bow.transform.rotation = size.z >= size.x && size.z >= size.y ? Quaternion.Euler(0f, 90f, 0f)
                                       : size.y >= size.x ? Quaternion.Euler(0f, 0f, 90f) : Quaternion.identity;
                yield return Wait(0.5f);
                Bounds whole = SkinBounds(bow);
                var strings = bow.GetComponentsInChildren<BowString>(true);
                Note(id + " skinned parts", string.Join(", ", bow.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(s =>
                    $"{s.name}{(strings.Any(b => b && b.gameObject != bow.gameObject && s.transform.IsChildOf(b.transform)) ? " (under its string object: goes off with it)" : "")}")));
                // Its own switch (a pooled item is off as a whole after despawn).
                string Working() => string.Join(", ", strings.Select(b => b ? $"{b.name} {(b.gameObject.activeSelf ? "active" : "inactive")}" : "gone"));
                string wasWorking = Working();
                var before = new HashSet<Component>(Debris());
                float floor = Physics.Raycast(PlayerFront(1.8f) + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 5f, ~0, QueryTriggerInteraction.Ignore) ? hit.point.y : PlayerPos().y;
                // Measured right after the cut, before the half falls: the bow and its half together where the bow was.
                Bounds after = new Bounds();
                bool measured = false;
                IEnumerator cut = CutAndCheck(bow, id, whole.center, Vector3.forward, Vector3.down, floor, t);
                while (cut.MoveNext())
                {
                    if (!measured && bow && LogHas("weapon cut " + id + " ("))
                    {
                        after = SkinBounds(bow);   // what is left of it, baked (its half is already on its way)
                        measured = true;
                    }
                    yield return cut.Current;
                }
                if (bow && measured)
                {
                    // Within where the whole bow was (baked at the wrong place or scale, it would stick out).
                    bool inside = after.min.x > whole.min.x - 0.03f && after.min.y > whole.min.y - 0.03f && after.min.z > whole.min.z - 0.03f &&
                                  after.max.x < whole.max.x + 0.03f && after.max.y < whole.max.y + 0.03f && after.max.z < whole.max.z + 0.03f;
                    Expect(id + " baked where it was", inside && after.size.sqrMagnitude > 0.01f, $"whole {whole.min}-{whole.max}, left {after.min}-{after.max}");
                    // In a hand: the game makes its string grabbable when the bow is grabbed.
                    RagdollHand hand = Player.local && Player.local.creature ? Player.local.creature.handRight : null;
                    Handle grip = bow.mainHandleRight ? bow.mainHandleRight : bow.GetComponentInChildren<Handle>();
                    if (hand && grip)
                    {
                        bow.physicBody.isKinematic = false;
                        hand.Grab(grip, true);
                        yield return Wait(0.5f);
                    }
                    Expect(id + " string cannot be grabbed", strings.Length > 0 && strings.All(b => !b || !b.stringHandle || !b.stringHandle.gameObject.activeInHierarchy),
                           $"held {(hand && hand.grabbedHandle == grip)}; {Working()}");
                    if (hand && hand.grabbedHandle) hand.UnGrab(false);
                    yield return Wait(0.2f);
                }
                if (bow)
                {
                    spawned.Remove(bow.gameObject);
                    bow.Despawn();
                    if (measured) Expect(id + " the bow it was after despawn", Working() == wasWorking, $"was [{wasWorking}], now [{Working()}]");
                }
                yield return null;
            }
            ClearDebris();
            t.Report(this, "bows cut and their halves sound");
        }

        /// <summary>Where a bow is drawn: its visible renderers (the skinned ones, or their baked stand-ins once cut).</summary>
        private static Bounds SkinBounds(Item item)
        {
            var b = new Bounds();
            bool any = false;
            foreach (Renderer r in item.GetComponentsInChildren<Renderer>())
            {
                if (!r || !r.enabled || r.forceRenderingOff || r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly) continue;
                if (!(r is MeshRenderer || r is SkinnedMeshRenderer) || r.name == "ImbueGeneratedMesh" || r.name.StartsWith("Zandatsu_WeaponCut")) continue;
                Bounds rb = r.bounds;
                // A skinned renderer's own bounds are loose (a bow's reached 0.8 m past it): its baked shape's.
                if (r is SkinnedMeshRenderer smr)
                {
                    var baked = new Mesh();
                    smr.BakeMesh(baked, false);
                    Bounds lb = baked.bounds;
                    rb = new Bounds(smr.transform.TransformPoint(lb.center), Vector3.zero);
                    for (int i = 0; i < 8; i++)
                        rb.Encapsulate(smr.transform.TransformPoint(lb.center + Vector3.Scale(lb.extents, new Vector3((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1))));
                    Destroy(baked);
                }
                if (!any) { b = rb; any = true; } else b.Encapsulate(rb);
            }
            return b;
        }

        /// <summary>
        /// Every shield cut: its cut face is what it is made of (its colliders' materials by volume): a wooden one wood, not
        /// hot steel (it showed hot steel, as all weapons do), a metal one hot steel.
        /// </summary>
        private IEnumerator S_shield_faces()
        {
            var t = new Tally();
            foreach (ItemData data in Catalog.GetDataList<ItemData>().Where(d => d.type == ItemData.Type.Shield).OrderBy(d => d.id))
            {
                ClearDebris();
                float floor = Physics.Raycast(PlayerFront(1.8f) + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 5f, ~0, QueryTriggerInteraction.Ignore) ? hit.point.y : PlayerPos().y;
                Item shield = null;
                yield return SpawnItem(data.id, x => shield = x, PlayerFront(1.8f) + Vector3.up * 1.2f);
                if (!shield) continue;
                shield.physicBody.isKinematic = true;
                yield return Wait(0.5f);
                float wood = 0f, metal = 0f;
                foreach (Collider c in shield.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger && c.sharedMaterial))
                {
                    string m = c.sharedMaterial.name.Replace(" (Instance)", "");
                    float v = c.bounds.size.x * c.bounds.size.y * c.bounds.size.z;
                    if (m == "Wood") wood += v;
                    else if (m == "Metal" || m == "Blade" || m == "Plate") metal += v;
                }
                int before = t.cut.Count;
                yield return CutAndCheck(shield, data.id, RenderBounds(shield).center, Vector3.forward, Vector3.down, floor, t);
                string face = t.cut.Count > before ? t.cut[t.cut.Count - 1] : data.id + "=not cut";
                bool wooden = wood > metal;
                Expect($"{data.id} cut face as its make", !face.EndsWith("not cut") && face.Contains("=Wood") == wooden,
                       $"{face} (wood {wood * 1000f:0.0} l, metal {metal * 1000f:0.0} l by its colliders' room)");
                if (shield) { spawned.Remove(shield.gameObject); shield.Despawn(); }
                yield return null;
            }
            ClearDebris();
            t.Report(this, "shields cut and their halves sound");
        }

        /// <summary>
        /// An "x-ray" of objects for the player: each one three times side by side, as seen, its colliders as wire frames
        /// coloured by their physic material (wood orange, metal blue, stone grey, ceramic pink, fabric and leather green,
        /// other yellow; triggers faint purple), and its visible mesh's triangles.
        /// </summary>
        private IEnumerator S_collider_view()
        {
            foreach (string id in new[] { "ShieldTribal", "AnvilSupport", "Pottery_01", "AxeShortCommon", "SwordLongCommon", "Barrel1" })
            {
                Item item = null;
                yield return SpawnItem(id, x => item = x, PlayerFront(1.8f) + Vector3.up * 1.4f);
                if (!item) { Note("warning", "no " + id); continue; }
                item.physicBody.isKinematic = true;
                yield return Wait(0.5f);
                yield return XRay(item, id);
                spawned.Remove(item.gameObject);
                item.Despawn();
                yield return null;
            }
        }

        private static Material wireMat;

        private IEnumerator XRay(Item item, string name)
        {
            yield return new WaitForEndOfFrame();
            shotFrames.Add(Time.frameCount);
            if (!wireMat)
            {
                wireMat = new Material(Shader.Find("Hidden/Internal-Colored"));
                wireMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                wireMat.SetInt("_Cull", 0);
                wireMat.SetInt("_ZWrite", 0);
            }
            Bounds look = RenderBounds(item);
            if (look.size.sqrMagnitude < 1e-4f) look = ColliderBounds(item);
            var go = new GameObject("ZandatsuLabCamera");
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = 40f;
            cam.nearClipPlane = 0.02f;
            const int w = 640, h = 480;
            var rt = new RenderTexture(w, h, 24);
            cam.targetTexture = rt;
            var sheet = new Texture2D(w * 3, h, TextureFormat.RGB24, false);
            float dist = Mathf.Max(look.extents.magnitude, 0.1f) / Mathf.Tan(20f * Mathf.Deg2Rad) * 1.1f;
            Vector3 toward = Vector3.ProjectOnPlane(PlayerPos() - look.center, Vector3.up).normalized;
            Vector3 view = (toward + Vector3.Cross(Vector3.up, toward) * 0.6f + Vector3.up * 0.55f).normalized;
            go.transform.position = look.center + view * dist;
            go.transform.LookAt(look.center);
            try
            {
                for (int panel = 0; panel < 3; panel++)
                {
                    cam.Render();
                    RenderTexture.active = rt;
                    if (panel > 0)
                    {
                        GL.PushMatrix();
                        GL.LoadProjectionMatrix(cam.projectionMatrix);
                        GL.modelview = cam.worldToCameraMatrix;
                        wireMat.SetPass(0);
                        GL.Begin(GL.LINES);
                        if (panel == 1)
                        {
                            foreach (Collider c in item.GetComponentsInChildren<Collider>())
                                if (c && c.enabled) Wire(c);
                        }
                        else MeshWire(item);
                        GL.End();
                        GL.PopMatrix();
                    }
                    sheet.ReadPixels(new Rect(0, 0, w, h), panel * w, 0);
                }
                sheet.Apply();
                File.WriteAllBytes(Path.Combine(dir, $"{++shot:00}_{name}_xray.png"), sheet.EncodeToPNG());
                var counts = item.GetComponentsInChildren<Collider>().Where(c => c && c.enabled)
                    .GroupBy(c => $"{c.GetType().Name.Replace("Collider", "")} {(c.isTrigger ? "trigger" : c.sharedMaterial ? c.sharedMaterial.name.Replace(" (Instance)", "") : "no material")}")
                    .Select(g => $"{g.Key} x{g.Count()}");
                Note(name + " colliders", string.Join(", ", counts));
            }
            catch (Exception e) { Note("warning", "x-ray: " + e.Message); }
            finally
            {
                RenderTexture.active = null;
                cam.targetTexture = null;
                Destroy(rt);
                Destroy(sheet);
                Destroy(go);
            }
        }

        private static Color WireColour(Collider c)
        {
            if (c.isTrigger) return new Color(0.6f, 0.4f, 1f, 0.35f);
            switch (c.sharedMaterial ? c.sharedMaterial.name.Replace(" (Instance)", "") : "")
            {
                case "Wood": case "Rope": return new Color(1f, 0.55f, 0.1f);
                case "Metal": case "Blade": case "Plate": case "Chainmail": return new Color(0.3f, 0.75f, 1f);
                case "Stone": return new Color(0.85f, 0.85f, 0.85f);
                case "Ceramic": return new Color(1f, 0.4f, 0.7f);
                case "Fabric": case "Leather": return new Color(0.4f, 1f, 0.35f);
                default: return new Color(1f, 1f, 0.2f);
            }
        }

        private static void Line(Vector3 a, Vector3 b) { GL.Vertex(a); GL.Vertex(b); }

        private static void Ring(Transform t, Vector3 centre, Vector3 u, Vector3 v, float r, float from = 0f, float to = 360f)
        {
            const int n = 24;
            Vector3 prev = Vector3.zero;
            for (int i = 0; i <= n; i++)
            {
                float a = Mathf.Lerp(from, to, i / (float)n) * Mathf.Deg2Rad;
                Vector3 p = t.TransformPoint(centre + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * r);
                if (i > 0) Line(prev, p);
                prev = p;
            }
        }

        private static void Wire(Collider c)
        {
            GL.Color(WireColour(c));
            Transform t = c.transform;
            switch (c)
            {
                case BoxCollider b:
                {
                    Vector3 hs = b.size * 0.5f;
                    var p = new Vector3[8];
                    for (int i = 0; i < 8; i++)
                        p[i] = t.TransformPoint(b.center + new Vector3((i & 1) != 0 ? hs.x : -hs.x, (i & 2) != 0 ? hs.y : -hs.y, (i & 4) != 0 ? hs.z : -hs.z));
                    Box(p);
                    break;
                }
                case SphereCollider s:
                    Ring(t, s.center, Vector3.right, Vector3.up, s.radius);
                    Ring(t, s.center, Vector3.right, Vector3.forward, s.radius);
                    Ring(t, s.center, Vector3.up, Vector3.forward, s.radius);
                    break;
                case CapsuleCollider k:
                {
                    Vector3 ax = k.direction == 0 ? Vector3.right : k.direction == 1 ? Vector3.up : Vector3.forward;
                    Vector3 u = k.direction == 0 ? Vector3.up : Vector3.right, v = Vector3.Cross(ax, u);
                    float half = Mathf.Max(0f, k.height * 0.5f - k.radius);
                    Vector3 e0 = k.center - ax * half, e1 = k.center + ax * half;
                    Ring(t, e0, u, v, k.radius);
                    Ring(t, e1, u, v, k.radius);
                    foreach (Vector3 side in new[] { u, -u, v, -v })
                        Line(t.TransformPoint(e0 + side * k.radius), t.TransformPoint(e1 + side * k.radius));
                    Ring(t, e1, u, ax, k.radius, -90f, 90f);
                    Ring(t, e1, v, ax, k.radius, -90f, 90f);
                    Ring(t, e0, u, -ax, k.radius, -90f, 90f);
                    Ring(t, e0, v, -ax, k.radius, -90f, 90f);
                    break;
                }
                case MeshCollider mc when mc.sharedMesh:
                {
                    Mesh m = Readable(mc.sharedMesh);
                    if (m) Triangles(t, m);
                    else Box(c.bounds);
                    break;
                }
                default:
                    Box(c.bounds);
                    break;
            }
        }

        private static void Box(Vector3[] p)
        {
            for (int i = 0; i < 8; i++)
                for (int bit = 1; bit < 8; bit <<= 1)
                    if ((i & bit) == 0) Line(p[i], p[i | bit]);
        }

        private static void Box(Bounds wb)
        {
            var p = new Vector3[8];
            for (int i = 0; i < 8; i++)
                p[i] = wb.center + Vector3.Scale(wb.extents, new Vector3((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1));
            Box(p);
        }

        private static void Triangles(Transform t, Mesh m)
        {
            Vector3[] v = m.vertices;
            int[] tri = m.triangles;
            for (int i = 0; i + 2 < tri.Length; i += 3)
            {
                Vector3 a = t.TransformPoint(v[tri[i]]), b = t.TransformPoint(v[tri[i + 1]]), d = t.TransformPoint(v[tri[i + 2]]);
                Line(a, b); Line(b, d); Line(d, a);
            }
        }

        /// <summary>A mesh to read on the CPU: itself, or the mod's copy read back from the graphics card.</summary>
        private static Mesh Readable(Mesh mesh)
        {
            if (!mesh || mesh.isReadable) return mesh;
            MethodInfo read = ZType("MeshReader")?.GetMethod("Readable", BindingFlags.Public | BindingFlags.Static);
            if (read == null) return null;
            object[] args = { mesh, null };
            Mesh copy = read.Invoke(null, args) as Mesh;
            return copy && copy.isReadable ? copy : null;
        }

        /// <summary>The visible meshes' triangles.</summary>
        private static void MeshWire(Item item)
        {
            GL.Color(new Color(1f, 1f, 1f, 0.55f));
            var far = new HashSet<Renderer>(item.GetComponentsInChildren<LODGroup>().SelectMany(g => g.GetLODs().Skip(1)).SelectMany(l => l.renderers).Where(r => r));
            foreach (Renderer r in item.GetComponentsInChildren<Renderer>())
            {
                if (!r || !r.enabled || r.forceRenderingOff || far.Contains(r) || r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly) continue;
                Mesh mesh = null;
                if (r is MeshRenderer) mesh = r.GetComponent<MeshFilter>() ? Readable(r.GetComponent<MeshFilter>().sharedMesh) : null;
                else if (r is SkinnedMeshRenderer smr) { mesh = new Mesh(); smr.BakeMesh(mesh, false); }
                if (mesh) Triangles(r.transform, mesh);
            }
        }

        /// <summary>
        /// Without Vanilla Dismemberment the game splits no body at the joints, spells and Shard Striker included (they
        /// slice past Damager.dismembermentEnabled; a head split off that way left every later creature from the pool with
        /// only a stump of the head's skin).
        /// </summary>
        private IEnumerator S_spell_off()
        {
            foreach (bool all in new[] { false, true })
            {
                string how = all ? "TrySliceAll (Shard Striker lightning/gravity)" : "SafeSlice of the head (gravity decapitation, lightning trail, Inner Flame)";
                Creature c = null;
                yield return Enemy(x => c = x);
                if (!c) yield break;
                int head = HeadSkin(c);
                if (all) c.ragdoll.TrySliceAll(); else c.ragdoll.GetPart(RagdollPart.Type.Head).SafeSlice();
                yield return Wait(2f);
                int sliced = c ? c.ragdoll.parts.Count(p => p && p.isSliced) : -1;
                Expect($"{how}: nothing split", sliced == 0 && c && HeadSkin(c) == head, $"sliced parts {sliced}, head skin {head} -> {(c ? HeadSkin(c) : -1)} verts");
                if (c) c.Despawn();
                yield return Wait(0.5f);
                Creature next = null;
                yield return Enemy(x => next = x);
                if (!next) yield break;
                Expect($"after {how}: the next creature's head whole", HeadSkin(next) == head, $"head skin {HeadSkin(next)} verts, fresh {head}");
                next.Despawn();
                yield return Wait(0.5f);
            }
        }

        private static int HeadSkin(Creature c)
        {
            SkinnedMeshRenderer skin = c.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault(r => r && r.name.StartsWith("Head_LOD0") && r.sharedMesh);
            return skin ? skin.sharedMesh.vertexCount : 0;
        }

        private static Vector3 MainCollider(Item item)
        {
            Collider main = item.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger)
                .OrderByDescending(c => c.bounds.size.x * c.bounds.size.y * c.bounds.size.z).FirstOrDefault();
            return main ? main.bounds.center : RenderBounds(item).center;
        }

        /// <summary>What a run over many objects found: cut (with the cut face), left to the game, missed, other, problems.</summary>
        private class Tally
        {
            public readonly List<string> cut = new List<string>(), compressed = new List<string>(), missed = new List<string>(),
                                         other = new List<string>(), problems = new List<string>();

            public void Report(LabRunner lab, string what)
            {
                lab.Note("all", $"cut {cut.Count}, compressed {compressed.Count}, not reached {missed.Count}, other {other.Count}, problems {problems.Count}");
                lab.Note("cut", string.Join(", ", cut));
                lab.Note("compressed", string.Join(", ", compressed));
                lab.Note("not reached", string.Join(", ", missed));
                lab.Note("other", string.Join(", ", other));
                foreach (string p in problems) lab.Note("problem", p);
                // The game's hit effects destroyed with a piece of ours instead of going back to their pool.
                int lost = lab.ScenarioLog().Count(l => l.msg.Contains("has been destroyed but it should not") && l.msg.Contains("Zandatsu"));
                lab.Expect("no game effects destroyed with pieces", lost == 0, $"{lost}: {lab.LastLines("has been destroyed but it should not", 1)}");
                lab.Expect(what, missed.Count == 0 && problems.Count == 0 &&
                    other.Count(o => !o.Contains("no spawn") && !o.Contains("skinned") && !o.Contains("works the level") && !o.Contains("hinged")) == 0,
                    $"not reached {missed.Count}, other {other.Count}, problems {problems.Count}");
            }
        }

        /// <summary>
        /// One object cut and all about it checked: cut (and with what cut face) or why not, every piece drawn (no magenta),
        /// and its cut-off half: there, its colliders covering it (an anvil stand's half once had only the small boxes
        /// under it), lying on the floor (halves of thin boards sank), cut again by a chop from above.
        /// </summary>
        private IEnumerator CutAndCheck(Item item, string id, Vector3 aim, Vector3 axis, Vector3 dir, float floor, Tally t)
        {
            Bounds look = item.GetComponentInChildren<SkinnedMeshRenderer>() ? SkinBounds(item) : RenderBounds(item);   // a bow has no plain renderer
            // How much of itself its own colliders cover (a scimitar's curve, a mace's flanges are left out by the game too):
            // its half is held to that, not to its whole shape.
            Bounds own = ColliderBounds(item);
            Vector3 cover = new Vector3(own.size.x / Mathf.Max(look.size.x, 1e-3f), own.size.y / Mathf.Max(look.size.y, 1e-3f), own.size.z / Mathf.Max(look.size.z, 1e-3f));
            float least = Mathf.Min(1f, Mathf.Min(cover.x, Mathf.Min(cover.y, cover.z)));
            var debrisBefore = new HashSet<Component>(Debris());
            int mark;
            lock (log) mark = log.Count;
            Item blade = null;
            yield return SpawnItem(Sword, x => blade = x);
            if (!blade) yield break;
            yield return Swing(blade, aim, axis, dir, 10f, false);
            Done(blade);
            yield return Wait(0.8f);
            List<string> lines;
            lock (log) lines = log.Skip(mark).Select(l => l.msg).ToList();
            string done = lines.FirstOrDefault(l => l.Contains("prop cut " + id + " (") || l.Contains("weapon cut " + id + " ("));
            string why = lines.FirstOrDefault(l => l.Contains("] " + id + " not cut:"));
            if (done == null)
            {
                if (why != null && why.Contains("compressed")) t.compressed.Add(id);
                else if (why != null) t.other.Add($"{id} ({why.Substring(why.IndexOf("not cut:") + 9).Trim()})");
                else
                {
                    string layers = string.Join("+", item ? item.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger)
                        .Select(c => LayerMask.LayerToName(c.gameObject.layer)).Distinct() : Enumerable.Empty<string>());
                    t.missed.Add($"{id} (size {look.size.x:0.00}x{look.size.y:0.00}x{look.size.z:0.00}, {layers})");
                }
                yield break;
            }
            var face = System.Text.RegularExpressions.Regex.Match(done, @"cut face (\S+)");
            t.cut.Add($"{id}={(face.Success ? face.Groups[1].Value.Replace("Zandatsu_Cap_", "").Replace("Zandatsu_", "") : "?")}");
            Component half = Debris().FirstOrDefault(d => !debrisBefore.Contains(d));
            var pieces = new List<GameObject> { item ? item.gameObject : null, half ? half.gameObject : null };
            string mats = string.Join(" | ", pieces.Where(p => p).Select(p => Materials(p)));
            bool bad = false;
            void Problem(string p) { t.problems.Add($"{id}: {p}"); bad = true; }
            if (mats.Contains("NOT SUPPORTED") || mats.Contains("InternalErrorShader")) Problem("a shader that does not draw (magenta): " + mats);
            if (!half)
            {
                if (!done.Contains("no debris")) Problem("no cut-off half");
                if (bad) yield return ShotCap(id, item);
                yield break;
            }
            yield return Wait(1.7f);
            if (!half) { Problem("its half went away within 2.5 s"); yield break; }
            Bounds hitB = DebrisBounds(half), seen = half.GetComponent<Renderer>().bounds;
            float need = 0.5f * least;   // as the mod gives a half its own shape below half of it (a sickle's curve is the game's own gap)
            if (hitB.size.x < seen.size.x * need || hitB.size.y < seen.size.y * need || hitB.size.z < seen.size.z * need)
                Problem($"half's colliders {hitB.size} short of it {seen.size} (the whole one's cover at least {least:0.00})");
            if (hitB.min.y < floor - 0.1f) Problem($"half below the floor: lowest {hitB.min.y:0.00}, floor {floor:0.00}");
            // Cut again held up off the floor: chopping one lying flat, the bench's sword met the floor with its cross-guard
            // 7 cm above the half (a player swings at an angle).
            Rigidbody hrb = half.GetComponent<Rigidbody>();
            if (hrb)
            {
                hrb.isKinematic = true;
                hrb.position = half.transform.position += Vector3.up * 0.6f;
            }
            yield return Wait(0.2f);
            int recuts = ScenarioLog().Count(l => l.msg.Contains("debris cut ("));
            if (half) yield return SwingAt(half, down: true);
            if (ScenarioLog().Count(l => l.msg.Contains("debris cut (")) <= recuts)
                Problem(half ? $"half not cut again (held at {DebrisBounds(half).center}, {DebrisBounds(half).size})" : "half not cut again (gone)");
            if (half && hrb) hrb.isKinematic = false;
            if (bad) yield return ShotNear(id, half ? DebrisBounds(half).center : look.center, 0.9f);
        }

        /// <summary>
        /// The game's lit shaders as loaded: LitMoss materials by keyword set (each set is a compiled variant), and the
        /// properties of LitMoss and the plain ThunderRoad Lit with a sample material's values.
        /// </summary>
        private IEnumerator S_shader_props()
        {
            Item barrel = null;
            yield return SpawnItem("Barrel1", x => barrel = x, PlayerFront(1.5f) + Vector3.up * 1.2f);
            Item cork = null;
            yield return SpawnItem("BottleCork", x => cork = x, PlayerFront(1.5f) + Vector3.up * 1.6f);
            yield return null;
            foreach (string sn in new[] { "ThunderRoad/LitMoss - ASshader", "ThunderRoad/Lit_DONOTUSE_USE_TRLitMoss_Instead", "Universal Render Pipeline/Lit" })
            {
                Material[] mats = Resources.FindObjectsOfTypeAll<Material>().Where(m => m && m.shader && m.shader.name == sn).ToArray();
                Note("shader", $"{sn}: found by name {(Shader.Find(sn) ? "yes" : "no")}, {mats.Length} materials loaded");
                foreach (var g in mats.GroupBy(m => string.Join(" ", m.shaderKeywords.OrderBy(k => k))).OrderBy(g => g.Key.Length).Take(12))
                    Note("keywords", $"{sn.Split('/').Last()} x{g.Count()} [{g.Key}] e.g. {g.First().name}");
                Material sample = mats.OrderBy(m => m.shaderKeywords.Length).FirstOrDefault();
                if (!sample) continue;
                Shader s = sample.shader;
                var props = new List<string>();
                for (int i = 0; i < s.GetPropertyCount(); i++)
                {
                    string name = s.GetPropertyName(i);
                    var type = s.GetPropertyType(i);
                    string value = type == UnityEngine.Rendering.ShaderPropertyType.Texture ? (sample.GetTexture(name) ? sample.GetTexture(name).name : "-")
                                 : type == UnityEngine.Rendering.ShaderPropertyType.Color ? sample.GetColor(name).ToString("0.##")
                                 : type == UnityEngine.Rendering.ShaderPropertyType.Vector ? sample.GetVector(name).ToString("0.##")
                                 : sample.GetFloat(name).ToString("0.###");
                    props.Add($"{name}={value}");
                }
                Note("properties", $"{sn.Split('/').Last()} ({sample.name}): {string.Join(" ", props)}");
            }
        }

        /// <summary>
        /// Every object Cut Props may cut: what its colliders are made of (physic material names, the game's MaterialData)
        /// and which shaders its visible materials use, with their texture slots.
        /// </summary>
        private IEnumerator S_prop_materials()
        {
            var types = new[] { ItemData.Type.Prop, ItemData.Type.Food, ItemData.Type.Potion, ItemData.Type.Tool };
            var byPhysic = new Dictionary<string, List<string>>();
            var byShader = new Dictionary<string, List<string>>();
            var slots = new Dictionary<string, string>();
            foreach (ItemData data in Catalog.GetDataList<ItemData>().Where(d => types.Contains(d.type)).OrderBy(d => d.id))
            {
                Item item = null;
                yield return SpawnItem(data.id, x => item = x, PlayerFront(1.5f) + Vector3.up * 1.2f);
                if (!item) continue;
                if (item.physicBody?.rigidBody) item.physicBody.rigidBody.isKinematic = true;
                yield return null;
                var phys = item.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger)
                    .Select(c => c.sharedMaterial ? c.sharedMaterial.name : "none").Distinct().OrderBy(n => n);
                string pk = string.Join("+", phys);
                if (!byPhysic.TryGetValue(pk, out var pl)) byPhysic[pk] = pl = new List<string>();
                pl.Add(data.id);
                foreach (Renderer r in item.renderers)
                {
                    if (!(r is MeshRenderer) || !r.enabled) continue;
                    foreach (Material m in r.sharedMaterials)
                    {
                        if (!m || !m.shader) continue;
                        string sk = m.shader.name;
                        if (!byShader.TryGetValue(sk, out var sl)) byShader[sk] = sl = new List<string>();
                        if (!sl.Contains(data.id)) sl.Add(data.id);
                        if (!slots.ContainsKey(sk))
                            slots[sk] = string.Join(" ", m.GetTexturePropertyNames().Select(t => $"{t}={(m.GetTexture(t) ? m.GetTexture(t).name : "-")}")) +
                                        " | keywords " + string.Join(" ", m.shaderKeywords) + $" | material {m.name}";
                    }
                }
                spawned.Remove(item.gameObject);
                item.Despawn();
                yield return null;
            }
            foreach (var kv in byPhysic.OrderByDescending(k => k.Value.Count)) Note("physic " + kv.Key, $"{kv.Value.Count}: {string.Join(", ", kv.Value)}");
            foreach (var kv in byShader.OrderByDescending(k => k.Value.Count)) Note("shader " + kv.Key, $"{kv.Value.Count}: {string.Join(", ", kv.Value.Take(12))}; slots {slots[kv.Key]}");
        }

        /// <summary>
        /// Every object Cut Props may cut (props, food, potions, tools): spawned one by one, can its mesh be cut (readable,
        /// or read back from the graphics card) or is it one of the game's compressed meshes (left to the game).
        /// </summary>
        private IEnumerator S_prop_census()
        {
            var types = new[] { ItemData.Type.Prop, ItemData.Type.Food, ItemData.Type.Potion, ItemData.Type.Tool };
            var cut = new List<string>();
            var compressed = new List<string>();
            var other = new List<string>();
            foreach (ItemData data in Catalog.GetDataList<ItemData>().Where(d => types.Contains(d.type)).OrderBy(d => d.id))
            {
                Item item = null;
                yield return SpawnItem(data.id, x => item = x, PlayerFront(1.5f) + Vector3.up * 1.2f);
                if (!item) { other.Add(data.id + " (no spawn)"); continue; }
                if (item.physicBody?.rigidBody) item.physicBody.rigidBody.isKinematic = true;
                for (int i = 0; i < 3; i++) yield return null;   // drawn once: on the graphics card
                bool breakable = item.GetComponentInChildren<Breakable>(true);
                var lower = new HashSet<Renderer>();
                LODGroup lod = item.GetComponentInChildren<LODGroup>();
                if (lod)
                {
                    LOD[] lods = lod.GetLODs();
                    var lod0 = new HashSet<Renderer>(lods.Length > 0 ? lods[0].renderers.Where(r => r) : Enumerable.Empty<Renderer>());
                    for (int l = 1; l < lods.Length; l++) foreach (Renderer r in lods[l].renderers) if (r && !lod0.Contains(r)) lower.Add(r);
                }
                string verdict = "cut";
                foreach (Renderer r in item.renderers)
                {
                    if (!r || lower.Contains(r)) continue;
                    if (r is SkinnedMeshRenderer) { verdict = "skinned"; break; }
                    if (!(r is MeshRenderer) || !r.enabled || !r.gameObject.activeInHierarchy || r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly) continue;
                    Mesh m = r.GetComponent<MeshFilter>()?.sharedMesh;
                    if (!m) continue;
                    if (m.isReadable) continue;
                    if (m.GetVertexAttributes().Any(a => a.format == UnityEngine.Rendering.VertexAttributeFormat.Float16)) { verdict = "compressed"; break; }
                }
                string entry = data.id + (breakable ? "*" : "");
                (verdict == "cut" ? cut : verdict == "compressed" ? compressed : other).Add(verdict == "cut" || verdict == "compressed" ? entry : entry + " (" + verdict + ")");
                spawned.Remove(item.gameObject);
                item.Despawn();
                yield return null;
            }
            Note("census", $"cut {cut.Count}, compressed (left to the game) {compressed.Count}, other {other.Count}; * = breakable");
            Note("cut", string.Join(", ", cut));
            Note("compressed", string.Join(", ", compressed));
            Note("other", string.Join(", ", other));
        }

        /// <summary>Control: the same swing with Cut Props off, does the game itself break the object?</summary>
        private IEnumerator S_vanilla_crate() => Vanilla("Crate");
        private IEnumerator S_vanilla_chair() => Vanilla("Chair1");
        private IEnumerator S_vanilla_pottery() => Vanilla("Pottery_01");

        private IEnumerator Vanilla(string id)
        {
            Set("CutProps", false);
            Item prop = null;
            yield return SpawnItem(id, x => prop = x, PlayerFront(1.8f) + Vector3.up * 0.6f);
            if (!prop) { Expect("object spawned", false, id); yield break; }
            yield return Wait(2.5f);
            Breakable br = prop.GetComponentInChildren<Breakable>(true);
            Bounds look = RenderBounds(prop);
            Item blade = null;
            yield return SpawnItem(Sword, x => blade = x);
            if (!blade) yield break;
            Vector3 fwd = Vector3.ProjectOnPlane(look.center - PlayerPos(), Vector3.up).normalized;
            yield return Swing(blade, look.center, Vector3.Cross(Vector3.up, fwd), fwd, 10f, false);
            yield return Wait(1.5f);
            Note("game alone", br ? $"broken {br.IsBroken}" : "no breakable");
            yield return ShotAt("after", look.center);
        }

        /// <summary>Every loaded mesh without CPU access: can its vertices be read back from the graphics card, by layout.</summary>
        private IEnumerator S_mesh_scan()
        {
            var classes = new Dictionary<string, int[]>();
            var zeroNames = new List<string>();
            int n = 0;
            foreach (Mesh m in Resources.FindObjectsOfTypeAll<Mesh>())
            {
                if (!m || m.isReadable || m.vertexCount == 0 || m.vertexBufferCount == 0) continue;
                string layout = string.Join(" ", m.GetVertexAttributes().Select(a => $"{a.attribute.ToString().Substring(0, 3)}{a.format.ToString().Replace("Float", "F").Replace("UNorm", "U").Replace("SNorm", "S")}x{a.dimension}"));
                bool ok;
                IntPtr native;
                try
                {
                    using (GraphicsBuffer vb = m.GetVertexBuffer(0))
                    {
                        native = vb.GetNativeBufferPtr();
                        var raw = new byte[Math.Min(vb.count * vb.stride, 4096)];
                        vb.GetData(raw, 0, 0, raw.Length);
                        ok = raw.Any(x => x != 0);
                    }
                }
                catch { ok = false; native = IntPtr.Zero; }
                string key = $"{layout} [{(native == IntPtr.Zero ? "no native" : "native")}]";
                if (!classes.TryGetValue(key, out int[] c)) classes[key] = c = new int[2];
                c[ok ? 0 : 1]++;
                if (!ok && zeroNames.Count < 40) zeroNames.Add(m.name);
                if (++n % 50 == 0) yield return null;
            }
            foreach (var kv in classes.OrderByDescending(k => k.Value[0] + k.Value[1]).Take(20))
                Note("layout", $"{kv.Key}: read {kv.Value[0]}, zero {kv.Value[1]}");
            Note("zero examples", string.Join(", ", zeroNames));
        }

        /// <summary>
        /// An object on the floor, a level swing through its middle: it is cut, a half falls off, the cut face shows the
        /// inside of what it is made of (kind: our cut face texture, or hot steel for Metal) laid as meant, the kept half's
        /// colliders shrink, a breakable one does not shatter.
        /// </summary>
        private IEnumerator Prop(string id, string kind = null)
        {
            Item prop = null;
            yield return SpawnItem(id, x => prop = x, PlayerFront(1.8f) + Vector3.up * 0.6f);
            if (!prop) { Expect("object spawned", false, id); yield break; }
            yield return Wait(2.5f);   // settles on the floor
            Breakable br = prop.GetComponentInChildren<Breakable>(true);
            bool couldBreak = br && br.canBreak;
            Bounds before = ColliderBounds(prop);
            Bounds look = RenderBounds(prop);
            int debrisBefore = DebrisCount();
            Note("object", $"{id} ({prop.data.type}){(br ? ", breakable" : "")}, size {look.size}, on layer {LayerMask.LayerToName(prop.gameObject.layer)}");
            {
                var colls = prop.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger).ToList();
                var owners = colls.Select(c => c.GetComponentInParent<Item>()).Distinct().Select(o => o ? $"{o.name}[{(o.data != null ? o.data.id : "no data")}]" : "none");
                var rends = prop.GetComponentsInChildren<Renderer>().Where(r => r.enabled).Select(r => $"{r.GetType().Name}:{r.name}:{(r.GetComponent<MeshFilter>()?.sharedMesh is Mesh mm && mm ? (mm.isReadable ? "readable" : "unreadable") : "-")}");
                Note("structure", $"{colls.Count} colliders owned by [{string.Join(", ", owners)}]; renderers [{string.Join(", ", rends.Take(8))}]; item.renderers {prop.renderers.Count}; sub items {(br ? br.subUnbrokenItems.Count + "/" + br.subBrokenItems.Count : "-")}");
            }
            yield return ShotAt("before", look.center);
            Item blade = null;
            yield return SpawnItem(Sword, x => blade = x);
            if (!blade) yield break;
            Vector3 fwd = Vector3.ProjectOnPlane(look.center - PlayerPos(), Vector3.up).normalized;
            Note("collider layers", string.Join(", ", prop.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger)
                .GroupBy(c => LayerMask.LayerToName(c.gameObject.layer)).Select(g => g.Key + " x" + g.Count())));
            yield return Swing(blade, look.center, Vector3.Cross(Vector3.up, fwd), fwd, 10f, false);
            bool canBreakAfter = br && br.canBreak;   // before the mod puts a disarmed one back (0.6 s)
            yield return Wait(1.5f);
            // A mesh the mod cannot read (the game's compressed ones): left to the game, a breakable one breaks as it did.
            string notCut = LastLines(id + " not cut", 1);
            if (!LogHas("prop cut " + id) && notCut.Contains("compressed"))
            {
                Note("left to the game", notCut);
                // (The bench's swing does not break even pottery with Cut Props off: the game's breaking is not tested.)
                if (br) Expect("still breaks as in the game", canBreakAfter == couldBreak, $"can break {couldBreak} -> {canBreakAfter}");
                yield return ShotAt("after", look.center);
                yield break;
            }
            Expect("object cut", LogHas("prop cut " + id), LastLines("prop cut", 1));
            Expect("a half fell off", DebrisCount() > debrisBefore, $"debris {debrisBefore} -> {DebrisCount()}");
            // Drawn, not just there: a barrel's half was seen only by its shadow (2026-10-26).
            var halves = Debris().Select(d => d.GetComponent<MeshRenderer>()).Where(r => r).ToList();
            Expect("its half is drawn", halves.Count > 0 && halves.All(r => r.enabled && !r.forceRenderingOff && r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly),
                   string.Join(", ", halves.Select(r => $"{r.name} {r.shadowCastingMode}{(r.forceRenderingOff ? " forced off" : "")}{(r.enabled ? "" : " off")}")));
            string line = ScenarioLog().Where(l => l.msg.Contains("prop cut " + id)).Select(l => l.msg.Substring(Math.Max(0, l.msg.IndexOf("[Zandatsu]")))).LastOrDefault() ?? "";
            if (line.Length == 0) Note("why", LastLines(id + " not cut", 1));
            // No kind: no texture of its own yet (fruit), the cut face is only noted.
            string face = kind == "Metal" ? "cut face Zandatsu_HotSteel" : "cut face Zandatsu_Cap_" + kind;
            if (kind != null) Expect($"cut face is {kind}", line.Contains(face), line.Length > 200 ? line.Substring(0, 200) : line);
            else Note("cut face", System.Text.RegularExpressions.Regex.Match(line, @"cut face [^,]+").Value);
            if (prop)
            {
                if (kind != null && kind != "Metal") Expect("cut face texture laid as meant", CapLaid(prop, kind, out string how), how);
                Note("cut face fill", CapFill(prop));
                var cm = System.Text.RegularExpressions.Regex.Match(line, @"colliders off (\d+) / shortened (\d+)");
                bool cutBack = cm.Success && int.Parse(cm.Groups[1].Value) + int.Parse(cm.Groups[2].Value) > 0;
                // One or two round colliders (a skull's) are kept whole on their side, not shortened.
                if (prop.GetComponentsInChildren<Collider>(true).Count(c => !c.isTrigger) >= 3)
                    Expect("kept half's colliders cut back", cutBack, cm.Success ? cm.Value : "no line");
                else Note("colliders", cm.Success ? cm.Value : "no line");
            }
            if (br) Expect("breakable did not shatter", !br.IsBroken, $"broken {br.IsBroken}");
            if (prop)
            {
                Note("lighting", Lighting(prop));
                if (LightProbeVolume.Exists) Expect("cut face lit as the object", CapLitAsItem(prop, out string lit), lit);
            }
            yield return ShotAt("after", look.center);
            yield return ShotCap("after", prop);
        }

        /// <summary>
        /// A cut crate taken into another area's light volume (as a player carries one): its cut face must go with it.
        /// Only on levels made of areas (DungeonOutpost).
        /// </summary>
        private IEnumerator S_prop_light()
        {
            if (!LightProbeVolume.Exists) { Note("skipped", "no light volumes on this level"); yield break; }
            yield return Prop("Crate", "Wood");
            Item prop = FindObjectsOfType<Item>().Where(i => i && i.data != null && i.data.id == "Crate" && i.GetComponentsInChildren<MeshRenderer>().Any(r => r.name == "Zandatsu_WeaponCut"))
                                                 .OrderBy(i => (i.transform.position - PlayerFront(1.8f)).sqrMagnitude).FirstOrDefault();
            if (!prop || !prop.lightVolumeReceiver) { Expect("cut crate found", false, prop ? "no receiver" : "none"); yield break; }
            LightProbeVolume was = prop.lightVolumeReceiver.currentLightProbeVolume;
            LightProbeVolume other = FindObjectsOfType<LightProbeVolume>().Where(v => v && v != was && v.GetComponent<Collider>())
                                                                         .OrderBy(v => (v.transform.position - prop.transform.position).sqrMagnitude).FirstOrDefault();
            if (!other) { Note("skipped", "no other volume to carry it into"); yield break; }
            prop.physicBody.isKinematic = true;
            Vector3 home = prop.transform.position;
            prop.transform.position = other.GetComponent<Collider>().bounds.center;
            yield return Wait(0.5f);
            Note("carried", $"from {(was ? was.name : "none")} into {other.name}: the object's volume now {(prop.lightVolumeReceiver.currentLightProbeVolume ? prop.lightVolumeReceiver.currentLightProbeVolume.name : "none")}");
            if (prop.lightVolumeReceiver.currentLightProbeVolume == was) Note("skipped", "the object's receiver did not change volumes");
            else Expect("cut face lit as the object in the other area", CapLitAsItem(prop, out string lit), lit);
            prop.transform.position = home;
            yield return Wait(0.5f);
            Expect("cut face lit as the object back home", CapLitAsItem(prop, out string back), back);
        }

        /// <summary>The kept cut face lit as the object: Unity probes off and the same volume's light as the object's own materials.</summary>
        private static bool CapLitAsItem(Item item, out string how)
        {
            Material own = item.GetComponentsInChildren<MeshRenderer>().Where(r => r.enabled && r.name != "Zandatsu_WeaponCut" && r.lightProbeUsage == UnityEngine.Rendering.LightProbeUsage.Off)
                               .SelectMany(r => r.sharedMaterials).FirstOrDefault(m => m && m.IsKeywordEnabled("_PROBEVOLUME_ON"));
            Texture want = own ? own.GetTexture("_ProbeVolumeShR") : null;
            var bad = new List<string>();
            int caps = 0;
            foreach (MeshRenderer cap in item.GetComponentsInChildren<MeshRenderer>().Where(r => r.enabled && r.name == "Zandatsu_WeaponCut"))
                foreach (Material m in cap.sharedMaterials.Where(m => m && m.HasProperty("_ProbeVolumeShR")))
                {
                    caps++;
                    Texture sh = m.GetTexture("_ProbeVolumeShR");
                    if (m.IsKeywordEnabled("_PROBEVOLUME_ON") && cap.lightProbeUsage != UnityEngine.Rendering.LightProbeUsage.Off)
                        bad.Add($"{m.name}: volume keyword on a renderer with {cap.lightProbeUsage}");
                    else if (want && sh != want) bad.Add($"{m.name}: light {(sh ? sh.name : "none")}, the object's {want.name}");
                }
            how = caps == 0 ? "no lit cut face" : bad.Count == 0 ? $"{caps} materials, light {(want ? want.name : "unity probes")}" : string.Join("; ", bad);
            return bad.Count == 0;
        }

        /// <summary>
        /// How the cut object and its pieces are lit: on levels made of areas (Outpost, dungeons) the game lights items
        /// through a LightProbeVolume (its LightVolumeReceiver registers the item's materials, the renderers' Unity probes
        /// off). A material with the volume's keyword on that is not registered, on a renderer of its own, went blinding
        /// white under bloom (pieces of enemies did, 2026-10-07).
        /// </summary>
        private static string Lighting(Item item)
        {
            var sb = new StringBuilder();
            sb.Append($"volumes on the level {LightProbeVolume.Exists}");
            LightVolumeReceiver rec = item.GetComponentInChildren<LightVolumeReceiver>(true);
            sb.Append(rec ? $"; receiver {rec.method}/{rec.volumeDetection}, volume {(rec.currentLightProbeVolume ? rec.currentLightProbeVolume.name : "none")}, {rec.materialInstances.Count} material instances"
                          : "; no receiver");
            var rends = item.GetComponentsInChildren<MeshRenderer>().Where(r => r.enabled).ToList();
            foreach (Component d in Debris()) if (d) rends.AddRange(d.GetComponentsInChildren<MeshRenderer>().Where(r => r.enabled));
            foreach (MeshRenderer r in rends.Take(6))
            {
                sb.Append($" | {r.name}: probes {r.lightProbeUsage}");
                foreach (Material m in r.sharedMaterials.Where(m => m).Take(3))
                {
                    Texture sh = m.HasProperty("_ProbeVolumeShR") ? m.GetTexture("_ProbeVolumeShR") : null;
                    sb.Append($", [{m.name}: keyword {m.IsKeywordEnabled("_PROBEVOLUME_ON")}, use {(m.HasProperty("_UseProbeVolume") ? m.GetFloat("_UseProbeVolume") : -1):0}, sh {(sh ? sh.name : "none")}]");
                }
            }
            return sb.ToString();
        }

        private static int DebrisCount() =>
            (int)(ZType("ItemDebris")?.GetProperty("Count", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ?? 0);

        private static Bounds ColliderBounds(Item item)
        {
            Bounds b = new Bounds(item.transform.position, Vector3.zero);
            bool any = false;
            foreach (Collider c in item.GetComponentsInChildren<Collider>())
            {
                if (!c || !c.enabled || c.isTrigger) continue;
                if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
            }
            return b;
        }

        private static Bounds RenderBounds(Item item)
        {
            Bounds b = new Bounds(item.transform.position, Vector3.zero);
            bool any = false;
            foreach (Renderer r in item.GetComponentsInChildren<Renderer>())
            {
                if (!r || !r.enabled || !(r is MeshRenderer)) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return b;
        }

        /// <summary>
        /// How much of its outline the cut face covers, per material: its area against its convex hull's (a solid disc
        /// about 1, the ring of a cup's wall much less), over the kept half's cut face.
        /// </summary>
        private static string CapFill(Item item)
        {
            MeshRenderer cap = item ? item.GetComponentsInChildren<MeshRenderer>().FirstOrDefault(r => r.name == "Zandatsu_WeaponCut") : null;
            Mesh mesh = cap ? cap.GetComponent<MeshFilter>().sharedMesh : null;
            if (!mesh || !mesh.isReadable) return "no cut face";
            Vector3[] v = mesh.vertices;
            Vector3 n = Vector3.zero;
            foreach (Vector3 x in mesh.normals) n += x;
            n.Normalize();
            Vector3 t1 = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized, t2 = Vector3.Cross(n, t1);
            var parts = new List<string>();
            Material[] mats = cap.sharedMaterials;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                int[] t = mesh.GetTriangles(s);
                double area = 0;
                for (int k = 0; k + 2 < t.Length; k += 3) area += Vector3.Cross(v[t[k + 1]] - v[t[k]], v[t[k + 2]] - v[t[k]]).magnitude * 0.5;
                var pts = t.Distinct().Select(i => new Vector2(Vector3.Dot(v[i], t1), Vector3.Dot(v[i], t2))).OrderBy(p => p.x).ThenBy(p => p.y).ToList();
                var hull = new List<Vector2>();
                double Turn(Vector2 o, Vector2 a, Vector2 b) => (double)(a.x - o.x) * (b.y - o.y) - (double)(a.y - o.y) * (b.x - o.x);
                foreach (var pass in new[] { pts, Enumerable.Reverse(pts).ToList() })
                {
                    int start = hull.Count;
                    foreach (Vector2 p in pass)
                    {
                        while (hull.Count >= start + 2 && Turn(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                        hull.Add(p);
                    }
                    hull.RemoveAt(hull.Count - 1);
                }
                double hullArea = 0;
                for (int i = 0, j = hull.Count - 1; i < hull.Count; j = i++) hullArea += (double)hull[j].x * hull[i].y - (double)hull[i].x * hull[j].y;
                hullArea = Math.Abs(hullArea) * 0.5;
                parts.Add($"{(s < mats.Length && mats[s] ? mats[s].name : "?")} {area * 1e4:0} cm² of {hullArea * 1e4:0} ({(hullArea > 0 ? area / hullArea : 0):0.00})");
            }
            return string.Join("; ", parts);
        }

        /// <summary>
        /// The cut face's texture as the mod lays it: a tiling one at its own scale (UV per metre = 1 / its tile size in
        /// CapTextures); wood as a log end inside the image (no repeat: UV within 0-1), at 0.15-9 UV per metre.
        /// </summary>
        private static bool CapLaid(Item item, string kind, out string how)
        {
            how = "no cut face";
            MeshRenderer cap = item.GetComponentsInChildren<MeshRenderer>().FirstOrDefault(r => r.name == "Zandatsu_WeaponCut");
            if (!cap) return false;
            Material mat = cap.sharedMaterials.FirstOrDefault(m => m);
            float d = Density(cap, m => m == mat);
            if (kind == "Wood")
            {
                Mesh capMesh = cap.GetComponent<MeshFilter>().sharedMesh;
                Vector2[] all = capMesh.uv;
                Vector2[] uv = capMesh.GetTriangles(0).Distinct().Select(i => all[i]).ToArray();   // the cut face, not a dark inside
                float lo = uv.Length > 0 ? uv.Min(u => Mathf.Min(u.x, u.y)) : 0f, hi = uv.Length > 0 ? uv.Max(u => Mathf.Max(u.x, u.y)) : 0f;
                how = $"{(mat ? mat.name : "-")}: UV {lo:0.##} to {hi:0.##}, {d:0.##} UV/m";
                return lo > -0.02f && hi < 1.02f && d > 0.15f && d < 9f;   // a log cut lengthwise: a long board, coarse grain
            }
            float want = 1f / (kind == "Stone" ? 0.4f : kind == "Bone" ? 0.08f : kind == "Paper" ? 0.06f : kind == "Wicker" ? 0.12f : kind == "Straw" ? 0.2f : 0.15f);
            how = $"{(mat ? mat.name : "-")}: {d:0.##} UV/m, meant {want:0.##}";
            return d > want * 0.8f && d < want * 1.25f;
        }

        private static float Density(MeshRenderer r, Func<Material, bool> which)
        {
            MeshFilter mf = r.GetComponent<MeshFilter>();
            Mesh mesh = mf ? mf.sharedMesh : null;
            if (!mesh || !mesh.isReadable) return 0f;
            var v = mesh.vertices;
            var uv = mesh.uv;
            if (uv.Length != v.Length) return 0f;
            Matrix4x4 m = r.transform.localToWorldMatrix;
            Material[] mats = r.sharedMaterials;
            double world = 0, tex = 0;
            for (int s = 0; s < mesh.subMeshCount && s < mats.Length; s++)
            {
                if (!which(mats[s])) continue;
                int[] t = mesh.GetTriangles(s);
                for (int k = 0; k + 2 < t.Length; k += 3)
                {
                    Vector3 p0 = m.MultiplyPoint3x4(v[t[k]]), p1 = m.MultiplyPoint3x4(v[t[k + 1]]), p2 = m.MultiplyPoint3x4(v[t[k + 2]]);
                    Vector2 a = uv[t[k + 1]] - uv[t[k]], b = uv[t[k + 2]] - uv[t[k]];
                    world += Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5;
                    tex += Mathf.Abs(a.x * b.y - a.y * b.x) * 0.5;
                }
            }
            return world > 1e-6 ? (float)Math.Sqrt(tex / world) : 0f;
        }

        /// <summary>Cut Weapons: a sword hanging in the air is cut in two by a swing through it.</summary>
        private IEnumerator S_weapon_cut()
        {
            Vector3 front = PlayerFront(2f) + Vector3.up * 1.2f;
            Item target = null;
            yield return SpawnItem(Sword, x => target = x, front);
            if (!target) yield break;
            target.physicBody.isKinematic = true;
            target.transform.rotation = Quaternion.Euler(0f, 0f, 90f);   // lying across
            yield return Wait(0.5f);
            Damager td = SlashDamager(target);
            Vector3 mid = td ? td.transform.position : target.transform.position;
            Item blade = null;
            yield return SpawnItem(Sword, x => blade = x);
            yield return Swing(blade, mid, Vector3.forward, Vector3.down, 10f, false);
            yield return Wait(1.5f);
            Expect("weapon cut", LogHas("weapon cut"), LastLines("weapon cut", 2));
            yield return ShotAt("after", mid);
        }

        /// <summary>
        /// A weapon with a hand-set centre of mass, cut 5 cm past it (away from the grip): the point stays on the kept side,
        /// but the part that went took its mass with it, so the centre moves towards the grip. It stayed where the whole
        /// weapon's was ("hand-set kept, moved 0 cm": an axe without its head balanced as the axe).
        /// </summary>
        private IEnumerator S_weapon_balance()
        {
            yield return Balance("AxeShortCommon");
            yield return Balance(Sword);
        }

        /// <summary>
        /// An axe without its head, in the player's hand, through a physics modifier (Time Stop sets one on every item and
        /// takes it off again; so do telekinesis and water): the game then sets the mass back from the collision handler's
        /// own copy, the whole axe's, and the stump weighed as the axe.
        /// </summary>
        private IEnumerator S_weapon_held()
        {
            Item axe = null;
            yield return SpawnItem("AxeShortCommon", x => axe = x, PlayerFront(2f) + Vector3.up * 1.2f);
            if (!axe) yield break;
            Rigidbody rb = axe.physicBody.rigidBody;
            string Body() => $"mass {rb.mass:0.00} kg (handlers' own {string.Join("/", axe.collisionHandlers.Select(h => OrgMass(h).ToString("0.00")))}), " +
                             $"centre {rb.centerOfMass.ToString("F3")}, inertia {rb.inertiaTensor.ToString("F4")}";
            Note("whole", Body());
            yield return CutAcross(axe, 0.05f);
            if (!axe) yield break;
            float cutMass = rb.mass;
            Vector3 cutCom = rb.centerOfMass;
            Note("cut", Body());
            Handle grip = axe.mainHandleRight ? axe.mainHandleRight : axe.GetComponentInChildren<Handle>();
            RagdollHand hand = Player.local && Player.local.creature ? Player.local.creature.handRight : null;
            if (!grip || !hand) { Expect("grabbed", false, grip ? "no player hand" : "no grip"); yield break; }
            axe.physicBody.isKinematic = false;
            hand.Grab(grip, true);
            yield return Wait(1f);
            Note("held", Body());
            Expect("held", hand.grabbedHandle == grip, hand.grabbedHandle ? hand.grabbedHandle.name : "nothing");
            Expect("held: the cut mass", Mathf.Abs(rb.mass - cutMass) < cutMass * 0.02f, $"{rb.mass:0.00} kg, cut {cutMass:0.00} kg");
            Expect("held: the cut centre of mass", (rb.centerOfMass - cutCom).magnitude < 0.01f, $"{rb.centerOfMass} vs {cutCom}");
            var modifier = new object();
            foreach (CollisionHandler ch in axe.collisionHandlers) ch.SetPhysicModifier(modifier, 0f);
            yield return Wait(0.3f);
            foreach (CollisionHandler ch in axe.collisionHandlers) ch.RemovePhysicModifier(modifier);
            yield return Wait(0.3f);
            Note("after a physics modifier", Body());
            Expect("the cut mass after a physics modifier (Time Stop)", Mathf.Abs(rb.mass - cutMass) < cutMass * 0.02f, $"{rb.mass:0.00} kg, cut {cutMass:0.00} kg");
            hand.UnGrab(false);
            yield return Wait(0.5f);
            Note("let go", Body());
            Expect("the cut centre of mass after letting go", (rb.centerOfMass - cutCom).magnitude < 0.01f, $"{rb.centerOfMass} vs {cutCom}");
            Done(axe);
        }

        private static float OrgMass(CollisionHandler h) =>
            typeof(CollisionHandler).GetField("orgMass", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(h) is float f ? f : -1f;

        /// <summary>A lying item cut across, `past` m beyond its centre of mass away from its grip (a new blade).</summary>
        private IEnumerator CutAcross(Item target, float past)
        {
            target.physicBody.isKinematic = true;
            target.transform.rotation = Quaternion.Euler(0f, 0f, 90f);   // lying across
            yield return Wait(0.5f);
            Handle grip = target.mainHandleRight ? target.mainHandleRight : target.GetComponentInChildren<Handle>();
            Vector3 com = target.physicBody.rigidBody.worldCenterOfMass;
            Vector3 along = grip ? Vector3.ProjectOnPlane(com - grip.transform.position, Vector3.up) : Vector3.zero;
            if (along.sqrMagnitude < 1e-4f) along = target.transform.up;
            along.Normalize();
            Item blade = null;
            yield return SpawnItem(Sword, x => blade = x);
            if (!blade) yield break;
            yield return Swing(blade, com + along * past, Vector3.Cross(Vector3.up, along), Vector3.down, 10f, false);
            Done(blade);
            yield return Wait(1f);
        }

        private IEnumerator Balance(string id)
        {
            Item target = null;
            yield return SpawnItem(id, x => target = x, PlayerFront(2f) + Vector3.up * 1.2f);
            if (!target) yield break;
            target.physicBody.isKinematic = true;
            target.transform.rotation = Quaternion.Euler(0f, 0f, 90f);   // lying across
            yield return Wait(0.5f);
            Handle grip = target.mainHandleRight ? target.mainHandleRight : target.GetComponentInChildren<Handle>();
            if (!target.useCustomCenterOfMass || !grip)
            {
                Note("warning", $"{id}: {(grip ? "no hand-set centre of mass" : "no grip")}, nothing to check");
                Done(target);
                yield break;
            }
            Rigidbody rb = target.physicBody.rigidBody;
            Vector3 com = rb.worldCenterOfMass, hold = grip.transform.position;
            Vector3 along = Vector3.ProjectOnPlane(com - hold, Vector3.up);
            if (along.sqrMagnitude < 1e-4f) along = target.transform.up;
            along.Normalize();
            Vector3 at = com + along * 0.05f;
            float mass = rb.mass;
            Item blade = null;
            yield return SpawnItem(Sword, x => blade = x);
            if (!blade) yield break;
            yield return Swing(blade, at, Vector3.Cross(Vector3.up, along), Vector3.down, 10f, false);
            Done(blade);
            yield return Wait(1f);
            if (!target || !rb) { Expect($"{id} still there", false, "gone"); yield break; }
            Vector3 now = rb.worldCenterOfMass;
            float towardsGrip = Vector3.Dot(com - now, along) * 100f;
            Expect($"{id} cut", rb.mass < mass * 0.99f, $"mass {mass:0.00} -> {rb.mass:0.00} kg; {LastLines($"weapon cut {id}: centre of mass", 1)}");
            Expect($"{id} centre of mass moved towards the grip", towardsGrip > 1f,
                   $"{towardsGrip:0.0} cm (centre {com} -> {now}, grip {hold}, cut at {at})");
            Expect($"{id} centre of mass on what is left", Vector3.Dot(at - now, along) > 0f, $"{Vector3.Dot(at - now, along) * 100f:0.0} cm before the cut");
            yield return ShotAt("after_" + id, now);
            Done(target);
        }

        /// <summary>A blade used up: away, so the game's item limit never takes a fresh one (one vanished while being set up).</summary>
        private void Done(Item used)
        {
            if (!used) return;
            spawned.Remove(used.gameObject);
            used.Despawn();
        }

        private static List<Component> Debris() =>
            ZType("ItemDebris") is Type t ? FindObjectsOfType(t).Cast<Component>().Where(c => c).ToList() : new List<Component>();

        /// <summary>All of a cut-off half's colliders together (the item's own it was given, or its shape).</summary>
        private static Bounds DebrisBounds(Component debris)
        {
            Collider[] cols = debris.GetComponentsInChildren<Collider>();
            if (cols.Length == 0) return new Bounds(debris.transform.position, Vector3.zero);
            Bounds b = cols[0].bounds;
            foreach (Collider c in cols) b.Encapsulate(c.bounds);
            return b;
        }

        /// <summary>A level swing (or a chop down) through the middle of a cut-off half lying where it fell (a new blade each time).</summary>
        private IEnumerator SwingAt(Component debris, bool down = false)
        {
            Vector3 at = DebrisBounds(debris).center;
            Item blade = null;
            yield return SpawnItem(Sword, x => blade = x);
            if (!blade) yield break;
            Vector3 fwd = Vector3.ProjectOnPlane(at - PlayerPos(), Vector3.up).normalized;
            yield return Swing(blade, at, Vector3.Cross(Vector3.up, fwd), down ? Vector3.down : fwd, 10f, false);
            yield return Wait(0.8f);
            Done(blade);
        }

        /// <summary>
        /// Cut Pieces Again on the cut-off halves of an object and of a weapon: the half that fell is cut, and its half
        /// again; each time one more half, with the cut faces the first cut made (the barrel's wood, the sword's steel).
        /// </summary>
        private IEnumerator S_debris_recut()
        {
            Item barrel = null;
            yield return SpawnItem("Barrel1", x => barrel = x, PlayerFront(1.8f) + Vector3.up * 0.4f);
            if (!barrel) { Expect("barrel spawned", false, ""); yield break; }
            yield return Wait(2f);
            Bounds look = RenderBounds(barrel);
            var before = new HashSet<Component>(Debris());
            Item blade = null;
            yield return SpawnItem(Sword, x => blade = x);
            Vector3 fwd = Vector3.ProjectOnPlane(look.center - PlayerPos(), Vector3.up).normalized;
            yield return Swing(blade, look.center, Vector3.Cross(Vector3.up, fwd), fwd, 10f, false);
            Done(blade);
            yield return Wait(2f);
            Component half = Debris().FirstOrDefault(d => !before.Contains(d));
            Expect("the barrel's cut-off half", half, "debris " + Debris().Count);
            if (!half) yield break;
            for (int k = 1; k <= 2; k++)
            {
                int count = Debris().Count, lines = ScenarioLog().Count(l => l.msg.Contains("debris cut ("));
                yield return SwingAt(half);
                int now = ScenarioLog().Count(l => l.msg.Contains("debris cut ("));
                Expect($"half cut again ({k})", now > lines && Debris().Count > count, $"debris {count} -> {Debris().Count}; {LastLines("debris cut (", 1)}");
                if (now > lines) Expect($"its cut face is the barrel's wood ({k})", LastLines("debris cut (", 9).Contains("cut face Zandatsu_Cap_Wood"), LastLines("debris cut (", 1));
                yield return Wait(1f);
            }
            yield return ShotAt("barrel halves", look.center);

            // A sword's cut-off half: hot steel.
            Item sword = null;
            Vector3 front = PlayerFront(1.8f) + Vector3.up * 1.0f;   // where the barrel stood: the half falls on clear floor
            yield return SpawnItem(Sword, x => sword = x, front);
            if (!sword) yield break;
            sword.physicBody.isKinematic = true;
            sword.transform.rotation = Quaternion.Euler(0f, 0f, 90f);
            yield return Wait(0.5f);
            // Through the middle of the whole sword: half a blade falls (at the blade's damager only its tip did, a sliver
            // too flat on the floor for any swing to get through).
            Vector3 mid = RenderBounds(sword).center;
            before = new HashSet<Component>(Debris());
            yield return SpawnItem(Sword, x => blade = x);
            yield return Swing(blade, mid, Vector3.forward, Vector3.down, 10f, false);
            Done(blade);
            yield return Wait(2f);
            half = Debris().FirstOrDefault(d => !before.Contains(d));
            Expect("the sword's cut-off half", half, "debris " + Debris().Count);
            if (!half) yield break;
            int swordLines = ScenarioLog().Count(l => l.msg.Contains("debris cut ("));
            // Held up off the floor and chopped from above: lying flat, the bench's sword met the floor with its cross-guard
            // above the half (it passed only when the half fell on the barrel's pieces).
            Rigidbody hrb = half.GetComponent<Rigidbody>();
            if (hrb)
            {
                hrb.isKinematic = true;
                hrb.position = half.transform.position += Vector3.up * 0.6f;
            }
            yield return Wait(0.2f);
            if (half) yield return SwingAt(half, down: true);
            if (half && hrb) hrb.isKinematic = false;
            Expect("sword half cut again, hot steel", ScenarioLog().Count(l => l.msg.Contains("debris cut (")) > swordLines &&
                                                    LastLines("debris cut (", 9).Contains("cut face hot steel"), LastLines("debris cut (", 1));
        }

        /// <summary>
        /// The objects a player cut on Outpost (2026-10-24): their cut-off halves went through blades and sank into the
        /// floor. Each one cut: what colliders its half got, whether it lies on the floor, whether a swing cuts it again.
        /// </summary>
        private IEnumerator S_debris_props()
        {
            foreach (string id in new[] { "Mug2", "AnvilSupport", "Crate", "CrateOpen2", "Pottery_03" })
            {
                Item prop = null;
                yield return SpawnItem(id, x => prop = x, PlayerFront(1.8f) + Vector3.up * 0.6f);
                if (!prop) { Note("warning", "no " + id); continue; }
                yield return Wait(2.5f);
                Bounds look = RenderBounds(prop);
                float floor = ColliderBounds(prop).min.y;
                Note(id + " colliders", string.Join(", ", prop.GetComponentsInChildren<Collider>().Where(k => k.enabled && !k.isTrigger)
                                                         .GroupBy(k => k.GetType().Name + (k is MeshCollider mc ? (mc.convex ? " convex" : " concave") : "")).Select(g => g.Key + " x" + g.Count())));
                var before = new HashSet<Component>(Debris());
                Item blade = null;
                yield return SpawnItem(Sword, x => blade = x);
                Vector3 fwd = Vector3.ProjectOnPlane(look.center - PlayerPos(), Vector3.up).normalized;
                yield return Swing(blade, look.center, Vector3.Cross(Vector3.up, fwd), fwd, 10f, false);
                Done(blade);
                yield return null;
                Component half = Debris().FirstOrDefault(d => !before.Contains(d));
                if (!half) { Expect(id + " cut", false, LastLines("prop cut " + id, 1)); Done(prop); continue; }
                var heights = new List<string>();
                for (int k = 0; k < 10 && half; k++)
                {
                    heights.Add($"{DebrisBounds(half).min.y:0.00}");
                    yield return Wait(0.25f);
                }
                Note(id + " half's lowest point every 0.25 s", string.Join(" ", heights));
                Note(id + " half", string.Join(", ", half.GetComponentsInChildren<Collider>().Select(k =>
                    $"{k.GetType().Name}{(k.enabled ? "" : " off")}{(k.isTrigger ? " trigger" : "")} {k.bounds.size.x:0.00}x{k.bounds.size.y:0.00}x{k.bounds.size.z:0.00}")) +
                    $"; render size {half.GetComponent<Renderer>().bounds.size}");
                Bounds hit = DebrisBounds(half), seen = half.GetComponent<Renderer>().bounds;
                // The game's own colliders only roughly follow an object (a vase's cover about 0.8 of it); a body left out
                // (an anvil stand's: 0.2 of it) is what this is for.
                Expect(id + " half's colliders cover it", hit.size.x >= seen.size.x * 0.6f && hit.size.y >= seen.size.y * 0.6f && hit.size.z >= seen.size.z * 0.6f,
                       $"colliders {hit.size}, seen {seen.size}");
                Expect(id + " half lies on the floor", hit.min.y > floor - 0.1f, $"its lowest point {hit.min.y:0.00}, floor {floor:0.00}");
                int lines = ScenarioLog().Count(l => l.msg.Contains("debris cut ("));
                yield return SwingAt(half);
                Expect(id + " half cut again", ScenarioLog().Count(l => l.msg.Contains("debris cut (")) > lines, LastLines("debris cut (", 1));
                yield return ShotNear(id, look.center, 1.2f);
                Done(prop);
                ClearDebris();
                yield return Wait(0.5f);
            }
        }

        /// <summary>
        /// A wide stump cut across high up: its top falls on what is left and must not sink into it (collisions between the
        /// halves were switched off for good when the cut was made), and it is lit where it lies (lit at the whole stump's
        /// origin, under the floor once the top lay there, it was black).
        /// </summary>
        private IEnumerator S_stump_stack()
        {
            foreach (string id in new[] { "AnvilSupport", "Trunk4" })
            {
                ClearDebris();
                Item stump = null;
                yield return SpawnItem(id, x => stump = x, PlayerFront(1.8f) + Vector3.up * 0.6f);
                if (!stump) { Note("warning", "no " + id); continue; }
                yield return Wait(2.5f);
                Bounds look = RenderBounds(stump);
                float cutY = look.min.y + look.size.y * 0.6f;
                Vector3 at = new Vector3(look.center.x, cutY, look.center.z);
                Vector3 fwd = Vector3.ProjectOnPlane(at - PlayerPos(), Vector3.up).normalized;
                var before = new HashSet<Component>(Debris());
                Item blade = null;
                yield return SpawnItem(Sword, x => blade = x);
                yield return Swing(blade, at, Vector3.Cross(Vector3.up, fwd), fwd, 10f, false);
                Done(blade);
                yield return Wait(3f);
                Component top = Debris().FirstOrDefault(d => !before.Contains(d));
                if (!top || !stump) { Expect(id + " cut", false, LastLines("prop cut " + id, 1)); if (stump) Done(stump); continue; }
                // How deep its colliders are in what is left (the bench's blade may push it off the cut, onto the floor).
                float deepest = 0f;
                foreach (Collider a in top.GetComponentsInChildren<Collider>())
                    foreach (Collider b in stump.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger))
                        if (Physics.ComputePenetration(a, a.transform.position, a.transform.rotation, b, b.transform.position, b.transform.rotation, out _, out float d))
                            deepest = Mathf.Max(deepest, d);
                Renderer tr = top.GetComponent<Renderer>();
                Note(id, $"cut at {cutY:0.00}, its top's lowest point {DebrisBounds(top).min.y:0.00}, lit from {(tr.probeAnchor ? tr.probeAnchor.position.ToString() : "its middle")}");
                Expect(id + " top not sunk into what is left", deepest < 0.03f, $"{deepest * 100f:0.0} cm deep");
                Expect(id + " top lit where it is", !tr.probeAnchor || tr.bounds.Contains(tr.probeAnchor.position),
                       tr.probeAnchor ? $"at {tr.probeAnchor.position}, it is {tr.bounds.min}-{tr.bounds.max}" : "its middle");
                yield return ShotAt(id, look.center);
                Done(stump);
            }
            ClearDebris();
        }

        /// <summary>
        /// A stab at a pot: point first, never a cut, so it must not switch the pot's breaking off as a blade swung edge first
        /// does (Disarm ran before the edge check: stabs and flat blows then neither cut nor broke pottery).
        /// </summary>
        private IEnumerator S_stab_pottery()
        {
            Item pot = null;
            yield return SpawnItem("Pottery_01", x => pot = x, PlayerFront(1.8f) + Vector3.up * 0.6f);
            if (!pot) yield break;
            yield return Wait(2.5f);
            Breakable br = pot.GetComponentInChildren<Breakable>(true);
            if (!br) { Note("skipped", "the pot is not breakable"); yield break; }
            Bounds look = RenderBounds(pot);
            Item blade = null;
            yield return SpawnItem(Sword, x => blade = x);
            if (!blade) yield break;
            bool everOff = false;
            IEnumerator stab = Thrust(blade, look.center, Vector3.ProjectOnPlane(look.center - PlayerPos(), Vector3.up), 10f, 0.05f);
            while (stab.MoveNext())
            {
                if (br && !br.canBreak) everOff = true;
                yield return stab.Current;
            }
            yield return Wait(1f);
            Note("pot", $"broken {(br ? br.IsBroken.ToString() : "gone")}, blade: {Stuck(blade)}");
            Expect("a stab leaves the pot's breaking on", !everOff, everOff ? "switched off during the stab" : "on");
            Expect("a stab does not cut it", !LogHas("prop cut Pottery_01"), LastLines("prop cut", 1));
            Done(blade);
        }

        /// <summary>
        /// A sword stuck in an enemy's forearm (a stab), then the arm cut off at the shoulder: where the stuck sword goes.
        /// Cut Weapons off, so the chop does not cut the stuck sword itself.
        /// </summary>
        private IEnumerator S_stab_stuck()
        {
            Set("CutWeapons", false);
            Creature c = null;
            yield return Enemy(x => c = x);
            if (!c) yield break;
            RagdollPart upper = c.ragdoll.GetPart(RagdollPart.Type.RightArm), hand = c.ragdoll.GetPart(RagdollPart.Type.RightHand);
            if (!upper || !hand) { Note("skipped", "no arm"); yield break; }
            Vector3 at = Vector3.Lerp(upper.transform.position, hand.transform.position, 0.6f);
            Item sword = null;
            yield return SpawnItem(Sword, x => sword = x);
            if (!sword) yield break;
            yield return Thrust(sword, at, Vector3.ProjectOnPlane(at - PlayerPos(), Vector3.up), 6f, 0.08f);
            yield return Wait(0.5f);
            Note("stuck before", Stuck(sword));
            if (!sword || !sword.isPenetrating) { Expect("the sword went in", false, Stuck(sword)); yield break; }
            Vector3 before = sword.transform.position;
            yield return SwingAt(c, RagdollPart.Type.RightArm, 10f, false, vertical: true);
            yield return Wait(2f);
            Note("stuck after", Stuck(sword) + $", moved {(sword ? (sword.transform.position - before).magnitude : 0f):0.00} m, down {(sword ? before.y - sword.transform.position.y : 0f):0.00} m");
            Expect("arm cut", Cuts(c) >= 1, $"{Cuts(c)} cuts");
            Expect("the stuck sword did not stay hanging in the air", !sword || before.y - sword.transform.position.y > 0.2f,
                   sword ? $"down {before.y - sword.transform.position.y:0.00} m; {Stuck(sword)}" : "gone");
        }

        /// <summary>
        /// A sword stuck in a barrel's side near its top, then the barrel cut across below it: the top half falls as debris,
        /// and where the stuck sword goes (it was joined to the barrel, which keeps its bottom half).
        /// </summary>
        private IEnumerator S_stab_stuck_prop()
        {
            Set("CutWeapons", false);
            Item barrel = null;
            yield return SpawnItem("Barrel1", x => barrel = x, PlayerFront(1.8f) + Vector3.up * 0.4f);
            if (!barrel) yield break;
            yield return Wait(2.5f);
            Bounds look = RenderBounds(barrel);
            Vector3 fwd = Vector3.ProjectOnPlane(look.center - PlayerPos(), Vector3.up).normalized;
            Vector3 high = look.center + Vector3.up * look.extents.y * 0.6f - fwd * look.extents.z;
            Item sword = null;
            yield return SpawnItem(Sword, x => sword = x);
            if (!sword) yield break;
            yield return Thrust(sword, high, fwd, 6f, 0.08f);
            yield return Wait(0.5f);
            Note("stuck before", Stuck(sword));
            if (!sword || !sword.isPenetrating) { Expect("the sword went in", false, Stuck(sword)); yield break; }
            Vector3 before = sword.transform.position;
            var debrisBefore = new HashSet<Component>(Debris());
            Item blade = null;
            yield return SpawnItem(Sword, x => blade = x);
            yield return Swing(blade, look.center - Vector3.up * look.extents.y * 0.2f, Vector3.Cross(Vector3.up, fwd), fwd, 10f, false);
            Done(blade);
            yield return Wait(2f);
            Component top = Debris().FirstOrDefault(d => !debrisBefore.Contains(d));
            Note("stuck after", Stuck(sword) + $", moved {(sword ? (sword.transform.position - before).magnitude : 0f):0.00} m; top half {(top ? DebrisBounds(top).center.ToString() : "none")}, barrel {(barrel ? RenderBounds(barrel).center.ToString() : "gone")}");
            Expect("barrel cut", LogHas("prop cut Barrel1"), LastLines("prop cut", 1));
            Expect("the stuck sword did not stay hanging in the air", !sword || before.y - sword.transform.position.y > 0.2f,
                   sword ? $"down {before.y - sword.transform.position.y:0.00} m; {Stuck(sword)}" : "gone");
        }

        /// <summary>How a stuck blade is held: by what body, where it is.</summary>
        private static string Stuck(Item blade)
        {
            if (!blade) return "gone";
            var joints = blade.GetComponentsInChildren<Joint>().Where(j => j && j.connectedBody)
                .Select(j => $"{j.connectedBody.name}{(j.connectedBody.isKinematic ? " (kinematic)" : "")}{(j.connectedBody.gameObject.activeInHierarchy ? "" : " (inactive)")}");
            return $"penetrating {blade.isPenetrating}, joined to [{string.Join(", ", joints)}], at {blade.transform.position}";
        }

        /// <summary>
        /// A sword hanging in the air cut through its middle: where its cut-off half falls, every quarter second, against the
        /// floor under it (a half of a blade was seen falling through the floor), and what its collider is.
        /// </summary>
        private IEnumerator S_debris_fall()
        {
            Item sword = null;
            yield return SpawnItem(Sword, x => sword = x, PlayerFront(1.8f) + Vector3.up * 1.0f);
            if (!sword) yield break;
            sword.physicBody.isKinematic = true;
            sword.transform.rotation = Quaternion.Euler(0f, 0f, 90f);
            yield return Wait(0.5f);
            Vector3 mid = RenderBounds(sword).center;
            bool ground = Physics.Raycast(mid + Vector3.down * 0.2f, Vector3.down, out RaycastHit hit, 5f, ~0, QueryTriggerInteraction.Ignore);
            float floor = ground ? hit.point.y : float.NaN;
            var before = new HashSet<Component>(Debris());
            Item blade = null;
            yield return SpawnItem(Sword, x => blade = x);
            yield return Swing(blade, mid, Vector3.forward, Vector3.down, 10f, false);
            Done(blade);
            for (int f = 0; f < 5 && Debris().All(d => before.Contains(d)); f++) yield return null;
            Component half = Debris().FirstOrDefault(d => !before.Contains(d));
            if (!half) { Expect("the sword's cut-off half", false, "no debris"); yield break; }
            var cols = half.GetComponentsInChildren<Collider>();
            var rb = half.GetComponent<Rigidbody>();
            Note("half", $"colliders {string.Join(", ", cols.Select(k => $"{k.GetType().Name} {k.bounds.size.x:0.000}x{k.bounds.size.y:0.000}x{k.bounds.size.z:0.000}"))}, " +
                         $"layer {LayerMask.LayerToName(half.gameObject.layer)}, mass {rb.mass:0.00} kg, detection {rb.collisionDetectionMode}; " +
                         $"floor at {floor:0.00} ({(ground ? hit.collider.name + " on " + LayerMask.LayerToName(hit.collider.gameObject.layer) : "none")})");
            var path = new List<string>();
            for (int k = 0; k < 12 && half; k++)
            {
                path.Add($"{half.transform.position.y:0.00}");
                yield return Wait(0.25f);
            }
            Note("height every 0.25 s", string.Join(" ", path));
            if (half) { Note("half materials", Materials(half.gameObject)); yield return ShotNear("half", DebrisBounds(half).center, 0.7f); }
            Expect("the half lies on the floor", half && DebrisBounds(half).min.y > floor - 0.03f,
                half ? $"its lowest point {DebrisBounds(half).min.y:0.00}, floor {floor:0.00}" : "gone");
        }

        /// <summary>
        /// Grab Pieces on a cut-off half (called straight, the bench has no grip button): the right hand holds it (joined,
        /// marked held), a cut keeps the side towards the hand on the held half and drops the other; letting go frees it.
        /// </summary>
        private IEnumerator S_debris_grab()
        {
            Item barrel = null;
            yield return SpawnItem("Barrel1", x => barrel = x, PlayerFront(1.8f) + Vector3.up * 0.4f);
            if (!barrel) yield break;
            yield return Wait(2f);
            Bounds look = RenderBounds(barrel);
            var before = new HashSet<Component>(Debris());
            Item blade = null;
            yield return SpawnItem(Sword, x => blade = x);
            Vector3 fwd = Vector3.ProjectOnPlane(look.center - PlayerPos(), Vector3.up).normalized;
            yield return Swing(blade, look.center, Vector3.Cross(Vector3.up, fwd), fwd, 10f, false);
            yield return Wait(2f);
            Component half = Debris().FirstOrDefault(d => !before.Contains(d));
            if (!half) { Expect("the barrel's cut-off half", false, ""); yield break; }

            Type grabType = ZType("PieceGrab");
            object grab = Activator.CreateInstance(grabType);
            RagdollHand hand = Player.local.creature.handRight;
            MethodInfo grabIt = grabType.GetMethod("Grab", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo release = grabType.GetMethod("Release", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo holdPoint = grabType.GetMethod("HoldPoint");
            FieldInfo held = half.GetType().GetField("held");
            grabIt.Invoke(grab, new object[] { 1, hand, null, half });
            yield return null;
            bool joined = half.GetComponent<ConfigurableJoint>();
            Expect("held in the hand", joined && (bool)held.GetValue(half), $"joint {joined}, held {held.GetValue(half)}");
            var point = (Vector3?)holdPoint.Invoke(grab, new object[] { half });
            Expect("hold point known", point.HasValue, point?.ToString() ?? "none");

            int count = Debris().Count;
            Vector3 c = DebrisBounds(half).center;
            MethodInfo cut = half.GetType().GetMethod("CutAgain");
            bool ok = (bool)cut.Invoke(half, new object[] { c, Vector3.up, Vector3.forward * 10f, 0f, null, point });
            yield return null;
            Expect("held half cut, still in the hand", ok && half && half.GetComponent<ConfigurableJoint>() && Debris().Count > count,
                $"cut {ok}, joint {(half ? (bool)half.GetComponent<ConfigurableJoint>() : false)}, debris {count} -> {Debris().Count}");
            release.Invoke(grab, new object[] { 1 });
            yield return null;
            Expect("let go", half && !half.GetComponent<ConfigurableJoint>() && !(bool)held.GetValue(half), "");
            yield return ShotAt("held half", c);
        }

        /// <summary>
        /// The Outer Rim lightsaber: it cuts; left inside the body and moved there for 2 s it does not cut the same enemy
        /// again and again (the stutter players reported).
        /// </summary>
        private IEnumerator S_saber()
        {
            Creature c = null;
            yield return Enemy(x => c = x);
            if (!c) yield break;
            Item saber = null;
            yield return SpawnItem(Saber, x => saber = x);
            if (!saber) { Expect("lightsaber spawned", false, "is The Outer Rim enabled?"); yield break; }
            Call(saber, "TOR.ItemLightsaber", "TurnOn", true);
            yield return Wait(0.5f);
            Vector3 chest = Target(c, RagdollPart.Type.Torso);
            yield return Swing(saber, chest, c.transform.right, c.transform.forward, 10f, false, stopInside: true);
            float t0 = Time.realtimeSinceStartup;
            int framesBefore = frames.Count;
            yield return Wiggle(saber, c.transform.forward, 8f, 2f);
            Expect("lightsaber cuts", Cuts(c) >= 1, $"{Cuts(c)} cuts");
            Expect("no cut loop while inside", Cuts(c) <= 2, $"{Cuts(c)} cuts in {Time.realtimeSinceStartup - t0:0.0} s");
            float worst = frames.Skip(framesBefore).DefaultIfEmpty(0f).Max() * 1000f;
            Expect("no stutter while inside", worst < 150f, $"worst frame {worst:0} ms");
            ExpectNotStretched();
            yield return Shot("after", c);
        }

        /// <summary>The Outer Rim blaster bolts (Only Player Cuts off, so the bolt's hit reaches the check): no cut.</summary>
        private IEnumerator S_blaster()
        {
            Set("PlayerOnly", false);
            Creature c = null;
            yield return Enemy(x => c = x);
            if (!c) yield break;
            // The Outer Rim's own bolt item (its ItemBlasterBolt script, its damager), launched at the chest the way the
            // blaster launches it: thrown, no gravity, fast.
            Vector3 chest = Target(c, RagdollPart.Type.Torso);
            float hp = c.currentHealth;
            for (int i = 0; i < 3; i++)
            {
                Item bolt = null;
                Vector3 from = chest + c.transform.forward * 1.2f + Vector3.up * (0.05f * i);
                yield return SpawnItem(Bolt, x => bolt = x, from);
                if (!bolt) { Expect("bolt spawned", false, "is The Outer Rim enabled?"); yield break; }
                Rigidbody rb = bolt.GetComponent<Rigidbody>() ?? bolt.GetComponentInChildren<Rigidbody>();
                rb.useGravity = false;
                rb.isKinematic = false;
                Vector3 dir = (chest - from).normalized;
                rb.rotation = bolt.transform.rotation = Quaternion.LookRotation(dir);
                bolt.Throw(1f, Item.FlyDetection.Forced);
                rb.velocity = dir * 60f;
                yield return Wait(0.5f);
                Note("bolt", $"{(bolt ? "still at " + bolt.transform.position : "gone: it hit")}, health {(c ? c.currentHealth.ToString("0") : "-")}");
            }
            yield return Wait(1f);
            Expect("bolts hit", !c || c.isKilled || c.currentHealth < hp, c ? $"health {hp:0} -> {c.currentHealth:0}" : "gone");
            Expect("bolts do not cut", Cuts(c) == 0, $"{Cuts(c)} cuts");
        }

        /// <summary>
        /// The Realistic Deaths report: the game cuts an arm off at the shoulder, the enemy lives on and walks away from
        /// his arm, then a Zandatsu cut across the upper torso. Nothing may stretch.
        /// </summary>
        private IEnumerator S_rd_arm()
        {
            Set("VanillaDismemberment", true);
            Creature c = null;
            yield return Enemy(x => c = x, brain: true);
            if (!c) yield break;
            RagdollPart arm = c.ragdoll.GetPart(RagdollPart.Type.RightArm);
            Expect("game slices the arm", arm && arm.TrySlice(), "");
            yield return Wait(6f);
            string gap = arm && arm.parentPart ? (arm.transform.position - arm.parentPart.transform.position).magnitude.ToString("0.00") + " m" : "?";
            Note("state", $"alive {c && !c.isKilled}, arm-to-shoulder {gap}");
            yield return Shot("arm_off", c);
            Item blade = null;
            yield return SpawnItem(Sword, x => blade = x);
            yield return Swing(blade, Target(c, RagdollPart.Type.Torso) + Vector3.up * 0.2f, c.transform.right, c.transform.forward, 10f, false);
            yield return Wait(3f);
            Expect("cut", Cuts(c) >= 1, $"{Cuts(c)} cuts");
            ExpectNotStretched();
            yield return Shot("after", c);
        }

        // ---------- actions ----------

        private IEnumerator Enemy(Action<Creature> done, string wear = null, bool brain = false)
        {
            Creature c = null;
            CreatureData data = Catalog.GetData<CreatureData>("HumanMale");
            Vector3 pos = PlayerFront(2.5f);
            Vector3 fwd = (PlayerPos() - pos).normalized;
            fwd.y = 0f;
            data.SpawnAsync(pos, Quaternion.LookRotation(fwd).eulerAngles.y, null, true, null, x => c = x);
            float t0 = Time.realtimeSinceStartup;
            while (!c && Time.realtimeSinceStartup - t0 < 15f) yield return null;
            if (!c) { Expect("enemy spawned", false, ""); yield break; }
            spawned.Add(c.gameObject);
            bool reused = !seenEnemies.Add(c.gameObject.GetInstanceID());
            string before = lastUse.TryGetValue(c.gameObject.GetInstanceID(), out string was) ? was : null;
            lastUse[c.gameObject.GetInstanceID()] = current;
            Note("pool", reused ? $"{c.name} is a REUSED creature, last used in {before}" : $"{c.name} is a new creature");
            if (reused) { reusedNow = true; reusedFrom = before; }
            if (wear != null)
            {
                ItemData item = Catalog.GetData<ItemData>(wear);
                if (item != null)
                {
                    // As the game equips an outfit: in the creature's container, worn (Zandatsu finds metal armour by the worn contents).
                    var content = new ItemContent(item, new ContentStateWorn());
                    c.container.AddContent(content);
                    c.equipment.EquipWardrobe(content, true);
                }
                else Note("warning", "no item " + wear);
            }
            yield return Wait(3f);
            if (!brain && c.brain) c.brain.enabled = false;   // stands still: the swings are aimed at where he is
            Measure(c.GetComponentsInChildren<SkinnedMeshRenderer>(), out float size, out float edge, out _);
            baseline = Mathf.Max(baseline, edge);
            Measure(c.GetComponentsInChildren<SkinnedMeshRenderer>(), out _, out float hullEdge, out _, out _, true);
            hullBaseline = Mathf.Max(hullBaseline, hullEdge);
            uncutIssues[c] = Integrity(c);
            if (uncutIssues[c].Count > 0) Note("uncut enemy", string.Join("; ", uncutIssues[c]));
            Note("enemy", $"{c.name} at {c.transform.position}{(wear != null ? ", wearing " + wear : "")}, uncut size {size:0.00} m, longest edge {edge:0.00} m");
            done(c);
        }

        private IEnumerator SpawnItem(string id, Action<Item> done, Vector3? at = null)
        {
            ItemData data = Catalog.GetData<ItemData>(id, false);
            if (data == null) { Note("error", "no item data " + id); done(null); yield break; }
            Item item = null;
            data.SpawnAsync(x => item = x, at ?? PlayerFront(1f) + Vector3.up * 3f, Quaternion.identity, null, false);
            float t0 = Time.realtimeSinceStartup;
            while (!item && Time.realtimeSinceStartup - t0 < 15f) yield return null;
            if (item) spawned.Add(item.gameObject);
            done(item);
        }

        private IEnumerator SwingAt(Creature c, RagdollPart.Type part, float speed, bool flat, string imbue = null, bool vertical = false)
        {
            Item blade = null;
            yield return SpawnItem(Sword, x => blade = x);
            if (!blade) yield break;
            if (imbue != null)
            {
                SpellCastCharge spell = Catalog.GetData<SpellCastCharge>(imbue);
                foreach (Imbue im in blade.imbues) im.Transfer(spell, 1000f);
                yield return Wait(0.5f);
                Note("imbue", string.Join(", ", blade.imbues.Select(i => $"{(i.spellCastBase != null ? i.spellCastBase.id : "none")} {i.energy:0}")));
            }
            Vector3 axis = vertical ? Vector3.up : c.transform.right;
            Vector3 dir = c.transform.forward;
            Vector3 at = Target(c, part);
            if (vertical)
            {
                // A chop past the side of the body, through the shoulder joint (the arm part's origin), along the body's own
                // shoulder line: a fresh enemy could stand turned from its transform, and a chop off its transform's right
                // went into the chest.
                RagdollPart.Type other = part == RagdollPart.Type.RightArm ? RagdollPart.Type.LeftArm : RagdollPart.Type.RightArm;
                Vector3 side = Vector3.ProjectOnPlane(at - Target(c, other), Vector3.up);
                if (side.sqrMagnitude > 1e-4f)
                {
                    side.Normalize();
                    Note("aim", $"shoulder line {Vector3.Angle(side, Vector3.Dot(side, c.transform.right) >= 0f ? c.transform.right : -c.transform.right):0} deg off the transform");
                    dir = Vector3.Cross(side, Vector3.up);
                    at += side * 0.09f;
                }
                else at += c.transform.right * 0.09f;
            }
            yield return Swing(blade, at, axis, dir, speed, flat);
            if (blade) blade.Despawn(0.5f);   // a used blade left floating where the swing ended could meet the next one
            // Close looks at the cut faces once things lie still: the body's, and the first two pieces'.
            yield return Wait(1.5f);
            Renderer cap = c ? c.GetComponentsInChildren<Renderer>().FirstOrDefault(r => r && r.enabled && r.name.Contains("Zandatsu_CreatureCap")) : null;
            if (cap) yield return ShotNear("cut_body", cap.bounds.center, 0.7f);
            int n = 0;
            foreach (Component p in Pieces().Take(2))
            {
                Renderer r = p ? p.GetComponentInChildren<Renderer>() : null;
                if (r) yield return ShotNear("cut_piece" + ++n, r.bounds.center, 0.8f);
            }
        }

        /// <summary>
        /// Flies a blade through a point along dir at speed, its blade along axis, edge first (or the flat first),
        /// as the player's blade: it counts as the player's (lastHandler) and sweeps like a held blade.
        /// </summary>
        private IEnumerator Swing(Item item, Vector3 target, Vector3 axis, Vector3 dir, float speed, bool flat, bool stopInside = false, bool interpolate = true)
        {
            Damager d = SlashDamager(item);
            if (!d) { Note("error", "no slash damager on " + item.name); yield break; }
            dir.Normalize();
            axis = Vector3.ProjectOnPlane(axis, dir).normalized;
            Quaternion want = Quaternion.LookRotation(flat ? Vector3.Cross(dir, axis) : dir, axis);
            Quaternion local = Quaternion.Inverse(item.transform.rotation) * d.transform.rotation;
            Quaternion rot = want * Quaternion.Inverse(local);
            item.transform.rotation = rot;
            Vector3 offset = item.transform.position - d.transform.position;
            const float run = 0.7f;
            Rigidbody rb = item.physicBody.rigidBody;
            // Placed first and held still for two physics steps without interpolation: an interpolated move from where
            // the blade spawned showed up as a 40 m/s swing (and cut) in the blade sweep. Then interpolated as a held
            // blade is (the game renders more often than it steps physics: without it the drawn blade jumps).
            RigidbodyInterpolation interpolation = rb.interpolation;
            rb.interpolation = RigidbodyInterpolation.None;
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.position = item.transform.position = target - dir * run + offset;
            rb.rotation = item.transform.rotation = rot;
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            yield return null;
            if (!item || !rb)
            {
                Note("error", $"the blade went away while it was being placed ({(item ? item.name + ", no body" : "gone")})");
                yield break;
            }
            rb.interpolation = !interpolate ? RigidbodyInterpolation.None
                             : interpolation == RigidbodyInterpolation.None ? RigidbodyInterpolation.Interpolate : interpolation;
            // A blade in a firm grip does not spin when it meets a body; a free one did, and its tip swept edge first.
            RigidbodyConstraints constraints = rb.constraints;
            rb.constraints = RigidbodyConstraints.FreezeRotation;
            rb.isKinematic = false;
            Creature player = Player.local ? Player.local.creature : null;
            if (player) item.lastHandler = player.handRight;
            HashSet<Item> swept = Swept();
            swept?.Add(item);
            rb.velocity = dir * speed;   // already moving at full speed when it gets there, like a swing
            float duration = (stopInside ? run : 2f * run) / speed;
            float t0 = Time.time;
            while (Time.time - t0 < duration)
            {
                Drive(rb, dir * speed);
                rb.angularVelocity = Vector3.zero;
                rb.rotation = rot;
                yield return new WaitForFixedUpdate();
            }
            rb.velocity = Vector3.zero;
            rb.isKinematic = true;
            rb.constraints = constraints;
            swept?.Remove(item);
        }

        /// <summary>
        /// A stab: the item flies point first (its pierce zone's direction along dir) at target at speed, as the player's
        /// held blade (swept like one), and stops depth past it, left free: a blade that went in stays as the game holds it.
        /// </summary>
        private IEnumerator Thrust(Item item, Vector3 target, Vector3 dir, float speed, float depth)
        {
            Damager d = item.collisionHandlers.SelectMany(ch => ch.damagers).FirstOrDefault(x => x && x.type == Damager.Type.Pierce);
            if (!d) { Note("error", "no pierce damager on " + item.name); yield break; }
            dir.Normalize();
            Quaternion rot = Quaternion.FromToRotation(d.transform.forward, dir) * item.transform.rotation;
            item.transform.rotation = rot;
            Vector3 offset = item.transform.position - d.transform.position;
            const float run = 0.5f;
            Rigidbody rb = item.physicBody.rigidBody;
            rb.interpolation = RigidbodyInterpolation.None;
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.position = item.transform.position = target - dir * run + offset;
            rb.rotation = rot;
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            if (!item || !rb) yield break;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            Creature player = Player.local ? Player.local.creature : null;
            if (player) item.lastHandler = player.handRight;
            HashSet<Item> swept = Swept();
            swept?.Add(item);
            rb.isKinematic = false;
            rb.velocity = dir * speed;
            float t0 = Time.time, duration = (run + depth) / speed;
            while (Time.time - t0 < duration && item && rb)
            {
                Drive(rb, dir * speed);
                rb.angularVelocity = Vector3.zero;
                yield return new WaitForFixedUpdate();
            }
            swept?.Remove(item);
            if (rb)
            {
                rb.velocity = Vector3.zero;
                rb.useGravity = true;
            }
        }

        private const float MaxAccel = 100000f;   // m/s²: in effect the speed is set (400 and 2000 let a standing arm stop the blade; corpses blew up with those too)

        /// <summary>
        /// Pushes a blade towards a velocity with a hand's limited force: a body it cuts through can slow it down. Set every
        /// step instead, its speed was unstoppable (infinite force), and a fresh corpse it went through was launched as one
        /// at 1e8 m/s.
        /// </summary>
        private static void Drive(Rigidbody rb, Vector3 velocity)
        {
            Vector3 dv = velocity - rb.velocity;
            float max = MaxAccel * Time.fixedDeltaTime;
            if (dv.sqrMagnitude > max * max) dv = dv.normalized * max;
            rb.AddForce(dv, ForceMode.VelocityChange);
            rb.angularVelocity = Vector3.zero;
        }

        /// <summary>Moves a blade that is inside a body back and forth (along dir, 10 cm) at speed for a while.</summary>
        private IEnumerator Wiggle(Item item, Vector3 dir, float speed, float seconds)
        {
            Rigidbody rb = item.physicBody.rigidBody;
            Quaternion rot = rb.rotation;
            rb.isKinematic = false;
            HashSet<Item> swept = Swept();
            swept?.Add(item);
            float t0 = Time.time;
            int sign = 1;
            float leg = 0.1f / speed, legStart = Time.time;
            while (Time.time - t0 < seconds)
            {
                if (Time.time - legStart > leg) { sign = -sign; legStart = Time.time; }
                Drive(rb, dir.normalized * speed * sign);
                rb.angularVelocity = Vector3.zero;
                rb.rotation = rot;
                yield return new WaitForFixedUpdate();
            }
            rb.velocity = Vector3.zero;
            rb.isKinematic = true;
            swept?.Remove(item);
        }

        private static Damager SlashDamager(Item item) =>
            item.collisionHandlers.SelectMany(ch => ch.damagers).Where(d => d && d.type == Damager.Type.Slash)
                .OrderByDescending(d => d.penetrationLength).FirstOrDefault();

        private static Vector3 Target(Creature c, RagdollPart.Type type)
        {
            RagdollPart p = c.ragdoll.GetPart(type);
            if (type == RagdollPart.Type.Torso) p = c.ragdoll.GetPart(RagdollPart.Type.Torso, RagdollPart.Section.Upper) ?? p;
            return p ? p.transform.position : c.transform.position + Vector3.up * 1.2f;
        }

        private void Set(string field, object value)
        {
            FieldInfo f = ZType("ZandatsuScript")?.GetField(field, BindingFlags.Public | BindingFlags.Static);
            if (f == null) { Note("error", "no option " + field); return; }
            f.SetValue(null, Convert.ChangeType(value, f.FieldType));
            Note("option", field + " = " + value);
        }

        private static HashSet<Item> Swept() =>
            ZType("ZandatsuScript")?.GetField("SweptItems", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as HashSet<Item>;

        private static Type TorType(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(x => x != null);

        private void Call(Item item, string type, string method, params object[] args)
        {
            Type t = TorType(type);
            Component comp = t != null ? item.GetComponent(t) : null;
            MethodInfo m = t?.GetMethods().FirstOrDefault(x => x.Name == method && x.GetParameters().Length >= args.Length);
            if (!comp || m == null) { Note("error", $"no {type}.{method} on {item.name}"); return; }
            object[] full = m.GetParameters().Select((p, i) => i < args.Length ? args[i] : (p.HasDefaultValue ? p.DefaultValue : null)).ToArray();
            try { m.Invoke(comp, full); }
            catch (Exception e) { Note("error", $"{type}.{method}: {e.InnerException?.Message ?? e.Message}"); }
        }

        /// <summary>Where the player stood when the bench began (the stage), else where the player is.</summary>
        private static Vector3 PlayerPos() =>
            staged ? stagePos : Player.local && Player.local.creature ? Player.local.creature.transform.position : Vector3.zero;

        private static Vector3 PlayerFront(float distance)
        {
            if (staged) return stagePos + stageFwd * distance;
            Transform t = Player.local && Player.local.creature ? Player.local.creature.transform : null;
            Vector3 fwd = t ? Vector3.ProjectOnPlane(t.forward, Vector3.up).normalized : Vector3.forward;
            if (fwd.sqrMagnitude < 0.1f) fwd = Vector3.forward;
            return PlayerPos() + fwd * distance;
        }

        // ---------- measuring ----------

        private static Type ZType(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Zandatsu." + name)).FirstOrDefault(x => x != null);

        private List<(float t, LogType type, string msg)> ScenarioLog()
        {
            lock (log) return log.Skip(logMark).ToList();
        }

        /// <summary>Zandatsu cuts of this creature in the current scenario (one log line per finished cut).</summary>
        private int Cuts(Creature c)
        {
            // The game's logger prefixes the time and the caller: "... : [Zandatsu] hybrid cut HumanMale0 (...".
            if (!c) return ScenarioLog().Count(l => l.msg.Contains("[Zandatsu] cut ") || l.msg.Contains("[Zandatsu] hybrid cut "));
            string a = "[Zandatsu] cut " + c.name + " ", b = "[Zandatsu] hybrid cut " + c.name + " ";
            return ScenarioLog().Count(l => l.msg.Contains(a) || l.msg.Contains(b));
        }

        private bool LogHas(string text) => ScenarioLog().Any(l => l.msg.Contains(text));

        private string LastLines(string text, int n) =>
            string.Join(" || ", ScenarioLog().Where(l => l.msg.Contains(text)).Select(l => l.msg.Length > 160 ? l.msg.Substring(0, 160) : l.msg).Take(n));

        private static List<Component> Pieces()
        {
            var all = ZType("SlicedPiece")?.GetField("all", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IEnumerable;
            var list = new List<Component>();
            if (all != null) foreach (object o in all) if (o is Component p && p) list.Add(p);
            return list;
        }

        private static float Size(Component piece)
        {
            Measure(piece.GetComponentsInChildren<SkinnedMeshRenderer>(), out float size, out _, out _);
            return size;
        }

        private static Vector3 Center(Component piece)
        {
            Measure(piece.GetComponentsInChildren<SkinnedMeshRenderer>(), out _, out _, out Vector3 c);
            return c;
        }

        private IEnumerable<Renderer> CutRenderers()
        {
            foreach (Component p in Pieces()) foreach (Renderer r in p.GetComponentsInChildren<Renderer>()) yield return r;
            foreach (Creature c in Creature.allActive)
                if (c && !c.isPlayer)
                    foreach (Renderer r in c.GetComponentsInChildren<Renderer>()) yield return r;
        }

        private bool HasMaterial(string prefix) =>
            CutRenderers().Any(r => r && r.sharedMaterials.Any(m => m && m.name.StartsWith(prefix)));

        private string MaterialsSeen() =>
            string.Join(",", CutRenderers().Where(r => r).SelectMany(r => r.sharedMaterials).Where(m => m && m.name.StartsWith("Zandatsu")).Select(m => m.name).Distinct());

        /// <summary>
        /// Every piece and creature, measured on the vertices as drawn now (renderer bounds are padded on purpose): skin
        /// edges are centimetres and cut faces a few tens; a stretched mesh has edges metres long.
        /// </summary>
        private void ExpectNotStretched()
        {
            float worst = 0f;
            // Some outfits have long triangles of their own (straps, cloth): measured on the enemy before the cut.
            float limit = Mathf.Max(0.7f, baseline + 0.15f);   // a cut face across the torso has edges up to ~0.55 m; the old stretch bug made 0.86+
            var sb = new StringBuilder($" limit {limit:0.00} (uncut enemy {baseline:0.00})");
            foreach (Component p in Pieces())
            {
                Measure(p.GetComponentsInChildren<SkinnedMeshRenderer>(), out float size, out float edge, out _, out string where);
                worst = Mathf.Max(worst, edge);
                sb.Append($" piece {size:0.00}/{edge:0.00}{(edge > limit ? " in " + where : "")}");
            }
            foreach (Creature c in Creature.allActive)
            {
                if (!c || c.isPlayer) continue;
                Measure(c.GetComponentsInChildren<SkinnedMeshRenderer>(), out float size, out float edge, out _, out string where);
                worst = Mathf.Max(worst, edge);
                sb.Append($" {c.name} {size:0.00}/{edge:0.00}{(edge > limit ? " in " + where : "")}");
            }
            Expect("nothing stretched", worst <= limit, $"longest edge {worst:0.00} m (size/edge:{sb})");
            foreach (Creature c in Creature.allActive)
                if (c && !c.isPlayer) ExpectFinite(c);

            // The invisible hull burning and shock effects are drawn on: it must follow the corpse too.
            if (hullBaseline <= 0f) return;
            float hullLimit = Mathf.Max(0.7f, hullBaseline + 0.15f), hullWorst = 0f;
            var hb = new StringBuilder($" limit {hullLimit:0.00} (uncut {hullBaseline:0.00})");
            foreach (Creature c in Creature.allActive)
            {
                if (!c || c.isPlayer) continue;
                Measure(c.GetComponentsInChildren<SkinnedMeshRenderer>(), out float size, out float edge, out _, out string where, true);
                hullWorst = Mathf.Max(hullWorst, edge);
                hb.Append($" {c.name} {size:0.00}/{edge:0.00}{(edge > hullLimit ? " in " + where : "")}");
            }
            // Known, not fixed: trimming the hull after a cut made corpses blow up now and then (2026-10-20), so it stays
            // whole and stretched; reported, not failed.
            if (hullWorst <= hullLimit) Note("PASS", $"effect hull not stretched: longest edge {hullWorst:0.00} m");
            else Note("known issue", $"effect hull stretched (not fixed on purpose): longest edge {hullWorst:0.00} m ({hb})");
        }

        private static bool Bad(Vector3 v) => float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z);

        private static bool Bad(Matrix4x4 m)
        {
            for (int i = 0; i < 16; i++) if (float.IsNaN(m[i]) || float.IsInfinity(m[i])) return true;
            return false;
        }

        /// <summary>
        /// NaN or infinite numbers on a creature: which parts (positions, rigidbody velocities), which renderer bones and
        /// which bind poses or vertices of its meshes. A mesh drawn from NaN is invisible or garbage.
        /// </summary>
        private void ExpectFinite(Creature c)
        {
            List<string> bad = Integrity(c);
            // What the enemy had before any cut is the game's own (some outfit meshes have more bones than bind poses).
            List<string> before = uncutIssues.TryGetValue(c, out List<string> b) ? b : new List<string>();
            List<string> added = bad.Except(before).ToList();
            Expect($"{c.name}: no NaN or broken skinning", added.Count == 0,
                   string.Join("; ", added.Distinct().Take(12)) + (before.Count > 0 ? $" (already before the cut: {string.Join("; ", before.Take(4))})" : ""));
        }

        private readonly Dictionary<Creature, List<string>> uncutIssues = new Dictionary<Creature, List<string>>();

        private static List<string> Integrity(Creature c)
        {
            var bad = new List<string>();
            foreach (RagdollPart p in c.ragdoll.parts)
            {
                if (!p) continue;
                Rigidbody rb = p.physicBody != null ? p.physicBody.rigidBody : null;
                if (Bad(p.transform.position)) bad.Add($"part {p.name} position");
                if (rb && (Bad(rb.velocity) || Bad(rb.angularVelocity))) bad.Add($"part {p.name} velocity");
                Vector3 home = c.ragdoll.rootPart ? c.ragdoll.rootPart.transform.position : c.transform.position;
                if (!Bad(p.transform.position) && (p.transform.position - home).magnitude > 50f) bad.Add($"part {p.name} flew {(p.transform.position - home).magnitude:0} m away");
                if (rb && !Bad(rb.velocity) && rb.velocity.magnitude > 100f) bad.Add($"part {p.name} at {rb.velocity.magnitude:0} m/s");
            }
            foreach (SkinnedMeshRenderer r in c.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (!r || !r.enabled || !r.sharedMesh || r.forceRenderingOff) continue;
                Transform[] bones = r.bones;
                for (int i = 0; i < bones.Length; i++)
                    if (bones[i] && (Bad(bones[i].position) || Bad(bones[i].lossyScale))) bad.Add($"{r.name} bone {i} {bones[i].name}");
                if (r.rootBone && Bad(r.rootBone.position)) bad.Add($"{r.name} root bone {r.rootBone.name}");
                Mesh m = r.sharedMesh;
                Matrix4x4[] bind = m.bindposes;
                for (int i = 0; i < bind.Length; i++) if (Bad(bind[i])) bad.Add($"{r.name} bindpose {i} NaN");
                if (m.isReadable && m.vertices.Any(Bad)) bad.Add($"{r.name} vertices NaN");
                if (bones.Length != bind.Length) bad.Add($"{r.name} {bones.Length} bones vs {bind.Length} bind poses");
            }
            return bad.Distinct().ToList();
        }

        private static bool IsVfx(SkinnedMeshRenderer r) =>
            r.name.IndexOf("vfx", StringComparison.OrdinalIgnoreCase) >= 0 ||
            r.sharedMaterials.Any(m => m && m.shader && m.shader.name.IndexOf("/VFX/", StringComparison.OrdinalIgnoreCase) >= 0);

        private static void Measure(SkinnedMeshRenderer[] renderers, out float size, out float edge, out Vector3 center) =>
            Measure(renderers, out size, out edge, out center, out _);

        /// <param name="where">The renderer and submesh material holding the longest edge, and its two ends.</param>
        private static void Measure(SkinnedMeshRenderer[] renderers, out float size, out float edge, out Vector3 center, out string where, bool vfxOnly = false)
        {
            size = edge = 0f;
            center = Vector3.zero;
            where = "";
            var baked = new Mesh();
            var vs = new List<Vector3>();
            var tris = new List<int>();
            bool any = false;
            Bounds b = default;
            foreach (SkinnedMeshRenderer r in renderers)
            {
                if (!r || !r.enabled || !r.gameObject.activeInHierarchy || !r.sharedMesh || r.forceRenderingOff) continue;
                if (IsVfx(r) != vfxOnly) continue;   // the invisible hull the game draws burning/shock effects on: measured on its own
                if (r.name.IndexOf("LOD", StringComparison.OrdinalIgnoreCase) >= 0 && r.name.IndexOf("LOD0", StringComparison.OrdinalIgnoreCase) < 0) continue;
                r.BakeMesh(baked, true);
                baked.GetVertices(vs);
                Matrix4x4 m = r.transform.localToWorldMatrix;
                for (int i = 0; i < vs.Count; i++)
                {
                    vs[i] = m.MultiplyPoint3x4(vs[i]);
                    if (!any) { b = new Bounds(vs[i], Vector3.zero); any = true; }
                    else b.Encapsulate(vs[i]);
                }
                for (int s = 0; s < baked.subMeshCount; s++)
                {
                    baked.GetTriangles(tris, s);
                    for (int k = 0; k + 2 < tris.Count; k += 3)
                    {
                        Vector3 p0 = vs[tris[k]], p1 = vs[tris[k + 1]], p2 = vs[tris[k + 2]];
                        float e = Mathf.Max((p0 - p1).magnitude, (p1 - p2).magnitude, (p2 - p0).magnitude);
                        if (e <= edge) continue;
                        edge = e;
                        Material mat = s < r.sharedMaterials.Length ? r.sharedMaterials[s] : null;
                        where = $"{r.name}/{(mat ? mat.name : "-")} {p0}-{p1}-{p2}";
                    }
                }
            }
            UnityEngine.Object.Destroy(baked);
            if (any) { size = b.size.magnitude; center = b.center; }
        }

        // ---------- scenario frame ----------

        private void Begin(string name)
        {
            current = name;
            HidePlayer();   // again: the game may show it back
            failed = false;
            frames.Clear();
            lock (log) logMark = log.Count;
            dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            shot = 0;
            baseline = hullBaseline = 0f;
            reusedNow = false;
            frameIds.Clear();
            foreach (var kv in defaults) kv.Key.SetValue(null, kv.Value);
            Note("begin", name);
        }

        private void Expect(string what, bool ok, string detail)
        {
            if (!ok) failed = true;
            Note(ok ? "PASS" : "FAIL", $"{what}: {detail}");
        }

        private void End()
        {
            var lines = ScenarioLog();
            var errors = lines.Where(l => (l.type == LogType.Exception || l.type == LogType.Error) &&
                                          l.msg.Contains("Zandatsu"))   // the game's own errors (its lore journal) are not ours
                              .Select(l => l.msg.Length > 220 ? l.msg.Substring(0, 220) : l.msg).Distinct().ToList();
            if (errors.Count > 0) Expect("no errors", false, string.Join(" || ", errors.Take(4)));
            float max = frames.DefaultIfEmpty(0f).Max() * 1000f;
            int slow = frames.Count(f => f > 0.05f);
            float avg = frames.Count > 0 ? frames.Average() * 1000f : 0f;
            Note("frames", $"{frames.Count} frames, avg {avg:0.0} ms, worst {max:0} ms, {slow} over 50 ms");
            // The slowest frames and what Zandatsu logged in them. A frame's time is read at the start of the next one,
            // so the work lies in the frames just before.
            foreach (int i in Enumerable.Range(0, frames.Count).Where(i => frames[i] > 0.04f).OrderByDescending(i => frames[i]).Take(3))
            {
                int f = frameIds[i];
                List<string> during;
                lock (log)
                    during = Enumerable.Range(logMark, log.Count - logMark)
                        .Where(j => logFrames[j] >= f - 2 && logFrames[j] < f && log[j].msg.Contains("[Zandatsu]"))
                        .Select(j => log[j].msg.Substring(Math.Max(0, log[j].msg.IndexOf("[Zandatsu]")))).Select(m => m.Length > 140 ? m.Substring(0, 140) : m)
                        .ToList();
                bool shotThen = shotFrames.Contains(f - 1) || shotFrames.Contains(f - 2);
                Note("slow frame", $"{frames[i] * 1000f:0} ms at frame {f}: " +
                                   (shotThen ? "the bench's own screenshot; " : "") +
                                   (during.Count > 0 ? string.Join(" || ", during) : "no Zandatsu work logged"));
            }
            summary.Add($"{(failed ? "FAIL" : "PASS")} {current} (worst frame {max:0} ms{(reusedNow ? ", enemy reused from " + reusedFrom : "")})");
            current = null;
        }

        private IEnumerator Cleanup()
        {
            foreach (Component p in Pieces())
            {
                MethodInfo kill = p.GetType().GetMethod("Kill", BindingFlags.Public | BindingFlags.Instance);
                try { kill?.Invoke(p, null); } catch { }
            }
            foreach (GameObject go in spawned)
            {
                if (!go) continue;
                Creature c = go.GetComponent<Creature>();
                Item i = go.GetComponent<Item>();
                try
                {
                    if (c) c.Despawn(0f);
                    else if (i) i.Despawn(0f);
                }
                catch { }
            }
            spawned.Clear();
            foreach (Creature c in Creature.allActive.ToArray())
                if (c && !c.isPlayer) try { c.Despawn(0f); } catch { }
            // Cut-off halves too: lying at the spot they took the next scenario's swing (cut again) before its object.
            ClearDebris();
            Swept()?.Clear();
            yield return Wait(2f);
        }

        /// <summary>The level's own loose items around the stage (the arena's maces by its start) away, all but the player's.</summary>
        private static void ClearStage()
        {
            foreach (Item i in Item.allActive.ToArray())
            {
                if (!i || i.holder || i.mainHandler || (i.transform.position - stagePos).magnitude > 6f) continue;
                try { i.Despawn(0f); } catch { }
            }
        }

        /// <summary>
        /// Runs a scenario with its nested steps (Swing, Wait...) itself, so an exception at any depth ends the scenario as
        /// a failure: a nested coroutine Unity ran died alone and the scenario waited for it forever (the game sat idle).
        /// </summary>
        private IEnumerator Guard(IEnumerator inner)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(inner);
            while (stack.Count > 0)
            {
                object cur;
                try
                {
                    IEnumerator top = stack.Peek();
                    if (!top.MoveNext()) { stack.Pop(); continue; }
                    cur = top.Current;
                }
                catch (Exception e)
                {
                    Expect("scenario ran", false, e.ToString());
                    yield break;
                }
                if (cur is IEnumerator nested) { stack.Push(nested); continue; }
                yield return cur;
            }
        }

        // ---------- screenshots and output ----------

        /// <summary>Looks at the creature's drawn body (it may lie anywhere after the cut), from the player's side.</summary>
        private IEnumerator Shot(string name, Creature c)
        {
            Vector3 target = PlayerFront(2.5f) + Vector3.up;
            if (c)
            {
                Measure(c.GetComponentsInChildren<SkinnedMeshRenderer>(), out float size, out _, out Vector3 center);
                if (size > 0f) target = center;
            }
            var view = new GameObject("ZandatsuLabView").transform;
            Vector3 toPlayer = Vector3.ProjectOnPlane(PlayerPos() - target, Vector3.up).normalized;
            if (toPlayer.sqrMagnitude < 0.1f) toPlayer = Vector3.back;
            view.rotation = Quaternion.LookRotation(toPlayer);
            yield return ShotAt(name, target, view);
            Destroy(view.gameObject);
        }

        private IEnumerator ShotAt(string name, Vector3 target, Transform facing = null)
        {
            yield return new WaitForEndOfFrame();
            shotFrames.Add(Time.frameCount);
            var go = new GameObject("ZandatsuLabCamera");
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = 60f;
            cam.nearClipPlane = 0.05f;
            var rt = new RenderTexture(960, 540, 24);
            cam.targetTexture = rt;
            try
            {
                Vector3 side = facing ? facing.right : Vector3.right;
                Vector3 front = facing ? facing.forward : Vector3.forward;
                foreach (var view in new[] { ("side", side * 3.2f + Vector3.up * 0.5f), ("front", front * 3.2f + Vector3.up * 0.7f) })
                {
                    go.transform.position = target + view.Item2;
                    go.transform.LookAt(target);
                    cam.Render();
                    RenderTexture.active = rt;
                    var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    tex.Apply();
                    File.WriteAllBytes(Path.Combine(dir, $"{++shot:00}_{name}_{view.Item1}.png"), tex.EncodeToPNG());
                    Destroy(tex);
                }
            }
            catch (Exception e) { Note("warning", "screenshot: " + e.Message); }
            finally
            {
                RenderTexture.active = null;
                cam.targetTexture = null;
                Destroy(rt);
                Destroy(go);
            }
        }

        /// <summary>A close look at a point from above and the side (a piece lying on the floor).</summary>
        private IEnumerator ShotNear(string name, Vector3 target, float distance)
        {
            yield return new WaitForEndOfFrame();
            shotFrames.Add(Time.frameCount);
            var go = new GameObject("ZandatsuLabCamera");
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = 50f;
            cam.nearClipPlane = 0.02f;
            var rt = new RenderTexture(960, 540, 24);
            cam.targetTexture = rt;
            try
            {
                Vector3 side = Vector3.Cross(Vector3.up, stageFwd.sqrMagnitude > 0.1f ? stageFwd : Vector3.forward);
                go.transform.position = target + (Vector3.up * 0.8f + side * 0.6f).normalized * distance;
                go.transform.LookAt(target);
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(Path.Combine(dir, $"{++shot:00}_{name}_near.png"), tex.EncodeToPNG());
                Destroy(tex);
            }
            catch (Exception e) { Note("warning", "screenshot: " + e.Message); }
            finally
            {
                RenderTexture.active = null;
                cam.targetTexture = null;
                Destroy(rt);
                Destroy(go);
            }
        }

        /// <summary>A close look straight at an object's cut face (its kept half).</summary>
        private IEnumerator ShotCap(string name, Item item)
        {
            MeshRenderer cap = item ? item.GetComponentsInChildren<MeshRenderer>().FirstOrDefault(r => r.name == "Zandatsu_WeaponCut") : null;
            Mesh mesh = cap ? cap.GetComponent<MeshFilter>().sharedMesh : null;
            if (!mesh || !mesh.isReadable || mesh.normals.Length == 0) yield break;
            Vector3 n = Vector3.zero;
            foreach (Vector3 v in mesh.normals) n += v;
            n = cap.transform.TransformDirection(n).normalized;
            Bounds b = cap.bounds;
            if (n.sqrMagnitude < 0.5f) yield break;
            yield return new WaitForEndOfFrame();
            shotFrames.Add(Time.frameCount);
            var go = new GameObject("ZandatsuLabCamera");
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = 50f;
            cam.nearClipPlane = 0.02f;
            var rt = new RenderTexture(960, 540, 24);
            cam.targetTexture = rt;
            try
            {
                go.transform.position = b.center + n * Mathf.Max(0.3f, b.size.magnitude * 1.1f) + Vector3.up * 0.02f;
                go.transform.LookAt(b.center);
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(Path.Combine(dir, $"{++shot:00}_{name}_cutface.png"), tex.EncodeToPNG());
                Destroy(tex);
            }
            catch (Exception e) { Note("warning", "screenshot: " + e.Message); }
            finally
            {
                RenderTexture.active = null;
                cam.targetTexture = null;
                Destroy(rt);
                Destroy(go);
            }
        }

        private static IEnumerator Wait(float seconds)
        {
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < seconds) yield return null;
        }

        private void Note(string key, string value)
        {
            Debug.Log($"[ZandatsuLab] {(current != null ? current + " " : "")}{key}: {value}");
            entries.Add($"  {{\"t\": {Time.realtimeSinceStartup:0.00}, \"scenario\": \"{Esc(current)}\", \"{Esc(key)}\": \"{Esc(value)}\"}}");
        }

        private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");

        private void Finish()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            File.WriteAllText(Path.Combine(root, "results.json"), "[\n" + string.Join(",\n", entries) + "\n]\n");
            File.WriteAllText(Path.Combine(root, "summary.txt"), string.Join("\n", summary) + "\n");
            File.WriteAllText(Path.Combine(Application.persistentDataPath, "ZandatsuLab", "last.txt"), root);
            Debug.Log("[ZandatsuLab] summary:\n" + string.Join("\n", summary));
            Debug.Log("[ZandatsuLab] done: " + root);
            Application.Quit();
        }
    }
}
