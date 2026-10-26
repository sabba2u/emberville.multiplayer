using System;
using Cysharp.Threading.Tasks;
using ImGuiNET;

namespace EmbervilleGhost;

/// <summary>
/// The Insert menu: connection controls, who's where, and the message log. Drawn by Hud while the menu is open,
/// on Unity's main thread.
/// </summary>
internal static class DebugPanel
{
    static bool autoScroll = true;

    public static void Draw()
    {
        var plugin = Hud.Plugin;
        ImGui.SetNextWindowPos(new Vector2(60, 90), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(560, 420), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin($"{Plugin.Title}###menu"))
        {
            ImGui.End();
            return;
        }

        if (ImGui.BeginTabBar("menuTabs"))
        {
            if (ImGui.BeginTabItem("Online / Hosting")) { DrawOnline(plugin); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem($"Who's connected ({plugin.PlayerCount})###who")) { DrawPlayers(plugin); ImGui.EndTabItem(); }
            if (GreyTab("Pets", Integrations.HasPets)) { Integrations.DrawPets(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Font Options")) { StoryText.DrawMenu(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("View")) { TiltView.DrawMenu(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Mouse Options"))
            {
                MouseWalk.DrawMenu();
                ImGui.EndTabItem();
            }
            if (GreyTab("Better Combat", Integrations.HasCombat)) { Integrations.DrawCombat(); ImGui.EndTabItem(); }
            if (GreyTab("Better Looting", Integrations.HasLooting)) { Integrations.DrawLooting(); ImGui.EndTabItem(); }
            if (GreyTab("NPCs & Quests", Integrations.HasNpcs)) { Integrations.DrawNpcs(); ImGui.EndTabItem(); }
            if (GreyTab("Weather", Integrations.HasWeather)) { Integrations.DrawWeather(); ImGui.EndTabItem(); }
            if (BuildMode.Enabled && ImGui.BeginTabItem("Construction")) { DrawConstruction(plugin); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Dungeon")) { Dungeon.DrawMenu(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Under the mouse")) { DebugWalls.DrawMenu(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("UI Inspector")) { UiInspector.DrawMenu(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Log")) { DrawLog(plugin); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }

        ImGui.End();
    }


    // ---- the tabs ----

    /// <summary>A tab whose mod may not be installed: greyed then (it still opens, to say what it needs).</summary>
    static bool GreyTab(string label, bool installed)
    {
        if (!installed) ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
        bool open = ImGui.BeginTabItem(label);
        if (!installed) ImGui.PopStyleColor();
        return open;
    }

    static void DrawOnline(Plugin plugin)
    {
        ImGui.Text($"Status: {plugin.Status}");
        if (plugin.IsClient) ImGui.Text($"Ping to host: {plugin.Remote.Ping} ms");
        ImGui.Separator();
        string address = plugin.AddressSetting.Value;
        if (ImGui.InputTextWithHint("Address", "127.0.0.1 (this PC)", ref address, 64)) plugin.AddressSetting.Value = address;
        ImGui.SameLine();
        if (ImGui.SmallButton("Paste address")) NetInfo.PasteAddress(plugin);
        if (plugin.IsHost) NetInfo.DrawHostAddresses(plugin);
        int port = plugin.PortSetting.Value;
        if (ImGui.InputInt("Port", ref port)) plugin.PortSetting.Value = Math.Max(1, Math.Min(65535, port));

        if (SteamBoot.Ready)
        {
            if (ImGui.Button("Host via Steam")) plugin.HostSteam();
            ImGui.SameLine();
            if (ImGui.Button("Invite friends (Steam)")) SteamLobby.OpenInviteDialog();
            ImGui.SameLine();
            ImGui.TextDisabled(SteamLobby.InLobby ? "(in a Steam lobby)" : "(friends join from their Steam friends list)");
        }
        else ImGui.TextDisabled("Steam isn't running: IP joining only");
        if (ImGui.Button("Host LAN (F9)")) HostWindow.Show();
        ImGui.SameLine();
        if (ImGui.Button("Join LAN (F10)")) plugin.Join();
        ImGui.SameLine();
        if (ImGui.Button("Disconnect (F10)")) plugin.Stop();
        Session.DrawSettings();
        TeamHud.DrawSettings();
    }

    static void DrawPlayers(Plugin plugin)
    {
        var me = LocalPlayer.Get();
        ImGui.Text($"Me: P{plugin.MyId + 1}   {LocalPlayer.Scene} / {LocalPlayer.Area}" + (me ? $"   at {me.transform.position}" : "   (no player)"));
        if (!plugin.Connected) { ImGui.TextDisabled("Not connected: host or join on the Online / Hosting tab."); return; }
        ImGui.Separator();
        foreach (var p in plugin.Players)
        {
            ImGui.Text($"{p.Name}: {p.Scene} / {p.Area}   {p.Health}/{p.MaxHealth} hp" + (p.Avatar != null && p.Avatar.Visible ? "   (visible)" : ""));
            ImGui.SameLine();
            bool sameScene = p.HaveState && p.Scene == LocalPlayer.Scene;
            if (!sameScene || !me) ImGui.BeginDisabled();
            if (ImGui.SmallButton($"Teleport to##tp{p.Id}")) TeleportTo(p);
            if (!sameScene || !me) ImGui.EndDisabled();
            if (p.HaveState && !sameScene) { ImGui.SameLine(); ImGui.TextDisabled("(another scene)"); }
        }
        ImGui.TextDisabled("O opens Online Players: trade, mute, kick.");
    }

    static void DrawLog(Plugin plugin)
    {
        ImGui.Text($"Copy #{SaveIsolation.CopyNumber}   Data: {SaveIsolation.DataPath}");
        if (ImGui.Button("Test toast")) NetLog.Info("Hello from the menu", toast: true);
        ImGui.SameLine();
        ImGui.Checkbox("Log every hit (recon)", ref HitProbe.Enabled);
        ImGui.SameLine();
        if (ImGui.Button(SeedTest.Running ? "Map seed test (running...)" : "Map seed test")) SeedTest.Run().Forget();
        if (SeedTest.Result.Length > 0) ImGui.TextWrapped(SeedTest.Result);
        if (plugin.Connected)
        {
            int bad = DesyncCheck.MismatchCount;
            if (bad > 0) ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), $"Mismatches right now: {bad} (details in the log)");
            else if (plugin.IOwnHere) ImGui.TextDisabled("Mismatch checker: everyone here agrees with your game");
            else ImGui.TextDisabled($"Mismatch checker: {plugin.NameOf(plugin.OwnerHere)} compares your game with theirs");
            if (Diag.FilePath != null) ImGui.TextDisabled($"Session log: {Diag.FilePath}");
        }
        string perf = $"Player packet {plugin.StateBytes} bytes.  Mod ms/frame:";
        foreach (var pair in Perf.Ms) perf += $"  {pair.Key} {pair.Value:0.0}";
        ImGui.TextDisabled(perf);
        ImGui.Separator();

        if (ImGui.Button("Clear log")) NetLog.Entries.Clear();
        ImGui.SameLine();
        ImGui.Checkbox("Auto-scroll", ref autoScroll);
        ImGui.BeginChild("log", new Vector2(0, 0), ImGuiChildFlags.Border);
        foreach (var e in NetLog.Entries)
            ImGui.TextColored(ColorFor(e.Kind), $"{e.Time:HH:mm:ss}  {e.Text}");
        if (autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 5f)
            ImGui.SetScrollHereY(1f);
        ImGui.EndChild();
    }

    /// <summary>
    /// Next to a teammate (same scene), with the game's own teleport: fade out, switch to their GameArea if it's another
    /// one, place us, camera, fade in (GameManager.TeleportPlayerFade).
    /// </summary>
    /// <summary>
    /// The estate (your farm in Town: plots, build mode, repairing buildings) is behind two story flags the demo
    /// never sets (BoolID.PlaceablesActive / RepairablesActive). Turning them on opens build mode on the estate.
    /// Connected: the host decides, and everyone gets the flags (EstateUnlock). (Chat: /estate.)
    /// </summary>
    static void DrawConstruction(Plugin plugin)
    {
        if (!GameData.HasWorldData) { ImGui.TextDisabled("Load a world first."); return; }
        bool guest = plugin.Connected && !plugin.IsHost;
        bool on = GameData.gameBools.IsTrue(BoolID.PlaceablesActive);
        if (guest) ImGui.BeginDisabled();
        if (ImGui.Checkbox("Building on your estate (place chests and furniture, repair buildings)", ref on))
        {
            SetEstate(on);
            if (on && plugin.Connected) SendEstateUnlock(plugin);
        }
        if (guest) ImGui.EndDisabled();
        ImGui.TextWrapped(guest
            ? "The host decides this (it's the host's world)."
            : "The demo never turns this on. With it on, walk onto your estate in Town, then use a chest or other placeable from your bag to place it." +
              (plugin.Connected ? " Turning it on turns it on for everyone." : ""));
    }

    static void SetEstate(bool on)
    {
        if (on) { UnlockEstate(); return; }
        GameData.gameBools[BoolID.PlaceablesActive] = false;
        GameData.gameBools[BoolID.RepairablesActive] = false;
        NetLog.Info("Estate building and repairs locked again", toast: true);
    }

    internal static void UnlockEstate()
    {
        GameData.gameBools[BoolID.PlaceablesActive] = true;
        GameData.gameBools[BoolID.RepairablesActive] = true;
        NetLog.Info("Estate building and repairs unlocked: walk onto your estate in Town to build", toast: true);
        ReportRepairables();
    }

    /// <summary>What can be repaired in the loaded scenes (in chat): how many, how many are done, the nearest one.</summary>
    internal static void ReportRepairables()
    {
        var all = UnityEngine.Object.FindObjectsByType<RepairableBuilding>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None);
        var me = LocalPlayer.Get();
        int done = 0, hidden = 0;
        RepairableBuilding nearest = null;
        float best = float.MaxValue;
        foreach (var b in all)
        {
            if (b.isRepaired) done++;
            if (!b.gameObject.activeInHierarchy) { hidden++; continue; }
            if (!me || b.isRepaired) continue;
            float d = UnityEngine.Vector2.Distance(me.transform.position, b.transform.position);
            if (d < best) { best = d; nearest = b; }
        }
        Chat.System(all.Length == 0 ? "No repairable buildings in this area." :
            $"Repairable buildings here: {all.Length} ({done} repaired, {hidden} hidden by the demo)." +
            (nearest ? $" Nearest broken one: {nearest.name}, {best:0} away: walk onto it, then F to repair." : ""));
        foreach (var b in all)
            NetLog.Info($"Repairable {b.name} (id {b.saveID}) repaired={b.isRepaired} active={b.gameObject.activeInHierarchy} at {b.transform.position}");
    }

