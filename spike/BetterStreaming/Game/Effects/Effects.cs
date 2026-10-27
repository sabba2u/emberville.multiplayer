using System.Linq;
using BetterStreaming.Core;
using UnityEngine;

namespace BetterStreaming;

/// <summary>The effects there are (part 1): each registered with the runner by its catalogue id.</summary>
internal static class Effects
{
    public static void Register()
    {
        foreach (IEffect fx in new IEffect[] { new Heal(), new Status("poison", EffectType.Poison), new Status("fire", EffectType.Burn, me => FireSound.Start(me), me => FireSound.Stop(me)), new InstantDeath(), new ChainLightning(), new Raid(), new ChickenRaid(), new Freeze(), new MysteryChest() })
            Streamer.Effects[fx.Id] = fx;
        Controls.Register();
        CameraFx.Register();
        Filters.Register();
        FakeCursors.Register();
        WeaponLock.Register();
        Streamer.Effects["roulette"] = new WeaponRoulette();
        NiceEffects.Register();
        Streamer.Effects["random2"] = new RandomPack("random2", 2);
        Streamer.Effects["random3"] = new RandomPack("random3", 3);
        Streamer.Effects["storm"] = new Storm();
    }

    public static PlayerMaster Player => Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;

    /// <summary>
    /// A ring under the player, in a colour: a copy of the player's extra-effects animator (as the game's
    /// PlayExtraVFXAsync makes one), its sprites and particles tinted, the speed-buff swirl played, gone after 2 s.
    /// </summary>
    public static void Glow(PlayerMaster p, Color tint)
    {
        var source = p ? p.extraFxAnimator : null;
        if (!source) return;
        try
        {
            var copy = Object.Instantiate(source, source.transform.parent);
            copy.gameObject.name = "BS_Glow";
            foreach (var sr in copy.GetComponentsInChildren<SpriteRenderer>(true)) sr.color = new Color(tint.r, tint.g, tint.b, sr.color.a);
            foreach (var ps in copy.GetComponentsInChildren<ParticleSystem>(true)) { var main = ps.main; main.startColor = tint; }
            copy.gameObject.SetActive(true);
            copy.SetTrigger("FastFoot");   // (the speed-buff swirl at the feet: a ring with no lettering; level up has "LVL UP" in it)
            Object.Destroy(copy.gameObject, 2f);
        }
        catch (System.Exception e) { Log.Warn($"Streaming: the glow: {e.Message}"); }
    }

    /// <summary>One of the game's own extra effects on you (LifeRecovery, Spawn...), on a copy so it cuts nothing off.</summary>
    public static void Burst(PlayerMaster p, string trigger)
    {
        var source = p ? p.extraFxAnimator : null;
        if (!source) return;
        try
        {
            var copy = Object.Instantiate(source, source.transform.parent);
            copy.gameObject.name = "BS_Burst";
            copy.gameObject.SetActive(true);
            copy.SetTrigger(trigger);
            Object.Destroy(copy.gameObject, 2f);
        }
        catch (System.Exception e) { Log.Warn($"Streaming: {trigger}: {e.Message}"); }
    }

    public static readonly Color Green = new Color(0.35f, 1f, 0.3f), Red = new Color(1f, 0.25f, 0.25f), Ice = new Color(0.55f, 0.85f, 1f);

    /// <summary>The word over you when it lands (the game's floating combat text, as "Stunned" is); null: none.</summary>
    public static string WordOf(string id) => id switch
    {
        "heal" => "Healed!",
        "poison" => "Poisoned",
        "death" => "Doomed",
        "chainlightning" => "Lightning!",
        "raid" => "Raid!",
        "chickens" => "Chickens!",
        "freeze" => "Frozen",
        "chest" => "A chest!",
        "backwards" => "Backwards!", "autowalk" => "Can't stop!", "swapped" => "Buttons swapped", "randomdodge" => "Twitchy!",
        "invisible" => "Invisible", "upsidedown" => "Upside down!", "mirror" => "Mirrored", "sideways" => "Sideways!",
        "dizzy" => "Dizzy...", "seasick" => "Seasick", "earthquake" => "Earthquake!", "zoomin" => "Too close!", "zoomout" => "So far away",
        "drunk" => "*hic*", "colourblind" => "Colour blind", "tunnel" => "Tunnel vision", "lightsout" => "Lights out!", "retro" => "Retro!", "cursors" => "Which one is me?",
        "fists" => "Fists only!", "fire" => "On fire!", "storm" => "Storm!", "roulette" => "Weapon roulette!", "random2" => "Double trouble!", "random3" => "Triple trouble!",
        "shield" => "Shielded!", "kymia" => "Infinite Kymia!", "regen" => "Regenerating", "haste" => "Haste!", "berserk" => "BERSERK!",
        "smite" => "Smite!", "battlechickens" => "Bawk bawk!", "guardians" => "Guardians!", "secondwind" => "Second wind ready",
        "loot" => "Loot!", "gold" => "Gold!", "goldenpet" => "Golden!", "fireworks" => "Woo!",
        "shock" => "Shocked!", "hireling" => "Reinforcements!", "bleed" => "Bleeding!",
        _ => null,
    };

