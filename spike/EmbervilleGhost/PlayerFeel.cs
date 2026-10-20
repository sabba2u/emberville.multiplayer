using System.Linq;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// What happens to another player, shown on their stand-in (a copy of the character with every game script stripped,
/// so it can't show any of it by itself):
///  - hits: PlayerMaster.ApplyDamage on the victim's own copy -> PlayerHurt (health lost, result) -> the others show
///    the game's own floating text over the stand-in (the number, "Blocked", "Dodged", "Critical") and flash it red;
///  - status effects: the victim's EffectsHandler starting / ending an effect (burning, poisoned, webbed, buffed...)
///    -> PlayerEffect -> the others show its name like the game does when it starts, and list what's on them under
///    their name tag. The effect's look on the character itself (child objects it switches on) already comes through
///    LookSync.
/// </summary>
[HarmonyPatch]
internal static class PlayerFeel
{
    public static Plugin Plugin;

    static bool Connected => Plugin != null && Plugin.Connected && Plugin.MyId >= 0 && Plugin.AnyoneElseHere;
    static bool Online => Plugin != null && Plugin.Connected && Plugin.MyId >= 0;

    static RemotePlayer Find(int id) => Plugin.Players.FirstOrDefault(p => p.Id == id);

    // ---- hits ----

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.ApplyDamage)), HarmonyPrefix]
    static void BeforeHurt(PlayerMaster __instance, out int __state) => __state = __instance.CurrentHealthPoints;

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.ApplyDamage)), HarmonyPostfix]
    static void AfterHurt(PlayerMaster __instance, DamageData damageData, int __state)
    {
        if (!Connected || !ReferenceEquals(__instance, LocalPlayer.Get())) return;
        int lost = __state - __instance.CurrentHealthPoints;
        var result = damageData.Result;
        if (lost <= 0 && result != DamageResult.Block && result != DamageResult.Parry && result != DamageResult.Critical) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.PlayerHurt);
        w.Put(System.Math.Max(lost, 0));
        w.Put((byte)result);
        Plugin.SendToPlace(w, DeliveryMethod.ReliableUnordered);
    }

    /// <summary>Healing (eating, potions, regen skills: PlayerMaster.RestoreHealth): the green "+6" for the others too.</summary>
    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.RestoreHealth)), HarmonyPrefix]
    static void BeforeHeal(PlayerMaster __instance, out int __state) => __state = __instance.CurrentHealthPoints;

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.RestoreHealth)), HarmonyPostfix]
    static void AfterHeal(PlayerMaster __instance, bool displayHealthFullNotification, int __state)
    {
        int healed = __instance.CurrentHealthPoints - __state;
        if (healed <= 0 || !displayHealthFullNotification || !Connected || !ReferenceEquals(__instance, LocalPlayer.Get())) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.PlayerHurt);
        w.Put(healed);
        w.Put((byte)DamageResult.Heal);
        Plugin.SendToPlace(w, DeliveryMethod.ReliableUnordered);
    }

    /// <summary>Kymia refills with feedback (PlayerMaster.RestoreKymia): the game shows the same green "+N" as healing.</summary>
    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.RestoreKymia)), HarmonyPrefix]
    static void BeforeKymia(PlayerMaster __instance, out float __state) => __state = __instance.CurrentKymiaPoints;

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.RestoreKymia)), HarmonyPostfix]
    static void AfterKymia(PlayerMaster __instance, bool showFeedback, float __state)
    {
        int gained = UnityEngine.Mathf.RoundToInt(__instance.CurrentKymiaPoints - __state);
        if (gained <= 0 || !showFeedback || !Connected || !ReferenceEquals(__instance, LocalPlayer.Get())) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.PlayerHurt);
        w.Put(gained);
        w.Put((byte)DamageResult.Heal);
        Plugin.SendToPlace(w, DeliveryMethod.ReliableUnordered);
    }

    public static void OnHurt(NetDataReader r, int from)
    {
        int lost = r.GetInt();
        var result = (DamageResult)r.GetByte();
        var avatar = Find(from)?.Avatar;
        if (avatar == null || !avatar.Visible) return;
        var at = avatar.Root.transform.position;
        if (result == DamageResult.Heal) { Bus<UIDamageEvent>.Invoke(new UIDamageEvent(lost, "", at, DamageResult.Heal)); return; }
        // As PlayerMaster.ApplyDamage shows it on the victim's own screen.
        if (result == DamageResult.Parry) { Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, "Dodged", at, DamageResult.Parry)); return; }
        if (result == DamageResult.Block) Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, "Blocked", at, DamageResult.Block));
        if (result == DamageResult.Critical) Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, "Critical", at, DamageResult.Critical));
        if (lost > 0)
        {
            Bus<UIDamageEvent>.Invoke(new UIDamageEvent(lost, "", at, DamageResult.Success));
            if (result != DamageResult.Effect) avatar.Flash();   // damage-over-time ticks don't flinch
        }
    }

    // ---- status effects ----

    [HarmonyPatch(typeof(EffectsHandler), nameof(EffectsHandler.CallEvent)), HarmonyPostfix]
    static void AfterEffectEvent(EffectsHandler __instance, Effect effect, string eventName)
    {
        bool on = eventName == "Start";
        if (!on && eventName != "End") return;
        var me = LocalPlayer.Get();
        if (!Online || !me || !ReferenceEquals(__instance.owner, me) || effect == null) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.PlayerEffect);
        w.Put(on);
        w.Put(effect.GetName() ?? effect.Type.ToString());
        w.Put(effect.Name ?? "");
        Plugin.SendToAll(w, DeliveryMethod.ReliableOrdered);   // the end must reach anyone holding a shared copy
    }

    public static void OnEffect(NetDataReader r, int from)
    {
        bool on = r.GetBool();
        string name = r.GetString();
        string rawName = r.GetString();
        if (!on) TeamBuffs.CasterLost(from, rawName);   // a buff they shared with us ends with theirs
        var p = Find(from);
        if (p == null || string.IsNullOrEmpty(name)) return;
        if (!on) { p.Effects.Remove(name); return; }
        if (!p.Effects.Contains(name)) p.Effects.Add(name);
        if (p.Avatar != null && p.Avatar.Visible)
            Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, name, p.Avatar.Root.transform.position, DamageResult.None));
    }
}
