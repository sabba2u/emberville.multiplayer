using System.Collections.Generic;
using System.Linq;
using BetterStreaming.Core;
using HarmonyLib;
using UnityEngine;

namespace BetterStreaming;

/// <summary>
/// The nice ones (chat helping the streamer): shield bubble, full Kymia, regeneration, haste, berserk, smite, battle
/// chickens, guardians, second wind, loot drop, gold rain, golden pet, fireworks.
/// </summary>
[HarmonyPatch]
internal static class NiceEffects
{
    public static void Register()
    {
        var all = new IEffect[]
        {
            new Switch("shield", me => { if (me.effectsHandler?.HandleEffect(new DamageEffect { Effect = EffectType.Shield, Chance = 100f, DamagePerSec = 3 }) ?? false) Bubble.Up(me); }, Bubble.Down),
            new Switch("kymia", me => me.RestoreKymiaPercentage(1f)),
            new Switch("regen"),
            new Once("haste", me => me.effectsHandler?.HandleEffect(new DamageEffect { Effect = EffectType.MoveSpeedBuff, Chance = 100f, DamagePerSec = Mathf.RoundToInt(Mathf.Max(1f, me.characterStats.GetSecondaryStatValue(SecondaryStat.MoveSpeed) * 0.5f)), Duration = Seconds("haste") }) ?? false),
            new Switch("berserk"),
            new Once("smite", Smite),
            new Squad("battlechickens", "chickens", 4, () => Hooks.BattleChickens != null, (me, n, secs, viewer) => Hooks.BattleChickens(me.transform.position, n, secs)),
            new Squad("guardians", "guardians", 3, () => Hooks.Guardians != null, (me, n, secs, viewer) => Hooks.Guardians(me.transform.position, n, secs, viewer)),
            new Switch("secondwind"),
            new Hooked("loot", () => Hooks.RollLoot != null, (me, p) => DropLoot(me)),
            new Switch("gold", _ => { LoadCoin(); nextPile = Time.time; }),
            new Squad("goldenpet", "golden", 1, () => Hooks.GoldenChicken != null, (me, n, secs, viewer) => Hooks.GoldenChicken(me.transform.position, secs),
                first: me => Hooks.GoldenPet != null && Hooks.GoldenPet(Seconds("goldenpet"))),   // (your pet golden: nothing to keep up)
            new Once("fireworks", me => { Overlay.Fireworks(); return true; }),
            new Hooked("hireling", () => Hooks.Hire != null, (me, p) =>
            {
                string why = Hooks.Hire(p.Viewer, Seconds("hireling"));
                if (!string.IsNullOrEmpty(why)) Log.Info($"Streaming: hireling: {why}");
                return string.IsNullOrEmpty(why);
            }),
            new Status("shock", EffectType.Shock, me => { StruckByLightning(me); Slow(me, true); }, me => Slow(me, false)),
            new Status("bleed", EffectType.Bleed),
        };
        foreach (var fx in all) Streamer.Effects[fx.Id] = fx;
    }

    /// <summary>When a game's loaded (the first scene): the coin ready before it's needed.</summary>
    public static void Preload() => LoadCoin();

    static float Seconds(string id) => Mathf.Max(1f, (float)Streamer.Timers.Left(id) > 0 ? (float)Streamer.Timers.Left(id) : Streamer.Def(id)?.Seconds ?? 30f);

    /// <summary>Something done once on you (true: it happened).</summary>
    sealed class Once : IEffect
    {
        readonly System.Func<PlayerMaster, bool> act;
        public string Id { get; }
        public Once(string id, System.Func<PlayerMaster, bool> act) { Id = id; this.act = act; }
        public bool Available(out string why) { why = ""; return true; }
        public bool Ready(out string why) => Effects.HavePlayer(out why);
        public bool Fire(Pending p, float size) => act(Effects.Player);
        public void Stop() { }
    }

    /// <summary>Something the multiplayer mod or its friends do (unavailable without them).</summary>
    sealed class Hooked : IEffect
    {
        readonly System.Func<bool> there;
        readonly System.Func<PlayerMaster, Pending, bool> act;
        public string Id { get; }
        public Hooked(string id, System.Func<bool> there, System.Func<PlayerMaster, Pending, bool> act) { Id = id; this.there = there; this.act = act; }
        public bool Available(out string why) { bool ok = there(); why = ok ? "" : "needs the Emberville mod pack"; return ok; }
        public bool Ready(out string why) => Available(out why) && Effects.HavePlayer(out why);
        public bool Fire(Pending p, float size) => act(Effects.Player, p);
        public void Stop() { }
    }