    /// <summary>The word floating up over the player.</summary>
    public static void Say(PlayerMaster p, string word)
    {
        if (!p || string.IsNullOrEmpty(word)) return;
        try { Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, word, p.transform.position, DamageResult.None)); }
        catch (System.Exception e) { Log.Warn($"Streaming: the word over you: {e.Message}"); }
    }

    /// <summary>Each effect's colour: its banner, its countdown, and the ring under you when it lands.</summary>
    public static Color ColourOf(string id) => id switch
    {
        "heal" => Red,
        "poison" => Green,
        "death" => new Color(0.75f, 0.08f, 0.1f),
        "chainlightning" => new Color(0.4f, 0.7f, 1f),
        "raid" => new Color(0.75f, 0.4f, 1f),
        "chickens" => new Color(1f, 0.8f, 0.3f),
        "freeze" => Ice,
        "chest" => new Color(1f, 0.82f, 0.35f),
        "backwards" or "autowalk" or "swapped" or "randomdodge" => new Color(1f, 0.6f, 0.2f),          // controls: orange
        "upsidedown" or "mirror" or "sideways" or "dizzy" or "seasick" or "earthquake" or "zoomin" or "zoomout" => new Color(1f, 0.45f, 0.75f),   // the camera: pink
        "drunk" => new Color(0.95f, 0.75f, 0.3f), "colourblind" => new Color(0.75f, 0.75f, 0.75f), "tunnel" or "lightsout" => new Color(0.55f, 0.5f, 0.7f),
        "retro" => new Color(0.62f, 0.78f, 0.42f), "invisible" => new Color(0.85f, 0.9f, 1f),
        "fists" or "roulette" => new Color(0.9f, 0.35f, 0.3f),
        "fire" => new Color(1f, 0.45f, 0.1f),
        "random2" or "random3" => new Color(0.4f, 1f, 0.9f),
        "shield" => new Color(0.5f, 0.85f, 1f), "kymia" => new Color(0.35f, 0.55f, 1f), "regen" => new Color(0.5f, 1f, 0.5f),
        "haste" => new Color(1f, 1f, 0.45f), "berserk" => new Color(1f, 0.15f, 0.15f), "smite" => new Color(1f, 0.95f, 0.6f),
        "battlechickens" => new Color(1f, 0.85f, 0.5f), "guardians" => new Color(0.55f, 1f, 0.6f), "secondwind" => new Color(1f, 0.75f, 0.85f),
        "loot" or "gold" or "goldenpet" => new Color(1f, 0.82f, 0.3f), "fireworks" => new Color(1f, 0.5f, 1f),
        "shock" => new Color(0.6f, 0.85f, 1f), "hireling" => new Color(0.4f, 0.9f, 0.85f), "bleed" => new Color(0.85f, 0.1f, 0.15f), "storm" => new Color(0.45f, 0.55f, 1f),
        _ => Color.white,
    };

    /// <summary>Most effects need a player to land on.</summary>
    public static bool HavePlayer(out string why)
    {
        why = Player ? "" : "no player";
        return Player;
    }
}

/// <summary>Full health.</summary>
internal sealed class Heal : IEffect
{
    public string Id => "heal";
    public bool Available(out string why) { why = ""; return true; }
    public bool Ready(out string why) => Effects.HavePlayer(out why);
    public bool Fire(Pending p, float size) { Effects.Player.RestoreHealthPercentage(1f); return true; }
    public void Stop() { }
}

