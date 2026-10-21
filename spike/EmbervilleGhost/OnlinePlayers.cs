using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using ImGuiNET;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine.InputSystem;

namespace EmbervilleGhost;

/// <summary>
/// The Online Players screen (O, or /online; like our BDSP mod's Tab list): who's connected, where they are, health, ping,
/// what they're doing. Mute anyone (this PC only, saved: their chat and speech bubbles are hidden). The host also
/// kicks and bans (a second click to confirm). Bans follow a random player ID each copy keeps in its config (sent on
/// joining: Identity), not the name, which anyone can change; the host keeps the list and can unban.
/// While it's open the game's controls are off (clicks would attack) and the mouse works on the window.
/// </summary>
internal static class OnlinePlayers
{
    public static Plugin Plugin;
    public static bool Visible { get; private set; }

    static ConfigEntry<Key> keyEntry;
    static ConfigEntry<string> muted, banned, myGuid;
    static List<(UnityEngine.InputSystem.InputActionMap map, bool on)> savedMaps;
    static string confirm = "";   // "kick:3" / "ban:3": waiting for the second click
    static float confirmAt;

    public static void Bind(ConfigFile config)
    {
        keyEntry = config.Bind("OnlinePlayers", "Key", Key.O, "Opens the Online Players screen (who's connected; mute, kick, ban). Tab, P, I, M, K, J are the game's.");
        muted = config.Bind("OnlinePlayers", "Muted", "", "Players whose chat you don't see (names, comma separated)");
        banned = config.Bind("OnlinePlayers", "Banned", "", "Host: banned players (id|name; ...)");
        myGuid = config.Bind("OnlinePlayers", "PlayerId", "", "This copy's random player ID (bans follow it). Leave it alone.");
        if (string.IsNullOrEmpty(myGuid.Value)) myGuid.Value = Guid.NewGuid().ToString("N");
    }

    public static string KeyName => keyEntry.Value.ToString();
    public static string MyGuid => myGuid.Value;

    // ---- mute (this PC) ----

