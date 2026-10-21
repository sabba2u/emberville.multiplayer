using BepInEx.Configuration;
using HarmonyLib;
using ImGuiNET;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using Vector2 = UnityEngine.Vector2;
using Vector3 = UnityEngine.Vector3;

namespace EmbervilleGhost;

/// <summary>
/// Mouse walking (like our BDSP mod): hold the mouse side button and our character walks toward the mouse. The point
/// under the mouse, relative to the player, becomes the move stick (PlayerMaster.GetInput reads Player.Move into
/// `input` every Update; a postfix replaces it), so the game's own movement does the walking and walls still block.
/// A small circle around the player is a dead zone; further out = faster, full speed at the outer ring. A quick click walks to that spot in a straight line (stops when there, stuck, or after a few seconds).
/// Keyboard / gamepad movement always wins. Settings: Insert menu.
/// </summary>
[HarmonyPatch]
internal static class MouseWalk
{
    static ConfigEntry<bool> on;
    static ConfigEntry<string> button;
    static ConfigEntry<float> deadZone, fullSpeed;

    const float TapSeconds = 0.25f, TapArrive = 0.3f, TapMaxSeconds = 6f, StuckSeconds = 0.6f;
    static readonly string[] Buttons = { "Back", "Forward", "Middle" };

    static bool holding;
    static float pressedAt;
    static bool hasTarget;
    static Vector2 target;
    static float targetSince, lastProgressAt, bestDistance;

    public static void Bind(ConfigFile config)
    {
        on = config.Bind("MouseWalk", "Enabled", true, "Hold a mouse button to walk toward the mouse; a quick click walks to that spot");
        button = config.Bind("MouseWalk", "Button", "Forward", "Which mouse button: Back (side / thumb button), Forward (the other side button) or Middle");
        deadZone = config.Bind("MouseWalk", "DeadZone", 0.4f, "No walking when the mouse is this close to your character (world units)");
        fullSpeed = config.Bind("MouseWalk", "FullSpeedDistance", 2.5f, "Full speed from this far out (world units); closer = slower");
    }

    static ButtonControl Button(Mouse m) => button.Value switch
    {
        "Forward" => m.forwardButton,
        "Middle" => m.middleButton,
        "Back" => m.backButton,
        _ => m.forwardButton,
    };

    static bool Blocked => PlayerStatusUtil.LocalBusy || Chat.IsOpen || ImGuiUnity.Typing || !Application.isFocused;

    /// <summary>Every frame (Plugin.Update): the button's press / release, tap targets.</summary>
    public static void Tick()
    {
        var m = Mouse.current;
        if (on == null || !on.Value || m == null) { holding = false; hasTarget = false; return; }
        var b = Button(m);
        if (b.wasPressedThisFrame && !Blocked) { holding = true; pressedAt = Time.unscaledTime; hasTarget = false; }
        if (holding && b.wasReleasedThisFrame)
        {
            holding = false;
            if (Time.unscaledTime - pressedAt < TapSeconds && MouseWorld(out var at))
            {
                hasTarget = true; target = at; targetSince = lastProgressAt = Time.unscaledTime; bestDistance = float.MaxValue;
            }
        }
        if (!b.isPressed) holding = false;
    }

    static bool MouseWorld(out Vector2 at)
    {
        at = default;
        var cam = Camera.main;
        var m = Mouse.current;
        if (!cam || m == null) return false;
        var p = cam.ScreenToWorldPoint(m.position.ReadValue());
        at = new Vector2(p.x, p.y);
        return true;
    }

    [HarmonyPatch(typeof(PlayerMaster), "GetInput"), HarmonyPostfix]
    static void AfterGetInput(PlayerMaster __instance)
    {
        if (on == null || !on.Value || (!holding && !hasTarget) || !ReferenceEquals(__instance, LocalPlayer.Get())) return;
        if (__instance.input.sqrMagnitude > 0.01f || Blocked) { hasTarget = false; return; }   // keys / pad win
        Vector2 me = __instance.transform.position;

        if (holding)
        {
            if (Time.unscaledTime - pressedAt < 0.08f || !MouseWorld(out var at)) return;   // a tap: wait for its release
            __instance.input = Stick(at - me);
            return;
        }

        // Walking to a clicked spot.
        float dist = Vector2.Distance(me, target);
        if (dist < TapArrive || Time.unscaledTime - targetSince > TapMaxSeconds) { hasTarget = false; return; }
        if (dist < bestDistance - 0.05f) { bestDistance = dist; lastProgressAt = Time.unscaledTime; }
        else if (Time.unscaledTime - lastProgressAt > StuckSeconds) { hasTarget = false; return; }   // a wall
        __instance.input = (target - me).normalized;
    }

    /// <summary>Mouse offset -> move stick: nothing inside the dead zone, then up to full speed at the outer ring.</summary>
    static Vector2 Stick(Vector2 offset)
    {
        float d = offset.magnitude;
        if (d < deadZone.Value) return Vector2.zero;
        float speed = Mathf.Clamp01((d - deadZone.Value) / Mathf.Max(0.01f, fullSpeed.Value - deadZone.Value));
        return offset / d * Mathf.Max(0.35f, speed);
    }

    /// <summary>From Hud.Draw: a marker on a clicked spot (nothing while held).</summary>
    public static void Draw()
    {
        if (on == null || !on.Value || holding || !hasTarget) return;
        var cam = Camera.main;
        if (!cam) return;
        var dl = ImGui.GetBackgroundDrawList();
        var t = ToScreen(cam, target);
        uint col = ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(1f, 0.85f, 0.4f, 0.8f));
        dl.AddCircle(t, 8f, col, 16, 2f);
        dl.AddCircleFilled(t, 3f, col);
    }

    static ImGuiNET.Vector2 ToScreen(Camera cam, Vector3 world)
    {
        var s = cam.WorldToScreenPoint(world);
        return new ImGuiNET.Vector2(s.x, Screen.height - s.y);
    }

    public static void DrawMenu()
    {
        ImGui.Separator();
        bool v = on.Value;
        if (ImGui.Checkbox("Mouse walking: hold a mouse button to walk toward the mouse, click to walk there", ref v)) on.Value = v;
        if (!on.Value) return;
        int i = System.Array.IndexOf(Buttons, button.Value);
        if (i < 0) i = 0;
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("Button##mousewalk", ref i, "Side button (Back)\0Side button (Forward)\0Middle (also pings)\0")) button.Value = Buttons[i];
    }
}
