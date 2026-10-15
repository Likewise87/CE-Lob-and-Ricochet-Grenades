using Verse;
using UnityEngine;
using System.Collections.Generic;
using CombatExtended;
using RimWorld;

namespace CE_LaR_Grenades
{
    /// <summary>
    /// Shared wall-bounce geometry for live flight and aim preview.
    /// </summary>
    public static class RicochetSim
    {
        public const int MaxBounces = 3;
        public const float MinRemainingDistance = 0.35f;

        /// <summary>Coupled energy: keep 50% travel and speed each bounce (loss independent of bounce index).</summary>
        public const float EnergyRetainPerBounce = 0.50f;

        public static float TravelRetainAfterBounce(int bounceIndex) => EnergyRetainPerBounce;

        private const float UnstickPush = 0.55f;
        private const float OutboundNudge = 0.20f;
        private const float ClearanceProbe = 0.55f;
        private const float BoundsEps = 0.02f;
        private const float PreviewStep = 0.22f;
        private const int PreviewMaxSteps = 120;
        private const float OverlayAltitude = 0.15f;

        public struct BounceResult
        {
            public Vector2 Normal;
            public Vector2 Outbound;
            public Vector3 Unstuck;
            public Vector2 HitXZ;
        }

        /// <summary>
        /// Same as UnityEngine.Vector2.Reflect (missing from RimWorld 1.7 UnityStubs).
        /// Assumes <paramref name="normal"/> is normalized.
        /// </summary>
        private static Vector2 Reflect(Vector2 direction, Vector2 normal) =>
            direction - 2f * Vector2.Dot(direction, normal) * normal;

        public static bool IsSolidWall(Thing thing)
        {
            if (thing?.def == null) return false;
            if (thing.def.Fillage != FillCategory.Full) return false;
            if (thing is Pawn) return false;
            if (thing.def.category == ThingCategory.Plant) return false;
            return thing.def.category == ThingCategory.Building || thing.def.building != null;
        }