/// <summary>
/// One of the game's damage-over-time statuses on you, for the effect's time, with the game's own look (poison: green
/// bubbles; burn: flames): poison, and on fire. They take you down to 1 health, never further (TwitchDeath).
/// </summary>
internal sealed class Status : IEffect
{
    public string Id { get; }
    readonly EffectType type;

    readonly System.Action<PlayerMaster> landing, ending;

    public Status(string id, EffectType type, System.Action<PlayerMaster> landing = null, System.Action<PlayerMaster> ending = null)
    { Id = id; this.type = type; this.landing = landing; this.ending = ending; }

    public bool Available(out string why) { why = ""; return true; }
    public bool Ready(out string why) => Effects.HavePlayer(out why);

    /// <summary>For as long as the timer says (a second one extends it: the game's reapply restarts the clock with
    /// the first one's length, so the status it has is given the whole time left).</summary>
    public bool Fire(Pending p, float size)
    {
        var fx = Effects.Player.effectsHandler;
        if (!fx) return false;
        landing?.Invoke(Effects.Player);
        float secs = Mathf.Max(1f, (float)Streamer.Timers.Left(Id));
        TwitchDeath.PoisonUntil = Mathf.Max(TwitchDeath.PoisonUntil, Time.time + secs + 2f);
        fx.HandleEffect(new DamageEffect { Effect = type, Chance = 100f, DamagePerSec = Mathf.RoundToInt(3f * size), Duration = secs });
        foreach (var e in fx.effects) if (e != null && !e.IsRemoved && e.Type == type) e.Duration = secs;
        return true;
    }

    public void Stop() { var me = Effects.Player; if (me) ending?.Invoke(me); }
}

/// <summary>Down you go: for real, unless the streamer made Twitch deaths free.</summary>
internal sealed class InstantDeath : IEffect
{
    public string Id => "death";
    public bool Available(out string why) { why = ""; return true; }
    public bool Ready(out string why) => Effects.HavePlayer(out why);
    public bool Fire(Pending p, float size) => TwitchDeath.Kill(Effects.Player);
    public void Stop() { }
}

/// <summary>
/// For its time, about once a second, every live enemy on screen is struck: a tenth of its health, from the player (so
/// the kill and its XP are yours, and in co-op the hit goes through the creature sync like any of yours). The bolt
/// chains: from you to the nearest, then from each to the nearest one not hit yet; each is stunned for a moment and
/// shows the game's own shock sparks for half a second.
/// </summary>
internal sealed class ChainLightning : IEffect
{
    public string Id => "chainlightning";
    static float until, next;

    public bool Available(out string why) { why = ""; return true; }
    public bool Ready(out string why) => Effects.HavePlayer(out why);

    public bool Fire(Pending p, float size)
    {
        until = Time.time + (float)Streamer.Timers.Left(Id);   // (the timer already holds the whole time, extensions included)
        next = Time.time;
        return true;
    }

    public void Stop() => until = 0f;

    /// <summary>The game's shock sparks on it for half a second (only if it wasn't shocked already: theirs stays).</summary>
    static void Spark(Enemy e)
    {
        if (e.appliedEffects.Contains(EffectType.Shock)) return;
        e.SetEffect(EffectType.Shock, true);
        sparking.Add((e, Time.time + 0.5f));
    }

    static Vector3 Middle(Component c)
    {
        if (c is ICenterOfMass m && m.CenterOfMass) return m.CenterOfMass.position;
        var col = c.GetComponentInChildren<Collider2D>();
        return col ? (Vector3)col.bounds.center : c.transform.position + Vector3.up * 0.5f;
    }

    /// <summary>A jagged bolt from you to it: two lines (a blue glow, a white core), re-jagged once, gone in 0.2 s.</summary>
    static void Bolt(Component from, Component to) => BoltAt(Middle(from), Middle(to));

    /// <summary>A jagged bolt between two points (a bolt from the sky: Shocked, without Better Weather).</summary>
    internal static void BoltAt(Vector3 a, Vector3 b)
    {
        if (!boltMaterial)
        {
            var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default") ?? Shader.Find("Sprites/Default");
            if (shader) boltMaterial = new Material(shader);
            else if (!noShaderSaid) { noShaderSaid = true; Log.Warn("Streaming: no sprite shader for the bolts: drawn on screen instead"); }
        }
        a.z = b.z = 0f;
        if (!boltMaterial) { screenBolts.Add((a, b, Time.unscaledTime + 0.2f, Random.Range(0, int.MaxValue))); return; }
        var sr = Effects.Player ? Effects.Player.GetComponentInChildren<SpriteRenderer>() : null;
        var go = new GameObject("BS_Bolt");
        Line(go.transform, Blue, 0.16f, a, b, sr, 0);
        Line(go.transform, Core, 0.05f, a, b, sr, 1);
        go.AddComponent<Flicker>().Ends = (a, b);
        Object.Destroy(go, 0.2f);
    }