    /// <summary>
    /// Summons kept up for the effect's time (battle chickens, guardians, the golden chicken): as many as it brought,
    /// once a second any fallen are replaced (living to the effect's end). first: something tried before them (the
    /// golden pet: your own pet turned golden: then nothing to keep up).
    /// </summary>
    sealed class Squad : IEffect
    {
        readonly string tag;
        readonly int count;
        readonly System.Func<bool> there;
        readonly System.Func<PlayerMaster, int, float, string, int> summon;
        readonly System.Func<PlayerMaster, bool> first;
        int wanted;
        string viewer;
        float nextCheck;
        public string Id { get; }

        public Squad(string id, string tag, int count, System.Func<bool> there, System.Func<PlayerMaster, int, float, string, int> summon, System.Func<PlayerMaster, bool> first = null)
        { Id = id; this.tag = tag; this.count = count; this.there = there; this.summon = summon; this.first = first; squads.Add(this); }

        public bool Available(out string why) { bool ok = there(); why = ok ? "" : "needs the Emberville mod pack"; return ok; }
        public bool Ready(out string why) => Available(out why) && Effects.HavePlayer(out why);

        public bool Fire(Pending p, float size)
        {
            viewer = p.Viewer;
            if (wanted > 0) return true;   // (on already: the timer got longer; they're kept up till then)
            if (first != null && first(Effects.Player)) return true;
            int made = summon(Effects.Player, count, Seconds(Id), viewer);
            wanted = made > 0 ? count : 0;
            nextCheck = Time.time + 1f;
            return made > 0;
        }

        public void Stop() => wanted = 0;

        public void Tick()
        {
            if (wanted <= 0 || Time.time < nextCheck || Hooks.SummonsAlive == null) return;
            nextCheck = Time.time + 1f;
            var me = Effects.Player;
            if (!me || me.IsDead || !Streamer.CanFight()) return;
            float left = (float)Streamer.Timers.Left(Id);
            if (left < 2f) return;
            int missing = wanted - Hooks.SummonsAlive(tag);
            if (missing > 0) summon(me, missing, left, viewer);
        }
    }

    static readonly List<Squad> squads = new();

    // ---- regeneration: 3% of your health a second ----

    static float nextRegen;
    static int regenTicks;

    /// <summary>Every frame (Plugin.Update).</summary>
    public static void Tick()
    {
        foreach (var q in squads) q.Tick();
        GoldTick();
        if (!Controls.On("regen") || Time.time < nextRegen) return;
        nextRegen = Time.time + 1f;
        var me = Effects.Player;
        if (!me || me.IsDead) return;
        me.RestoreHealthPercentage(0.03f);
        if (++regenTicks % 3 == 1) Effects.Burst(me, "LifeRecovery");   // (the game's heal burst every 3 s: you're regenerating)
    }

    // ---- infinite Kymia: nothing costs any while it runs (full when it lands) ----

