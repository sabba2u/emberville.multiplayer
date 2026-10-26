using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using BepInEx.Logging;
using ImGuiNET;
using UnityEngine;
using UnityEngine.InputSystem;
using Vector2 = ImGuiNET.Vector2;
using Vector4 = ImGuiNET.Vector4;

namespace EmbervilleGhost;

/// <summary>
/// The ~ console: a drop-down box at the top of the screen that runs what the chat box runs (with or without the "/"),
/// and shows what came of it: every mod's log lines and chat's own replies. Up / Down: earlier commands. Tab: finish a
/// command's name (the ones /help lists). Esc or ~ again: closed. While it's open the game's input is off, as for chat.
/// </summary>
internal static class GameConsole
{
    const int MaxLines = 400;

    public static ConfigEntry<Key> ConsoleKey;
    public static bool IsOpen { get; private set; }

    struct Line { public string Text; public Vector4 Color; }
    static readonly List<Line> lines = new();
    static readonly object sync = new();
    static readonly List<string> history = new();
    static int historyAt = -1;
    static string typing = "";
    static bool focus, scrollDown, listening;
    const float SlideSeconds = 0.18f;
    static float shown;   // 0 hidden .. 1 all the way down
    static CursorLockMode savedLock;
    static bool savedVisible;
    static List<string> names;
    static List<string> capture;

    public static void Init(ConfigFile config)
    {
        ConsoleKey = config.Bind("Console", "Key", Key.Backquote, "Opens and closes the console (~): runs chat commands, shows the mods' log.");
        if (!listening) { BepInEx.Logging.Logger.Listeners.Add(new Listener()); listening = true; }
    }

    /// <summary>Chat's reply lines (Chat.System) land here too.</summary>
    internal static bool Reply(string text, Vector4 color)
    {
        if (capture != null) { capture.Add(text); return true; }
        Add(text, color);
        return false;
    }

    static void Add(string text, Vector4 color)
    {
        lock (sync)
        {
            foreach (var l in (text ?? "").Replace("\r", "").Split('\n')) lines.Add(new Line { Text = l, Color = color });
            if (lines.Count > MaxLines) lines.RemoveRange(0, lines.Count - MaxLines);
        }
        scrollDown = true;
    }

    /// <summary>Every frame (Plugin.Update): the key.</summary>
    public static void Tick()
    {
        var kb = Keyboard.current;
        if (kb == null || ConsoleKey == null || !Application.isFocused) return;
        if (!kb[ConsoleKey.Value].wasPressedThisFrame) return;
        if (IsOpen) Close();
        else if (!Chat.IsOpen && !ImGuiUnity.Typing) Open();
    }

    static void Open()
    {
        IsOpen = true;
        focus = scrollDown = true;
        typing = "";
        historyAt = -1;
        GameInput.Block("console", alsoUi: true);
        savedLock = Cursor.lockState; savedVisible = Cursor.visible;
        Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        if (lines.Count == 0) Add("Type a command (help lists them). Up/Down: earlier ones, Tab: finish a name, Esc or ~: close.", Grey);
    }

    static void Close()
    {
        IsOpen = false;
        GameInput.Unblock("console");
        Cursor.lockState = savedLock; Cursor.visible = savedVisible;
    }

    static readonly Vector4 Grey = new(0.7f, 0.7f, 0.7f, 1f), White = new(1f, 1f, 1f, 1f), Gold = new(1f, 0.8f, 0.3f, 1f),
        Yellow = new(1f, 0.85f, 0.35f, 1f), Red = new(1f, 0.45f, 0.4f, 1f);