    static LineRenderer Line(Transform parent, Color colour, float width, Vector3 a, Vector3 b, SpriteRenderer like, int above)
    {
        var go = new GameObject("Line");
        go.transform.SetParent(parent, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.material = boltMaterial;
        lr.startColor = lr.endColor = colour;
        lr.startWidth = lr.endWidth = width;
        lr.useWorldSpace = true;
        lr.numCapVertices = 2;
        if (like) { lr.sortingLayerID = like.sortingLayerID; lr.sortingOrder = like.sortingOrder + 50 + above; }
        Jag(lr, a, b);
        return lr;
    }

    /// <summary>The bolt's points: straight from a to b, each middle point pushed sideways at random.</summary>
    static void Jag(LineRenderer lr, Vector3 a, Vector3 b)
    {
        const int Points = 9;
        lr.positionCount = Points;
        var side = Vector3.Cross(b - a, Vector3.forward).normalized;
        float spread = Mathf.Min(0.6f, (b - a).magnitude * 0.08f);
        for (int i = 0; i < Points; i++)
        {
            float t = i / (float)(Points - 1);
            var p = Vector3.Lerp(a, b, t);
            if (i > 0 && i < Points - 1) p += side * Random.Range(-spread, spread);
            lr.SetPosition(i, p);
        }
    }

    /// <summary>Re-jags the bolt once, half way through its life: it flickers.</summary>
    sealed class Flicker : MonoBehaviour
    {
        public (Vector3 a, Vector3 b) Ends;
        float at;
        bool done;
        void Start() => at = Time.time + 0.09f;
        void Update()
        {
            if (done || Time.time < at) return;
            done = true;
            foreach (var lr in GetComponentsInChildren<LineRenderer>()) Jag(lr, Ends.a, Ends.b);
        }
    }

    static readonly System.Collections.Generic.List<(Enemy e, float off)> sparking = new();
    static Material boltMaterial;
    static bool noShaderSaid;
    static readonly System.Collections.Generic.List<(Vector3 a, Vector3 b, float until, int seed)> screenBolts = new();

    /// <summary>The bolts drawn on screen (ImGui, with the overlay) when there was no shader to draw them in the world.</summary>
    public static void DrawScreen()
    {
        screenBolts.RemoveAll(x => Time.unscaledTime >= x.until);
        var cam = Camera.main;
        if (screenBolts.Count == 0 || !cam) return;
        var dl = ImGuiNET.ImGui.GetBackgroundDrawList();
        uint glow = ImGuiNET.ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(Blue.r, Blue.g, Blue.b, 0.9f));
        uint core = ImGuiNET.ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(Core.r, Core.g, Core.b, 1f));
        foreach (var (a, b, until, seed) in screenBolts)
        {
            var rng = new System.Random(seed + (until - Time.unscaledTime < 0.11f ? 1 : 0));   // (re-jagged half way: a flicker)
            var sa = cam.WorldToScreenPoint(a); var sb = cam.WorldToScreenPoint(b);
            if (sa.z < 0f || sb.z < 0f) continue;
            var pa = new ImGuiNET.Vector2(sa.x, Screen.height - sa.y); var pb = new ImGuiNET.Vector2(sb.x, Screen.height - sb.y);
            float dx = pb.x - pa.x, dy = pb.y - pa.y, len = Mathf.Sqrt(dx * dx + dy * dy);
            if (len < 1f) continue;
            float nx = -dy / len, ny = dx / len, spread = Mathf.Min(24f, len * 0.08f);
            const int Points = 9;
            var pts = new ImGuiNET.Vector2[Points];
            for (int i = 0; i < Points; i++)
            {
                float t = i / (float)(Points - 1), off = i == 0 || i == Points - 1 ? 0f : (float)(rng.NextDouble() * 2 - 1) * spread;
                pts[i] = new ImGuiNET.Vector2(pa.x + dx * t + nx * off, pa.y + dy * t + ny * off);
            }
            for (int i = 0; i < Points - 1; i++) dl.AddLine(pts[i], pts[i + 1], glow, 6f);
            for (int i = 0; i < Points - 1; i++) dl.AddLine(pts[i], pts[i + 1], core, 2f);
        }
    }
    static readonly Color Blue = new Color(0.45f, 0.75f, 1f), Core = new Color(0.9f, 0.97f, 1f);

    /// <summary>Every frame (Plugin.Update).</summary>
    public static void Tick()
    {
        for (int i = sparking.Count - 1; i >= 0; i--)
            if (Time.time >= sparking[i].off) { if (sparking[i].e) sparking[i].e.SetEffect(EffectType.Shock, false); sparking.RemoveAt(i); }
        if (Time.time >= until || Time.time < next) return;
        next = Time.time + 1f;
        var me = Effects.Player;
        var cam = Camera.main;
        if (!me || !cam) return;
        var left = new System.Collections.Generic.List<Enemy>();
        foreach (var e in EnemiesManager.AllEnemies.ToList())
        {
            if (!e || e.IsDead || !e.gameObject.activeInHierarchy || e.initHealth <= 0f) continue;
            // (not the townsfolk, the livestock, or anything on your side; Twitch's own chickens are fair game)
            if (e.name.StartsWith("NPC_") || Hooks.IsFriendly(e) || (e.enemyType == EnemyType.None && !Hooks.IsTwitchCreature(e))) continue;
            var v = cam.WorldToViewportPoint(e.transform.position);
            if (v.z < 0f || v.x < 0f || v.x > 1f || v.y < 0f || v.y > 1f) continue;
            left.Add(e);
        }
        // the chain: from you to the nearest, then on from each to the nearest not hit yet
        Component from = me;
        while (left.Count > 0)
        {
            var at = Middle(from);
            var e = left.OrderBy(x => ((Vector2)(Middle(x) - at)).sqrMagnitude).First();
            left.Remove(e);
            Bolt(from, e);
            Spark(e);
            bool boss = Hooks.IsBoss(e) || e.name.StartsWith("Boss_");
            int amount = Mathf.Max(1, Mathf.RoundToInt(e.initHealth * 0.01f * (boss ? BossPercent.Value : Percent.Value)));
            CombatSystem.Resolve(new DamageData(me, e, amount, DamageHitType.Magic, KnockBack.None));
            if (e && !e.IsDead) e.Stun(StunFor);   // (the game's own dizzy spell)
            from = e;
        }
    }

    const float StunFor = 0.6f;

    /// <summary>How much of an enemy's health a strike takes (bosses: less; 30 strikes at 3% is 90%).</summary>
    public static BepInEx.Configuration.ConfigEntry<int> Percent, BossPercent;

    public static void Bind(BepInEx.Configuration.ConfigFile config)
    {
        Percent = config.Bind("Effect.chainlightning", "Percent", 10, "Chain lightning: % of an enemy's health each strike takes (a strike a second)");
        BossPercent = config.Bind("Effect.chainlightning", "BossPercent", 3, "Chain lightning: % of a boss's health each strike takes");
    }
}

