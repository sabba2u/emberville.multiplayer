using ImGuiNET;
using UnityEngine;

namespace BetterCompanions;

/// <summary>
/// Insert > Companions (the multiplayer mod's menu): who it is and how it is; its name; call it or send it home, back to
/// following you, its inventory (Trade), a potion (Heal); your other companions (swap one in,
/// delete one); a new one, or a copy of one of your characters (the one out goes to your other companions).
/// </summary>
internal static class CompanionMenu
{
    static int slot;
    static (string label, bool can) heal;
    static float healAt;
    static string deleting;   // (the roster's Delete: pressed once, asking)
    static float deletingAt;
    static string name = "";
    static PlayerData named;   // (the companion the box was filled from: a new one fills it again)

    public static void Draw()
    {
        if (!CompanionSave.Has)
        {
            ImGui.Text("No companion yet.");
        }
        else
        {
            var data = CompanionSave.Data;
            ImGui.Text($"{CompanionSave.Describe()}: {Companion.State}");

            // its name
            if (named != data) { named = data; name = data.playerName ?? ""; }
            ImGui.SetNextItemWidth(200f);
            bool enter = ImGui.InputTextWithHint("##name", "its name", ref name, 24, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            string wanted = (name ?? "").Trim();
            bool can = wanted.Length > 0 && wanted != data.playerName;
            if (!can) ImGui.BeginDisabled();
            if ((ImGui.Button("Rename") || enter) && can) Rename(wanted);
            if (!can) ImGui.EndDisabled();

            if (Companion.Out) { if (ImGui.Button("Send home")) Hooks.Say(Companion.Dismiss()); }
            else if (ImGui.Button("Call")) Hooks.Say(Companion.Call());
            if (Companion.Out)
            {
                ImGui.SameLine();
                if (ImGui.Button("Follow me")) Companion.Follow();
                ImGui.SameLine();
                if (ImGui.Button("Trade")) CardActions.Trade();
                ImGui.SameLine();
                if (Time.unscaledTime > healAt) { heal = CardActions.HealOption(); healAt = Time.unscaledTime + 0.5f; }   // (your bag searched: not every frame)
                var (label, canHeal) = heal;
                if (!canHeal) ImGui.BeginDisabled();
                if (ImGui.Button(label + "##heal")) { var said = CardActions.Heal(); if (said != "") Hooks.Say(said); }
                if (!canHeal) ImGui.EndDisabled();
            }
            ImGui.TextDisabled("Its class, attributes and powers: Trade, then Class / Stats on its inventory; its card's slots for powers.");
        }

        // the ones not out
        var kept = CompanionRoster.All();
        ImGui.SeparatorText($"Your other companions ({kept.Count})");
        if (kept.Count == 0) ImGui.TextDisabled("None yet: a companion you replace (a clone, a new one) waits here, to swap back in.");
        else if (ImGui.BeginTable("roster", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            foreach (var k in kept.ToArray())
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(k.Name);
                ImGui.TableNextColumn();
                ImGui.Text($"level {k.Level}" + (k.Class != "" ? $" {k.Class}" : ""));
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"Swap in##in{k.Stamp}")) Hooks.Say(CompanionRoster.SwapIn(k.Stamp));
                ImGui.SameLine();
                bool sure = deleting == k.Stamp && Time.unscaledTime - deletingAt < 15f;
                if (ImGui.SmallButton((sure ? "Sure? Delete" : "Delete") + $"##del{k.Stamp}"))
                {
                    if (sure) { deleting = null; Hooks.Say(CompanionRoster.Delete(k.Stamp)); }
                    else { deleting = k.Stamp; deletingAt = Time.unscaledTime; }
                }
            }
            ImGui.EndTable();
        }
        if (kept.Count > 0) ImGui.TextDisabled("Swap in: the one out now waits here instead, as it is. Delete asks again; a copy stays in the Saves folder.");

