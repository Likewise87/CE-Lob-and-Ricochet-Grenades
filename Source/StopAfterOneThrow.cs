using HarmonyLib;
using Verse;
using Verse.AI;
using CombatExtended;

namespace CE_LaR_Grenades
{
    /// <summary>
    /// After a successful grenade throw, end the current job so the pawn does not
    /// keep chucking grenades until the stack is empty (ported from CE StopAfterOneThrow).
    /// </summary>
    public static class StopAfterOneThrow
    {
        public static void Apply(Harmony h)
        {
            var m = AccessTools.Method(typeof(Verb_LaunchProjectileCE), nameof(Verb_LaunchProjectileCE.TryCastShot));
            if (m == null)
            {
                Log.Warning("[CE_LaR_Grenades] Missing TryCastShot for stop-after-one");
                return;
            }
            h.Patch(m, postfix: new HarmonyMethod(typeof(StopAfterOneThrow), nameof(TryCastShot_Postfix)));
        }

        public static void TryCastShot_Postfix(Verb_LaunchProjectileCE __instance, bool __result)
        {
            if (!__result) return;

            // Only hand grenades / one-use throws — not guns or launchers.
            var fqn = __instance.GetType().FullName;
            if (fqn != "CombatExtended.Verb_ShootCEOneUse" && fqn != "CombatExtended.Verb_ThrowGrenade")
                return;

            var pawn = __instance.CasterPawn;
            if (pawn == null) return;

            pawn.stances?.CancelBusyStanceSoft();

            try
            {
                pawn.jobs?.EndCurrentJob(JobCondition.Succeeded, true);
            }
            catch
            {
                pawn.jobs?.EndCurrentJob(JobCondition.Succeeded);
            }
        }
    }
}