/// <summary>Creatures that fit the area, in a ring around the player (the multiplayer mod's spawning).</summary>
internal sealed class Raid : IEffect
{
    public string Id => "raid";

    public bool Available(out string why)
    {
        why = Hooks.SpawnRaid == null ? "needs the Emberville mod pack" : "";
        return Hooks.SpawnRaid != null;
    }

    public bool Ready(out string why) => Available(out why) && Effects.HavePlayer(out why);

    public bool Fire(Pending p, float size)
    {
        int count = Mathf.Clamp(Mathf.RoundToInt(3f * size), 1, 15);
        float tough = 1f + 0.1f * (size - 1f);
        return Hooks.SpawnRaid(Effects.Player.transform.position, count, tough, p.Viewer);
    }

    public void Stop() { }
}

/// <summary>Hostile chickens from the screen's edges, in waves (the multiplayer mod's chicken level).</summary>
internal sealed class ChickenRaid : IEffect
{
    public string Id => "chickens";

    public bool Available(out string why)
    {
        why = Hooks.ChickenRaid == null ? "needs the Emberville mod pack" : "";
        return Hooks.ChickenRaid != null;
    }

    public bool Ready(out string why) => Available(out why) && Effects.HavePlayer(out why);

    public bool Fire(Pending p, float size)
    {
        int waves = Mathf.Clamp(Mathf.CeilToInt(size), 1, 10);
        return Hooks.ChickenRaid(Effects.Player.transform.position, waves, 1f + 0.1f * (size - 1f), p.Viewer);
    }