        ImGui.SeparatorText(CompanionSave.Has ? "A different companion" : "Get a companion");
        ImGui.TextWrapped("Two ways:");
        ImGui.BulletText("Clone one of your characters: a copy of the character in that save slot (its level, class, gear and skills) fights beside you.");
        ImGui.BulletText("Start a new one: a fresh level 1 adventurer, bare-handed, who grows as you play together.");
        ImGui.BulletText("Or one of ours: Sabba the knight, Ellie the mage, Rory the archer (level 1, their class on, a weapon in hand).");
        // your characters' save slots, by name
        if (!slotsRead) ReadSlots();
        if (!slots.Exists(x => x.slot == slot) && slots.Count > 0) slot = (slots.Find(x => x.slot != CompanionSave.PlayingSlot) ?? slots[0]).slot;
        var picked = slots.Find(x => x.slot == slot);
        ImGui.SetNextItemWidth(300f);
        if (ImGui.BeginCombo("##slot", picked != null ? picked.Label : slots.Count == 0 ? "(no saved characters)" : $"slot {slot}"))
        {
            if (ImGui.IsWindowAppearing()) ReadSlots();   // (opened: the folder again)
            foreach (var x in slots)
            {
                bool mine = x.slot == CompanionSave.PlayingSlot;
                if (mine) ImGui.BeginDisabled();
                if (ImGui.Selectable(x.Label + (mine ? "  (you're playing it)" : ""), x.slot == slot)) slot = x.slot;
                if (mine) ImGui.EndDisabled();
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        bool canClone = picked != null && picked.slot != CompanionSave.PlayingSlot;
        if (!canClone) ImGui.BeginDisabled();
        if (ImGui.Button(picked != null ? $"Clone {picked.Name}" : "Clone")) Commands.Run(slot.ToString());
        if (!canClone) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Start a new one")) Commands.Run("new");
        // the ready-made ones
        ImGui.Text("Or one of ours:");
        for (int i = 0; i < CompanionSave.Presets.Length; i++)
        {
            var p = CompanionSave.Presets[i];
            ImGui.SameLine();
            if (ImGui.Button($"{p.name} ({p.cls})##preset{i}")) { var said = CompanionSave.StartPreset(i); Hooks.Say(said); Companion.Reload(); }
        }
        if (CompanionSave.Has) ImGui.TextDisabled($"{CompanionSave.Data.playerName} isn't lost: it goes to your other companions, to swap back in any time.");

        ImGui.Spacing();
        ImGui.TextColored(new ImGuiNET.Vector4(1f, 0.8f, 0.4f, 1f), "Good to know");
        ImGui.TextWrapped("Companions live outside the game's own save data: each has its own file, next to the character it's with. " +
                          "A clone is a copy, not the character itself: the XP, levels and gear it earns stay with the companion " +
                          "and are not written back to that character's save. Your character's save is never changed by its clone.");
    }

    sealed class SlotInfo
    {
        public int slot;
        public string Name;
        public int Level;
        public string Label => $"Slot {slot}: {Name} (level {Level})";
    }

    static readonly System.Collections.Generic.List<SlotInfo> slots = new();
    static bool slotsRead;

    /// <summary>The save folder's characters (player_slotN.evp): each one's name and level. Read once, and again each
    /// time the list is opened (reading every save is slow: not while the menu draws).</summary>
    static void ReadSlots()
    {
        slotsRead = true;
        slots.Clear();
        try
        {
            string dir = SaveLoad.GetPath("");
            if (!System.IO.Directory.Exists(dir)) return;
            foreach (var f in System.IO.Directory.GetFiles(dir, "player_slot*.evp"))
            {
                var stem = System.IO.Path.GetFileNameWithoutExtension(f);
                if (!int.TryParse(stem.Substring("player_slot".Length), out int n)) continue;
                try
                {
                    var d = JsonUtility.FromJson<PlayerData>(SaveLoad.Load(stem + ".evp"));
                    if (d == null) continue;
                    int level = 1;
                    try { level = System.Math.Max(1, d.GetCurrentLevel(PlayerClassType.None)); } catch { }
                    slots.Add(new SlotInfo { slot = n, Name = string.IsNullOrEmpty(d.playerName) ? "(no name)" : d.playerName, Level = level });
                }
                catch (System.Exception e) { Log.Warn($"Companion menu: slot {n}: {e.Message}"); }
            }
            slots.Sort((a, b) => a.slot.CompareTo(b.slot));
        }
        catch (System.Exception e) { Log.Warn($"Companion menu: the save slots: {e.Message}"); }
    }

    static void Rename(string to)
    {
        var data = CompanionSave.Data;
        if (data == null) return;
        string was = data.playerName;
        data.playerName = to;
        CompanionSave.Write();
        Hooks.Say($"Companion: {was} is now {to}.");
        Log.Info($"Companion: renamed {was} to {to}");
    }
}
