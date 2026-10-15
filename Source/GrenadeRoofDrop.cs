using HarmonyLib;
using Verse;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Reflection;
using CombatExtended;

namespace CE_LaR_Grenades
{
    /// <summary>
    /// Custom throw modes: underside roof hits (from inside) drop to the ground.
    /// Hits onto the roof from above stay stock CE. CE Normal left alone entirely.
    /// </summary>
    public static class GrenadeRoofDrop
    {
        // Not near-vertical: LookRotation(down) is parallel to world-up and hides the mesh
        // while the ground shadow still draws — looked like "disappeared, only shadow".
        private const float DropAngleDeg = -45f;
        private const float UnderRoofClearance = 0.25f;
        private const float MinHeight = 0.05f;
        private const float MaxStartHeight = 1.85f;
        private const float MinSpeed = 2.5f;
        private const float MaxSpeed = 7f;
        private const float TargetFallSec = 0.95f;
        private const float DriftCells = 0.9f;

        private static readonly HashSet<int> Dropped = new HashSet<int>();
        private static FieldInfo? ignoreRoofField;

        public static void Apply(Harmony h)
        {
            ignoreRoofField = AccessTools.Field(typeof(ProjectileCE), "ignoreRoof");

            var roof = AccessTools.Method(typeof(ProjectileCE), "TryCollideWithRoof", new[] { typeof(IntVec3) });
            if (roof != null)
                h.Patch(roof, prefix: new HarmonyMethod(typeof(GrenadeRoofDrop), nameof(TryCollideWithRoof_Prefix)));
            else
                Log.Warning("[CE_LaR_Grenades] RoofDrop: missing TryCollideWithRoof");

            var destroy = AccessTools.Method(typeof(Thing), nameof(Thing.Destroy), new[] { typeof(DestroyMode) });
            if (destroy != null)
                h.Patch(destroy, prefix: new HarmonyMethod(typeof(GrenadeRoofDrop), nameof(Destroy_Prefix)));
        }

        private static bool IsEligible(ProjectileCE p)
        {
            if (p == null) return false;
            return LobFlightFx.IsLobTracked(p) || GrenadeRicochet.IsTracked(p.thingIDNumber);
        }

        private static bool IsIgnoringRoof(ProjectileCE p)
        {
            if (ignoreRoofField == null) return false;
            try { return (bool)ignoreRoofField.GetValue(p); }
            catch { return false; }
        }

        private static void SetIgnoreRoof(ProjectileCE p, bool value)
        {
            if (ignoreRoofField == null) return;
            try { ignoreRoofField.SetValue(p, value); }
            catch { /* ignore */ }
        }

        public static bool TryCollideWithRoof_Prefix(ProjectileCE __instance, IntVec3 cell, ref bool __result)
        {
            if (!IsEligible(__instance)) return true;
            if (__instance.landed) return true;
            if (Dropped.Contains(__instance.thingIDNumber)) return true;
            if (IsIgnoringRoof(__instance)) return true;

            var map = __instance.Map;
            if (map == null || !cell.InBounds(map) || !cell.Roofed(map)) return true;

            RoofDef roof = cell.GetRoof(map);
            Bounds bounds = CE_Utility.GetBoundsFor(cell, roof);
            Ray shotLine = __instance.ShotLine;
            if (!bounds.IntersectRay(shotLine, out float distance)) return true;

            float stepSq = (__instance.ExactPosition - __instance.LastPos).sqrMagnitude;
            if (distance * distance > stepSq) return true;

            Vector3 hit = shotLine.GetPoint(distance);

            // Only drop when the grenade hits the underside from inside the building.
            // Falling onto the roof from above → leave stock CE Impact alone.
            if (__instance.LastPos.y >= bounds.min.y - 0.01f)
                return true;

            float underY = bounds.min.y - UnderRoofClearance;
            underY = Mathf.Min(underY, MaxStartHeight);
            underY = Mathf.Max(MinHeight, underY);

            Vector3 start = new Vector3(hit.x, underY, hit.z);

            LobFlightFx.Unregister(__instance);
            SetIgnoreRoof(__instance, true);

            if (!TryRebakeDrop(__instance, start))
            {
                CE_LaR_GrenadesLog.L($"[CE_LaR_Grenades] RoofDrop: rebake FAILED at {cell} — stock Impact");
                return true;
            }

            Dropped.Add(__instance.thingIDNumber);
            __result = true;

            CE_LaR_GrenadesLog.L(
                $"[CE_LaR_Grenades] RoofDrop: #{__instance.thingIDNumber} at {cell} " +
                $"hitY={hit.y:F2} roofMin={bounds.min.y:F2}→underY={underY:F2} " +
                $"speed={__instance.shotSpeed:F1} angle={__instance.shotAngle * Mathf.Rad2Deg:F0}° " +
                $"ticks={__instance.ticksToImpact}");

            return false;
        }

