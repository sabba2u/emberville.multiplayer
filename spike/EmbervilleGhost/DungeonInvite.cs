using ImGuiNET;
using UnityEngine;
using Vector2 = ImGuiNET.Vector2;
using Vector4 = ImGuiNET.Vector4;

namespace EmbervilleGhost;

/// <summary>
/// When a teammate goes into a generated dungeon (or down a floor of the descent), a card at the top of the screen
/// says so, with Join and Not now (45 s, or until answered). The buttons are hit-tested from the mouse here (as the
/// dungeon's Leave button is: ImGui doesn't get the mouse while you're playing); while the mouse is on one, the game's
/// controls pause, so the click doesn't swing a weapon too.
/// </summary>
internal static class DungeonInvite
{
    static Rect joinBox, noBox;
    static int hovered;   // 0 none, 1 join, 2 not now

    public static void Tick()
    {
        hovered = 0;
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse != null && Application.isFocused && Dungeon.HasInvite && !PlayerStatusUtil.LocalBusy)
        {
            var p = mouse.position.ReadValue();
            var at = new UnityEngine.Vector2(p.x, Screen.height - p.y);
            if (joinBox.width > 0 && joinBox.Contains(at) && Dungeon.CanJoinInvite) hovered = 1;
            else if (noBox.width > 0 && noBox.Contains(at)) hovered = 2;
            if (hovered != 0 && mouse.leftButton.wasReleasedThisFrame)
            {
                if (hovered == 1) Dungeon.JoinInvite(); else Dungeon.DismissInvite();
                hovered = 0;
            }
        }
        if (hovered != 0) GameInput.Block("invite"); else GameInput.Unblock("invite");
    }

    public static void Draw()
    {
        joinBox = noBox = default;
        if (!Dungeon.HasInvite) return;
        var vp = ImGui.GetMainViewport();
        float y = vp.WorkPos.y + (Dungeon.Active ? 44f : 8f);   // under the dungeon's floor bar when we're in one
        ImGui.SetNextWindowPos(new Vector2(vp.WorkPos.x + vp.WorkSize.x / 2f, y), ImGuiCond.Always, new Vector2(0.5f, 0f));
        ImGui.SetNextWindowBgAlpha(0.7f);
        if (ImGui.Begin("##dungeoninvite", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav))
        {
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.4f, 1f), Dungeon.InviteText());
            if (Dungeon.CanJoinInvite)
            {
                ImGui.SameLine();
                if (hovered == 1) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonHovered));
                ImGui.SmallButton("Join");
                if (hovered == 1) ImGui.PopStyleColor();
                joinBox = Box();
            }
            ImGui.SameLine();
            if (hovered == 2) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonHovered));
            ImGui.SmallButton("Not now");
            if (hovered == 2) ImGui.PopStyleColor();
            noBox = Box();
        }
        ImGui.End();
    }

    static Rect Box()
    {
        var a = ImGui.GetItemRectMin(); var b = ImGui.GetItemRectMax();
        return new Rect(a.x, a.y, b.x - a.x, b.y - a.y);
    }
}
