using HarmonyLib;
using Verse;

namespace CE_LaR_Grenades
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            try
            {
                var h = new Harmony("CE_LaR_Grenades");
                AngleToggle.Apply(h);
                CastPatches.Apply(h);
                StopAfterOneThrow.Apply(h);
                LobFlightFx.Apply(h);
                GrenadeRicochet.Apply(h);
                GrenadeRoofDrop.Apply(h);
                RicochetPreview.Apply(h);

                Log.Message("[CE_LaR_Grenades] init (angles, lean, ballistic speed, stop-after-one, apex tick warp, ricochet×3, roof-drop, aim preview)");
            }
            catch (System.Exception e)
            {
                Log.Error("Error in static constructor of CE_LaR_Grenades.Bootstrap: " + e);
            }
        }
    }
}
