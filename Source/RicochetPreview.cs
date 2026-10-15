using HarmonyLib;
using Verse;
using UnityEngine;
using System.Collections.Generic;
using CombatExtended;
using RimWorld;

namespace CE_LaR_Grenades
{
    /// <summary>
    /// Aim previews: ricochet multi-bounce polyline, and lob ballistic arcs with
    /// clear (cyan) vs wall-clip (red) coloring.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class RicochetPreview
    {
        private static readonly List<Vector3> TmpPoints = new List<Vector3>(64);
        private static readonly List<bool> TmpClipFlags = new List<bool>(64);

        // MetaOverlay = solid aim overlays (Transparent + line tex still reads washed out).
        private static readonly Material MatRicoFirst = MaterialPool.MatFrom(
            GenDraw.LineTexPath, ShaderDatabase.MetaOverlay, new Color(0.85f, 0.95f, 1f, 1f));
        private static readonly Material MatRicoBounce = MaterialPool.MatFrom(
            GenDraw.LineTexPath, ShaderDatabase.MetaOverlay, new Color(1f, 0.85f, 0.35f, 1f));
        private static readonly Material MatLobClear = MaterialPool.MatFrom(
            GenDraw.LineTexPath, ShaderDatabase.MetaOverlay, new Color(0.25f, 0.95f, 1f, 1f));
        private static readonly Material MatLobClip = MaterialPool.MatFrom(
            GenDraw.LineTexPath, ShaderDatabase.MetaOverlay, new Color(1f, 0.15f, 0.1f, 1f));

        private const int LobSamples = 48;
        private const float DrawAltitude = 0.12f;
        private const float PreviewLineWidth = 0.45f;
        /// <summary>Skip clearance at the thrower so the pawn/cell never paints the arc red.</summary>
        private const float OriginGraceDist = 0.85f;

        private static float ClearancePad =>
            CE_LaR_GrenadesMod.Settings?.previewClearancePad ?? 0.15f;
        private static float CoverHeightMargin =>
            CE_LaR_GrenadesMod.Settings?.previewCoverMargin ?? 1.0f;
        private static float MountainCoverFloor =>
            CE_LaR_GrenadesMod.Settings?.previewMountainRockFloor ?? 2.75f;
        private static bool CheckRoofs =>
            CE_LaR_GrenadesMod.Settings?.previewCheckRoofs ?? true;
        private static bool CoverChecksEnabled =>
            CE_LaR_GrenadesMod.Settings?.PreviewCoverChecksEnabled ?? true;
        private static float RoofClipHeight =>
            CE_LaR_GrenadesMod.Settings?.previewRoofHeight ?? 2.00f;
        private static float MountainRoofClipHeight =>
            CE_LaR_GrenadesMod.Settings?.previewMountainRoofHeight ?? 2.00f;

        public static void Apply(Harmony h)
        {
            var m = AccessTools.Method(typeof(Verb), nameof(Verb.DrawHighlight), new[] { typeof(LocalTargetInfo) });
            if (m != null)
                h.Patch(m, postfix: new HarmonyMethod(typeof(RicochetPreview), nameof(DrawHighlight_Postfix)));
            else
                Log.Warning("[CE_LaR_Grenades] RicochetPreview: missing Verb.DrawHighlight");
        }

        public static void DrawHighlight_Postfix(Verb __instance, LocalTargetInfo target)
        {
            if (__instance == null || !target.IsValid) return;
            if (!CastPatches.IsGrenadeVerb(__instance)) return;

            var pawn = __instance.Caster as Pawn;
            if (pawn == null || !AngleToggle.IsCustomAngleMode(pawn)) return;

            var map = pawn.Map;
            if (map == null || !target.Cell.InBounds(map)) return;

            IntVec3 root = ResolveOrigin(pawn, __instance, target);
            float verbRange = __instance.verbProps?.range ?? 0f;
            float minRange = __instance.verbProps?.minRange ?? 0f;
            float dist = (target.Cell - root).LengthHorizontal;
            if (dist > verbRange || dist < minRange) return;

            Vector2 origin = new Vector2(root.x + 0.5f, root.z + 0.5f);
            Vector2 aim = new Vector2(target.Cell.x + 0.5f, target.Cell.z + 0.5f);

            if (AngleToggle.IsRicochetMode(pawn))
            {
                RicochetSim.BuildPreviewPath(map, origin, aim, TmpPoints, RicochetSim.MaxBounces);
                DrawRicochetPath(TmpPoints);
                return;
            }

            BuildLobArc(map, origin, aim, AngleToggle.GetAngleDegFor(pawn), TmpPoints, TmpClipFlags);
            DrawLobPath(TmpPoints, TmpClipFlags);
        }

        private static void BuildLobArc(
            Map map,
            Vector2 origin,
            Vector2 aim,
            int angleDeg,
            List<Vector3> points,
            List<bool> clipFlags)
        {
            points.Clear();
            clipFlags.Clear();

            float range = Vector2.Distance(origin, aim);
            if (range < 0.05f) return;

            float angleRad = CastPatches.EffectiveLaunchAngleRad(angleDeg, range);
            float shotHeight = CastPatches.DefaultShotHeight;
            float gravity = CastPatches.DefaultGravityPerWidth;
            float heightDelta = 0f - shotHeight;

            if (!CastPatches.TryComputeThrowSpeed(range, angleRad, heightDelta, gravity, out float speed))
            {
                points.Add(ToDraw(origin, shotHeight));
                points.Add(ToDraw(aim, 0.05f));
                clipFlags.Add(false);
                clipFlags.Add(false);
                return;
            }

            float k = AngleToggle.GetFlightScaleForDegrees(angleRad * Mathf.Rad2Deg);
            speed *= k;
            gravity *= k * k;

            Vector2 dir = (aim - origin) / range;
            bool hitClip = false;
            float prevH = shotHeight;

            for (int i = 0; i <= LobSamples; i++)
            {
                float t = i / (float)LobSamples;
                float d = range * t;
                float h = HeightAtDistance(d, shotHeight, angleRad, speed, gravity);
                if (h < 0f)
                {
                    // Last step down to the ground — still test roofs on this descent.
                    float groundH = 0f;
                    Vector2 ground = origin + dir * d;
                    IntVec3 groundCell = new IntVec3(Mathf.FloorToInt(ground.x), 0, Mathf.FloorToInt(ground.y));
                    if (d >= OriginGraceDist && groundCell.InBounds(map)
                        && CheckRoofs && CellRoofBlocks(map, groundCell, groundH, prevH))
                    {
                        hitClip = true;
                    }
                    points.Add(ToDraw(ground, 0.05f));
                    clipFlags.Add(hitClip);
                    break;
                }

                Vector2 xz = origin + dir * d;
                IntVec3 cell = new IntVec3(Mathf.FloorToInt(xz.x), 0, Mathf.FloorToInt(xz.y));
                bool clips = false;
                if (d >= OriginGraceDist && cell.InBounds(map))
                {
                    if (CoverChecksEnabled)
                    {
                        float coverH = CoverHeightAt(map, cell);
                        float need = coverH > 0.01f ? coverH * CoverHeightMargin + ClearancePad : 0f;
                        if (need > 0.01f && h <= need)
                        {
                            clips = true;
                            hitClip = true;
                        }
                    }

                    if (!clips && CheckRoofs && CellRoofBlocks(map, cell, h, prevH))
                    {
                        clips = true;
                        hitClip = true;
                    }
                }

                points.Add(ToDraw(xz, h));
                // Paint red from first clip onward, but keep sampling so the full arc stays visible.
                clipFlags.Add(clips || hitClip);
                prevH = h;
            }

            if (points.Count == 1)
            {
                points.Add(ToDraw(aim, 0.05f));
                clipFlags.Add(hitClip);
            }
        }

        private static float HeightAtDistance(float d, float shotHeight, float angleRad, float speed, float gravity)
        {
            float cos = Mathf.Cos(angleRad);
            if (Mathf.Abs(cos) < 1e-5f) return shotHeight;
            float tan = Mathf.Tan(angleRad);
            return shotHeight + d * tan - (gravity * d * d) / (2f * speed * speed * cos * cos);
        }

        private static float CoverHeightAt(Map map, IntVec3 cell)
        {
            // Do NOT use HighestCoverAt — CE counts trees/bushes as cover; grenades ignore them.
            float max = 0f;

            try
            {
                Thing cover = cell.GetCover(map);
                if (IsSolidPreviewObstacle(cover))
                    max = Mathf.Max(max, CE_Utility.GetBoundsFor(cover).size.y);
            }
            catch { /* ignore */ }

            var things = map.thingGrid.ThingsListAtFast(cell);
            if (things != null)
            {
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (!IsSolidPreviewObstacle(t)) continue;
                    if (!RicochetSim.IsSolidWall(t)) continue;
                    try
                    {
                        max = Mathf.Max(max, CE_Utility.GetBoundsFor(t).size.y);
                    }
                    catch { /* ignore */ }
                }
            }

            if (MountainCoverFloor > 0.01f && CellHasNaturalRock(map, cell))
                max = Mathf.Max(max, MountainCoverFloor);

            return max;
        }

