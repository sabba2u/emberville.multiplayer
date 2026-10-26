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
/// (Who runs this area, "Cohh is the authority of this map", is drawn top-right by Hud: the game uses the bottom corners.) While the box is open the game's own input is off, so typing doesn't
/// walk, attack or open menus. Sent to everyone (the host relays).
/// </summary>
[HarmonyLib.HarmonyPatch]   // its death patches (PlayerMaster.Die / ApplyDamage) only run with this on the class
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

    public static bool IsOpen { get; private set; }

    /// <summary>How far above the bottom of the window the chat sits, as a fraction of the window height.</summary>
    public static BepInEx.Configuration.ConfigEntry<float> Lift;

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
            if (!UnityEngine.Application.isFocused || ImGuiUnity.IsOpen || GameConsole.IsOpen || !CanOpen()) return;
            if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) Open();
            return;
        }

        if (kb.escapeKey.wasPressedThisFrame) { Close(); return; }
        if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
        {
            if (Time.unscaledTime - openedAt > 0.1f) { Send(typing); Close(); }   // not the Enter that opened it
            return;
        }
        // Ctrl+V: paste (an address from the host's Copy button, ...).
        if (kb.vKey.wasPressedThisFrame && kb.ctrlKey.isPressed)
        {
            string paste = NetInfo.Clipboard.Replace("\r", " ").Replace("\n", " ");
            typing = (typing + paste).Length > 200 ? (typing + paste).Substring(0, 200) : typing + paste;
            return;
        }
        // Backspace: once on press, then repeating while held.
        if (kb.backspaceKey.wasPressedThisFrame) { Backspace(); repeatAt = Time.unscaledTime + 0.4f; }
        else if (kb.backspaceKey.isPressed && Time.unscaledTime >= repeatAt) { Backspace(); repeatAt = Time.unscaledTime + 0.04f; }
    }

    /// <summary>
    /// Enter only opens chat while actually playing: in dialogue, cutscenes and menus Enter belongs to the game
    /// (it advances dialogue; a chat box there swallowed it and switched the game's input off).
    /// </summary>
    static bool CanOpen()
    {
        if (!Singleton<GameManager>.HasMain || Singleton<GameManager>.main.gameState != GameState.Playing) return false;
        return !UIPanels.HasOpenedPanel;
    }

    static float repeatAt;
    static void Backspace() { if (typing.Length > 0) typing = typing.Substring(0, typing.Length - 1); }

    static void OnTextInput(char c)
    {
        if (!IsOpen || c < 32 || c == 127 || typing.Length >= 200) return;
        if (c == 'v' && Keyboard.current != null && Keyboard.current.ctrlKey.isPressed) return;   // Ctrl+V is paste (Tick)
        typing += c;
    }

    static void Open()
    {
        IsOpen = true;
        openedAt = Time.unscaledTime;
        typing = "";
        if (!hooked && Keyboard.current != null) { Keyboard.current.onTextInput += OnTextInput; hooked = true; }
        GameInput.Block("chat", alsoUi: true);   // game input off while typing (WASD would walk, Esc would pause...)
    }

    static void Close()
    {
        IsOpen = false;
        typing = "";
        GameInput.Unblock("chat");
    }

    /// <summary>Chat commands. True = it was one (not sent as chat).</summary>
    /// <summary>A chat command from elsewhere (an NPC's "command ...", the Api): as if typed after "/".</summary>
    internal static bool Run(string cmd) => Command((cmd ?? "").Trim());

    static bool Command(string cmd)
    {
        if (cmd.StartsWith("trade")) { Trade.InviteByName(cmd.Substring(5)); return true; }
        if (cmd == "sethome" || cmd.StartsWith("sethome ")) { Homes.Set(cmd.Length > 7 ? cmd.Substring(8) : ""); return true; }   // here is "home" (or that name)
        if (cmd == "delhome" || cmd.StartsWith("delhome ")) { Homes.Delete(cmd.Length > 7 ? cmd.Substring(8) : ""); return true; }
        if (cmd == "homes") { Homes.List(); return true; }
        if (cmd == "home") { if (!Homes.Go("home")) NetLog.Info("No home yet: /sethome where you stand", toast: true); return true; }
        if (cmd == "tp" || cmd.StartsWith("tp "))
        {
            string to = cmd.Length > 2 ? cmd.Substring(3) : "";
            if (!Homes.Go(to)) Travel.TeleportTo(Travel.Find(to));   // (a home of yours by that name first; else a player)
            return true;
        }
        if (cmd == "summon all") { Travel.SummonAll(); return true; }
        if (cmd == "room" || cmd.StartsWith("room ")) { CustomRoom.Command(cmd.Length > 4 ? cmd.Substring(5) : ""); return true; }
        if (cmd == "summon" || cmd.StartsWith("summon ")) { Travel.Summon(Travel.Find(cmd.Length > 6 ? cmd.Substring(7) : "")); return true; }
        if (cmd == "charscreen" || cmd.StartsWith("charscreen ")) { UnlockClasses.CharacterScreen(cmd.Length > 10 ? cmd.Substring(11) : ""); return true; }   // the character screen unlocked and opened (off: locked)
        if (cmd == "unlockscreens" || cmd.StartsWith("unlockscreens ")) { UnlockClasses.AllScreens(cmd.Length > 13 ? cmd.Substring(14) : ""); return true; }   // every screen the story unlocks (off: locked)
        if (cmd == "classscreen" || cmd.StartsWith("classscreen ")) { UnlockClasses.Screen(cmd.Length > 11 ? cmd.Substring(12) : ""); return true; }   // the class screen unlocked and opened (off: locked)
        if (cmd == "unlockclasses" || cmd.StartsWith("unlockclasses ")) { UnlockClasses.Run(cmd.Length > 13 ? cmd.Substring(14) : ""); return true; }   // testing: every class (or one) learned
        if (cmd == "lockclasses" || cmd.StartsWith("lockclasses ")) { UnlockClasses.Lock(cmd.Length > 11 ? cmd.Substring(12) : ""); return true; }   // testing: classes forgotten
        if (cmd == "classes") { UnlockClasses.List(); return true; }   // every class: learned, equipped
        if (cmd.StartsWith("classes ")) { UnlockClasses.Detail(cmd.Substring(8)); return true; }   // one class: what it is
        if (cmd == "lockmasteries" || cmd.StartsWith("lockmasteries ")) { Arsenal.LockMasteries(cmd.Length > 13 ? cmd.Substring(14) : ""); return true; }   // testing: masteries back off
        if (cmd == "give" || cmd.StartsWith("give ")) { Arsenal.Give(cmd.Length > 4 ? cmd.Substring(5) : ""); return true; }   // testing: any item into your bag
        if (Integrations.Command(cmd)) return true;   // /bowlock, /moveattack, /pet, /clucking, /risen (the split-out mods)
        if (cmd == "resources" || cmd.StartsWith("resources ") || cmd == "mats" || cmd.StartsWith("mats "))
        {
            var arg = cmd.Contains(" ") ? cmd.Substring(cmd.IndexOf(' ') + 1).Trim() : "";
            Stockpile.Give(int.TryParse(arg, out int n) ? n : 100);
            return true;
        }
        switch (cmd)
        {
            case "sleep": TimeSync.ToggleSleep(); return true;   // everyone goes to bed
            case "estate":
                if (Plugin.Connected && !Plugin.IsHost) { System("Only the host can unlock the estate (it's the host's world)."); return true; }
                if (!GameData.HasWorldData) { System("Load a world first."); return true; }
                DebugPanel.UnlockEstate();
                if (Plugin.Connected) DebugPanel.SendEstateUnlock(Plugin);
                return true;
            case "repair": Repairs.RepairNearest(); return true;
            case "devmode": DevMode.Toggle(); return true;
            case "online":
            case "party":
            case "players": OnlinePlayers.Toggle(); return true;
            case "devpanel": DevMode.TogglePanel(); return true;
            case "arena": DevMode.GoToFirst(); return true;
            case "uidump": UiDump.Dump(); return true;   // the game's UI on screen -> BepInEx/UIDumps
            case "dungeon": Dungeon.Command(); return true;
            case "nodeath": NoDeath.Toggle(); return true;   // testing: take no damage
            case "walls": DebugWalls.Toggle(); return true;   // debug: what is solid in a generated place
            case "tilt": TiltView.Switch(ViewMode.Tilted); return true;   // the tilted 2.5D view
            case "chase": TiltView.Switch(ViewMode.Chase); return true;   // the camera behind the player
            case "fps": TiltView.Switch(ViewMode.FirstPerson); return true;   // through the player's eyes
            case "tiltshift": TiltShift.Toggle(!TiltShift.On); return true;   // the miniature look
            case "noclip": NoClip.Toggle(); return true;   // testing: walk through walls
            case "hook": HunterHook.Give(); return true;   // a Hunter's Hook
            case "respec": Respec.Run(); return true;   // every stat point back to spend again
            case "unlockmasteries": Arsenal.UnlockMasteries(); return true;   // testing: every weapon type's mastery
            case "weapons": Arsenal.Weapons(); return true;   // every weapon in the game, by type
            case "hookshout": HookSounds.Shout(); return true;   // try the hook's shout sound
            case "exitarrow": Delve.ToggleExitArrow(); return true;   // testing: an arrow to the stairs down
            case "exitpath": Delve.ToggleExitPath(); return true;   // testing: a line along the floor to the stairs
            case "onehitkill": OneHitKill.Toggle(); return true;   // testing: 500 damage a hit
            case "help":
            case "?":
                System("/sleep: ready to skip to morning (everyone must)   /estate: unlock building on your estate (host)");
                System("/devmode: the game's developer tools on/off   /devpanel: open the developer panel   /arena: to this scene's test arena   /dungeon: into a generated dungeon (or out)   /walls: show what blocks you (debug)   /noclip: walk through walls   /clucking: a Staff of Clucking   /risen: a Staff of the Risen   /hook: a Hunter's Hook   /respec: your stat points back to spend again   /classes: every class, learned or not (/classes mage: what it does)   /charscreen: unlock the character screen and open it (off: lock it)   /unlockscreens: every screen (inventory, quests, masteries, classes, character, minimap; off: lock them)   /classscreen: unlock the class screen and open it (off: lock it)   /unlockclasses (mage): learn every class (or one)   /lockclasses (mage): forget them (not the one you have on)   /unlockmasteries: every weapon mastery   /lockmasteries (all): the unused ones (or all) back off   /weapons: every weapon in the game   /give name [count]: any item into your bag   /stash: your shared stash (U)   /sethome (name): here is home (or that name)   /tp name: go to your home of that name, or to a player   /home: go home   /homes: your homes   /delhome name: forget it   /summon name (or all): bring them to you   /rain: a rain storm on/off   /snow: snow on/off (Better Weather)   /exitarrow: arrow to the stairs down   /exitpath: a line to them   /nodeath: take no damage   /onehitkill: 500 damage a hit (testing)   /bowlock: arrows home in (off to start; /bowlock 15 = 15° cone)   /moveattack: move while attacking (off to start)");
                System("/resources (or /resources 250): 100 of every material, and enough for the town's repairs   /repair: fix the nearest broken building");
                System("/trade P2 (or a name): trade items with someone   /pet on (pets start off), /pet: befriend / call / send home, /pet list, /pet glowbat, /pet name ..., /pet color red");
                System($"/online (or {OnlinePlayers.KeyName}): who's connected, mute; the host also kicks and bans   X at a chest (Better Chests mod): put in what it already has");
                if (Integrations.TranslationsHelp != null) System(Integrations.TranslationsHelp);   // (Better Translations)
                return true;
        }
        return false;
    }

    static void Send(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (text.StartsWith("/") && Command(text.Substring(1).Trim().ToLowerInvariant())) return;
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
        if (id != Plugin.MyId && OnlinePlayers.IsMuted(who)) return;   // muted (Online Players screen): not shown, no bubble
        var color = id == 0 ? new Vector4(1f, 0.8f, 0.3f, 1f) : new Vector4(0.45f, 0.8f, 1f, 1f);
        lines.Add(new Line { Time = Time.unscaledTime, Who = who, Text = text, Color = color });
        foreach (var p in Plugin.Players)
            if (p.Id == id) { p.Said = text; p.SaidAt = Time.unscaledTime; }   // speech bubble over their head (Hud)
        if (lines.Count > 50) lines.RemoveAt(0);
        NetLog.Info($"[chat] {who}: {text}");
    }

    // ---- death announcements ----

    static string lastHitBy = "";

    /// <summary>PvP: the next "killed by" names this player (their stand-in isn't a game object with a name).</summary>
    public static void LastHitBy(string who) => pvpHitBy = who;
    static string pvpHitBy;

    /// <summary>Remember what hit our player last, for "Cohh was killed by a Spiderling".</summary>
    [HarmonyLib.HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.ApplyDamage)), HarmonyLib.HarmonyPrefix]
    static void RememberHitter(PlayerMaster __instance, DamageData damageData)
    {
        if (!ReferenceEquals(__instance, LocalPlayer.Get())) return;   // (a companion that's a real player too)
        if (pvpHitBy != null) { lastHitBy = pvpHitBy; pvpHitBy = null; }
        else if (damageData.Aggressor is UnityEngine.MonoBehaviour mb && mb) lastHitBy = CleanName(mb.name);
        else if (damageData.Aggressor != null) lastHitBy = "";
    }

    /// <summary>Our player died: tell everyone (chat line + toast).</summary>
    [HarmonyLib.HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.Die)), HarmonyLib.HarmonyPostfix]
    static void AfterDie(PlayerMaster __instance)
    {
        if (Plugin == null || !ReferenceEquals(__instance, LocalPlayer.Get())) return;
        Died(Plugin.MyId, lastHitBy);
        if (!Plugin.Connected || Plugin.MyId < 0) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.PlayerDied);
        w.Put(Plugin.MyId);
        w.Put(lastHitBy);
        w.Put(__instance.goreDeath);   // which death they play: Dead + scorch mark, or DeadGore + gore splatter
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

    internal static void System(string text, Vector4 color)
    {
        if (GameConsole.Reply(text, color)) return;   // (the console finding command names: not shown)
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
        var me = LocalPlayer.Get();
        if (!IsOpen && me && me.IsDead) return;   // the "YOU DIED" screen: nothing of ours on it
        var vp = ImGui.GetMainViewport();
        float now = Time.unscaledTime;
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.AlwaysAutoResize;

        // Above the game's own HUD (health bar + hotbar in the bottom-left), which scales with the window.
        float lift = vp.WorkSize.y * (Lift?.Value ?? 0.25f);
        ImGui.SetNextWindowPos(new Vector2(vp.WorkPos.x + 12, vp.WorkPos.y + vp.WorkSize.y - lift), ImGuiCond.Always, new Vector2(0f, 1f));
        // A fixed width: the box doesn't grow as you type (only taller, up to half the screen).
        ImGui.SetNextWindowSizeConstraints(new Vector2(Width, 0), new Vector2(Width, vp.WorkSize.y * 0.5f));
        ImGui.SetNextWindowBgAlpha(IsOpen ? 0.7f : 0.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, IsOpen ? 1f : 0f);   // closed: just the lines, no box outline
        bool shown = ImGui.Begin("##chat", flags | ImGuiWindowFlags.NoInputs);
        ImGui.PopStyleVar();
        if (shown)
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
                ImGui.PushTextWrapPos(0f);   // at the window's edge
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
                // Wrapped by width, letter by letter (a long word or a pasted link wraps too); a long message shows
                // its last few lines, so the box stays its size while you type.
                var rows = WrapByWidth("> " + typing + (caret ? "_" : " "), ImGui.GetContentRegionAvail().x);
                int first = Math.Max(0, rows.Count - TypingRows);
                if (first > 0) ImGui.TextDisabled("...");
                for (int r = first; r < rows.Count; r++) ImGui.TextUnformatted(rows[r]);
                ImGui.TextDisabled("Enter: send   Esc: cancel");
            }
        }
        ImGui.End();
    }

    const float Width = 440f;
    const int TypingRows = 4;

    /// <summary>Break text into rows no wider than <paramref name="width"/>: at spaces when it can, else mid-word.</summary>
    static List<string> WrapByWidth(string text, float width)
    {
        var rows = new List<string>();
        int start = 0;
        while (start < text.Length)
        {
            int end = start, lastSpace = -1;
            while (end < text.Length && ImGui.CalcTextSize(text.Substring(start, end - start + 1)).x <= width)
            {
                if (text[end] == ' ') lastSpace = end;
                end++;
            }
            if (end < text.Length && lastSpace > start) end = lastSpace + 1;   // break after the last space that fits
            if (end == start) end = start + 1;                                  // a single wide character
            rows.Add(text.Substring(start, end - start));
            start = end;
        }
        if (rows.Count == 0) rows.Add("");
        return rows;
    }

    /// <summary>Who runs the area we're in, for the HUD: "Sabba is the authority of this map" / "You are the authority of this map" (not "host": the tags under our feet already say who hosts the game). Empty when not connected.</summary>
    public static string HostHereLine()
    {
        if (Plugin == null || !Plugin.Connected || Plugin.MyId < 0) return "";
        int owner = Plugin.OwnerHere;
        return owner == Plugin.MyId ? "You are the authority of this map" : $"{OwnerName(owner)} is the authority of this map";
    }

    static string OwnerName(int id)
    {
        if (id == Plugin.MyId) return MyName();
        foreach (var p in Plugin.Players)
            if (p.Id == id) return string.IsNullOrEmpty(p.CharacterName) ? p.Tag : p.CharacterName;
        return id == 0 ? "The host" : $"P{id + 1}";
    }
}
