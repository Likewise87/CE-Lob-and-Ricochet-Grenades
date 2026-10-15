using HarmonyLib;
using Verse;
using UnityEngine;
using System.Collections.Generic;
using System.Reflection;
using CombatExtended;

namespace CE_LaR_Grenades
{
    /// <summary>
    /// Shared height→speed curve for custom-angle lobs (30/50/80).
    /// Also hardens roof hits: CE skips CheckCellForCollision entirely when
    /// ExactPosition.y &gt; HighestCoverAt (walls ~2), so high lobs never reach
    /// TryCollideWithRoof. We force the roof test for registered lobs.
    /// </summary>
    public static class LobFlightFx
    {
        /// <summary>
        /// Height→playback rate. All lobs start ~1.0 (CE ShotHeight); 30° lives here,
        /// 50/80 climb higher. Keep the low band hot so 30° doesn't feel sluggish.
        /// </summary>
        private static readonly Vector2[] HeightRateCurve =
        {
            new Vector2(1.00f, 1.40f),
            new Vector2(1.35f, 1.52f),
            new Vector2(1.70f, 1.65f),
            new Vector2(2.00f, 1.75f),
            new Vector2(2.35f, 1.85f),
            new Vector2(2.70f, 1.95f),
            new Vector2(3.10f, 2.05f),
            new Vector2(3.50f, 2.15f),
        };

        private const float MinWarpHeight = 1.00f;
        private const int MaxExtraStepsPerTick = 1;

        private static readonly HashSet<int> LobProjectileIds = new HashSet<int>();
        private static readonly Dictionary<int, float> ExtraTickCarry = new Dictionary<int, float>(64);

        private static MethodInfo? tryCollideWithRoof;
        private static MethodInfo? checkForCollisionBetween;
        private static FieldInfo? ignoreRoofField;

        private static bool advancingExtra;

        public static void RegisterLob(ProjectileCE projectile)
        {
            if (projectile == null) return;
            LobProjectileIds.Add(projectile.thingIDNumber);
        }

        public static void Unregister(ProjectileCE projectile)
        {
            if (projectile == null) return;
            int id = projectile.thingIDNumber;
            LobProjectileIds.Remove(id);
            ExtraTickCarry.Remove(id);
        }

        public static bool IsRegistered(ProjectileCE p) =>
            p != null && LobProjectileIds.Contains(p.thingIDNumber);

        public static void Apply(Harmony h)
        {
            tryCollideWithRoof = AccessTools.Method(typeof(ProjectileCE), "TryCollideWithRoof", new[] { typeof(IntVec3) });
            checkForCollisionBetween = AccessTools.Method(typeof(ProjectileCE), "CheckForCollisionBetween");
            ignoreRoofField = AccessTools.Field(typeof(ProjectileCE), "ignoreRoof");

            var mTick = AccessTools.Method(typeof(ProjectileCE), nameof(ProjectileCE.Tick));
            if (mTick != null)
                h.Patch(mTick, postfix: new HarmonyMethod(typeof(LobFlightFx), nameof(Tick_Postfix)));

            var mCell = AccessTools.Method(typeof(ProjectileCE), "CheckCellForCollision", new[] { typeof(IntVec3) });
            if (mCell != null)
                h.Patch(mCell, postfix: new HarmonyMethod(typeof(LobFlightFx), nameof(CheckCellForCollision_Postfix)));
            else
                Log.Warning("[CE_LaR_Grenades] LobFlightFx: missing CheckCellForCollision");

            var mDestroy = AccessTools.Method(typeof(Thing), nameof(Thing.Destroy), new[] { typeof(DestroyMode) });
            if (mDestroy != null)
                h.Patch(mDestroy, prefix: new HarmonyMethod(typeof(LobFlightFx), nameof(Destroy_Prefix)));
        }

        private static bool IsLob(ProjectileCE p) => IsRegistered(p);

        /// <summary>Public accessor for roof-drop / other systems that need lob tracking.</summary>
        public static bool IsLobTracked(ProjectileCE p) => IsRegistered(p);

