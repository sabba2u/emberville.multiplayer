using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace EmbervilleGhost;

/// <summary>
/// The Online Players screen (O) in the game's own look (GameUi kit): the game's framed window, a row per player on its
/// dark row panels, its copper buttons: Teleport to, Trade, Mute, and for the host Kick / Ban (a second click within
/// 3 s confirms), then the host's ban list with Unban. Rebuilt whenever what it shows changes (twice a second).
/// If the window can't be built (pieces missing), OnlinePlayers draws its old one instead.
/// </summary>
internal static class OnlinePlayersWindow
{
    static GameUi.Window win;
    static string shown;
    static float nextCheck;
    static bool failed;

    /// <summary>Show / hide. False = couldn't: draw the old window.</summary>
    public static bool Show(bool on)
    {
        if (failed) return false;
        if (!on) { win?.Show(false); shown = null; return true; }
        try
        {
            if (win == null || !win.Root) win = GameUi.MakeWindow("Online Players", new Vector2(980, 640), () => OnlinePlayers.Close());
            win.Show(true);
            win.Root.SetAsLastSibling();
            shown = null;
            Refresh();
            return true;
        }
        catch (System.Exception e)
        {
            failed = true;
            NetLog.Warn($"Online Players (game look): {e.Message}; using the old window", toast: false);
            win?.Destroy();
            win = null;
            return false;
        }
    }

    public static bool Active => !failed && win != null && win.Shown;

    /// <summary>Every frame while it's up: rebuilt when something it shows changed.</summary>
    public static void Tick()
    {
        if (!Active || Time.unscaledTime < nextCheck) return;
        nextCheck = Time.unscaledTime + 0.5f;
        Refresh();
    }

    static string Signature()
    {
        var p = OnlinePlayers.Plugin;
        if (p == null || !p.Connected) return "offline";
        string s = $"{p.MyId}|{p.IsHost}|{LocalPlayer.Scene}|{LocalPlayer.Area}|{OnlinePlayers.ConfirmKey}|";
        foreach (var x in p.Players.OrderBy(x => x.Id))
            s += $"{x.Id}:{x.Name}:{x.HaveState}:{x.Scene}:{x.Area}:{x.Health}/{x.MaxHealth}:{x.Status}:{p.PingOf(x.Id) / 20}:{OnlinePlayers.IsMuted(x.Name)};";
        if (p.IsHost) s += "|" + string.Join(",", OnlinePlayers.BanList().Select(b => b.id));
        return s;
    }

    static void Refresh()
    {
        string sig = Signature();
        if (sig == shown) return;
        shown = sig;
        var c = win.Content;
        GameUi.Clear(c);
        var plugin = OnlinePlayers.Plugin;
        if (plugin == null || !plugin.Connected)
        {
            Note(c, "Not connected. F9 hosts, F10 joins.");
            return;
        }
        bool host = plugin.IsHost;
        Note(c, host ? "You're the host: kick and ban are yours. Mute is just for you." : "Mute is just for you (their chat and bubbles disappear).");

        PlayerRow(c, plugin.MyId, "You", host ? "host" : "client", LocalPlayer.Scene + " / " + LocalPlayer.Area, "", -1, false, null);
        foreach (var p in plugin.Players.OrderBy(p => p.Id))
        {
            string place = p.HaveState ? p.Scene + " / " + p.Area : "loading...";
            string health = p.MaxHealth > 0 ? $"{p.Health}/{p.MaxHealth} HP" : "";
            string status = PlayerStatusUtil.Label(p.Status);
            PlayerRow(c, p.Id, p.Name, p.Id == 0 ? "host" : "client", place, health + (status.Length > 0 ? "  " + status : ""), plugin.PingOf(p.Id), host && p.Id != 0, p);
        }

        if (host)
        {
            var bans = OnlinePlayers.BanList();
            if (bans.Count > 0)
            {
                Note(c, "Banned");
                foreach (var b in bans)
                {
                    var row = GameUi.Row(c, 52);
                    var name = GameUi.Label(row, b.name, 20, GameUi.Dim);
                    GameUi.Flexible(name);
                    var id = b.id;
                    GameUi.Button(row, "Unban", () => OnlinePlayers.Unban(id), 110, square: true);
                }
            }
        }
        Note(c, $"{OnlinePlayers.KeyName} or Esc closes this.");
    }

