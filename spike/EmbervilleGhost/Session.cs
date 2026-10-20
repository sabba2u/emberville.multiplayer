using System;
using BepInEx.Configuration;
using ImGuiNET;
using LiteNetLib;
using LiteNetLib.Utils;

namespace EmbervilleGhost;

/// <summary>
/// The host's rules for the session, picked in the host window (F8) and sent to everyone (SessionSettings: on joining
/// and whenever the host changes them in the Insert menu). Defaults come from the config file.
///  - Shared XP: everyone in the area gets the XP of a kill (EnemySync.ShareKillXp). Default on.
///  - Instanced loot: weapons, armour and gear that drop are there once for EVERY player (each can take their own copy,
///    once); materials, consumables, placeables, key items stay shared, first come first served (DropSync). Default off.
///  - PvP: players can hurt each other (PlayerHits). Default off.
///  - Share buffs: a buff a player casts on themselves (Swift Foot...) also lands on teammates near them (TeamBuffs).
///    Default on.
/// </summary>
internal static class Session
{
    public static Plugin Plugin;
    public static ConfigEntry<bool> DefaultSharedXp, DefaultInstancedLoot, DefaultPvp, DefaultShareBuffs;

    public static bool SharedXp = true;
    public static bool InstancedLoot;
    /// <summary>Players can hurt each other (PlayerHits). Default off.</summary>
    public static bool Pvp;
    /// <summary>Self-buffs also land on teammates nearby (TeamBuffs). Default on.</summary>
    public static bool ShareBuffs = true;

    public static void Bind(ConfigFile config)
    {
        DefaultSharedXp = config.Bind("Session", "SharedXp", true, "Host default: everyone in the area gets the XP of a kill");
        DefaultInstancedLoot = config.Bind("Session", "InstancedLoot", false, "Host default: weapons/armour/gear drops are there once for every player (materials stay shared)");
        DefaultPvp = config.Bind("Session", "Pvp", false, "Host default: players can hurt each other");
        DefaultShareBuffs = config.Bind("Session", "ShareBuffs", true, "Host default: a buff a player casts on themselves also lands on teammates near them");
        ShareBuffs = DefaultShareBuffs.Value;
        SharedXp = DefaultSharedXp.Value;
        InstancedLoot = DefaultInstancedLoot.Value;
        Pvp = DefaultPvp.Value;
    }

    /// <summary>Weapons, armour, gear (not materials, consumables, placeables, key items): one copy per player when instanced.</summary>
    public static bool IsInstanced(ItemObject item) =>
        InstancedLoot && item?.data != null && item.data.itemBehaviour != ItemBehaviour.KeyItem &&
        (item.data.itemCategory & (ItemCategory.Weapon | ItemCategory.Armor | ItemCategory.Gear)) != 0;

    static NetDataWriter Message()
    {
        var w = new NetDataWriter();
        w.Put((byte)MsgType.SessionSettings);
        w.Put(SharedXp);
        w.Put(InstancedLoot);
        w.Put(Pvp);
        w.Put(ShareBuffs);
        return w;
    }

    /// <summary>Host: tell one new player / everyone.</summary>
    public static void SendTo(int playerId) => Plugin.SendTo(playerId, Message());
    public static void Broadcast() { if (Plugin.IsHost) Plugin.SendToAllClients(Message(), DeliveryMethod.ReliableOrdered); }

    public static void OnSettings(NetDataReader r)
    {
        bool xp = r.GetBool(), loot = r.GetBool(), pvp = r.GetBool(), buffs = r.GetBool();
        if (xp != SharedXp || loot != InstancedLoot || pvp != Pvp || buffs != ShareBuffs)
            NetLog.Info($"Host's rules: shared XP {(xp ? "on" : "off")}, instanced loot {(loot ? "on" : "off")}, PvP {(pvp ? "ON" : "off")}, shared buffs {(buffs ? "on" : "off")}", toast: true);
        SharedXp = xp;
        InstancedLoot = loot;
        Pvp = pvp;
        ShareBuffs = buffs;
    }