        public static bool TryGetWallBounds(Thing wall, out Bounds bounds)
        {
            bounds = default;
            try
            {
                bounds = CE_Utility.GetBoundsFor(wall);
                return bounds.extents.x > 0.01f && bounds.extents.z > 0.01f;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Resolve a valid bank off <paramref name="wall"/> from approach→cur motion.
        /// </summary>
        public static bool TryComputeBounce(
            Vector2 inbound,
            Vector3 last,
            Vector3 cur,
            Thing wall,
            out BounceResult result)
        {
            result = default;
            if (!TryGetWallBounds(wall, out Bounds bounds))
                return false;

            if (inbound.sqrMagnitude < 1e-8f)
                return false;
            inbound.Normalize();

            Vector3 approach = ResolveApproachPoint(last, cur, inbound, bounds);
            if (!TryResolveBounceNormal(inbound, approach, cur, bounds, out Vector2 normal, out Vector2 hitXZ))
                return false;

            if (Vector2.Dot(normal, inbound) > 0f)
                normal = -normal;

            // Hand-rolled: RW 1.7 UnityStubs has Vector3.Reflect but not Vector2.Reflect.
            Vector2 outbound = Reflect(inbound, normal);
            if (outbound.sqrMagnitude < 1e-8f)
                return false;
            outbound.Normalize();

            if (Vector2.Dot(outbound, normal) < 0.05f)
                return false;
            if (Vector2.Dot(outbound, inbound) > 0.92f)
                return false;

            if (!TryBuildUnstuckPose(hitXZ, cur.y, normal, outbound, wall, bounds, out Vector3 unstuck))
                return false;

            result = new BounceResult
            {
                Normal = normal,
                Outbound = outbound,
                Unstuck = unstuck,
                HitXZ = hitXZ
            };
            return true;
        }

        public static Vector2 ClampToMap(Vector2 from, Vector2 to, Map map)
        {
            Vector2 dir = to - from;
            float len = dir.magnitude;
            if (len < 1e-4f) return from;
            dir /= len;

            float lo = 0f, hi = len;
            for (int i = 0; i < 12; i++)
            {
                float mid = (lo + hi) * 0.5f;
                Vector2 p = from + dir * mid;
                IntVec3 c = new IntVec3(Mathf.FloorToInt(p.x), 0, Mathf.FloorToInt(p.y));
                if (c.InBounds(map)) lo = mid;
                else hi = mid;
            }
            return from + dir * lo;
        }

        /// <summary>
        /// Flat XZ ricochet path for aim preview. Appends overlay points into <paramref name="points"/>.
        /// Returns bounce count used.
        /// </summary>
        public static int BuildPreviewPath(
            Map map,
            Vector2 origin,
            Vector2 aim,
            List<Vector3> points,
            int maxBounces = MaxBounces)
        {
            points.Clear();
            if (map == null) return 0;

            float travel = Vector2.Distance(origin, aim);
            if (travel < 0.05f)
            {
                points.Add(ToOverlay(origin));
                points.Add(ToOverlay(aim));
                return 0;
            }

            Vector2 dir = (aim - origin) / travel;
            Vector2 pos = origin;
            points.Add(ToOverlay(pos));

            int bounces = 0;
            int ignoreThingId = -1;
            float traveledSinceBounce = 0f;

            for (int step = 0; step < PreviewMaxSteps && travel > MinRemainingDistance; step++)
            {
                float stepLen = Mathf.Min(PreviewStep, travel);
                Vector2 next = pos + dir * stepLen;

                IntVec3 cell = new IntVec3(Mathf.FloorToInt(next.x), 0, Mathf.FloorToInt(next.y));
                if (!cell.InBounds(map))
                {
                    Vector2 clamped = ClampToMap(pos, next, map);
                    points.Add(ToOverlay(clamped));
                    break;
                }

                Thing wall = FindBlockingWall(map, cell, pos, next, ignoreThingId, traveledSinceBounce);
                if (wall != null)
                {
                    Vector3 last3 = new Vector3(pos.x, OverlayAltitude, pos.y);
                    Vector3 cur3 = new Vector3(next.x, OverlayAltitude, next.y);
                    if (bounces < maxBounces && TryComputeBounce(dir, last3, cur3, wall, out BounceResult bounce))
                    {
                        points.Add(ToOverlay(new Vector2(bounce.HitXZ.x, bounce.HitXZ.y)));
                        pos = new Vector2(bounce.Unstuck.x, bounce.Unstuck.z);
                        points.Add(ToOverlay(pos));
                        dir = bounce.Outbound;
                        travel = Mathf.Max(MinRemainingDistance, (travel - stepLen) * TravelRetainAfterBounce(bounces));
                        bounces++;
                        ignoreThingId = wall.thingIDNumber;
                        traveledSinceBounce = 0f;
                        continue;
                    }

                    // Impact — end at hit.
                    points.Add(ToOverlay(next));
                    break;
                }

                pos = next;
                travel -= stepLen;
                traveledSinceBounce += stepLen;
                if (step % 2 == 0)
                    points.Add(ToOverlay(pos));
            }

            if (points.Count == 1)
                points.Add(ToOverlay(pos));
            else if ((points[points.Count - 1] - ToOverlay(pos)).sqrMagnitude > 0.01f)
                points.Add(ToOverlay(pos));

            return bounces;
        }

        /// <summary>
        /// Walk cells along from→to and return the first solid wall whose bounds the segment hits.
        /// </summary>
        public static Thing? FindWallAlongSegment(
            Map map,
            Vector3 from,
            Vector3 to,
            int ignoreThingId = -1,
            int ignoreUntilTick = 0)
        {
            if (map == null) return null;

            Vector2 a = new Vector2(from.x, from.z);
            Vector2 b = new Vector2(to.x, to.z);
            IntVec3 c0 = from.ToIntVec3();
            IntVec3 c1 = to.ToIntVec3();

            // Include endpoints + LoS cells so fat steps still see thin walls.
            var cells = GenSight.PointsOnLineOfSight(c0, c1);
            Thing? best = null;
            float bestDist = float.MaxValue;

            void ConsiderCell(IntVec3 cell)
            {
                if (!cell.InBounds(map)) return;
                var things = map.thingGrid.ThingsListAtFast(cell);
                if (things == null) return;
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (!IsSolidWall(t)) continue;
                    if (t.thingIDNumber == ignoreThingId
                        && Find.TickManager.TicksGame < ignoreUntilTick)
                        continue;
                    if (!TryGetWallBounds(t, out Bounds bounds)) continue;
                    if (!SegmentHitsBoundsXZ(a, b, bounds)) continue;

                    float d = (t.TrueCenter() - from).MagnitudeHorizontalSquared();
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = t;
                    }
                }
            }

            ConsiderCell(c0);
            ConsiderCell(c1);
            foreach (IntVec3 cell in cells)
                ConsiderCell(cell);

            return best;
        }

