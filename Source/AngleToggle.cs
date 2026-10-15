using HarmonyLib;
using RimWorld;
using Verse;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace CE_LaR_Grenades
{
    /// <summary>
    /// Game-wide storage for per-pawn grenade throw mode.
    /// Survives map changes, vehicles, caravans, and save/load.
    /// </summary>
    public class GameComponent_GrenadeThrowMode : GameComponent
    {
        public Dictionary<int, int> pawnAngleDeg = new Dictionary<int, int>();

		// RimWorld instantiates via Activator.CreateInstance(type, game).
		public GameComponent_GrenadeThrowMode(Game game) { }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref pawnAngleDeg, "pawnAngleDeg", LookMode.Value, LookMode.Value);
            if (pawnAngleDeg == null)
                pawnAngleDeg = new Dictionary<int, int>();

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                AngleToggle.MigrateLegacyRicochetAngles(pawnAngleDeg);
                AngleToggle.HydrateSessionFrom(pawnAngleDeg);
            }
        }

		public static GameComponent_GrenadeThrowMode? Get()
		{
			return Current.Game?.GetComponent<GameComponent_GrenadeThrowMode>();
		}
    }

    /// <summary>
    /// Per-pawn throw mode gizmo: CE Normal / 30° / 50° / 80° / Ricochet (~2°).
    /// Modes are stored on <see cref="GameComponent_GrenadeThrowMode"/> (game-wide).
    /// This MapComponent remains only to migrate older per-map saves into the game component.
    /// </summary>
    public class AngleToggle : MapComponent
    {
        public const int ModeCeNormal = 0;
        /// <summary>Ricochet mode: low flat toss (also the launch angle in degrees).</summary>
        public const int ModeRicochet = 2;
        /// <summary>Pre-2° sentinel still present in some saves.</summary>
        private const int LegacyRicochetDeg = 8;

        public AngleToggle(Map map) : base(map) { }

        // Legacy per-map dict — only read during save migration.
        private Dictionary<int, int> pawnAngleDeg = new Dictionary<int, int>();

        // Session backup so Launch/CanHit still see the mode if GameComponent is briefly unavailable.
        private static readonly Dictionary<int, int> SessionAngles = new Dictionary<int, int>(128);

        // 0 = CE Normal; ModeRicochet = flat bank-shot; otherwise fixed lob degrees.
        private static readonly int[] States = new[] { ModeCeNormal, 30, 50, 80, ModeRicochet };

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref pawnAngleDeg, "pawnAngleDeg", LookMode.Value, LookMode.Value);
            if (pawnAngleDeg == null)
                pawnAngleDeg = new Dictionary<int, int>();

            if (Scribe.mode == LoadSaveMode.PostLoadInit && pawnAngleDeg.Count > 0)
            {
                MigrateLegacyRicochetAngles(pawnAngleDeg);
                MergeIntoGameComponent(pawnAngleDeg);
                pawnAngleDeg.Clear();
            }
        }

        private static void MergeIntoGameComponent(Dictionary<int, int> fromMap)
        {
            var gameComp = GameComponent_GrenadeThrowMode.Get();
            if (gameComp == null) return;

            foreach (var kv in fromMap)
            {
                // Prefer an existing game-wide value; only fill gaps from legacy map data.
                if (!gameComp.pawnAngleDeg.ContainsKey(kv.Key))
                    gameComp.pawnAngleDeg[kv.Key] = kv.Value;
            }

            HydrateSessionFrom(gameComp.pawnAngleDeg);
        }

        internal static void HydrateSessionFrom(Dictionary<int, int> dict)
        {
            foreach (var kv in dict)
                SessionAngles[kv.Key] = NormalizeMode(kv.Value);
        }

        internal static void MigrateLegacyRicochetAngles(Dictionary<int, int> dict)
        {
            var keys = new List<int>(dict.Keys);
            foreach (int key in keys)
            {
                if (dict[key] == LegacyRicochetDeg)
                    dict[key] = ModeRicochet;
            }
        }

        private static int NormalizeMode(int deg)
        {
            if (deg == LegacyRicochetDeg) return ModeRicochet;
            return deg;
        }

		private static Dictionary<int, int>? Store()
		{
			return GameComponent_GrenadeThrowMode.Get()?.pawnAngleDeg;
		}

        public static int GetAngleDegFor(Pawn pawn)
        {
            if (pawn == null) return ModeCeNormal;
            var id = pawn.thingIDNumber;

            var store = Store();
            if (store != null)
            {
                if (!store.TryGetValue(id, out var deg))
                {
                    deg = ModeCeNormal;
                    store[id] = deg;
                }
                deg = NormalizeMode(deg);
                if (store[id] != deg)
                    store[id] = deg;
                SessionAngles[id] = deg;
                return deg;
            }

            if (SessionAngles.TryGetValue(id, out var cached))
            {
                cached = NormalizeMode(cached);
                SessionAngles[id] = cached;
                return cached;
            }
            return ModeCeNormal;
        }

        public static bool IsCustomAngleMode(Pawn pawn)
        {
            return pawn != null && GetAngleDegFor(pawn) != ModeCeNormal;
        }

        public static bool IsRicochetMode(Pawn pawn)
        {
            return pawn != null && GetAngleDegFor(pawn) == ModeRicochet;
        }

        /// <summary>
        /// Per-angle launch scale (v·k, g·k²). 30° lives mostly under wall-height, so it
        /// needs a real k — apex tick-warp alone never catches up to 50°/80°.
        /// </summary>
        public static float GetFlightScale(int angleDeg) => GetFlightScaleForDegrees(angleDeg);

        public static float GetFlightScaleForDegrees(float angleDeg)
        {
            const float kFlat = 1.0f; // ricochet / low arcs — already fast
            const float k30 = 1.22f;
            const float k50 = 1.32f;
            const float k80 = 1.48f;

            if (angleDeg <= 2.5f) return kFlat; // ricochet ~2°
            if (angleDeg <= 30f)
                return Mathf.Lerp(kFlat, k30, Mathf.Clamp01(angleDeg / 30f));
            if (angleDeg >= 80f) return k80;
            if (angleDeg <= 50f)
                return Mathf.Lerp(k30, k50, (angleDeg - 30f) / 20f);
            return Mathf.Lerp(k50, k80, (angleDeg - 50f) / 30f);
        }

        private static string ModeLabel(int deg)
        {
            if (deg == ModeCeNormal) return "Throw: CE";
            if (deg == ModeRicochet) return "Throw: Ricochet";
            return $"Throw: {deg}°";
        }

        private static void SetNextAngle(Pawn pawn)
        {
            if (pawn == null) return;

            var id = pawn.thingIDNumber;
            var current = GetAngleDegFor(pawn);
            var idx = System.Array.IndexOf(States, current);
            if (idx < 0) idx = 0;
            var next = States[(idx + 1) % States.Length];

            SessionAngles[id] = next;
            var store = Store();
            if (store != null)
                store[id] = next;

            string msg = next == ModeCeNormal
                ? "Grenade throw set to CE Normal"
                : next == ModeRicochet
                    ? "Grenade throw set to Ricochet (low bank shot)"
                    : $"Throw angle set to {next}°";
            Messages.Message(msg, new LookTargets(pawn), MessageTypeDefOf.TaskCompletion, false);
        }

        public static bool PawnHasGrenadeLike(Pawn pawn)
        {
            if (pawn == null) return false;

            var inv = pawn.inventory?.innerContainer;
            if (inv != null)
            {
                foreach (var t in inv)
                {
                    var dn = t?.def?.defName;
                    if (!string.IsNullOrEmpty(dn) && dn.IndexOf("grenade", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }

            var eq = pawn.equipment?.Primary;
            if (eq != null)
            {
                var dn = eq.def?.defName;
                if (!string.IsNullOrEmpty(dn) && dn.IndexOf("grenade", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        public static void Apply(Harmony h)
        {
            var m = AccessTools.Method(typeof(Pawn), nameof(Pawn.GetGizmos));
            if (m != null)
                h.Patch(m, postfix: new HarmonyMethod(typeof(AngleToggle), nameof(GetGizmos_Postfix)));
        }

        public static void GetGizmos_Postfix(Pawn __instance, ref IEnumerable<Gizmo> __result)
        {
            if (!__instance.IsColonistPlayerControlled) return;
            if (!PawnHasGrenadeLike(__instance)) return;

            var list = __result != null ? __result.ToList() : new List<Gizmo>();

            var deg = GetAngleDegFor(__instance);

            var cmd = new Command_Action
            {
                defaultLabel = ModeLabel(deg),
                defaultDesc = "Toggle grenade throw: CE Normal, 30° / 50° / 80° lobs (arc preview), or Ricochet (~2° bank shot, up to 3 bounces).",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/GrenadeAngle", true),
                action = () => SetNextAngle(__instance),
                groupKey = 987654321
            };

            list.Add(cmd);
            __result = list;
        }
    }
}