    /// <summary>Insert menu: the host can change them during the session; clients see them.</summary>
    public static void DrawSettings()
    {
        bool host = Plugin.IsHost || !Plugin.Connected;
        if (!host) ImGui.BeginDisabled();
        bool xp = SharedXp, loot = InstancedLoot;
        if (ImGui.Checkbox("Shared XP (everyone in the area gets kill XP)", ref xp)) { SharedXp = xp; Broadcast(); }
        if (ImGui.Checkbox("Instanced loot (weapons/armour/gear: a copy for every player)", ref loot)) { InstancedLoot = loot; Broadcast(); }
        bool pvp = Pvp;
        if (ImGui.Checkbox("PvP (players can hurt each other)", ref pvp)) { Pvp = pvp; Broadcast(); }
        bool buffs = ShareBuffs;
        if (ImGui.Checkbox("Share buffs (self-buffs also land on teammates nearby)", ref buffs)) { ShareBuffs = buffs; Broadcast(); }
        if (!host) { ImGui.EndDisabled(); ImGui.SameLine(); ImGui.TextDisabled("(host's rules)"); }
    }
}

/// <summary>The window F8 opens: port, the session rules, and "Host now".</summary>
internal static class HostWindow
{
    public static Plugin Plugin;
    public static bool Visible { get; private set; }

    public static void Show()
    {
        Visible = true;
        Session.SharedXp = Session.DefaultSharedXp.Value;
        Session.InstancedLoot = Session.DefaultInstancedLoot.Value;
        Session.Pvp = Session.DefaultPvp.Value;
        Session.ShareBuffs = Session.DefaultShareBuffs.Value;
    }

    public static void Draw()
    {
        if (!Visible || Plugin == null) return;
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(new Vector2(vp.WorkPos.x + vp.WorkSize.x * 0.5f, vp.WorkPos.y + vp.WorkSize.y * 0.3f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(440, 0), ImGuiCond.Always);
        bool open = true;
        if (!ImGui.Begin("Host a game##host", ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        if (!LocalPlayer.Get())
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.35f, 1f), "Load your world first: players join the world you're in.");

        int port = Plugin.PortSetting.Value;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Port", ref port)) Plugin.PortSetting.Value = Math.Max(1, Math.Min(65535, port));
        NetInfo.DrawHostAddresses(Plugin);

        ImGui.Separator();
        ImGui.Checkbox("Shared XP", ref Session.SharedXp);
        ImGui.TextDisabled("    Everyone in the area gets the XP of a kill, not just the killer.");
        ImGui.Checkbox("Instanced loot", ref Session.InstancedLoot);
        ImGui.TextDisabled("    Weapons, armour and gear drop once for every player (each takes their own copy).");
        ImGui.TextDisabled("    Materials, consumables and key items stay shared: first come, first served.");
        ImGui.Checkbox("PvP", ref Session.Pvp);
        ImGui.TextDisabled("    Players can hurt (and kill) each other.");
        ImGui.Checkbox("Share buffs", ref Session.ShareBuffs);
        ImGui.TextDisabled($"    A buff you cast on yourself (Swift Foot...) also lands on teammates within {TeamBuffs.Range:0} m.");
        ImGui.Separator();

        if (ImGui.Button("HOST NOW", new Vector2(160, 0)))
        {
            Session.DefaultSharedXp.Value = Session.SharedXp;          // remembered for next time
            Session.DefaultInstancedLoot.Value = Session.InstancedLoot;
            Session.DefaultPvp.Value = Session.Pvp;
            Session.DefaultShareBuffs.Value = Session.ShareBuffs;
            Plugin.Host();
            Visible = false;
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(100, 0))) Visible = false;

        ImGui.End();
        if (!open) Visible = false;
    }
}