    /// <summary>A cost (more than 0) costs nothing: it's turned into a refresh of the bar instead. The game's own refreshes
    /// (0, forceUpdate: a new most Kymia from a weapon, a level) and refunds go through as they are.</summary>
    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.ConsumeKymia)), HarmonyPrefix]
    static void FreeKymia(PlayerMaster __instance, ref float value, ref bool forceUpdate)
    {
        if (value <= 0f || !Controls.On("kymia") || !ReferenceEquals(__instance, Effects.Player)) return;
        value = 0f;
        forceUpdate = true;
    }

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.CheckAvailableKymia)), HarmonyPrefix]
    static bool EnoughKymia(PlayerMaster __instance, ref bool __result)
    {
        if (!Controls.On("kymia") || !ReferenceEquals(__instance, Effects.Player)) return true;
        __result = true;
        return false;
    }

    // ---- berserk: your hits on creatures count double ----

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.ApplyDamage)), HarmonyPrefix]
    static void Berserk(ref DamageData damageData)
    {
        if (!Controls.On("berserk") || !(damageData.Aggressor is PlayerMaster p) || !ReferenceEquals(p, Effects.Player)) return;
        damageData.Amount *= 2;
    }

    // ---- smite: the toughest (not a boss) on screen, half its health ----

    static bool Smite(PlayerMaster me)
    {
        var cam = Camera.main;
        if (!cam) return false;
        Enemy best = null;
        foreach (var e in EnemiesManager.AllEnemies)
        {
            if (!e || e.IsDead || !e.gameObject.activeInHierarchy || e.initHealth <= 0f || e.name.StartsWith("NPC_") || Hooks.IsBoss(e) || Hooks.IsFriendly(e)) continue;
            if (e.enemyType == EnemyType.None && !Hooks.IsTwitchCreature(e) && !Hooks.IsHostileFauna(e)) continue;
            var v = cam.WorldToViewportPoint(e.transform.position);
            if (v.z < 0f || v.x < 0f || v.x > 1f || v.y < 0f || v.y > 1f) continue;
            if (!best || e.currentHealth > best.currentHealth) best = e;
        }
        if (!best) return false;   // (no enemy on screen: it waits)
        Hooks.Strike?.Invoke(best.transform.position);
        CombatSystem.Resolve(new DamageData(me, best, Mathf.Max(1, Mathf.RoundToInt(best.initHealth * 0.5f)), DamageHitType.Magic, KnockBack.High));
        if (best && !best.IsDead) best.Stun(1.5f);
        return true;
    }

    // ---- second wind: the next death in its time, you get up instead (once) ----

    /// <summary>From TwitchDeath's death patch: true if second wind caught this one (and is used up).</summary>
    public static bool CatchDeath(PlayerMaster me)
    {
        if (!Controls.On("secondwind")) return false;
        Streamer.EndEarly("secondwind");
        Controls.Running.Remove("secondwind");
        return true;
    }

    // ---- loot drop and gold rain ----

    static bool DropLoot(PlayerMaster me)
    {
        var items = Hooks.RollLoot(6)?.ToList();
        if (items == null || items.Count == 0) return false;
        foreach (var it in items) Singleton<ItemDropHandler>.main.DropToWorld(me.transform.position, it, it.amount, 1.6f);
        return true;
    }

    // gold rain: for its time, a pile of the game's coins near you every ~1.5 s (the coin: item_gld, loaded by its key)
    static ItemData coin;
    static bool coinLoading;
    static float nextPile;

    static void LoadCoin()
    {
        if (coin || coinLoading) return;
        coinLoading = true;
        try
        {
            UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<ItemData>("item_gld").Completed += h =>
            {
                coinLoading = false;
                if (h.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded) coin = h.Result;
                else Log.Warn("Streaming: gold rain: the game's coin (item_gld) didn't load");
            };
        }
        catch (System.Exception e) { coinLoading = false; Log.Warn($"Streaming: gold rain: {e.Message}"); }
    }

    static void GoldTick()
    {
        if (!Controls.On("gold") || Time.time < nextPile) return;
        var me = Effects.Player;
        if (!coin || !me || !Streamer.CanFight()) return;
        nextPile = Time.time + Random.Range(1.1f, 1.9f);
        int amount = Random.Range(10, 31);
        var at = me.transform.position + (Vector3)(Random.insideUnitCircle * 2.5f);
        try { Singleton<ItemDropHandler>.main.DropToWorld(at, coin.CreateItem(amount, -1), amount, 0.8f); }
        catch (System.Exception e) { Log.Warn($"Streaming: gold rain: {e.Message}"); }
    }

    /// <summary>Shocked: struck first: Better Weather's lightning on you, or a bolt from above.</summary>
    static void StruckByLightning(PlayerMaster me)
    {
        var at = me.transform.position;
        if (Hooks.Strike != null) { Hooks.Strike(at); return; }
        var mid = me.CenterOfMass ? me.CenterOfMass.position : at + Vector3.up * 0.6f;
        ChainLightning.BoltAt(mid + new Vector3(Random.Range(-1.5f, 1.5f), 9f, 0f), mid);
    }

    // ---- shocked: half speed for its time (as the game's trapped status does it: a speed modifier) ----

    static StatModifier<SecondaryStat> slow;
    static CharacterStats slowOn;

    static void Slow(PlayerMaster me, bool on)
    {
        if (on)
        {
            if (slow != null) return;   // (on already: the timer got longer)
            slow = new StatModifier<SecondaryStat> { stat = SecondaryStat.MoveSpeed, modifier = new Modifier(-50f, ModifierType.Multiplier) };
            slowOn = me.characterStats;
            slowOn.AddModifier(slow);
        }
        else if (slow != null)
        {
            if (slowOn) slowOn.RemoveModifier(slow);
            slow = null; slowOn = null;
        }
    }
}

/// <summary>
/// On fire, heard: the game's own sounds (its Wwise events): catching fire as it lands, a torch's crackle on you for as
/// long as you burn, and a burn tick about every 1.2 s; the crackle stopped when it ends.
/// </summary>
internal static class FireSound
{
    static GameObject on;
    static float nextTick;

    public static void Start(PlayerMaster me)
    {
        if (on == me.gameObject) return;   // (burning already: the timer got longer)
        on = me.gameObject;
        Post("Play_Player_getBurned", on);
        Post("Play_Amb_Emitter_Torch_Loop", on);
        nextTick = Time.time + 1.2f;
    }

    public static void Stop(PlayerMaster me)
    {
        if (on) Post("Stop_Amb_Emitter_Torch", on);
        on = null;
    }

    /// <summary>Every frame (Plugin.Update): the burn ticks.</summary>
    public static void Tick()
    {
        if (!on || Time.time < nextTick) return;
        nextTick = Time.time + 1.2f;
        Post("Play_Player_Burned_Tick", on);
    }