        private static bool CellHasNaturalRock(Map map, IntVec3 cell)
        {
            var things = map.thingGrid.ThingsListAtFast(cell);
            if (things == null) return false;
            for (int i = 0; i < things.Count; i++)
            {
                if (IsNaturalRockMass(things[i])) return true;
            }
            return false;
        }

        /// <summary>Walls / buildings only — never pawns, trees, bushes, or other plants.</summary>
        private static bool IsSolidPreviewObstacle(Thing thing)
        {
            if (thing?.def == null) return false;
            if (thing is Pawn) return false;
            if (thing is Plant) return false;
            if (thing.def.category == ThingCategory.Plant) return false;
            return true;
        }

        private static bool IsNaturalRockMass(Thing thing)
        {
            var b = thing?.def?.building;
            if (b == null) return false;
            return b.isNaturalRock || b.isResourceRock;
        }

        /// <summary>
        /// Roof hit if this sample's height segment (prevH→h) overlaps the roof slab.
        /// Catches ascent into the underside, descent onto the top, and thin slabs
        /// skipped between samples. Slider sets slab underside; top from CE bounds.
        /// </summary>
        private static bool CellRoofBlocks(Map map, IntVec3 cell, float height, float prevHeight)
        {
            if (!cell.Roofed(map)) return false;
            try
            {
                RoofDef roof = cell.GetRoof(map);
                if (roof == null) return false;

                bool mountain = roof.isNatural || roof.isThickRoof;
                float underside = mountain ? MountainRoofClipHeight : RoofClipHeight;

                Bounds b = CE_Utility.GetBoundsFor(cell, roof);
                float slabLo = underside;
                float slabHi = Mathf.Max(b.max.y, underside + 0.20f);

                float y0 = Mathf.Min(prevHeight, height);
                float y1 = Mathf.Max(prevHeight, height);
                // Vertical segment overlaps [slabLo, slabHi]
                return y0 <= slabHi && y1 >= slabLo;
            }
            catch
            {
                float underside = RoofClipHeight;
                float y0 = Mathf.Min(prevHeight, height);
                float y1 = Mathf.Max(prevHeight, height);
                return y0 <= underside + 0.25f && y1 >= underside;
            }
        }