        private static bool TryRebakeDrop(ProjectileCE p, Vector3 start)
        {
            try
            {
                float height = Mathf.Max(MinHeight, start.y);
                float angle = DropAngleDeg * Mathf.Deg2Rad;

                float inbound = Mathf.Max(0.05f, p.shotSpeed);
                float heightBased = height / Mathf.Max(0.2f, TargetFallSec * Mathf.Abs(Mathf.Sin(angle)));
                float speed = Mathf.Clamp(Mathf.Max(inbound * 0.35f, heightBased), MinSpeed, MaxSpeed);

                // Small horizontal drift so LookRotation has a non-vertical forward and the
                // lerped path is not a degenerate origin==destination.
                Vector2 forward = Vector2.up.RotatedBy(p.shotRotation);
                if (forward.sqrMagnitude < 1e-6f)
                    forward = Vector2.right;
                forward.Normalize();

                Vector2 origin2 = new Vector2(start.x, start.z);
                Vector2 dest2 = origin2 + forward * DriftCells;

                p.origin = origin2;
                p.OriginIV3 = new IntVec3(Mathf.FloorToInt(origin2.x), 0, Mathf.FloorToInt(origin2.y));
                p.Destination = dest2;
                p.shotHeight = height;
                p.shotAngle = angle;
                p.shotSpeed = speed;
                p.initialSpeed = speed;
                p.FlightTicks = 0;
                p.LastPos = start;
                p.ExactPosition = start;
                p.landed = false;
                p.minCollisionDistance = 0f;

                p.velocity = p.TrajectoryWorker.GetInitialVelocity(p.shotSpeed, p.shotRotation, p.shotAngle);

                float flightSec = p.TrajectoryWorker.GetFlightTime(
                    p.shotAngle, p.shotSpeed, p.GravityPerWidth, p.shotHeight);
                if (float.IsNaN(flightSec) || flightSec < 0.05f || flightSec > 3f)
                {
                    float vert = Mathf.Max(0.05f, speed * Mathf.Abs(Mathf.Sin(angle)));
                    flightSec = Mathf.Clamp(height / vert, 0.2f, 1.2f);
                }

                p.startingTicksToImpact = flightSec * GenTicks.TicksPerRealSecond;
                p.ticksToImpact = Mathf.Max(1, Mathf.CeilToInt(p.startingTicksToImpact));

                var fCache = AccessTools.Field(typeof(ProjectileCE), "cachedPredictedPositions");
                fCache?.SetValue(p, null);

                return true;
            }
            catch (Exception e)
            {
                Log.Warning("[CE_LaR_Grenades] RoofDrop rebake failed: " + e.Message);
                return false;
            }
        }

        public static void Destroy_Prefix(Thing __instance)
        {
            if (__instance is ProjectileCE)
                Dropped.Remove(__instance.thingIDNumber);
        }
    }
}
