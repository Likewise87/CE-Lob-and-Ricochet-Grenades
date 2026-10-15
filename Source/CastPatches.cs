using HarmonyLib;
using Verse;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;

namespace CE_LaR_Grenades
{
    /// <summary>
    /// Custom throw-angle modes (30/50/80):
    /// - CanHitTargetFrom / line finder: allow in-range aim (incl. behind walls); preserve lean when CE succeeds.
    /// - Launch: fixed angle + inverse-ballistics speed; flight-time scale via g·k².
    /// CE Normal (angle 0): all hooks no-op.
    /// </summary>
    public static class CastPatches
    {
        private const float MinDenomEpsilon = 1e-4f;
        private const float MinSpeedFloor = 0.05f;
        private const float MaxSpeedClamp = 80.0f;

        // CE draws projectiles by offsetting screen Y with ExactPosition.y * 0.84 (stock ExactPosToDrawPos).
        // A true steep lob to long range peaks very high → sprite sits far above its shadow and "looks tiny"
        // even though drawSize is unchanged. Cap peak height so stock CE drawing stays sane.
        // Flat-ground approximation: peak ≈ (R/4) * tan(θ).
        private const float MaxLobPeakHeight = 3.5f;

        private struct AimInfo
        {
            public IntVec3 Root;
            public IntVec3 TargetCell;
            public Vector3 TargetPos;
            public int Tick;
            public bool ForcedLine;
        }

        private struct PendingLaunch
        {
            public float WantedSpeed;
            public float GravityScale; // k²
            public float IntendedRange;
            public bool EnforceYaw;
            public float YawDeg;
        }

        private static readonly Dictionary<int, AimInfo> LastAimByPawnId = new Dictionary<int, AimInfo>(128);
        private static readonly List<IntVec3> TmpLeanCells = new List<IntVec3>(8);

        private static bool hasPendingLaunch;
        private static PendingLaunch pendingLaunch;

        public static void Apply(Harmony h)
        {
            int patched = 0;

            // CE's real targeting gate (vanilla Verb.CanHitTarget forwards to CanHitTargetFrom).
            var mCanHitFrom = AccessTools.Method(
                typeof(Verb_LaunchProjectileCE),
                nameof(Verb_LaunchProjectileCE.CanHitTargetFrom),
                new[] { typeof(IntVec3), typeof(LocalTargetInfo) });
            if (mCanHitFrom != null)
            {
                h.Patch(mCanHitFrom, prefix: new HarmonyMethod(typeof(CastPatches), nameof(CanHitTargetFrom_Prefix)));
                patched++;
            }
            else
                Log.Warning("[CE_LaR_Grenades] Missing CanHitTargetFrom(IntVec3, LocalTargetInfo)");

            var mCanHitFromReport = AccessTools.Method(
                typeof(Verb_LaunchProjectileCE),
                "CanHitTargetFrom",
                new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(string).MakeByRefType() });
            if (mCanHitFromReport != null)
            {
                h.Patch(mCanHitFromReport, prefix: new HarmonyMethod(typeof(CastPatches), nameof(CanHitTargetFromReport_Prefix)));
                patched++;
            }

            var mCanHit = AccessTools.Method(typeof(Verb), nameof(Verb.CanHitTarget), new[] { typeof(LocalTargetInfo) });
            if (mCanHit != null)
            {
                h.Patch(mCanHit, prefix: new HarmonyMethod(typeof(CastPatches), nameof(CanHitTarget_Prefix)));
                patched++;
            }

            var mValidate = AccessTools.Method(typeof(Verb), nameof(Verb.ValidateTarget), new[] { typeof(LocalTargetInfo), typeof(bool) });
            if (mValidate != null)
            {
                h.Patch(mValidate, prefix: new HarmonyMethod(typeof(CastPatches), nameof(ValidateTarget_Prefix)));
                patched++;
            }

