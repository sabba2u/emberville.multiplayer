using HarmonyLib;
using ImGuiNET;
using Vector2 = ImGuiNET.Vector2;
using Vector4 = ImGuiNET.Vector4;

namespace EmbervilleGhost;

/// <summary>
/// /nodeath: for testing, our character takes no damage at all (no health lost, no knockback, no hurt) and can't
/// die. Only our own character; off again with /nodeath. Not saved: every game starts with it off.
/// </summary>
internal static class NoDeath
{
    public static bool On { get; private set; }

    public static void Toggle()
    {
        On = !On;
        NetLog.Warn(On ? "No death: ON. You take no damage (/nodeath again to turn it off)" : "No death: off", toast: true);
    }

    static bool Mine(PlayerMaster p) => On && p && Singleton<GameManager>.HasMain && p == Singleton<GameManager>.main.player;

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.ApplyDamage))]
    static class NoDamage
    {
        static bool Prefix(PlayerMaster __instance) => !Mine(__instance);
    }

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.Die))]
    static class NoDying
    {
        static bool Prefix(PlayerMaster __instance)
        {
            if (!Mine(__instance)) return true;
            __instance.ResetToFullHealth();
            return false;
        }
    }

    /// <summary>From Hud.Draw: a reminder that it's on.</summary>
    public static void Draw()
    {
        if (!On && !OneHitKill.On) return;
        if (!On) { DrawText("ONE-HIT KILL"); return; }
        DrawText(OneHitKill.On ? "NO DEATH + ONE-HIT KILL" : "NO DEATH");
    }

    static void DrawText(string text)
    {
        var vp = ImGui.GetMainViewport();
        var dl = ImGui.GetForegroundDrawList();
        var size = ImGui.CalcTextSize(text);
        float x = vp.WorkPos.x + vp.WorkSize.x / 2f - size.x / 2f, y = vp.WorkPos.y + vp.WorkSize.y - 34f;
        dl.AddRectFilled(new Vector2(x - 6, y - 2), new Vector2(x + size.x + 6, y + size.y + 2), ImGui.ColorConvertFloat4ToU32(new Vector4(0.4f, 0f, 0f, 0.6f)), 4f);
        dl.AddText(new Vector2(x, y), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.4f, 0.4f, 1f)), text);
    }
}

/// <summary>/onehitkill: for testing, every hit of ours on a creature does 500 damage. Not saved.</summary>
internal static class OneHitKill
{
    public const int Damage = 500;
    public static bool On { get; private set; }

    public static void Toggle()
    {
        On = !On;
        NetLog.Warn(On ? $"One-hit kill: ON. Your hits do {Damage} damage (/onehitkill again to turn it off)" : "One-hit kill: off", toast: true);
    }

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.ApplyDamage))]
    static class Big
    {
        [HarmonyPriority(Priority.First)]
        static void Prefix(ref DamageData damageData)
        {
            if (!On || !(damageData.Aggressor is PlayerMaster p) || !Singleton<GameManager>.HasMain || p != Singleton<GameManager>.main.player) return;
            if (damageData.Result == DamageResult.Parry) return;   // a dodge stays a dodge
            damageData.Amount = Damage;
        }
    }
}
