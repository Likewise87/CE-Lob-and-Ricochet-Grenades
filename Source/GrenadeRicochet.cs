using HarmonyLib;
using Verse;
using UnityEngine;
using System;
using System.Collections.Generic;
using CombatExtended;

namespace CE_LaR_Grenades
{
    /// <summary>
    /// Ricochet-mode grenade wall bounce (always on when ricochet throw mode is selected).
    ///
    /// CE skips things closer than minCollisionDistance (cover grace). Close walls therefore
    /// never reach TryCollideWith — we zero that for ricochet throws and also sweep each tick
    /// so fat/high-speed steps cannot tunnel past a wall without a bounce.
    /// </summary>
    public static class GrenadeRicochet
    {
        private const int IgnoreGraceTicks = 24;
        private const float ContinuationAngleDeg = -12f;

        private struct State
        {
            public int BounceCount;
            public int IgnoreThingId;
            public int IgnoreUntilTick;
        }

        private static readonly HashSet<int> Registered = new HashSet<int>();
        private static readonly Dictionary<int, State> States = new Dictionary<int, State>(64);

        // Avoid re-entrant bounce from Tick sweep while TryCollideWith is already handling.
        private static bool applyingBounce;

        public static void Apply(Harmony h)
        {
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
                h.Patch(launch8, postfix: new HarmonyMethod(typeof(GrenadeRicochet), nameof(Launch_Postfix))
                {
                    priority = Priority.Last - 50
                });
            }

            var collide = AccessTools.Method(typeof(ProjectileCE), "TryCollideWith", new[] { typeof(Thing) });
            if (collide != null)
                h.Patch(collide, prefix: new HarmonyMethod(typeof(GrenadeRicochet), nameof(TryCollideWith_Prefix)));
            else
                Log.Warning("[CE_LaR_Grenades] Ricochet: missing TryCollideWith");

            var tick = AccessTools.Method(typeof(ProjectileCE), nameof(ProjectileCE.Tick));
            if (tick != null)
                h.Patch(tick, postfix: new HarmonyMethod(typeof(GrenadeRicochet), nameof(Tick_Postfix)));

            var destroy = AccessTools.Method(typeof(Thing), nameof(Thing.Destroy), new[] { typeof(DestroyMode) });
            if (destroy != null)
                h.Patch(destroy, prefix: new HarmonyMethod(typeof(GrenadeRicochet), nameof(Destroy_Prefix)));
        }

        public static void Launch_Postfix(
            ProjectileCE __instance,
            Thing launcher,
            Thing equipment)
        {
            if (__instance == null) return;

            var pawn = launcher as Pawn;
            if (pawn == null) return;
            if (!AngleToggle.IsRicochetMode(pawn)) return;
            if (!CastPatches.IsGrenadeProjectile(__instance, equipment)) return;

            LobFlightFx.Unregister(__instance);

            // CE uses this to ignore near cover so lean-fire doesn't eat the wall you're behind.
            // For bank shots that wall IS the target — leaving it >0 means TryCollideWith never runs
            // (silent "through wall"), especially on close walls / long fast throws.
            float oldMin = __instance.minCollisionDistance;
            __instance.minCollisionDistance = 0f;

            int id = __instance.thingIDNumber;
            Registered.Add(id);
            States[id] = new State { BounceCount = 0, IgnoreThingId = -1, IgnoreUntilTick = 0 };

            CE_LaR_GrenadesLog.L(
                $"[CE_LaR_Grenades] Ricochet: registered #{id} minColDist {oldMin:F2}→0 " +
                $"speed={__instance.shotSpeed:F2} origin={__instance.origin} dest={__instance.Destination} " +
                $"angle={__instance.shotAngle * Mathf.Rad2Deg:F1}°");
        }

        public static bool TryCollideWith_Prefix(ProjectileCE __instance, Thing thing, ref bool __result)
        {
            if (__instance == null || thing == null) return true;
            if (__instance.landed) return true;

            int id = __instance.thingIDNumber;
            if (!Registered.Contains(id)) return true;

            if (States.TryGetValue(id, out var st)
                && st.IgnoreThingId == thing.thingIDNumber
                && Find.TickManager.TicksGame < st.IgnoreUntilTick)
            {
                CE_LaR_GrenadesLog.L($"[CE_LaR_Grenades] Ricochet: TryCollide skip grace vs {thing.LabelCap} #{thing.thingIDNumber}");
                __result = false;
                return false;
            }

            if (!RicochetSim.IsSolidWall(thing))
                return true;

            if (__instance.ExactPosition.y > CollisionVertical.WallCollisionHeight)
            {
                CE_LaR_GrenadesLog.L(
                    $"[CE_LaR_Grenades] Ricochet: TryCollide skip height y={__instance.ExactPosition.y:F2} " +
                    $"> wallH={CollisionVertical.WallCollisionHeight} vs {thing.LabelCap}");
                return true;
            }

            if (!States.TryGetValue(id, out st))
                st = new State();

            if (st.BounceCount >= RicochetSim.MaxBounces)
            {
                CE_LaR_GrenadesLog.L($"[CE_LaR_Grenades] Ricochet: TryCollide at bounce cap — stock Impact vs {thing.LabelCap}");
                return true;
            }

            if (TryApplyBounce(__instance, thing, st, "TryCollideWith"))
            {
                States[id] = st;
                __result = true;
                return false;
            }

            // Bounce failed — let CE Impact.
            return true;
        }

