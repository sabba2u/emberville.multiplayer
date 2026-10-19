using System;
using System.Collections.Generic;
using ImGuiNET;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine.InputSystem;
using Time = UnityEngine.Time;

namespace EmbervilleGhost;

/// <summary>
/// Chat, like our BDSP mod: Enter opens a text box, Enter sends (an empty line just closes), Esc cancels.
/// Bottom-left corner: the last messages, fading out after a while (all of them stay visible while typing).
/// (Who runs this area, "Cohh is the host here", is drawn top-right by Hud: the game uses the bottom corners.) While the box is open the game's own input is off, so typing doesn't
/// walk, attack or open menus. Sent to everyone (the host relays).
/// </summary>
internal static class Chat
{
    public static Plugin Plugin;

    const float ShowSeconds = 12f, FadeSeconds = 2f;
    const int MaxLines = 8;

    struct Line { public float Time; public string Who, Text; public Vector4 Color; }
    static readonly List<Line> lines = new List<Line>();
    static string typing = "";
    static bool hooked;
    static float openedAt;
    static List<(UnityEngine.InputSystem.InputActionMap map, bool on)> savedMaps;

    public static bool IsOpen { get; private set; }

    /// <summary>
    /// Every frame, before ImGui builds its frame. The text box reads the keyboard itself (Unity's text input for
    /// characters; Enter / Esc / Backspace straight from the keyboard) instead of an ImGui text field, so it can never
    /// lose focus and leave you stuck with the game's controls switched off.
    /// </summary>
    public static void Tick()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        if (!IsOpen)
        {
            if (!UnityEngine.Application.isFocused || ImGuiUnity.IsOpen) return;
            if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) Open();
            return;
        }

        if (kb.escapeKey.wasPressedThisFrame) { Close(); return; }
        if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
        {
            if (Time.unscaledTime - openedAt > 0.1f) { Send(typing); Close(); }   // not the Enter that opened it
            return;
        }
        // Backspace: once on press, then repeating while held.
        if (kb.backspaceKey.wasPressedThisFrame) { Backspace(); repeatAt = Time.unscaledTime + 0.4f; }
        else if (kb.backspaceKey.isPressed && Time.unscaledTime >= repeatAt) { Backspace(); repeatAt = Time.unscaledTime + 0.04f; }
    }

    static float repeatAt;
    static void Backspace() { if (typing.Length > 0) typing = typing.Substring(0, typing.Length - 1); }

    static void OnTextInput(char c)
    {
        if (!IsOpen || c < 32 || c == 127 || typing.Length >= 200) return;
        typing += c;
    }

    static void Open()
    {
        IsOpen = true;
        openedAt = Time.unscaledTime;
        typing = "";
        if (!hooked && Keyboard.current != null) { Keyboard.current.onTextInput += OnTextInput; hooked = true; }
        // Game input off while typing (WASD would walk, Esc would pause...). Remember which maps were on.
        savedMaps = new List<(UnityEngine.InputSystem.InputActionMap, bool)>();
        foreach (var map in MainInput.Actions.asset.actionMaps)
        {
            savedMaps.Add((map, map.enabled));
            map.Disable();
        }
    }

    static void Close()
    {
        IsOpen = false;
        typing = "";
        if (savedMaps != null)
            foreach (var (map, on) in savedMaps) if (on) map.Enable();
        savedMaps = null;
    }

    static void Send(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (text.Length > 200) text = text.Substring(0, 200);
        Add(Plugin.MyId, text);
        if (!Plugin.Connected || Plugin.MyId < 0) { System("Not connected: nobody else sees this."); return; }
        var w = new NetDataWriter();
        w.Put((byte)MsgType.Chat);
        w.Put(Plugin.MyId);
        w.Put(text);
        if (Plugin.IsHost) Plugin.SendToAllClients(w, DeliveryMethod.ReliableOrdered);
        else Plugin.SendTo(0, w);
    }

    /// <summary>A chat line arrived (host: from a client, relay done by Plugin; client: from anyone via the host).</summary>
    public static void OnChat(int fromId, string text) => Add(fromId, text.Length > 200 ? text.Substring(0, 200) : text);

    static void Add(int id, string text)
    {
        string who = id == Plugin.MyId ? MyName() : Plugin.NameOf(id);
        var color = id == 0 ? new Vector4(1f, 0.8f, 0.3f, 1f) : new Vector4(0.45f, 0.8f, 1f, 1f);
        lines.Add(new Line { Time = Time.unscaledTime, Who = who, Text = text, Color = color });
        if (lines.Count > 50) lines.RemoveAt(0);
        NetLog.Info($"[chat] {who}: {text}");
    }

    // ---- death announcements ----

    static string lastHitBy = "";

    /// <summary>Remember what hit our player last, for "Cohh was killed by a Spiderling".</summary>
    [HarmonyLib.HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.ApplyDamage)), HarmonyLib.HarmonyPrefix]
    static void RememberHitter(DamageData damageData)
    {
        if (damageData.Aggressor is UnityEngine.MonoBehaviour mb && mb) lastHitBy = CleanName(mb.name);
        else if (damageData.Aggressor != null) lastHitBy = "";
    }

    /// <summary>Our player died: tell everyone (chat line + toast).</summary>
    [HarmonyLib.HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.Die)), HarmonyLib.HarmonyPostfix]
    static void AfterDie()
    {
        if (Plugin == null) return;
        Died(Plugin.MyId, lastHitBy);
        if (!Plugin.Connected || Plugin.MyId < 0) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.PlayerDied);
        w.Put(Plugin.MyId);
        w.Put(lastHitBy);
        if (Plugin.IsHost) Plugin.SendToAllClients(w, DeliveryMethod.ReliableOrdered);
        else Plugin.SendTo(0, w);
    }

    public static void Died(int id, string cause)
    {
        string who = id == Plugin.MyId ? "You" : OwnerName(id);
        string text = $"{who} {(id == Plugin.MyId ? "were" : "was")} killed" + (string.IsNullOrEmpty(cause) ? "" : $" by {cause}");
        System(text, new Vector4(1f, 0.4f, 0.4f, 1f));
        NetLog.Warn(text);
    }

    /// <summary>"Spiderling(Clone)" -> "Spiderling".</summary>
    static string CleanName(string n)
    {
        n = n.Replace("(Clone)", "").Trim();
        int at = n.IndexOf('#');
        return at > 0 ? n.Substring(0, at).Trim() : n;
    }

    public static void System(string text) => System(text, new Vector4(0.7f, 0.7f, 0.7f, 1f));

    static void System(string text, Vector4 color)
    {
        lines.Add(new Line { Time = Time.unscaledTime, Who = "", Text = text, Color = color });
        if (lines.Count > 50) lines.RemoveAt(0);
    }

    static string MyName()
    {
        string n = GameData.HasPlayerData ? GameData.playerData.playerName : "";
        return string.IsNullOrEmpty(n) ? "You" : n;
    }

    /// <summary>Bottom-left: who runs this area, recent messages, and the text box while typing.</summary>
    public static void Draw()
    {
        if (Plugin == null) return;
        var vp = ImGui.GetMainViewport();
        float now = Time.unscaledTime;
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.AlwaysAutoResize;

        ImGui.SetNextWindowPos(new Vector2(vp.WorkPos.x + 12, vp.WorkPos.y + vp.WorkSize.y - 12), ImGuiCond.Always, new Vector2(0f, 1f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(380, 0), new Vector2(520, vp.WorkSize.y * 0.5f));
        ImGui.SetNextWindowBgAlpha(IsOpen ? 0.7f : 0.0f);
        if (ImGui.Begin("##chat", flags | ImGuiWindowFlags.NoInputs))
        {
            int start = Math.Max(0, lines.Count - MaxLines);
            for (int i = start; i < lines.Count; i++)
            {
                var l = lines[i];
                float age = now - l.Time;
                float alpha = IsOpen ? 1f : Math.Max(0f, Math.Min(1f, (ShowSeconds - age) / FadeSeconds));
                if (alpha <= 0f) continue;
                if (l.Who.Length > 0)
                {
                    var c = l.Color; c.w = alpha;
                    ImGui.TextColored(c, l.Who + ":");
                    ImGui.SameLine();
                }
                ImGui.PushTextWrapPos(500);
                var tc = l.Who.Length > 0 ? new Vector4(1, 1, 1, 1) : l.Color;   // system lines (deaths...) in their own colour
                tc.w = alpha;
                ImGui.TextColored(tc, l.Text);
                ImGui.PopTextWrapPos();
            }

            if (IsOpen)
            {
                // Our own text line (see Tick): what's typed so far and a blinking caret.
                bool caret = (now * 2f) % 2f < 1f;
                ImGui.Separator();
                ImGui.PushTextWrapPos(500);
                ImGui.TextUnformatted("> " + typing + (caret ? "_" : " "));
                ImGui.PopTextWrapPos();
                ImGui.TextDisabled("Enter: send   Esc: cancel");
            }
        }
        ImGui.End();
    }

    /// <summary>Who runs the area we're in, for the HUD: "Sabba is the host here" / "You are the host here". Empty when not connected.</summary>
    public static string HostHereLine()
    {
        if (Plugin == null || !Plugin.Connected || Plugin.MyId < 0) return "";
        int owner = Plugin.OwnerHere;
        return owner == Plugin.MyId ? "You are the host here" : $"{OwnerName(owner)} is the host here";
    }

    static string OwnerName(int id)
    {
        if (id == Plugin.MyId) return MyName();
        foreach (var p in Plugin.Players)
            if (p.Id == id) return string.IsNullOrEmpty(p.CharacterName) ? p.Tag : p.CharacterName;
        return id == 0 ? "The host" : $"P{id + 1}";
    }
}
