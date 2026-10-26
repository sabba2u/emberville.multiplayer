namespace BetterCompanions.Core;

/// <summary>How the companion fights: walks up and swings, or keeps its distance and shoots.</summary>
public enum Style { Melee, Ranged }

/// <summary>
/// The fixed numbers of the companion (tiles and seconds), and the small rules everything else asks: which weapons
/// shoot, what the companion's save is called, which game slot a save prefix names, and whether a slot may be copied.
/// </summary>
public static class Rules
{
    public const float FollowGap = 2f, Leash = 12f, TeleportFar = 20f, StuckSeconds = 4f, CutOffSeconds = 1f;
    public const float ReviveHold = 2f, ReviveHealth = 0.5f, XpWindow = 5f;

    /// <summary>The game's WeaponCategory: bows (9) and staffs (12) shoot; everything else is fought up close.</summary>
    public static Style StyleOf(int weaponCategory) => weaponCategory is 9 or 12 ? Style.Ranged : Style.Melee;

    public static float AttackRange(Style s) => s == Style.Ranged ? 6f : 1f;

    /// <summary>The companion's own save (no extension), one per character you play.</summary>
    public static string CompanionFile(int playingSlot) => "companion_slot" + playingSlot;

    /// <summary>"player_slot3" → 3 (the game's save prefix for a character); anything else → null.</summary>
    public static int? SlotFromPrefix(string prefix)
    {
        const string head = "player_slot";
        if (string.IsNullOrEmpty(prefix) || !prefix.StartsWith(head)) return null;
        return int.TryParse(prefix.Substring(head.Length), out int n) && n > 0 ? n : null;
    }

    /// <summary>May game slot <paramref name="slot"/> be copied into the companion? Null if so, else why not.</summary>
    public static string CanClone(int slot, int playingSlot, bool fileExists)
    {
        if (slot < 1) return $"there's no save slot {slot} (slots start at 1)";
        if (slot == playingSlot) return $"slot {slot} is the character you're playing";
        return null;   // (an empty slot: a brand-new adventurer)
    }
}

/// <summary>
/// Whether the inventory screen is showing the companion (opened from it) or the player (opened with I): every
/// opening decides afresh, and the mode ends the moment neither the inventory nor the character panel shows (closed,
/// or another panel took over), so the companion's top half can never stick to an ordinary I. Tick once a frame.
/// </summary>
public class GearMode
{
    public const int ShowWithin = 120;   // frames: opened from the companion but never shown → give up
    public bool On { get; private set; }
    bool shown;
    int waited;

    public void Opened(bool fromCompanion) { On = fromCompanion; shown = false; waited = 0; }
    public void Closed() { On = false; shown = false; }

    public void Tick(bool inventoryShows, bool statsShows)
    {
        if (!On) return;
        if (inventoryShows || statsShows) { shown = true; return; }
        if (shown || ++waited >= ShowWithin) Closed();
    }
}

/// <summary>A question asked twice: the second ask of the same thing within 15 s is the yes.</summary>
public class Confirm
{
    const float Window = 15f;
    string asked;
    float at;

    public bool Ask(string what, float now)
    {
        bool yes = asked == what && now - at <= Window;
        if (yes) { asked = null; return true; }
        asked = what;
        at = now;
        return false;
    }
}
