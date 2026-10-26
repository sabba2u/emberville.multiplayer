using BepInEx;
using BepInEx.Logging;
using System.Linq;

namespace BetterCompanions;

/// <summary>
/// Better Companions: an adventurer made from one of your other characters (/companion 2 copies save slot 2 into the
/// companion's own save; the game's slot is never written). It follows you and fights, enemies fight it back, F opens
/// the game's inventory with its gear on top, and it levels up on its own. Works on its own; next to the multiplayer
/// mod, everyone sees everyone's companion.
/// </summary>
[BepInPlugin(Guid, "Better Companions", Version)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "spike.emberville.bettercompanions";
    public const string Version = "0.2.0";
    internal static ManualLogSource Logger_;
    static BepInEx.Configuration.ConfigEntry<int> copyFrom;

    void Awake()
    {
        Logger_ = Logger;
        var harmony = new HarmonyLib.Harmony(Guid);
        harmony.PatchAll(typeof(Plugin).Assembly);
        RealPatches.PatchFinds(harmony);
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
        Logger.LogInfo($"Better Companions v{Version}: /companion");
    }

    void OnDestroy() { GearScreen.Close(); Aggro.ReleaseAll(); }

    void Update()
    {
        try { GearScreen.Tick(Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null); }   // (first, and on its own: the mode must always be able to end)
        catch (System.Exception e) { Logger.LogWarning(e); }
        try { Companion.Tick(); }
        catch (System.Exception e) { Logger.LogWarning(e); }
        try { CompanionPanel.Tick(Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null); CompanionPanel.Attributes.Tick(); CompanionPanel.Classes.Tick(); CompanionPanel.Powers.Tick(); }
        catch (System.Exception e) { Logger.LogWarning(e); }
    }

    // ---- for the multiplayer mod: our companion, to show on teammates' screens ----

    /// <summary>Its power slot i (0-3), for its card: the skill's icon (none: empty), cooldown left / total in seconds,
    /// running now, and Time.time when it last cast it.</summary>
    /// <summary>Its card's slot i clicked: that power now. Empty: done; else why not.</summary>
    public static string UseCompanionPower(int i) => RealSkills.UseNow(RealPlayer.Current, i);

    /// <summary>The powers it could put in a slot (active ones with points on its own tree): icon, name, rank, ID.</summary>
    public static System.Collections.Generic.List<(UnityEngine.Sprite icon, string name, int rank, string id)> CompanionPowerChoices() =>
        CompanionPanel.Powers.LearnedNodes().Select(n => (n.skill.uiIcon, SkillName(n.skill), n.CurrentPoints, n.skill.ID)).ToList();

    /// <summary>Power slot i (0-3) set to that skill (its ID; null: empty), into its save.</summary>
    public static void SetCompanionPower(int i, string id)
    {
        var skill = id == null ? null : CompanionPanel.Powers.LearnedNodes().Select(n => n.skill).FirstOrDefault(s => s.ID == id);
        CompanionPanel.Powers.Set(i, skill);
    }

    static string SkillName(Skill s)
    {
        try { var n = s.Name.GetLocalizedString(); if (!string.IsNullOrEmpty(n)) return n; } catch { }
        return s.name;
    }

    public static bool CompanionPower(int i, out UnityEngine.Sprite icon, out float cooldownLeft, out float cooldownTotal, out bool running, out float castAt) =>
        RealSkills.Power(i, out icon, out cooldownLeft, out cooldownTotal, out running, out castAt);

    /// <summary>The companion's body (null: none out), its animators, facing, health 0..1, downed, name.</summary>
    public static UnityEngine.GameObject CompanionRoot => RealPlayer.Current ? RealPlayer.Current.gameObject : Companion.Body != null && !Companion.Body.Lost ? Companion.Body.Root : null;
    public static UnityEngine.Animator[] CompanionAnimators => RealPlayer.Current ? RealPlayer.Current.GetComponentsInChildren<UnityEngine.Animator>() : Companion.Body?.Animators;
    public static bool CompanionFlipped => RealPlayer.Current ? RealPlayer.Current.isFliped : Companion.Body != null && Companion.Body.Flipped;
    public static float CompanionHealth => RealPlayer.Current ? RealPlayer.Current.HealthPercent
        : Companion.Body?.Unit ? (float)Companion.Body.Unit.Health / UnityEngine.Mathf.Max(1, Companion.Body.Unit.MaxHealth) : 0f;
    public static bool CompanionDown => RealPlayer.Current ? RealPlayer.Down(RealPlayer.Current) : Companion.Body?.Unit && Companion.Body.Unit.Revive.Down;
    public static string CompanionName => CompanionSave.Data?.playerName ?? "";
    public static int CompanionLevel => CompanionSave.Level;
    /// <summary>Its Kymia, 0..1; -1 when it has none to show (no real companion out, or no Kymia at all).</summary>
    public static float CompanionKymia
    {
        get
        {
            var p = RealPlayer.Current;
            if (!p) return -1f;
            float max; try { max = p.MaxKymiaPoints; } catch { return -1f; }
            return max > 0f ? UnityEngine.Mathf.Clamp01(p.CurrentKymiaPoints / max) : -1f;
        }
    }
    /// <summary>Its card's menu: the Heal button's words (and whether it can be pressed), healing it with your potion, trading.</summary>
    /// <summary>Insert > Companions (the multiplayer mod's menu).</summary>
    public static void DrawMenu() => CompanionMenu.Draw();

    // ---- its levels by hand (the multiplayer mod's Respec, in Insert > Companions) ----

    /// <summary>Its body (the real one: null with the old body, or none out) and its save.</summary>
    public static PlayerMaster CompanionPlayer => RealPlayer.Current;
    public static PlayerData CompanionData => CompanionSave.Data;
    public static void SaveCompanion() => CompanionSave.Write();

    /// <summary>XP as a kill gives it: to its level, its class, the weapon in hand, the armour worn.</summary>
    public static void CompanionKillXp(int xp)
    {
        var p = RealPlayer.Current;
        var data = CompanionSave.Data;
        if (!p || data == null) return;
        var stats = p.characterStats;
        var key = stats.baseClass.playerClassType;
        data.GetPlayerExperience(key);   // (first: AddXP on a key it hasn't got goes wrong)
        data.AddXP(key, xp);
        var cls = stats.equippedClass;
        if (cls) { data.GetPlayerExperience(cls.playerClassType); data.AddXP(cls.playerClassType, xp); }
        if (p.currentWeapon) { data.GetPlayerExperience(p.currentWeapon.weaponCategory); data.AddXP(p.currentWeapon.weaponCategory, xp); }
        RealPlayer.ArmorXP(data, stats, xp);
    }

    /// <summary>Its tree changed (a respec): its skills from its save again, its power slots and class screen too.</summary>
    public static void CompanionSkillsChanged()
    {
        var p = RealPlayer.Current;
        if (!p || !p.SkillHandler) return;
        foreach (var s in p.SkillHandler.availableSkills.Keys.ToList())
            try { p.SkillHandler.RemoveSkill(s, update: false); } catch (System.Exception e) { Log.Warn($"Companion skills: {s.name}: {e.Message}"); }
        try { RealSkills.Load(p); } catch (System.Exception e) { Log.Warn($"Companion skills: {e.Message}"); }
        CompanionPanel.Powers.Refresh();
        if (CompanionPanel.Classes.On) CompanionPanel.Classes.Refresh();
    }

    public static (string label, bool can) HealOption() => CardActions.HealOption();
    public static string Heal() => CardActions.Heal();
    public static void Trade() => CardActions.Trade();
    /// <summary>Holding F at it: how far the revive has got (0..1).</summary>
    public static float CompanionRevive => RealBrain.ReviveProgress;

    static ItemInfoWindow[] tips = new ItemInfoWindow[0];
    static float tipsAt = -10f;

    /// <summary>One of the game's item hover boxes is showing (the inventory's, the F5-F8 bar's, the weapon wheel's).</summary>
    static bool TipShowing()
    {
        if (UnityEngine.Time.unscaledTime - tipsAt > 2f)
        {
            tipsAt = UnityEngine.Time.unscaledTime;
            tips = UnityEngine.Object.FindObjectsByType<ItemInfoWindow>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None);
        }
        foreach (var t in tips) if (t && t.gameObject.activeInHierarchy) return true;
        return false;
    }

    /// <summary>Our companion's name tag and health bar (ImGui: from the multiplayer mod's HUD).</summary>
    public static void DrawHud()
    {
        var cam = UnityEngine.Camera.main;
        if (!cam || TipShowing()) return;   // (an item's hover box is up: the tag, drawn over all the game's UI, would cover it)
        if (RealPlayer.Current) { CompanionMarks.Tag(cam, RealPlayer.Current.transform.position, CompanionName, CompanionHealth, CompanionDown, RealBrain.ReviveProgress); return; }
        var b = Companion.Body;
        if (b == null || b.Lost || !b.Unit) return;
        CompanionMarks.Tag(cam, b.Position, CompanionName, CompanionHealth, CompanionDown, Companion.ReviveProgress);
    }

    /// <summary>A teammate revived our downed companion on their screen.</summary>
    public static void Revived() => Companion.RevivedByTeammate();

    /// <summary>A middle-click order from the multiplayer mod: the companion goes there and fights.</summary>
    public static void Send(UnityEngine.Vector3 point) => Companion.Send(point);
    /// <summary>With <paramref name="stay"/> (Shift): it goes there and holds the spot, guarding it.</summary>
    public static void Send(UnityEngine.Vector3 point, bool stay) => Companion.Send(point, stay);
    /// <summary>Holding a spot: back to following you (a middle click on it).</summary>
    public static void Follow() => Companion.Follow();
    /// <summary>The spot it's holding, if it is.</summary>
    public static UnityEngine.Vector3? Post => RealBrain.Post;

    /// <summary>A line drawn with the middle mouse (the multiplayer mod): the companion walks it.</summary>
    public static void SendPath(System.Collections.Generic.IList<UnityEngine.Vector3> points) => Companion.SendPath(points);
    public static void SendPath(System.Collections.Generic.IList<UnityEngine.Vector3> points, bool stay) => Companion.SendPath(points, stay);

    /// <summary>A companion is out right now (pings become orders to it: the red mark).</summary>
    public static bool CompanionOut => CompanionRoot;

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