        public static float RateAtHeight(float height)
        {
            var curve = HeightRateCurve;
            if (curve.Length == 0) return 1f;
            if (height <= curve[0].x) return curve[0].y;

            for (int i = 1; i < curve.Length; i++)
            {
                if (height <= curve[i].x)
                {
                    float t = Mathf.InverseLerp(curve[i - 1].x, curve[i].x, height);
                    return Mathf.Lerp(curve[i - 1].y, curve[i].y, t);
                }
            }

            return curve[curve.Length - 1].y;
        }

        /// <summary>
        /// CE returns early when height &gt; cover (walls), never testing roofs.
        /// Force TryCollideWithRoof for our high lobs.
        /// </summary>
        public static void CheckCellForCollision_Postfix(ProjectileCE __instance, IntVec3 cell, ref bool __result)
        {
            if (__result) return;
            if (!IsLob(__instance) || __instance.landed) return;
            var map = __instance.Map;
            if (map == null || !cell.InBounds(map)) return;
            if (!cell.Roofed(map)) return;
            if (IsIgnoringRoof(__instance)) return;

            if (TryCollideRoof(__instance, cell))
            {
                CE_LaR_GrenadesLog.L(
                    $"[CE_LaR_Grenades] Lob: roof hit at {cell} y={__instance.ExactPosition.y:F2} " +
                    $"(CE skipped cell — height above cover)");
                __result = true;
            }
        }

        public static void Tick_Postfix(ProjectileCE __instance)
        {
            if (advancingExtra) return;
            if (!IsLob(__instance) || __instance.landed) return;
            // Ricochet banks must never get apex tick-warp (reads as accelerating).
            if (GrenadeRicochet.IsTracked(__instance.thingIDNumber)) return;
            if (__instance.Map == null) return;

            float height = __instance.ExactPosition.y;
            if (height < MinWarpHeight)
                return;

            float rate = RateAtHeight(height);
            float extra = rate - 1f;
            if (extra <= 0.01f) return;

            int id = __instance.thingIDNumber;
            if (!ExtraTickCarry.TryGetValue(id, out float carry))
                carry = 0f;

            carry += extra;
            int steps = (int)carry;
            if (steps > MaxExtraStepsPerTick)
                steps = MaxExtraStepsPerTick;
            carry -= steps;
            ExtraTickCarry[id] = carry;
            if (steps <= 0) return;

            advancingExtra = true;
            try
            {
                for (int i = 0; i < steps; i++)
                {
                    if (__instance.landed || __instance.ticksToImpact <= 0)
                        break;
                    if (!TryAdvanceOneStep(__instance))
                        break;
                }
            }
            finally
            {
                advancingExtra = false;
            }
        }

        private static bool TryAdvanceOneStep(ProjectileCE p)
        {
            p.LastPos = p.ExactPosition;
            p.ticksToImpact--;
            p.FlightTicks++;

            Vector3 next = p.TrajectoryWorker.MoveForward(p);
            if (p.Map == null || !next.InBounds(p.Map))
                return false;

            p.ExactPosition = next;
            p.Position = next.ToIntVec3();

            // Extra steps used to skip collision entirely (including roofs). Run CE's check;
            // our CheckCellForCollision postfix supplies the missing roof test.
            if (checkForCollisionBetween != null)
            {
                try
                {
                    if ((bool)checkForCollisionBetween.Invoke(p, null))
                        return false;
                }
                catch
                {
                    // fall through
                }
            }

            if (p.ExactPosition.y <= 0f || p.ticksToImpact <= 0)
            {
                p.ImpactSomething();
                return false;
            }
            return true;
        }

        private static bool IsIgnoringRoof(ProjectileCE p)
        {
            if (ignoreRoofField == null) return false;
            try { return (bool)ignoreRoofField.GetValue(p); }
            catch { return false; }
        }

        private static bool TryCollideRoof(ProjectileCE p, IntVec3 cell)
        {
            if (tryCollideWithRoof == null) return false;
            try { return (bool)tryCollideWithRoof.Invoke(p, new object[] { cell }); }
            catch { return false; }
        }

        public static void Destroy_Prefix(Thing __instance)
        {
            if (__instance is ProjectileCE)
            {
                int id = __instance.thingIDNumber;
                LobProjectileIds.Remove(id);
                ExtraTickCarry.Remove(id);
            }
        }
    }
}