    internal static void SendEstateUnlock(Plugin plugin)
    {
        var w = new LiteNetLib.Utils.NetDataWriter();
        w.Put((byte)MsgType.EstateUnlock);
        plugin.SendToAll(w, LiteNetLib.DeliveryMethod.ReliableOrdered);
    }

    internal static void TeleportTo(RemotePlayer p)
    {
        var me = LocalPlayer.Get();
        if (!me || !Singleton<GameManager>.HasMain) return;
        var target = p.Avatar != null && p.Avatar.Visible ? p.Avatar.Root.transform.position : p.LastPosition;
        target += new UnityEngine.Vector3(p.Flip ? 0.8f : -0.8f, 0f, 0f);   // beside them, not on top

        GameArea area = null;
        if (p.Area != LocalPlayer.Area)
            foreach (var a in UnityEngine.Object.FindObjectsByType<GameArea>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None))
                if (a.name == p.Area) { area = a; break; }

        Singleton<GameManager>.main.TeleportPlayerFade(target, p.Flip ? Direction.Left : Direction.Right, area, area ? area.CameraConfiner : null).Forget();
        NetLog.Info($"Teleporting to {p.Name}" + (area ? $" ({p.Area})" : ""));
    }

    static Vector4 ColorFor(LogKind kind) => kind switch
    {
        LogKind.Success => new Vector4(0.4f, 0.9f, 0.5f, 1f),
        LogKind.Warning => new Vector4(1f, 0.8f, 0.3f, 1f),
        LogKind.Error => new Vector4(1f, 0.4f, 0.4f, 1f),
        _ => new Vector4(0.85f, 0.85f, 0.85f, 1f),
    };
}