        /// <summary>
        /// After CE's CheckForCollisionBetween: catch walls CE skipped (minCollisionDistance,
        /// height-cover gate, or step tunneling past a thin cell).
        /// </summary>
        public static void Tick_Postfix(ProjectileCE __instance)
        {
            if (applyingBounce) return;
            if (__instance == null || __instance.landed) return;

            int id = __instance.thingIDNumber;
            if (!Registered.Contains(id)) return;
            if (__instance.ExactPosition.y > CollisionVertical.WallCollisionHeight) return;
            if (!States.TryGetValue(id, out var st)) return;
            if (st.BounceCount >= RicochetSim.MaxBounces) return;

            var map = __instance.Map;
            if (map == null) return;

            // Keep minCollisionDistance suppressed for the whole ricochet life.
            if (__instance.minCollisionDistance > 0.01f)
            {
                CE_LaR_GrenadesLog.L(
                    $"[CE_LaR_Grenades] Ricochet: tick forcing minColDist {__instance.minCollisionDistance:F2}→0 " +
                    $"(was re-set mid-flight)");
                __instance.minCollisionDistance = 0f;
            }

            Vector3 from = __instance.LastPos;
            Vector3 to = __instance.ExactPosition;
            float step = (to - from).MagnitudeHorizontal();
            if (step < 1e-4f) return;

            Thing? wall = RicochetSim.FindWallAlongSegment(
                map, from, to, st.IgnoreThingId, st.IgnoreUntilTick);

            if (wall == null) return;

            // If CE already handled this tick via TryCollideWith, ExactPosition should be unstuck
            // outside; FindWallAlongSegment on the post-bounce segment shouldn't re-hit (grace).
            CE_LaR_GrenadesLog.L(
                $"[CE_LaR_Grenades] Ricochet: SWEEP caught {wall.LabelCap} #{wall.thingIDNumber} " +
                $"step={step:F2} (CE likely skipped via minColDist/cover/tunnel) " +
                $"from={from.ToIntVec3()}→{to.ToIntVec3()} bounces={st.BounceCount}");

            if (TryApplyBounce(__instance, wall, st, "TickSweep"))
                States[id] = st;
        }