    public void Stop() { }
}

/// <summary>
/// Frozen solid for its time: you can't move, turn, dodge, attack or cast, your animation stops, you're tinted ice
/// blue, and the game's own frost loop plays over you (an ice-blue ring as it lands). Buying it again while frozen adds time. Thaws when the timer ends.
/// </summary>
internal sealed class Freeze : IEffect
{
    public string Id => "freeze";
    static PlayerMaster frozen;
    static bool frosted;   // (we put the frost on: we take it off; a real freeze from a creature stays)
    static readonly System.Collections.Generic.List<(SpriteRenderer sr, Color c)> tinted = new();
    static readonly System.Collections.Generic.List<(Animator a, float speed)> stopped = new();

    public bool Available(out string why) { why = ""; return true; }
    public bool Ready(out string why) => Effects.HavePlayer(out why);

    public bool Fire(Pending p, float size)
    {
        var me = Effects.Player;
        if (frozen == me && frozen) return true;   // (already frozen: the timer just got longer)
        frozen = me;
        me.StopVelocity();
        me.CanMove = me.CanRotate = me.CanDodge = me.CanPerfomAttacks = me.CanPerformSkills = false;
        tinted.Clear(); stopped.Clear();
        var fxLayer = me.extraFxAnimator ? me.extraFxAnimator.transform : null;
        foreach (var sr in me.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (fxLayer && sr.transform.IsChildOf(fxLayer)) continue;   // (the frost shows in its own colours)
            tinted.Add((sr, sr.color));
            sr.color = new Color(sr.color.r * Effects.Ice.r, sr.color.g * Effects.Ice.g, sr.color.b * Effects.Ice.b, sr.color.a);
        }
        foreach (var a in me.GetComponentsInChildren<Animator>(true))
            if (a != me.extraFxAnimator) { stopped.Add((a, a.speed)); a.speed = 0f; }   // (the effects animator plays on: the frost)
        frosted = !me.appliedEffects.Contains(EffectType.Freeeze);
        if (frosted) me.SetEffect(EffectType.Freeeze, true);   // the game's own frost loop over you
        return true;
    }

    public void Stop()
    {
        foreach (var (sr, c) in tinted) if (sr) sr.color = new Color(c.r, c.g, c.b, sr.color.a);
        foreach (var (a, speed) in stopped) if (a) a.speed = speed;
        tinted.Clear(); stopped.Clear();
        var me = frozen;
        frozen = null;
        if (me && frosted) me.SetEffect(EffectType.Freeeze, false);
        frosted = false;
        if (me && !me.IsDead) me.CanMove = me.CanRotate = me.CanDodge = me.CanPerfomAttacks = me.CanPerformSkills = true;
    }
}

/// <summary>A mystery chest appears beside you: even odds, full of loot, or a trap that springs a raid when opened.</summary>
internal sealed class MysteryChest : IEffect
{
    public string Id => "chest";

    public bool Available(out string why)
    {
        why = Hooks.SpawnChest == null ? "needs the Emberville mod pack" : "";
        return Hooks.SpawnChest != null;
    }

    public bool Ready(out string why) => Available(out why) && Effects.HavePlayer(out why);

    public bool Fire(Pending p, float size) => Hooks.SpawnChest(Effects.Player.transform.position, Random.value < 0.5f, p.Viewer);

    public void Stop() { }
}

/// <summary>
/// A lightning storm: Better Weather's rain for its time (your weather before it comes back after), and strikes all
/// around you, more for bigger cheers (one a second, x the size up to 3 a second). It hunts you: most land within a few
/// steps, and standing still for 1.5 s brings the next one down on you, so keep moving. A strike lands first and
/// hurts half a second later if you're still within reach (so it can be dodged): 8% of your health, a Twitch hit.
/// </summary>
internal sealed class Storm : IEffect
{
    public string Id => "storm";
    static float until, nextStrike, every = 1f;
    static bool raining;