    static void Post(string evt, GameObject at)
    {
        try { AkSoundEngine.PostEvent(evt, at); }
        catch (System.Exception e) { Log.Warn($"Streaming: fire sound {evt}: {e.Message}"); }
    }
}

/// <summary>
/// The shield bubble, seen and heard for as long as the game's shield lasts (its hits): a see-through blue sphere round
/// you with a pulsing rim and a pip for each hit it has left (ImGui, with the overlay); the Acolyte Sage's shield
/// sound as it goes up, the knight's shield bash for every hit it takes, and the sound again as it pops.
/// </summary>
internal static class Bubble
{
    static PlayerMaster on;
    static int hits;
    static float since;

    public static void Up(PlayerMaster me)
    {
        if (on != me) Post("Play_Acolyte_Sage_ShieldActivate", me.gameObject);
        on = me;
        since = Time.time;
        hits = Hits(me);
    }

    /// <summary>The game's shield on you now (its own hit count: ShieldEffect hides Effect.Stacks with its own).</summary>
    static ShieldEffect Shield(PlayerMaster me)
    {
        var fx = me ? me.effectsHandler : null;
        if (!fx) return null;
        foreach (var e in fx.effects) if (e is ShieldEffect s && !s.IsRemoved) return s;
        return null;
    }

    static int Hits(PlayerMaster me) { var s = Shield(me); return s != null ? Mathf.Max(0, s.Stacks) : 0; }

    /// <summary>Its time up (the effect's timer): the shield taken off, the bubble gone.</summary>
    public static void Down(PlayerMaster me)
    {
        var s = Shield(me);
        if (s != null) { try { me.effectsHandler.RemoveEffect(s); } catch (System.Exception e) { Log.Warn($"Streaming: shield: {e.Message}"); } }
        if (on) Post("Play_Acolyte_Sage_ShieldActivate", on.gameObject);
        on = null;
        hits = 0;
    }

    /// <summary>Every frame (Plugin.Update): a hit taken (the bash), the bubble gone (the pop).</summary>
    public static void Tick()
    {
        if (!on) return;
        int now = Hits(on);
        if (now < hits) Post(now > 0 ? "Play_sfx_plyr_knight_ShieldBash" : "Play_Acolyte_Sage_ShieldActivate", on.gameObject);
        hits = now;
        if (now <= 0 || on.IsDead)
        {
            on = null;
            if (Controls.On("shield")) { Streamer.EndEarly("shield"); Controls.Running.Remove("shield"); }   // (all its hits taken: over before its time; a new one can come)
        }
    }

    public static void Draw()
    {
        if (!on) return;
        var cam = Camera.main;
        if (!cam) return;
        var mid = on.CenterOfMass ? on.CenterOfMass.position : on.transform.position + Vector3.up * 0.6f;
        var c3 = cam.WorldToScreenPoint(mid);
        var top = cam.WorldToScreenPoint(mid + Vector3.up * 1.0f);
        if (c3.z < 0f) return;
        var c = new ImGuiNET.Vector2(c3.x, UnityEngine.Screen.height - c3.y);
        float r = Mathf.Max(24f, Mathf.Abs(top.y - c3.y) * 1.1f);
        float t = Time.time - since, pulse = 0.5f + 0.5f * Mathf.Sin(t * 3f);
        float grow = Mathf.Clamp01(t / 0.25f);   // (it swells up as it lands)
        var dl = ImGuiNET.ImGui.GetBackgroundDrawList();
        uint fill = ImGuiNET.ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(0.45f, 0.8f, 1f, 0.16f + 0.06f * pulse));
        uint rim = ImGuiNET.ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(0.7f, 0.92f, 1f, 0.55f + 0.35f * pulse));
        uint shine = ImGuiNET.ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(1f, 1f, 1f, 0.35f));
        dl.AddCircleFilled(c, r * grow, fill, 48);
        dl.AddCircle(c, r * grow, rim, 48, 2.5f + pulse);
        dl.AddCircle(new ImGuiNET.Vector2(c.x - r * 0.3f, c.y - r * 0.35f), r * 0.22f * grow, shine, 24, 2f);   // (a glint)
        // a pip for each hit it has left, under you
        for (int i = 0; i < hits; i++)
            dl.AddCircleFilled(new ImGuiNET.Vector2(c.x + (i - (hits - 1) / 2f) * 10f, c.y + r + 8f), 3.5f, rim, 12);
    }

    static void Post(string evt, GameObject at)
    {
        try { AkSoundEngine.PostEvent(evt, at); }
        catch (System.Exception e) { Log.Warn($"Streaming: shield sound {evt}: {e.Message}"); }
    }
}