        private static bool TryApplyBounce(ProjectileCE p, Thing wall, State st, string source)
        {
            if (applyingBounce) return false;
            applyingBounce = true;
            try
            {
                Vector3 last = p.LastPos;
                Vector3 cur = p.ExactPosition;

                Vector2 inbound = new Vector2(p.velocity.x, p.velocity.z);
                if (inbound.sqrMagnitude < 1e-8f)
                    inbound = new Vector2(cur.x - last.x, cur.z - last.z);
                if (inbound.sqrMagnitude < 1e-8f)
                {
                    float rad = (p.shotRotation + 90f) * Mathf.Deg2Rad;
                    inbound = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
                }
                inbound.Normalize();

                if (RicochetSim.TryGetWallBounds(wall, out Bounds wallBounds))
                {
                    Vector3 fromOrigin = new Vector3(p.origin.x, cur.y, p.origin.y);
                    if (RicochetSim.IsOutsideWallXZ(fromOrigin, wallBounds)
                        && !RicochetSim.IsOutsideWallXZ(last, wallBounds))
                    {
                        last = fromOrigin;
                    }
                    else if (!RicochetSim.IsOutsideWallXZ(last, wallBounds))
                    {
                        Vector3 rewind = cur;
                        for (int i = 0; i < 16; i++)
                        {
                            rewind.x -= inbound.x * 0.4f;
                            rewind.z -= inbound.y * 0.4f;
                            if (RicochetSim.IsOutsideWallXZ(rewind, wallBounds))
                            {
                                last = rewind;
                                break;
                            }
                        }
                    }
                }

                float remaining = Vector2.Distance(new Vector2(cur.x, cur.z), p.Destination);
                if (remaining < RicochetSim.MinRemainingDistance)
                {
                    CE_LaR_GrenadesLog.L(
                        $"[CE_LaR_Grenades] Ricochet: {source} vs {wall.LabelCap} remaining={remaining:F2} too small — Impact");
                    return false;
                }

                if (!RicochetSim.TryComputeBounce(inbound, last, cur, wall, out var bounce))
                {
                    CE_LaR_GrenadesLog.L(
                        $"[CE_LaR_Grenades] Ricochet: {source} bounce resolve FAILED vs {wall.LabelCap} " +
                        $"inbound=({inbound.x:F2},{inbound.y:F2}) last={last.ToIntVec3()} cur={cur.ToIntVec3()} — Impact");
                    return false;
                }

                float travel = remaining * RicochetSim.TravelRetainAfterBounce(st.BounceCount);
                Vector2 newDest2 = new Vector2(bounce.Unstuck.x, bounce.Unstuck.z) + bounce.Outbound * travel;

                var map = p.Map;
                if (map == null) return false;

                IntVec3 destCell = new IntVec3(Mathf.FloorToInt(newDest2.x), 0, Mathf.FloorToInt(newDest2.y));
                if (!destCell.InBounds(map))
                {
                    Vector2 from = new Vector2(bounce.Unstuck.x, bounce.Unstuck.z);
                    Vector2 clamped = RicochetSim.ClampToMap(from, newDest2, map);
                    if ((clamped - from).sqrMagnitude < RicochetSim.MinRemainingDistance * RicochetSim.MinRemainingDistance)
                    {
                        CE_LaR_GrenadesLog.L($"[CE_LaR_Grenades] Ricochet: {source} map-clamp too short — Impact");
                        return false;
                    }
                    newDest2 = clamped;
                    travel = (newDest2 - from).magnitude;
                }

                LobFlightFx.Unregister(p);

                if (!TryRebakeTrajectory(p, bounce.Unstuck, newDest2, bounce.Outbound, st.BounceCount, travel))
                {
                    CE_LaR_GrenadesLog.L($"[CE_LaR_Grenades] Ricochet: {source} rebake FAILED vs {wall.LabelCap}");
                    return false;
                }

                // Stay immune to minCollisionDistance after rebake.
                p.minCollisionDistance = 0f;

                st.BounceCount++;
                st.IgnoreThingId = wall.thingIDNumber;
                st.IgnoreUntilTick = Find.TickManager.TicksGame + IgnoreGraceTicks;

                CE_LaR_GrenadesLog.L(
                    $"[CE_LaR_Grenades] Ricochet: {source} BOUNCE {st.BounceCount}/{RicochetSim.MaxBounces} " +
                    $"vs {wall.LabelCap} remaining={remaining:F2}→{travel:F2} " +
                    $"speed={p.shotSpeed:F2} (was inbound-capped) " +
                    $"n=({bounce.Normal.x:F1},{bounce.Normal.y:F1}) out=({bounce.Outbound.x:F2},{bounce.Outbound.y:F2}) " +
                    $"unstuck={bounce.Unstuck.ToIntVec3()} dest={newDest2}");

                return true;
            }
            finally
            {
                applyingBounce = false;
            }
        }

        private static bool TryRebakeTrajectory(
            ProjectileCE p,
            Vector3 start,
            Vector2 dest2,
            Vector2 outbound,
            int bounceIndex,
            float travel)
        {
            try
            {
                float shotRotation = (-90f + Mathf.Atan2(outbound.y, outbound.x) * Mathf.Rad2Deg) % 360f;
                if (shotRotation > 180f) shotRotation -= 360f;
                if (shotRotation < -180f) shotRotation += 360f;

                float height = Mathf.Max(0.05f, start.y);
                float angle = ContinuationAngleDeg * Mathf.Deg2Rad;

                // Coupled: same retain on travel (caller) and speed — no separate visual band.
                float inboundSpeed = Mathf.Max(0.05f, p.shotSpeed);
                float retain = RicochetSim.TravelRetainAfterBounce(bounceIndex);
                float speed = Mathf.Max(0.05f, inboundSpeed * retain);
                float dist = Mathf.Max(RicochetSim.MinRemainingDistance, travel);

                p.origin = new Vector2(start.x, start.z);
                p.OriginIV3 = new IntVec3(p.origin);
                p.Destination = dest2;
                p.shotRotation = shotRotation;
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
                if (float.IsNaN(flightSec) || flightSec < 0.05f)
                {
                    float horizSpeed = Mathf.Max(0.05f, p.shotSpeed * Mathf.Cos(p.shotAngle));
                    flightSec = dist / horizSpeed;
                }

                p.startingTicksToImpact = flightSec * GenTicks.TicksPerRealSecond;
                p.ticksToImpact = Mathf.Max(1, Mathf.CeilToInt(p.startingTicksToImpact));

                var fCache = AccessTools.Field(typeof(ProjectileCE), "cachedPredictedPositions");
                fCache?.SetValue(p, null);

                CE_LaR_GrenadesLog.L(
                    $"[CE_LaR_Grenades] Ricochet: rebake speed {inboundSpeed:F1}→{speed:F1} " +
                    $"(retain={retain:F2}) travel={dist:F2} sec={flightSec:F2}");

                return true;
            }
            catch (Exception e)
            {
                Log.Warning("[CE_LaR_Grenades] Ricochet rebake failed: " + e.Message);
                return false;
            }
        }

        public static bool IsTracked(int thingId) => Registered.Contains(thingId);

        public static void Destroy_Prefix(Thing __instance)
        {
            if (__instance is ProjectileCE)
            {
                int id = __instance.thingIDNumber;
                Registered.Remove(id);
                States.Remove(id);
            }
        }
    }
}