    public bool Available(out string why)
    {
        why = Hooks.Strike == null ? "needs the Emberville mod pack" : "";
        return Hooks.Strike != null;
    }

    public bool Ready(out string why) => Available(out why) && Effects.HavePlayer(out why);

    public bool Fire(Pending p, float size)
    {
        until = Time.time + (float)Streamer.Timers.Left(Id);
        every = 1f / Mathf.Clamp(size, 1f, 3f);
        nextStrike = Time.time + 1f;
        if (!raining) raining = Hooks.StormOn?.Invoke() ?? false;
        return true;
    }

    public void Stop()
    {
        until = 0f;
        if (raining) { raining = false; Hooks.StormOff?.Invoke(); }
    }

    /// <summary>Every frame (Plugin.Update): the strikes.</summary>
    public static void Tick()
    {
        if (Time.time >= until || Time.time < nextStrike) return;
        nextStrike = Time.time + every * Random.Range(0.6f, 1.4f);
        var me = Effects.Player;
        if (!me || !Streamer.CanFight()) return;
        // it hunts you: standing still (under half a step in 1.5 s), the next one is on you; else most land close
        var here = me.transform.position;
        bool still = ((Vector2)(here - stillFrom)).magnitude < 0.6f && Time.time - stillSince >= 1.5f;
        var at = still ? here + (Vector3)(Random.insideUnitCircle * 0.3f)
               : Random.value < 0.6f ? here + (Vector3)(Random.insideUnitCircle * 2.5f)
               : here + (Vector3)(Random.insideUnitCircle * 6f);
        Hooks.Strike?.Invoke(at);
        pending.Add((at, Time.time + 0.5f));
    }

    static readonly System.Collections.Generic.List<(Vector3 at, float when)> pending = new();
    static Vector3 stillFrom;
    static float stillSince;

    /// <summary>Every frame (Plugin.Update): strikes half a second old hurt whoever is still within reach.</summary>
    public static void TickHits()
    {
        // where you've been standing (moved half a step or more: the clock starts again)
        var p = Effects.Player;
        if (p && ((Vector2)(p.transform.position - stillFrom)).magnitude >= 0.6f) { stillFrom = p.transform.position; stillSince = Time.time; }
        if (pending.Count == 0) return;
        var me = Effects.Player;
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            if (Time.time < pending[i].when) continue;
            var at = pending[i].at;
            pending.RemoveAt(i);
            if (!me || me.IsDead || ((Vector2)(at - me.transform.position)).magnitude > 1.2f) continue;
            int hurt = Mathf.Max(1, Mathf.RoundToInt(me.characterStats.GetSecondaryStatValue(SecondaryStat.HealthPoints) * 0.08f));
            TwitchDeath.Mark(1f);
            me.ApplyDamage(new DamageData(null, me, hurt, DamageHitType.Magic, KnockBack.Low) { Result = DamageResult.Success });
        }
    }
}

/// <summary>
/// Random x2 / x3: that many different effects at once, picked at random from the naughty ones (switched on, able to
/// happen here; never another random pack), each with its own banner from the same viewer and its own time.
/// </summary>
internal sealed class RandomPack : IEffect
{
    public string Id { get; }
    readonly int count;

    public RandomPack(string id, int count) { Id = id; this.count = count; }

    static System.Collections.Generic.List<EffectDef> Pool() => Streamer.Defs.Where(d =>
        d.Enabled && d.Mood != Mood.Nice && d.Id != "random2" && d.Id != "random3" &&
        Streamer.Effects.TryGetValue(d.Id, out var fx) && fx.Ready(out _)).ToList();

    public bool Available(out string why) { why = ""; return true; }

    public bool Ready(out string why)
    {
        if (!Effects.HavePlayer(out why)) return false;
        if (Pool().Count == 0) { why = "no naughty effects can happen here"; return false; }
        return true;
    }

    public bool Fire(Pending p, float size)
    {
        var pool = Pool();
        if (pool.Count == 0) return false;
        for (int i = 0; i < count && pool.Count > 0; i++)
        {
            var d = pool[Random.Range(0, pool.Count)];
            pool.Remove(d);
            Streamer.Fire(new Pending { EffectId = d.Id, Viewer = p.Viewer, Message = "", Size = 1f, Source = p.Source, Mood = d.Mood });
        }
        return true;
    }

    public void Stop() { }
}