        private static Thing? FindBlockingWall(
            Map map,
            IntVec3 cell,
            Vector2 from,
            Vector2 to,
            int ignoreThingId,
            float traveledSinceBounce)
        {
            // Short grace distance after a bounce (~same idea as tick grace).
            const float graceDist = 0.55f;
            var things = map.thingGrid.ThingsListAtFast(cell);
            if (things == null) return null;

            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (!IsSolidWall(t)) continue;
                if (t.thingIDNumber == ignoreThingId && traveledSinceBounce < graceDist)
                    continue;
                if (!TryGetWallBounds(t, out Bounds b)) continue;

                if (SegmentHitsBoundsXZ(from, to, b))
                    return t;
            }

            return null;
        }

        private static bool SegmentHitsBoundsXZ(Vector2 from, Vector2 to, Bounds b)
        {
            // Quick reject / accept using slab on XZ.
            float minX = b.min.x - BoundsEps, maxX = b.max.x + BoundsEps;
            float minZ = b.min.z - BoundsEps, maxZ = b.max.z + BoundsEps;

            float dx = to.x - from.x;
            float dz = to.y - from.y;
            float t0 = 0f, t1 = 1f;

            if (!ClipAxis(from.x, dx, minX, maxX, ref t0, ref t1)) return false;
            if (!ClipAxis(from.y, dz, minZ, maxZ, ref t0, ref t1)) return false;
            return t1 >= t0;
        }

        private static bool ClipAxis(float p, float d, float min, float max, ref float t0, ref float t1)
        {
            if (Mathf.Abs(d) < 1e-8f)
                return p >= min && p <= max;

            float inv = 1f / d;
            float tNear = (min - p) * inv;
            float tFar = (max - p) * inv;
            if (tNear > tFar)
            {
                float tmp = tNear;
                tNear = tFar;
                tFar = tmp;
            }
            if (tNear > t0) t0 = tNear;
            if (tFar < t1) t1 = tFar;
            return t1 >= t0;
        }

        private static Vector3 ToOverlay(Vector2 xz) =>
            new Vector3(xz.x, OverlayAltitude, xz.y);

        public static bool IsOutsideWallXZ(Vector3 p, Bounds b)
        {
            const float BoundsEps = 0.02f;
            return p.x < b.min.x - BoundsEps
                || p.x > b.max.x + BoundsEps
                || p.z < b.min.z - BoundsEps
                || p.z > b.max.z + BoundsEps;
        }

        private static bool IsOutsideXZ(Vector3 p, Bounds b) => IsOutsideWallXZ(p, b);

        private static Vector3 ResolveApproachPoint(Vector3 last, Vector3 cur, Vector2 inbound, Bounds b)
        {
            if (IsOutsideXZ(last, b))
                return last;

            Vector3 p = cur;
            for (int i = 0; i < 8; i++)
            {
                p.x -= inbound.x * 0.35f;
                p.z -= inbound.y * 0.35f;
                if (IsOutsideXZ(p, b))
                    return p;
            }

            return new Vector3(
                cur.x - inbound.x * 1.5f,
                cur.y,
                cur.z - inbound.y * 1.5f);
        }

        private static bool TryResolveBounceNormal(
            Vector2 inbound,
            Vector3 approach,
            Vector3 cur,
            Bounds b,
            out Vector2 normal,
            out Vector2 hitXZ)
        {
            normal = Vector2.zero;
            hitXZ = new Vector2(approach.x, approach.z);

            if (TryRayAabbEntryFace(approach, cur, b, out normal, out hitXZ))
                return true;

            float cx = Mathf.Clamp(approach.x, b.min.x, b.max.x);
            float cz = Mathf.Clamp(approach.z, b.min.z, b.max.z);
            Vector2 delta = new Vector2(approach.x - cx, approach.z - cz);

            if (delta.sqrMagnitude > 1e-8f)
            {
                if (Mathf.Abs(delta.x) >= Mathf.Abs(delta.y))
                    normal = new Vector2(Mathf.Sign(delta.x), 0f);
                else
                    normal = new Vector2(0f, Mathf.Sign(delta.y));
                hitXZ = new Vector2(cx, cz);
                return true;
            }

            float penL = approach.x - b.min.x;
            float penR = b.max.x - approach.x;
            float penS = approach.z - b.min.z;
            float penN = b.max.z - approach.z;
            float minPen = Mathf.Min(Mathf.Min(penL, penR), Mathf.Min(penS, penN));
            if (minPen == penL) { normal = new Vector2(-1f, 0f); hitXZ = new Vector2(b.min.x, approach.z); }
            else if (minPen == penR) { normal = new Vector2(1f, 0f); hitXZ = new Vector2(b.max.x, approach.z); }
            else if (minPen == penS) { normal = new Vector2(0f, -1f); hitXZ = new Vector2(approach.x, b.min.z); }
            else { normal = new Vector2(0f, 1f); hitXZ = new Vector2(approach.x, b.max.z); }

            if (Vector2.Dot(normal, inbound) > 0f)
            {
                Vector2 alt = -normal;
                if (Vector2.Dot(alt, inbound) < 0f)
                    normal = alt;
            }
            return true;
        }

