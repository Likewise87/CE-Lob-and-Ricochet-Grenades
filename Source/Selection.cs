using Verse;

namespace CE_LaR_Grenades
{
    public static class CE_LaR_GrenadesLog
    {
        public static void L(string msg)
        {
            if (CE_LaR_GrenadesMod.Settings?.enableLogs ?? false)
                Log.Message(msg);
        }
    }
}