    static void Note(Transform parent, string text)
    {
        var t = GameUi.Label(parent, text, 18, GameUi.Dim);
        GameUi.Fixed(t, -1, 26);
    }

    static void PlayerRow(Transform parent, int id, string name, string role, string place, string info, int ping, bool canModerate, RemotePlayer them)
    {
        bool me = them == null;
        var row = GameUi.Row(parent, me ? 64 : 112);
        var left = GameUi.Column(row, 300);
        var top = GameUi.Label(left, $"<color=#{(id == 0 ? "FFCC66" : "8CD1FF")}>[{role}]</color>  {name}", 24);
        top.richText = true;
        GameUi.Fixed(top, -1, 30);
        var where = GameUi.Label(left, place, 17, GameUi.Dim);
        GameUi.Fixed(where, -1, 22);
        if (info.Length > 0 || ping >= 0)
        {
            var extra = GameUi.Label(left, info + (ping >= 0 ? $"   ping {ping} ms" : ""), 17, GameUi.Dim);
            GameUi.Fixed(extra, -1, 22);
        }
        if (me) return;

        // the actions, in two rows of buttons on the right
        var right = GameUi.Column(row, 560, 6);
        GameUi.Flexible(right);
        var line1 = Line(right);
        bool sameScene = them.HaveState && them.Scene == LocalPlayer.Scene;
        var tp = GameUi.Button(line1, "Teleport to", () => { DebugPanel.TeleportTo(them); OnlinePlayers.Close(); }, 150, square: true);
        tp.interactable = sameScene && LocalPlayer.Get();
        GameUi.Button(line1, "Trade", () => { Trade.Invite(id); OnlinePlayers.Close(); }, 110, square: true);
        if (!sameScene)
        {
            var why = GameUi.Label(right, them.HaveState ? "In another scene: go through the same door to teleport" : "Not heard from them yet", 15, GameUi.Dim);
            GameUi.Fixed(why, -1, 20);
        }
        // second row: Mute | Kick | Ban (kick and ban for the host)
        var line2 = Line(right);
        bool muted = OnlinePlayers.IsMuted(name);
        GameUi.Button(line2, muted ? "Unmute" : "Mute", () => { OnlinePlayers.SetMuted(name, !muted); shown = null; }, 110, square: true);
        if (canModerate)
        {
            string kick = "kick:" + id, ban = "ban:" + id;
            GameUi.Button(line2, OnlinePlayers.Armed(kick) ? "Kick? (again)" : "Kick", () => { if (OnlinePlayers.Arm(kick)) plugin().Kick(id, ban: false); shown = null; }, 170, square: true);
            GameUi.Button(line2, OnlinePlayers.Armed(ban) ? "Ban? (again)" : "Ban", () => { if (OnlinePlayers.Arm(ban)) plugin().Kick(id, ban: true); shown = null; }, 170, square: true);
        }
    }

    static Plugin plugin() => OnlinePlayers.Plugin;

    static RectTransform Line(Transform parent)
    {
        var go = new GameObject("EG_Line", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var h = go.AddComponent<HorizontalLayoutGroup>();
        h.spacing = 8;
        h.childAlignment = TextAnchor.MiddleRight;
        h.childControlWidth = true; h.childControlHeight = true;
        h.childForceExpandWidth = false; h.childForceExpandHeight = false;
        var le = go.AddComponent<LayoutElement>(); le.preferredHeight = 38; le.minHeight = 38;
        return (RectTransform)go.transform;
    }
}