        /// <summary>CE-style height bias into Z so the arc reads in the iso view.</summary>
        private static Vector3 ToDraw(Vector2 xz, float height)
        {
            float visual = Mathf.Max(0f, height) * 0.84f;
            return new Vector3(xz.x, DrawAltitude + Mathf.Max(0f, height) * 0.02f, xz.y + visual);
        }

        private static IntVec3 ResolveOrigin(Pawn pawn, Verb verb, LocalTargetInfo target)
        {
            if (CastPatches.TryGetLastAimRoot(pawn, out IntVec3 aimRoot))
                return aimRoot;

            if (verb is Verb_LaunchProjectileCE ceVerb)
            {
                try
                {
                    ShootLine line = default;
                    Vector3 targetPos = default;
                    var m = AccessTools.Method(
                        typeof(Verb_LaunchProjectileCE),
                        "TryFindCEShootLineFromTo",
                        new[]
                        {
                            typeof(IntVec3), typeof(LocalTargetInfo),
                            typeof(ShootLine).MakeByRefType(), typeof(Vector3).MakeByRefType()
                        });
                    if (m != null)
                    {
                        object[] args = { pawn.Position, target, line, targetPos };
                        bool ok = (bool)m.Invoke(ceVerb, args);
                        if (ok)
                        {
                            line = (ShootLine)args[2];
                            return line.Source;
                        }
                    }
                }
                catch
                {
                    // fall through
                }
            }

            return pawn.Position;
        }

        private static void DrawRicochetPath(List<Vector3> points)
        {
            if (points == null || points.Count < 2) return;

            Vector2 prevDir = Vector2.zero;
            bool bounced = false;

            for (int i = 0; i < points.Count - 1; i++)
            {
                Vector3 a = points[i];
                Vector3 b = points[i + 1];
                Vector2 d = new Vector2(b.x - a.x, b.z - a.z);
                if (d.sqrMagnitude > 1e-6f)
                {
                    d.Normalize();
                    if (prevDir.sqrMagnitude > 0.5f && Vector2.Dot(prevDir, d) < 0.75f)
                        bounced = true;
                    prevDir = d;
                }

                GenDraw.DrawLineBetween(a, b, bounced ? MatRicoBounce : MatRicoFirst, PreviewLineWidth);
            }
        }

        private static void DrawLobPath(List<Vector3> points, List<bool> clipFlags)
        {
            if (points == null || points.Count < 2) return;

            for (int i = 0; i < points.Count - 1; i++)
            {
                bool clip = (i + 1 < clipFlags.Count && clipFlags[i + 1])
                    || (i < clipFlags.Count && clipFlags[i]);
                GenDraw.DrawLineBetween(
                    points[i], points[i + 1],
                    clip ? MatLobClip : MatLobClear,
                    PreviewLineWidth);
            }
        }
    }
}