    /// <summary>From Hud.Draw.</summary>
    public static unsafe void Draw()
    {
        // slides down from the top when opened, back up when closed (the game's controls are back as soon as it closes)
        shown = Mathf.MoveTowards(shown, IsOpen ? 1f : 0f, Time.unscaledDeltaTime / SlideSeconds);
        if (shown <= 0f) return;
        float eased = 1f - (1f - shown) * (1f - shown) * (1f - shown);
        var vp = ImGui.GetMainViewport();
        float height = vp.Size.y * 0.45f;
        ImGui.SetNextWindowPos(new Vector2(vp.Pos.x, vp.Pos.y - height * (1f - eased)));
        ImGui.SetNextWindowSize(new Vector2(vp.Size.x, height));
        ImGui.SetNextWindowBgAlpha(0.88f);
        if (!ImGui.Begin("##console", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings))
        { ImGui.End(); return; }

        float inputHeight = ImGui.GetFrameHeightWithSpacing();
        ImGui.BeginChild("##lines", new Vector2(0, -inputHeight), ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);
        lock (sync)
            foreach (var l in lines) { ImGui.PushStyleColor(ImGuiCol.Text, l.Color); ImGui.TextUnformatted(l.Text); ImGui.PopStyleColor(); }
        if (scrollDown) { ImGui.SetScrollHereY(1f); scrollDown = false; }
        ImGui.EndChild();

        if (!IsOpen) { ImGui.TextColored(Gold, ">"); ImGui.End(); return; }   // (on its way up: nothing to type into)
        ImGui.TextColored(Gold, ">"); ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        if (focus) { ImGui.SetKeyboardFocusHere(); focus = false; }
        bool enter = ImGui.InputText("##cmd", ref typing, 300,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CallbackHistory | ImGuiInputTextFlags.CallbackCompletion, OnKey);
        typing = typing.Replace("`", "").Replace("~", "");   // (the key that opened it)
        // the rest of the command it's likely to be, in grey after what's typed (Tab takes it; typing on works too)
        string ghost = Ghost(typing);
        if (ghost.Length > 0 && ImGui.IsItemActive())
        {
            var at = ImGui.GetItemRectMin();
            var pad = ImGui.GetStyle().FramePadding;
            ImGui.GetWindowDrawList().AddText(new Vector2(at.x + pad.x + ImGui.CalcTextSize(typing).x, at.y + pad.y),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.5f, 0.5f, 0.5f, 0.8f)), ghost);
        }
        if (enter) { Run(typing); typing = ""; focus = true; }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) Close();
        ImGui.End();
    }

    static void Run(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (history.Count == 0 || history[history.Count - 1] != text) history.Add(text);
        historyAt = -1;
        Add("> " + text, White);
        string cmd = (text.StartsWith("/") ? text.Substring(1) : text).Trim().ToLowerInvariant();
        if (cmd == "clear" || cmd == "cls") { lock (sync) lines.Clear(); return; }
        try { if (!Chat.Run(cmd)) Add($"Not a command: {cmd} (help lists them)", Red); }
        catch (Exception e) { Add($"{cmd}: {e.Message}", Red); }
    }

    static unsafe int OnKey(ImGuiInputTextCallbackData* raw)
    {
        var d = new ImGuiInputTextCallbackDataPtr(raw);
        string now = d.BufTextLen > 0 ? new string((sbyte*)d.Buf, 0, d.BufTextLen) : "";
        string put = null;
        if (d.EventFlag == ImGuiInputTextFlags.CallbackHistory && history.Count > 0)
        {
            if (d.EventKey == ImGuiKey.UpArrow) historyAt = historyAt < 0 ? history.Count - 1 : Math.Max(0, historyAt - 1);
            else if (d.EventKey == ImGuiKey.DownArrow) historyAt = historyAt < 0 ? -1 : historyAt + 1 >= history.Count ? -1 : historyAt + 1;
            put = historyAt < 0 ? "" : history[historyAt];
        }
        else if (d.EventFlag == ImGuiInputTextFlags.CallbackCompletion)
        {
            string word = now.TrimStart('/');
            if (word.Contains(' ')) return 0;
            var all = Names().Where(n => n.StartsWith(word.ToLowerInvariant())).ToList();
            if (all.Count > 1) Add(string.Join("   ", all), Grey);   // (the others it could be)
            if (all.Count > 0) put = now + Ghost(now) + " ";
        }
        if (put != null) { d.DeleteChars(0, d.BufTextLen); d.InsertChars(0, put); }
        return 0;
    }

    /// <summary>What's left of the first command name that starts with what's typed (nothing once there's a space).</summary>
    static string Ghost(string text)
    {
        string word = (text ?? "").TrimStart('/');
        if (word.Length == 0 || word.Contains(' ')) return "";
        string low = word.ToLowerInvariant();
        string name = Names().FirstOrDefault(n => n.Length > low.Length && n.StartsWith(low));
        return name == null ? "" : name.Substring(low.Length);
    }

    /// <summary>Command names, from what /help says (the first time).</summary>
    static List<string> Names()
    {
        if (names != null) return names;
        capture = new List<string>();
        try { Chat.Run("help"); } catch { }
        var text = string.Join(" ", capture);
        capture = null;
        names = Regex.Matches(text, @"/([a-z][a-z0-9]*)").Cast<Match>().Select(m => m.Groups[1].Value)
            .Concat(new[] { "help", "clear" }).Distinct().OrderBy(n => n).ToList();
        return names;
    }

    /// <summary>Every mod's log lines (not the game's own: too many), from whatever thread.</summary>
    sealed class Listener : ILogListener
    {
        public void LogEvent(object sender, LogEventArgs e)
        {
            if (e.Source?.SourceName == "Unity Log") return;
            var color = (e.Level & (LogLevel.Error | LogLevel.Fatal)) != 0 ? Red : (e.Level & LogLevel.Warning) != 0 ? Yellow : Grey;
            Add($"[{e.Source?.SourceName}] {e.Data}", color);
        }
        public void Dispose() { }
    }
}