    static HashSet<string> MutedSet() => new HashSet<string>(
        (muted.Value ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);

    public static bool IsMuted(string name) => !string.IsNullOrEmpty(name) && MutedSet().Contains(name);

    internal static void SetMuted(string name, bool on)
    {
        var s = MutedSet();
        if (on) s.Add(name); else s.Remove(name);
        muted.Value = string.Join(", ", s);
        NetLog.Info(on ? $"Muted {name} (only for you)" : $"Unmuted {name}", toast: true);
    }

    // ---- bans (host) ----

    static List<(string id, string name)> Bans() =>
        (banned.Value ?? "").Split(';').Select(e => e.Trim()).Where(e => e.Length > 0)
            .Select(e => { int bar = e.IndexOf('|'); return bar < 0 ? (e, e) : (e.Substring(0, bar), e.Substring(bar + 1)); }).ToList();

    static void SaveBans(List<(string id, string name)> list) => banned.Value = string.Join("; ", list.Select(b => b.id + "|" + b.name.Replace(";", ",").Replace("|", "/")));

    public static bool IsBanned(string guid) => !string.IsNullOrEmpty(guid) && Bans().Any(b => b.id == guid);

    public static void AddBan(string guid, string name)
    {
        var list = Bans();
        if (list.All(b => b.id != guid)) list.Add((guid, name));
        SaveBans(list);
    }

    // ---- the window ----

    public static void Toggle() => SetVisible(!Visible);
    public static void Close() => SetVisible(false);

    // ---- for the game-look window (OnlinePlayersWindow) ----
    internal static string ConfirmKey => confirm == "" || UnityEngine.Time.unscaledTime - confirmAt >= 3f ? "" : confirm;
    internal static bool Armed(string key) => confirm == key && UnityEngine.Time.unscaledTime - confirmAt < 3f;
    /// <summary>A button needing a second click within 3 s: true on that second click.</summary>
    internal static bool Arm(string key)
    {
        if (Armed(key)) { confirm = ""; return true; }
        confirm = key;
        confirmAt = UnityEngine.Time.unscaledTime;
        return false;
    }
    internal static List<(string id, string name)> BanList() => Bans();
    internal static void Unban(string id)
    {
        var bans = Bans();
        var b = bans.FirstOrDefault(x => x.id == id);
        SaveBans(bans.Where(x => x.id != id).ToList());
        NetLog.Info($"Unbanned {b.name}", toast: true);
    }

    static void SetVisible(bool on)
    {
        if (on == Visible) return;
        Visible = on;
        confirm = "";
        OnlinePlayersWindow.Show(on);   // the game-look window (if it can't be built, Draw shows the old one)
        if (on)
        {
            // The game's controls off while it's up (a click on a button would also swing the weapon).
            savedMaps = new List<(UnityEngine.InputSystem.InputActionMap, bool)>();
            foreach (var map in MainInput.Actions.asset.actionMaps)
            {
                savedMaps.Add((map, map.enabled));
                // the game-look window's buttons need the game's UI actions (its EventSystem reads them); its Esc
                // (UI.Cancel opens the pause menu) stays off: our own Esc closes the window
                if (map.name == "UI" && OnlinePlayersWindow.Active) { map.Enable(); MainInput.Actions.UI.Cancel.Disable(); }
                else map.Disable();
            }
            UnityEngine.Cursor.visible = true;
            UnityEngine.Cursor.lockState = UnityEngine.CursorLockMode.None;
        }
        else
        {
            MainInput.Actions.UI.Cancel.Enable();
            if (savedMaps != null) foreach (var (map, was) in savedMaps) { if (was) map.Enable(); else map.Disable(); }
            savedMaps = null;
        }
    }

    /// <summary>Every frame: the key opens / closes it; Esc closes it.</summary>
    public static void Tick()
    {
        var kb = Keyboard.current;
        if (kb == null || !UnityEngine.Application.isFocused || Chat.IsOpen || ImGuiUnity.Typing) return;
        if (kb[keyEntry.Value].wasPressedThisFrame && (Visible || !PlayerStatusUtil.LocalBusy)) Toggle();
        else if (Visible && kb.escapeKey.wasPressedThisFrame) SetVisible(false);
        OnlinePlayersWindow.Tick();
    }

    public static void Draw()
    {
        if (!Visible || Plugin == null || OnlinePlayersWindow.Active) return;   // (the game-look window is up instead)
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(new Vector2(vp.WorkPos.x + vp.WorkSize.x * 0.5f, vp.WorkPos.y + vp.WorkSize.y * 0.3f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(560, 0), ImGuiCond.Always);
        bool open = true;
        if (!ImGui.Begin("Online Players##online", ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        if (!Plugin.Connected) ImGui.TextDisabled("Not connected. F9 hosts, F10 joins.");
        else
        {
            bool host = Plugin.IsHost;
            ImGui.TextDisabled(host ? "You're the host: kick and ban are yours. Mute is just for you." : "Mute is just for you (their chat and bubbles disappear).");
            ImGui.Separator();
            Row(Plugin.MyId, "You", host ? "[host]" : "[client]", LocalPlayer.Scene + " / " + LocalPlayer.Area, "", -1, false);
            foreach (var p in Plugin.Players.OrderBy(p => p.Id))
            {
                string place = p.HaveState ? p.Scene + " / " + p.Area : "loading...";
                string health = p.MaxHealth > 0 ? $"{p.Health}/{p.MaxHealth} HP" : "";
                string status = PlayerStatusUtil.Label(p.Status);
                Row(p.Id, p.Name, p.Id == 0 ? "[host]" : "[client]", place, health + (status.Length > 0 ? "  " + status : ""), Plugin.PingOf(p.Id), host && p.Id != 0);
            }

            if (host)
            {
                var bans = Bans();
                if (bans.Count > 0)
                {
                    ImGui.Separator();
                    ImGui.Text("Banned");
                    foreach (var b in bans)
                    {
                        ImGui.TextDisabled("  " + b.name);
                        ImGui.SameLine(260);
                        if (ImGui.SmallButton($"Unban##{b.id}")) { SaveBans(bans.Where(x => x.id != b.id).ToList()); NetLog.Info($"Unbanned {b.name}", toast: true); }
                    }
                }
            }
        }
        ImGui.Separator();
        ImGui.TextDisabled($"{KeyName} or Esc closes this.");
        ImGui.End();
        if (!open) SetVisible(false);
    }

    static void Row(int id, string name, string role, string place, string info, int ping, bool canModerate)
    {
        ImGui.PushID(id);
        ImGui.TextColored(id == 0 ? new Vector4(1f, 0.8f, 0.3f, 1f) : new Vector4(0.45f, 0.8f, 1f, 1f), role);
        ImGui.SameLine(70);
        ImGui.Text(name);
        ImGui.SameLine(250);
        ImGui.TextDisabled(place);
        if (info.Length > 0 || ping >= 0)
        {
            ImGui.Indent(70);
            ImGui.TextDisabled(info + (ping >= 0 ? $"   ping {ping} ms" : ""));
            ImGui.Unindent(70);
        }
        if (id != Plugin.MyId)
        {
            ImGui.Indent(70);
            bool isMuted = IsMuted(name);
            var them = Plugin.Players.FirstOrDefault(x => x.Id == id);
            bool sameScene = them != null && them.HaveState && them.Scene == LocalPlayer.Scene;
            if (!sameScene) ImGui.BeginDisabled();
            if (ImGui.SmallButton("Teleport to")) { DebugPanel.TeleportTo(them); SetVisible(false); }
            if (!sameScene)
            {
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(them == null || !them.HaveState ? "Not heard from them yet" : "They're in another scene (go through the same door first)");
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Trade")) { Trade.Invite(id); SetVisible(false); }
            ImGui.SameLine();
            if (ImGui.SmallButton(isMuted ? "Unmute" : "Mute")) SetMuted(name, !isMuted);
            if (canModerate)
            {
                ImGui.SameLine();
                if (Confirm("Kick", "kick:" + id)) { Plugin.Kick(id, ban: false); confirm = ""; }
                ImGui.SameLine();
                if (Confirm("Ban", "ban:" + id)) { Plugin.Kick(id, ban: true); confirm = ""; }
            }
            ImGui.Unindent(70);
        }
        ImGui.Spacing();
        ImGui.PopID();
    }

    /// <summary>A button that needs a second click within 3 s ("Kick?") so a stray click doesn't throw someone out.</summary>
    static bool Confirm(string label, string key)
    {
        bool armed = confirm == key && UnityEngine.Time.unscaledTime - confirmAt < 3f;
        if (!ImGui.SmallButton(armed ? label + "? (click again)" : label)) return false;
        if (armed) return true;
        confirm = key;
        confirmAt = UnityEngine.Time.unscaledTime;
        return false;
    }
}
