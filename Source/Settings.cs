using System;
using Verse;
using UnityEngine;
using CombatExtended;

namespace CE_LaR_Grenades
{
    public enum SliderFormat { Fixed0, Fixed1, Fixed2, Multiplier, Percent, PercentDecimal }

    public class Settings : ModSettings
    {
        public bool enableLogs = false;

        public float previewClearancePad = 0.15f;
        public float previewCoverMargin = 1.0f;
        public float previewMountainRockFloor = 2.75f;
        public bool previewCheckRoofs = true;
        public float previewRoofHeight = 2.00f;
        public float previewMountainRoofHeight = 2.00f;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref enableLogs, "enableLogs", false);
            Scribe_Values.Look(ref previewClearancePad, "previewClearancePad", 0.15f);
            Scribe_Values.Look(ref previewCoverMargin, "previewCoverMargin", 1.0f);
            Scribe_Values.Look(ref previewMountainRockFloor, "previewMountainFloor", 2.75f);
            Scribe_Values.Look(ref previewMountainRockFloor, "previewMountainRockFloor", previewMountainRockFloor);
            Scribe_Values.Look(ref previewCheckRoofs, "previewCheckRoofs", true);
            Scribe_Values.Look(ref previewRoofHeight, "previewRoofHeight", 2.00f);
            Scribe_Values.Look(ref previewMountainRoofHeight, "previewMountainRoofHeight", 2.00f);
        }

        public bool PreviewCoverChecksEnabled =>
            previewCoverMargin > 0.001f || previewClearancePad > 0.001f;

        public void ResetToDefaults()
        {
            enableLogs = false;
            previewClearancePad = 0.15f;
            previewCoverMargin = 1.0f;
            previewMountainRockFloor = 2.75f;
            previewCheckRoofs = true;
            previewRoofHeight = 2.00f;
            previewMountainRoofHeight = 2.00f;
        }
    }

    public class CE_LaR_GrenadesMod : Mod
    {
        public static Settings Settings;

        private static readonly Color SectionHeaderColor = new Color(0.55f, 0.85f, 1f);
        private static readonly Color HintColor = new Color(0.55f, 0.55f, 0.55f, 1f);

        public CE_LaR_GrenadesMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<Settings>();
        }

        public override string SettingsCategory() => "CE Lob and Ricochet Grenades";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            // Persist via Mod.WriteSettings when the options dialog closes — not every frame.
            Listing_Standard list = new Listing_Standard();
            list.Begin(inRect);

            DrawResetButton(list, () =>
            {
                Settings.ResetToDefaults();
            });

            Hint(list,
                "You can fine-tune how the trajectory preview works — when the aim line turns red because the path looks blocked. The default is quite good and rather too conservative. If you find better settings that more reliably predict whether the grenade will pass, please let me know.");

            DrawSectionHeader(list, "Aim Line for Walls");
            Hint(list, "Trees and bushes are always ignored.");
            Hint(list, "Line turns red if the grenade is not high enough over a wall (walls are usually height 2.0).");
            list.Gap(4f);

            Settings.previewCoverMargin = LabeledSlider(list, "Wall height factor", Settings.previewCoverMargin, 0f, 1.30f,
                "Scales how tall walls count for the preview. 1.0 = normal. Higher = red sooner. 0 = ignore walls.",
                0.01f, SliderFormat.Fixed2, 1.0f);
            Settings.previewClearancePad = LabeledSlider(list, "Clearance padding", Settings.previewClearancePad, 0f, 0.80f,
                "Extra height required above the wall. Higher = red sooner. 0 = none.",
                0.01f, SliderFormat.Fixed2, 0.15f);
            Settings.previewMountainRockFloor = LabeledSlider(list, "Rock wall floor height", Settings.previewMountainRockFloor, 0f, 4.00f,
                "Minimum clearance for mountain/ore rock walls only (not wood walls, not roofs). 0 = off.",
                0.01f, SliderFormat.Fixed2, 2.75f);

            float wallNeed = CollisionVertical.WallCollisionHeight * Settings.previewCoverMargin + Settings.previewClearancePad;
            list.Gap(2f);
            Hint(list, Settings.PreviewCoverChecksEnabled
                ? $"A normal wall currently needs the arc above ~{wallNeed:F2}"
                : "Wall checks are OFF");

            DrawSectionHeader(list, "Aim Line for Roofs");
            Hint(list, "Raise these to clear roofs more often. Lower = red sooner. Checks roofs on the way up and when aiming onto a roofed cell. Grenades start ~1.0, peak ~3.5; CE roof underside is usually 2.0.");
            list.Gap(4f);

            DrawCheckbox(list, "Check roofs (indoors / caves)", ref Settings.previewCheckRoofs,
                "When enabled, the aim line checks roofs for clearance.", defaultValue: true);
            Settings.previewRoofHeight = LabeledSlider(list, "House roof clearance", Settings.previewRoofHeight, 1.00f, 5.00f,
                "Red when the arc crosses this roof band. Higher = more cyan. Try 3.6+ to clear peaks.",
                0.01f, SliderFormat.Fixed2, 2.00f);
            Settings.previewMountainRoofHeight = LabeledSlider(list, "Cave / mountain roof clearance", Settings.previewMountainRoofHeight, 1.00f, 5.00f,
                "Same for thick/natural roofs. Higher = more cyan.",
                0.01f, SliderFormat.Fixed2, 2.00f);

            list.Gap(6f);
            DrawSectionHeader(list, "Developer");
            DrawCheckbox(list, "Debug logs", ref Settings.enableLogs,
                "Writes throw-angle / ricochet / roof-drop diagnostics to Player.log. Leave off in normal play.", defaultValue: false);

            list.End();
        }

        private static void Hint(Listing_Standard list, string text)
        {
            GameFont oldFont = Text.Font;
            Color oldColor = GUI.color;
            Text.Font = GameFont.Tiny;
            GUI.color = HintColor;
            list.Label(text);
            Text.Font = oldFont;
            GUI.color = oldColor;
        }

        private static void DrawResetButton(Listing_Standard l, Action onReset)
        {
            if (onReset == null) return;
            l.Gap(4f);
            Rect row = l.GetRect(30f);
            float resetW = Mathf.Min(250f, row.width);
            if (resetW < 120f) resetW = Mathf.Max(100f, row.width * 0.35f);
            Rect resetRect = new Rect(row.x, row.y, resetW, row.height);
            Color prev = GUI.color;
            GUI.color = new Color(1f, 0.5f, 0.5f);
            if (Widgets.ButtonText(resetRect, "Restore defaults")) onReset();
            GUI.color = prev;
            l.Gap(6f);
        }

        private static void DrawSectionHeader(Listing_Standard l, string label)
        {
            l.Gap(8f);
            Text.Font = GameFont.Small;
            string colorHex = ColorUtility.ToHtmlStringRGBA(SectionHeaderColor);
            string styled = $"<size=15><b><color=#{colorHex}>{label}</color></b></size>";
            float h = Mathf.Max(22f, Text.CalcHeight(styled, l.ColumnWidth) + 2f);
            TextAnchor prev = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(l.GetRect(h), styled);
            Text.Anchor = prev;
            l.GapLine(4f);
            l.Gap(4f);
        }

        private static float LabeledSlider(Listing_Standard l, string label, float val, float min, float max, string tooltip = null, float step = 0.1f, SliderFormat format = SliderFormat.Fixed1, float? defaultValue = null)
        {
            l.Gap(2f);
            Rect r = l.GetRect(24f);
            if (!tooltip.NullOrEmpty()) TooltipHandler.TipRegion(r, TooltipWithDefault(tooltip, defaultValue, format));
            Widgets.Label(r.LeftPart(0.5f), $"{label}: {FormatValue(val, format).Colorize(Color.cyan)}");
            return Widgets.HorizontalSlider(r.RightPart(0.5f), val, min, max, false, null, null, null, step);
        }

        private static void DrawCheckbox(Listing_Standard l, string label, ref bool value, string tooltip = null, bool? defaultValue = null)
        {
            l.Gap(2f);
            Rect r = l.GetRect(24f);
            if (!tooltip.NullOrEmpty()) TooltipHandler.TipRegion(r, TooltipWithDefault(tooltip, defaultValue));
            Widgets.CheckboxLabeled(r, label, ref value);
        }

        private static string TooltipWithDefault(string tooltip, float? defaultValue, SliderFormat format = SliderFormat.Fixed1)
        {
            if (!defaultValue.HasValue) return tooltip;
            return AppendDefault(tooltip, FormatValue(defaultValue.Value, format));
        }

        private static string TooltipWithDefault(string tooltip, bool? defaultValue)
        {
            if (!defaultValue.HasValue) return tooltip;
            return AppendDefault(tooltip, defaultValue.Value ? "On" : "Off");
        }

        private static string AppendDefault(string tooltip, string def)
        {
            string line = $"Default: {def}";
            return tooltip.NullOrEmpty() ? line : tooltip + "\n" + line;
        }

        private static string FormatValue(float val, SliderFormat format) => format switch
        {
            SliderFormat.Fixed0 => val.ToString("F0"),
            SliderFormat.Fixed1 => val.ToString("F1"),
            SliderFormat.Fixed2 => val.ToString("F2"),
            SliderFormat.Multiplier => val.ToString("F2") + "x",
            SliderFormat.Percent => (val * 100f).ToString("F0") + " %",
            SliderFormat.PercentDecimal => (val * 100f).ToString("F1") + " %",
            _ => val.ToString()
        };
    }
}
