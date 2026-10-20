using System;
using ImGuiNET;

namespace EmbervilleGhost;

/// <summary>
/// The "Join host" window that opens on F9: connection, the host's world (downloading / ready / host not in a world),
/// which of our characters to bring, and "Connect now", which loads us into the host's world next to the host, from
/// the main menu or from inside a game (GuestWorld.Load).
/// </summary>
internal static class JoinWindow
{
    public static Plugin Plugin;
    public static bool Visible { get; private set; }

    static (string name, string file)[] characters = new (string, string)[0];
    static bool[] inUse = new bool[0];   // played by another running copy on this PC (CharacterSafety)
    static int selected = -1;
    static string current;   // in a game: the character we're playing (we join as that one, no picking)

    public static void Show()
    {
        Visible = true;
        RefreshCharacters();
    }

    public static void Hide() => Visible = false;

    static void RefreshCharacters()
    {
        var files = GameData.GetPlayerFiles();
        characters = new (string, string)[files.Length];
        inUse = new bool[files.Length];
        selected = -1;
        current = LocalPlayer.Get() && GameData.HasPlayerData ? GameData.playerFilePrefix : null;   // in a game: bring that one
        string currentName = current != null ? GameData.playerData?.playerName : null;
        for (int i = 0; i < files.Length; i++)
        {
            var (data, file) = files[i];
            characters[i] = (string.IsNullOrEmpty(data.playerName) ? file : data.playerName, file);
            inUse[i] = CharacterSafety.InUseElsewhere(file);
            if (current != null && string.Equals(file, current, StringComparison.OrdinalIgnoreCase)) selected = i;
        }
        // In a game but the file didn't match (renamed?): by the character's name, never just "the first one".
        if (current != null && selected < 0 && !string.IsNullOrEmpty(currentName))
            for (int i = 0; i < characters.Length; i++) if (characters[i].name == currentName && !inUse[i]) { selected = i; break; }
        if (current == null && selected < 0)
            for (int i = 0; i < characters.Length; i++) if (!inUse[i]) { selected = i; break; }
    }

    public static void Draw()
    {
        if (!Visible || Plugin == null) return;
        if (GuestWorld.Status == GuestWorld.State.Loading) { Visible = false; return; }

        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(new Vector2(vp.WorkPos.x + vp.WorkSize.x * 0.5f, vp.WorkPos.y + vp.WorkSize.y * 0.3f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(420, 0), ImGuiCond.Always);
        bool open = true;
        if (!ImGui.Begin("Join host##join", ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        // ---- connection ----
        if (Plugin.IsClient && Plugin.MyId >= 0) Line(true, $"Connected as P{Plugin.MyId + 1}  (ping {Plugin.Remote?.Ping ?? 0} ms)");
        else if (Plugin.OnSteam) Line(null, char.ToUpper(Plugin.Status[0]) + Plugin.Status.Substring(1));
        else if (Plugin.IsClient || Plugin.Status.StartsWith("connecting")) Line(null, $"Connecting to {Plugin.AddressSetting.Value}:{Plugin.PortSetting.Value}...");
        else Line(false, $"Not connected ({Plugin.Status})");

        // ---- the host's world ----
        switch (GuestWorld.Status)
        {
            case GuestWorld.State.None: Line(null, "Host's world: waiting for the connection"); break;
            case GuestWorld.State.Downloading: Line(null, "Host's world: downloading..."); break;
            case GuestWorld.State.NoWorld: Line(false, "The host isn't in a world yet. Load one there, then join again."); break;
            case GuestWorld.State.Ready:
                Line(true, $"Host's world \"{GuestWorld.WorldName}\" downloaded ({GuestWorld.SizeKB} KB, seed {GuestWorld.Seed})");
                Line(GuestWorld.GotMap ? true : (bool?)null, GuestWorld.GotMap
                    ? "Map: the host's generated map (no generating here)"
                    : "Map: the host hasn't generated it yet; nothing to download");
                break;
        }

        ImGui.Separator();

        // ---- character ----
        if (characters.Length == 0) ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f), "No characters yet: create one in the game first (Play > New).");
        else if (current != null)
        {
            // In a game: we join as the character we're playing (never a different one by accident).
            if (selected >= 0) ImGui.Text($"You'll join as {characters[selected].name} (the character you're playing).");
            else ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f), "Couldn't find the character you're playing: go to the title screen and join from there.");
        }
        else
        {
            string label = selected >= 0 ? characters[selected].name : "pick one";
            ImGui.SetNextItemWidth(260);
            if (ImGui.BeginCombo("Character", label))
            {
                for (int i = 0; i < characters.Length; i++)
                {
                    if (inUse[i]) ImGui.BeginDisabled();
                    if (ImGui.Selectable($"{characters[i].name}{(inUse[i] ? "  (in use by another copy)" : "")}##{characters[i].file}", i == selected)) selected = i;
                    if (inUse[i]) ImGui.EndDisabled();
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("refresh")) RefreshCharacters();
        }

        // ---- go ----
        bool can = GuestWorld.Ready && selected >= 0 && selected < characters.Length && !inUse[selected];
        if (!can) ImGui.BeginDisabled();
        if (ImGui.Button("Connect now", new Vector2(160, 0)))
        {
            GuestWorld.Load(characters[selected].file);
            Visible = false;
        }
        if (!can) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(100, 0))) { Plugin.Stop(); Visible = false; }
        if (LocalPlayer.Get()) ImGui.TextDisabled("You're in a game: it's saved first, then you join the host.");

        ImGui.End();
        if (!open) Visible = false;
    }

    static void Line(bool? ok, string text)
    {
        var c = ok == true ? new Vector4(0.4f, 0.9f, 0.5f, 1f) : ok == false ? new Vector4(1f, 0.45f, 0.4f, 1f) : new Vector4(1f, 0.85f, 0.4f, 1f);
        ImGui.TextColored(c, ok == true ? "[ok]" : ok == false ? "[x] " : "[..]");
        ImGui.SameLine();
        ImGui.TextWrapped(text);
    }
}