            var mLineCE = AccessTools.Method(
                typeof(Verb_LaunchProjectileCE),
                "TryFindCEShootLineFromTo",
                new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(ShootLine).MakeByRefType(), typeof(Vector3).MakeByRefType() }
            );
            if (mLineCE != null)
            {
                h.Patch(mLineCE, postfix: new HarmonyMethod(typeof(CastPatches), nameof(TryFindCELine_Postfix)));
                patched++;
            }
            else
                Log.Warning("[CE_LaR_Grenades] Missing TryFindCEShootLineFromTo");

            // The overload TryCastShot actually calls.
            var launch8 = AccessTools.Method(
                typeof(ProjectileCE),
                "Launch",
                new[]
                {
                    typeof(Thing), typeof(Vector2), typeof(float), typeof(float),
                    typeof(float), typeof(float), typeof(Thing), typeof(float)
                });
            if (launch8 != null)
            {
                h.Patch(launch8,
                    prefix: new HarmonyMethod(typeof(CastPatches), nameof(ProjectileLaunch_Prefix)) { priority = Priority.Last },
                    postfix: new HarmonyMethod(typeof(CastPatches), nameof(ProjectileLaunch_Postfix)) { priority = Priority.Last });
                patched++;
            }
            else
                Log.Warning("[CE_LaR_Grenades] Missing ProjectileCE.Launch(8-arg)");