        private static bool TryRayAabbEntryFace(
            Vector3 from,
            Vector3 to,
            Bounds b,
            out Vector2 normal,
            out Vector2 hitXZ)
        {
            normal = Vector2.zero;
            hitXZ = default;

            float dx = to.x - from.x;
            float dz = to.z - from.z;
            float bestT = float.MaxValue;
            Vector2 bestN = Vector2.zero;
            Vector2 bestHit = default;
            bool found = false;

            void Consider(float t, float hx, float hz, Vector2 n, float minA, float maxA, float axisVal)
            {
                if (t < -0.05f || t > 1.05f) return;
                if (axisVal < minA - BoundsEps || axisVal > maxA + BoundsEps) return;
                if (t >= bestT) return;
                bestT = t;
                bestN = n;
                bestHit = new Vector2(hx, hz);
                found = true;
            }

            if (Mathf.Abs(dx) > 1e-8f)
            {
                float tL = (b.min.x - from.x) / dx;
                float zL = from.z + tL * dz;
                Consider(tL, b.min.x, zL, new Vector2(-1f, 0f), b.min.z, b.max.z, zL);

                float tR = (b.max.x - from.x) / dx;
                float zR = from.z + tR * dz;
                Consider(tR, b.max.x, zR, new Vector2(1f, 0f), b.min.z, b.max.z, zR);
            }

            if (Mathf.Abs(dz) > 1e-8f)
            {
                float tS = (b.min.z - from.z) / dz;
                float xS = from.x + tS * dx;
                Consider(tS, xS, b.min.z, new Vector2(0f, -1f), b.min.x, b.max.x, xS);

                float tN = (b.max.z - from.z) / dz;
                float xN = from.x + tN * dx;
                Consider(tN, xN, b.max.z, new Vector2(0f, 1f), b.min.x, b.max.x, xN);
            }

            if (!found) return false;

            normal = bestN;
            hitXZ = bestHit;

            Vector2 step = new Vector2(dx, dz);
            if (step.sqrMagnitude > 1e-8f && Vector2.Dot(step.normalized, normal) > 0.15f)
                normal = -normal;

            return true;
        }

        private static bool TryBuildUnstuckPose(
            Vector2 hitXZ,
            float heightY,
            Vector2 normal,
            Vector2 outbound,
            Thing wall,
            Bounds b,
            out Vector3 unstuck)
        {
            unstuck = new Vector3(hitXZ.x, Mathf.Max(0.05f, heightY), hitXZ.y);

            float[] pushes = { UnstickPush, UnstickPush + 0.25f, UnstickPush + 0.55f, UnstickPush + 0.9f };
            for (int i = 0; i < pushes.Length; i++)
            {
                Vector3 candidate = unstuck;
                candidate.x = hitXZ.x + normal.x * pushes[i] + outbound.x * OutboundNudge;
                candidate.z = hitXZ.y + normal.y * pushes[i] + outbound.y * OutboundNudge;

                if (StillInsideWall(candidate, wall, b))
                    continue;

                Vector3 probe = candidate;
                probe.x += outbound.x * ClearanceProbe;
                probe.z += outbound.y * ClearanceProbe;
                if (StillInsideWall(probe, wall, b))
                    continue;

                unstuck = candidate;
                return true;
            }

            return false;
        }

        private static bool StillInsideWall(Vector3 pos, Thing wall, Bounds b)
        {
            Bounds test = b;
            test.Expand(-0.04f);
            if (test.Contains(pos)) return true;
            return pos.ToIntVec3() == wall.Position && !IsOutsideXZ(pos, b);
        }
    }
}
