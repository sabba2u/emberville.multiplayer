using BepInEx.Configuration;
using ImGuiNET;
using Unity.Cinemachine;
using UnityEngine;
using Vector2 = UnityEngine.Vector2;
using Vector3 = UnityEngine.Vector3;

namespace BetterCombat;

/// <summary>
/// The camera stays on you. The game's camera trails behind (its follow is damped, has a dead zone you can move in
/// before it moves, and looks ahead of where you're going), so a dash leaves you near the edge of the screen while it
/// catches up. With this on, the camera's lag is cut to CamLag of the game's (0: locked on you), the dead zone and the
/// look-ahead are off, and you're in the middle of the screen. Map edges still stop it (the game's confiner). Off: the
/// game's own camera, exactly as it was.
/// </summary>
public static class CenterCam
{
    static ConfigEntry<bool> on;
    static ConfigEntry<float> lag;

    public static void Bind(ConfigFile config)
    {
        on = config.Bind("Camera", "CenterCam", false, "Keep the camera centred on you (dashes too). /centercam, or the Insert menu");
        lag = config.Bind("Camera", "CamLag", 0f, "How far the camera trails behind you, as a share of the game's own lag (0 = locked on you, 1 = the game's)");
    }

    /// <summary>/centercam: on / off; /centercam 30 sets the lag to 30% of the game's (and turns it on).</summary>
    public static void Command(string arg)
    {
        if (float.TryParse(arg.Trim().TrimEnd('%'), out float pct))
        {
            lag.Value = Mathf.Clamp(pct, 0f, 100f) / 100f;
            on.Value = true;
        }
        else on.Value = !on.Value;
        Log.Info(on.Value ? $"Centred camera: on (lag {lag.Value * 100f:0}% of the game's; /centercam again to turn it off)" : "Centred camera: off (the game's camera)", toast: true);
    }

    public static void DrawMenu()
    {
        ImGui.SeparatorText("Camera");
        bool v = on.Value;
        if (ImGui.Checkbox("Keep the camera centred on you, dashes too (/centercam)", ref v)) on.Value = v;
        if (!on.Value) ImGui.BeginDisabled();
        float s = lag.Value * 100f;
        ImGui.SetNextItemWidth(280);
        if (ImGui.SliderFloat("Camera lag##centercam", ref s, 0f, 100f, "%.0f%%")) lag.Value = s / 100f;
        ImGui.SameLine(); ImGui.TextDisabled("0% locked on you, 100% the game's smooth trailing");
        if (!on.Value) ImGui.EndDisabled();
    }

    // ---- the camera ----

    static CinemachineCamera cam;   // (the one we changed, and the game's values on it)
    static CinemachinePositionComposer composer;
    static CinemachineFollow follow;
    static Vector3 gameDamping, gameFollowDamping;
    static bool gameLookahead, gameDeadZone;
    static Vector2 gameScreen;
    static bool applied;
    static float appliedLag = -1f;
    static float nextLook;

    /// <summary>Every frame: our settings on the game's camera (found again after a scene change), or the game's back.</summary>
    public static void Tick()
    {
        if (Time.unscaledTime >= nextLook || !cam)
        {
            nextLook = Time.unscaledTime + 1f;
            var manager = Object.FindAnyObjectByType<CameraManager>();
            var now = manager ? manager.mainCam : null;
            if (now != cam) Take(now);
        }
        if (!cam) return;
        if (on.Value && (!applied || appliedLag != lag.Value)) Apply();
        else if (!on.Value && applied) Restore();
    }

    /// <summary>A new camera: what the game set on it, kept to put back.</summary>
    static void Take(CinemachineCamera c)
    {
        if (applied) Restore();
        cam = c;
        composer = c ? c.GetComponent<CinemachinePositionComposer>() : null;
        follow = c ? c.GetComponent<CinemachineFollow>() : null;
        if (composer)
        {
            gameDamping = composer.Damping;
            gameLookahead = composer.Lookahead.Enabled;
            gameDeadZone = composer.Composition.DeadZone.Enabled;
            gameScreen = composer.Composition.ScreenPosition;
        }
        if (follow) gameFollowDamping = follow.TrackerSettings.PositionDamping;
        if (c) Log.Info($"Centred camera: the game's camera is {(composer ? $"a position composer (lag {gameDamping}, look-ahead {gameLookahead}, dead zone {gameDeadZone})" : follow ? $"a follow (lag {gameFollowDamping})" : "neither kind we know: left as it is")}");
    }

    static void Apply()
    {
        float k = Mathf.Clamp01(lag.Value);
        if (composer)
        {
            composer.Damping = gameDamping * k;
            var l = composer.Lookahead; l.Enabled = false; composer.Lookahead = l;
            var comp = composer.Composition;
            comp.DeadZone.Enabled = false;
            comp.ScreenPosition = Vector2.zero;   // (the middle)
            composer.Composition = comp;
        }
        if (follow)
        {
            var t = follow.TrackerSettings; t.PositionDamping = gameFollowDamping * k; follow.TrackerSettings = t;
        }
        applied = true;
        appliedLag = lag.Value;
    }

    static void Restore()
    {
        if (composer)
        {
            composer.Damping = gameDamping;
            var l = composer.Lookahead; l.Enabled = gameLookahead; composer.Lookahead = l;
            var comp = composer.Composition;
            comp.DeadZone.Enabled = gameDeadZone;
            comp.ScreenPosition = gameScreen;
            composer.Composition = comp;
        }
        if (follow)
        {
            var t = follow.TrackerSettings; t.PositionDamping = gameFollowDamping; follow.TrackerSettings = t;
        }
        applied = false;
        appliedLag = -1f;
    }
}