            Log.Message($"[CE_LaR_Grenades] Applied {patched} Harmony patches");
        }

        public static bool IsGrenadeVerb(Verb v)
        {
            if (v == null) return false;
            var fqn = v.GetType().FullName;
            return fqn == "CombatExtended.Verb_ShootCEOneUse" || fqn == "CombatExtended.Verb_ThrowGrenade";
        }

        /// <summary>True for grenade-like projectiles/equipment (not guns / bullet ammo).</summary>
        public static bool IsGrenadeProjectile(ProjectileCE projectile, Thing equipment)
        {
            if (NameHasGrenade(equipment?.def?.defName)) return true;
            if (NameHasGrenade(projectile?.def?.defName)) return true;
            return false;
        }

        private static bool NameHasGrenade(string? s) =>
            !string.IsNullOrEmpty(s) && s.IndexOf("grenade", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Last marked aim root (lean/forced) for ricochet preview origin.</summary>
        public static bool TryGetLastAimRoot(Pawn pawn, out IntVec3 root)
        {
            root = default;
            if (pawn == null) return false;
            if (!LastAimByPawnId.TryGetValue(pawn.thingIDNumber, out var aim)) return false;
            // Stale aim from a previous throw — only trust recent marks.
            if (Find.TickManager.TicksGame - aim.Tick > 5) return false;
            root = aim.Root;
            return true;
        }

        private static bool ShouldIntercept(Verb v, out Pawn pawn)
        {
            pawn = null;
            if (v == null) return false;
            pawn = v.Caster as Pawn;
            if (pawn == null || !IsGrenadeVerb(v)) return false;
            return AngleToggle.IsCustomAngleMode(pawn);
        }

        private static bool InRange(Verb v, IntVec3 root, IntVec3 cell)
        {
            var vp = v?.verbProps;
            if (vp == null) return false;
            float d = (cell - root).LengthHorizontal;
            return d <= vp.range && d >= vp.minRange;
        }

        private static float NormalizeDeg(float deg)
        {
            deg %= 360f;
            if (deg >= 180f) deg -= 360f;
            if (deg < -180f) deg += 360f;
            return deg;
        }

        private static float BearingDeg(IntVec3 from, IntVec3 to)
        {
            var dv = to - from;
            float rad = Mathf.Atan2(dv.z, dv.x);
            return NormalizeDeg(rad * Mathf.Rad2Deg);
        }

        private static void MarkAim(Pawn pawn, IntVec3 root, IntVec3 targetCell, Vector3 targetPos, bool forcedLine)
        {
            LastAimByPawnId[pawn.thingIDNumber] = new AimInfo
            {
                Root = root,
                TargetCell = targetCell,
                TargetPos = targetPos,
                Tick = Find.TickManager.TicksGame,
                ForcedLine = forcedLine
            };
        }

        /// <summary>
        /// Keep the player's chosen angle when the arc stays under MaxLobPeakHeight;
        /// otherwise soften θ so peak ≈ MaxLobPeakHeight (still clears walls, stock CE draw looks normal).
        /// </summary>
        public static float EffectiveLaunchAngleRad(int angleDeg, float range)
        {
            float requested = angleDeg * Mathf.Deg2Rad;
            if (range < 0.5f) return requested;

            float maxTan = (4f * MaxLobPeakHeight) / range;
            if (maxTan <= 0f) return requested;

            float capped = Mathf.Atan(maxTan);
            return Mathf.Min(requested, capped);
        }

        public const float DefaultShotHeight = 1.0f;
        public const float DefaultGravityPerWidth = 1.96f;

        public static bool TryComputeThrowSpeed(float range, float angleRad, float heightDelta, float gravityPerWidth, out float speed)
        {
            speed = 0f;
            if (range < 0.01f || gravityPerWidth <= 0f) return false;

            float cos = Mathf.Cos(angleRad);
            if (Mathf.Abs(cos) < 1e-5f) return false;

            float denom = range * Mathf.Tan(angleRad) - heightDelta;
            if (denom <= MinDenomEpsilon) return false;

            float vSq = (gravityPerWidth * range * range) / (2f * cos * cos * denom);
            if (vSq <= 0f || float.IsNaN(vSq) || float.IsInfinity(vSq)) return false;

            speed = Mathf.Clamp(Mathf.Sqrt(vSq), MinSpeedFloor, MaxSpeedClamp);
            return true;
        }

        private static bool TryForceInRange(Verb verb, Pawn pawn, IntVec3 root, LocalTargetInfo target, ref bool __result)
        {
            var map = pawn.Map;
            if (map == null || !target.IsValid) return true;
            if (!target.Cell.InBounds(map) || !InRange(verb, root, target.Cell))
                return true;

            MarkAim(pawn, root, target.Cell, target.Cell.ToVector3Shifted(), forcedLine: false);
            __result = true;
            return false;
        }

        public static bool CanHitTargetFrom_Prefix(Verb_LaunchProjectileCE __instance,
            [HarmonyArgument(0)] IntVec3 root,
            [HarmonyArgument(1)] LocalTargetInfo targ,
            ref bool __result)
        {
            if (!ShouldIntercept(__instance, out var pawn)) return true;
            return TryForceInRange(__instance, pawn, root, targ, ref __result);
        }

        public static bool CanHitTargetFromReport_Prefix(Verb_LaunchProjectileCE __instance,
            [HarmonyArgument(0)] IntVec3 root,
            [HarmonyArgument(1)] LocalTargetInfo targ,
            ref string report,
            ref bool __result)
        {
            if (!ShouldIntercept(__instance, out var pawn)) return true;
            bool runOriginal = TryForceInRange(__instance, pawn, root, targ, ref __result);
            if (!runOriginal)
                report = "";
            return runOriginal;
        }

        public static bool CanHitTarget_Prefix(Verb __instance,
            [HarmonyArgument(0)] LocalTargetInfo targ,
            ref bool __result)
        {
            if (!ShouldIntercept(__instance, out var pawn)) return true;
            return TryForceInRange(__instance, pawn, pawn.Position, targ, ref __result);
        }

        public static bool ValidateTarget_Prefix(Verb __instance,
            [HarmonyArgument(0)] LocalTargetInfo targ,
            [HarmonyArgument(1)] bool showMessages,
            ref bool __result)
        {
            if (!ShouldIntercept(__instance, out var pawn)) return true;
            return TryForceInRange(__instance, pawn, pawn.Position, targ, ref __result);
        }

        public static void TryFindCELine_Postfix(Verb_LaunchProjectileCE __instance,
            [HarmonyArgument(0)] IntVec3 root,
            [HarmonyArgument(1)] LocalTargetInfo targ,
            ref ShootLine resultingLine,
            ref Vector3 targetPos,
            ref bool __result)
        {
            if (!ShouldIntercept(__instance, out var pawn)) return;

            var map = pawn.Map;
            if (map == null || !targ.IsValid) return;
            if (!targ.Cell.InBounds(map) || !InRange(__instance, root, targ.Cell)) return;

            if (__result)
            {
                MarkAim(pawn, resultingLine.Source, targ.Cell, targetPos, forcedLine: false);
                return;
            }

            IntVec3 src = root;
            TmpLeanCells.Clear();
            CellRect cellRect = (!targ.HasThing) ? CellRect.SingleCell(targ.Cell) : targ.Thing.OccupiedRect();
            ShootLeanUtility.LeanShootingSourcesFromTo(root, cellRect.ClosestCellTo(root), map, TmpLeanCells);
            if (TmpLeanCells.Count > 0)
                src = TmpLeanCells.OrderBy(c => c.DistanceTo(targ.Cell)).First();

            targetPos = targ.HasThing ? targ.Thing.DrawPos : targ.Cell.ToVector3Shifted();
            resultingLine = new ShootLine(src, targ.Cell);
            __result = true;

            MarkAim(pawn, src, targ.Cell, targetPos, forcedLine: true);
            CE_LaR_GrenadesLog.L($"[CE_LaR_Grenades] Forced lob line from {(src == root ? "root" : "lean")} {src} → {targ.Cell}");
        }

        public static void ProjectileLaunch_Prefix(
            ProjectileCE __instance,
            [HarmonyArgument(0)] Thing launcher,
            [HarmonyArgument(1)] Vector2 origin,
            [HarmonyArgument(2)] ref float shotAngle,
            [HarmonyArgument(3)] ref float shotRotation,
            [HarmonyArgument(4)] float shotHeight,
            [HarmonyArgument(5)] ref float shotSpeed,
            [HarmonyArgument(6)] Thing equipment,
            [HarmonyArgument(7)] float distance)
        {
            hasPendingLaunch = false;

            var pawn = launcher as Pawn;
            if (pawn == null || !AngleToggle.IsCustomAngleMode(pawn)) return;
            // Launch is shared by every CE projectile — never rewrite gun bullets just because
            // this pawn's grenade throw gizmo is on 30°/Ricochet/etc.
            if (!IsGrenadeProjectile(__instance, equipment)) return;

            int angleDeg = AngleToggle.GetAngleDegFor(pawn);

            bool haveAim = LastAimByPawnId.TryGetValue(pawn.thingIDNumber, out var aim);
            float range = haveAim
                ? Vector2.Distance(origin, new Vector2(aim.TargetPos.x, aim.TargetPos.z))
                : distance;

            if (range < 0.01f)
            {
                CE_LaR_GrenadesLog.L($"[CE_LaR_Grenades] Launch: angle set to {angleDeg}° but range unusable (aim={haveAim} d={distance:F2})");
                return;
            }

            // Soften extreme arcs so CE's stock height→draw offset does not park the sprite in the sky.
            float angleRad = EffectiveLaunchAngleRad(angleDeg, range);
            shotAngle = angleRad;

            float heightDelta = 0f - shotHeight;
            float gravityPerWidth = 1.96f;
            if (__instance?.def?.projectile is ProjectilePropertiesCE props)
                gravityPerWidth = props.GravityPerWidth;

            if (!TryComputeThrowSpeed(range, angleRad, heightDelta, gravityPerWidth, out float baseSpeed))
            {
                Log.Warning($"[CE_LaR_Grenades] Cannot solve throw speed for angle={angleDeg}° (eff={angleRad * Mathf.Rad2Deg:F1}°) range={range:F2} Δh={heightDelta:F2}");
                return;
            }

            float k = AngleToggle.GetFlightScaleForDegrees(angleRad * Mathf.Rad2Deg);
            float wantedSpeed = Mathf.Clamp(baseSpeed * k, MinSpeedFloor, MaxSpeedClamp);
            shotSpeed = wantedSpeed;

            bool enforceYaw = haveAim && aim.ForcedLine;
            float yawDeg = 0f;
            if (enforceYaw)
            {
                yawDeg = NormalizeDeg(BearingDeg(aim.Root, aim.TargetCell) - 90f);
                shotRotation = yawDeg;
            }

            pendingLaunch = new PendingLaunch
            {
                WantedSpeed = wantedSpeed,
                GravityScale = k * k,
                IntendedRange = range,
                EnforceYaw = enforceYaw,
                YawDeg = yawDeg
            };
            hasPendingLaunch = true;

            CE_LaR_GrenadesLog.L(
                $"[CE_LaR_Grenades] Launch: mode={angleDeg}° effAngle={angleRad * Mathf.Rad2Deg:F1}° k={k:F2} range={range:F2} " +
                $"vBase={baseSpeed:F3}→v={wantedSpeed:F3} gScale={k * k:F2} forcedLine={enforceYaw}");
        }

        public static void ProjectileLaunch_Postfix(ProjectileCE __instance, Thing launcher)
        {
            if (!hasPendingLaunch) return;
            var pending = pendingLaunch;
            hasPendingLaunch = false;
            if (__instance == null) return;

            try
            {
                float k2 = pending.GravityScale;
                var props = __instance.Props;

                __instance.shotSpeed = pending.WantedSpeed;
                if (pending.EnforceYaw)
                    __instance.shotRotation = pending.YawDeg;

                __instance.gravity = props.Gravity * k2;
                __instance.GravityPerWidth = props.GravityPerWidth * k2;
                __instance.GravityPerHeight = props.GravityPerHeight * k2;

                float travel = pending.IntendedRange > 0.01f
                    ? pending.IntendedRange
                    : __instance.TrajectoryWorker.DistanceTraveled(
                        __instance.shotHeight, __instance.shotSpeed, __instance.shotAngle, __instance.GravityPerWidth);

                __instance.Destination = __instance.origin + Vector2.up.RotatedBy(__instance.shotRotation) * travel;
                __instance.velocity = __instance.TrajectoryWorker.GetInitialVelocity(
                    __instance.shotSpeed, __instance.shotRotation, __instance.shotAngle);
                __instance.initialSpeed = __instance.shotSpeed;

                __instance.startingTicksToImpact =
                    __instance.TrajectoryWorker.GetFlightTime(
                        __instance.shotAngle, __instance.shotSpeed, __instance.GravityPerWidth, __instance.shotHeight)
                    * GenTicks.TicksPerRealSecond;
                __instance.ticksToImpact = Mathf.CeilToInt(__instance.startingTicksToImpact);

                // CE cover-grace skips nearby walls — custom throws must hit them while still low
                // (otherwise adjacent walls are phased through on both lobs and ricochets).
                float oldMin = __instance.minCollisionDistance;
                __instance.minCollisionDistance = 0f;

                // Ricochet is a flat bank shot — lob tick-warp skips wall checks and tunnels.
                if (launcher is Pawn lp && AngleToggle.IsRicochetMode(lp))
                    LobFlightFx.Unregister(__instance);
                else
                    LobFlightFx.RegisterLob(__instance);

                CE_LaR_GrenadesLog.L(
                    $"[CE_LaR_Grenades] PostLaunch: speed={__instance.shotSpeed:F3} gW={__instance.GravityPerWidth:F3} " +
                    $"travel={travel:F2} ticks={__instance.ticksToImpact} minColDist {oldMin:F2}→0");
            }
            catch (Exception e)
            {
                Log.Warning("[CE_LaR_Grenades] Post-Launch gravity/TOF recompute failed (non-fatal): " + e.Message);
            }
        }
    }
}
