using BepInEx;
using BepInEx.Logging;

namespace BetterCompanion;

/// <summary>
/// Better Companion: an adventurer made from one of your other characters (/companion 2 copies save slot 2 into the
/// companion's own save; the game's slot is never written). It follows you and fights, enemies fight it back, F opens
/// the game's inventory with its gear on top, and it levels up on its own. Works on its own; next to the multiplayer
/// mod, everyone sees everyone's companion.
/// </summary>
[BepInPlugin(Guid, "Better Companion", Version)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "spike.emberville.bettercompanion";
    public const string Version = "0.1.0";
    internal static ManualLogSource Logger_;
    static BepInEx.Configuration.ConfigEntry<int> copyFrom;

    void Awake()
    {
        Logger_ = Logger;
        new HarmonyLib.Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
        copyFrom = Config.Bind("Companion", "CopyFromSlot", 0,
            "Without the multiplayer mod (no chat for /companion): set a save slot number here, and the next time you load a character its companion is copied from that slot (0: leave it). Set back to 0 once done.");
        // (ours runs among the game's own load handlers: a throw here would stop the game loading the save, so never)
        GameData.Register(GameData.Call.OnLoad, (_, _) =>
        {
            try
            {
                CompanionSave.Load();
                if (copyFrom.Value > 0) { Log.Info(CompanionSave.CloneFrom(copyFrom.Value), toast: true); copyFrom.Value = 0; }
                Companion.ReloadSoon();
            }
            catch (System.Exception e) { Logger.LogWarning(e); }
        });
        Logger.LogInfo($"Better Companion v{Version}: /companion");
    }

    void OnDestroy() { GearScreen.Close(); Aggro.ReleaseAll(); }

    void Update()
    {
        try { GearScreen.Tick(Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null); }   // (first, and on its own: the mode must always be able to end)
        catch (System.Exception e) { Logger.LogWarning(e); }
        try { Companion.Tick(); }
        catch (System.Exception e) { Logger.LogWarning(e); }
    }

    // ---- for the multiplayer mod: our companion, to show on teammates' screens ----

    /// <summary>The companion's body (null: none out), its animators, facing, health 0..1, downed, name.</summary>
    public static UnityEngine.GameObject CompanionRoot => Companion.Body != null && !Companion.Body.Lost ? Companion.Body.Root : null;
    public static UnityEngine.Animator[] CompanionAnimators => Companion.Body?.Animators;
    public static bool CompanionFlipped => Companion.Body != null && Companion.Body.Flipped;
    public static float CompanionHealth => Companion.Body?.Unit ? (float)Companion.Body.Unit.Health / UnityEngine.Mathf.Max(1, Companion.Body.Unit.MaxHealth) : 0f;
    public static bool CompanionDown => Companion.Body?.Unit && Companion.Body.Unit.Revive.Down;
    public static string CompanionName => CompanionSave.Data?.playerName ?? "";

    /// <summary>Our companion's name tag and health bar (ImGui: from the multiplayer mod's HUD).</summary>
    public static void DrawHud()
    {
        var b = Companion.Body;
        var cam = UnityEngine.Camera.main;
        if (b == null || b.Lost || !b.Unit || !cam) return;
        CompanionMarks.Tag(cam, b.Position, CompanionName, CompanionHealth, CompanionDown, Companion.ReviveProgress);
    }

    /// <summary>A teammate revived our downed companion on their screen.</summary>
    public static void Revived() => Companion.RevivedByTeammate();

    /// <summary>A middle-click order from the multiplayer mod: the companion goes there and fights.</summary>
    public static void Send(UnityEngine.Vector3 point) => Companion.Send(point);

    /// <summary>A chat command ("companion ..."); true if it was ours.</summary>
    public static bool Command(string cmd)
    {
        cmd = (cmd ?? "").Trim();
        string low = cmd.ToLowerInvariant();
        if (low != "companion" && !low.StartsWith("companion ")) return false;
        Commands.Run(cmd.Length > 9 ? cmd.Substring(10) : "");
        return true;
    }
}
