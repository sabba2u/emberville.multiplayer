using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using UnityEngine;
using Vector4 = ImGuiNET.Vector4;
using Vector3 = UnityEngine.Vector3;

namespace EmbervilleGhost;

/// <summary>
/// The arena's "Tiny Swords waves": every creature of ours (MonsterArt.dll), one at a time, the next once the last is
/// down, until they're all gone. The small ones first, the bosses (Queen Bee, Great Bear, Troll) last. Each one
/// knows where you are from the moment it lands (Monster.Hunts).
/// </summary>
internal static partial class Arena
{
    // the order, easier to harder (any creature not listed comes just before the bosses)
    static readonly string[] WaveOrder =
    {
        "Bumblebee", "Pig", "Gnome", "Snake", "Giant Bat", "Slingshot Gnome", "Thief", "Spear Goblin", "Pig Rider", "Torch Goblin", "Lizard",
        "Gnoll", "Imp", "Turtle", "Spider", "Paddle Shark", "Harpoon Shark", "Bomb Fish", "Hex Shaman", "Skull", "Panda",
        "Minotaur", "Bear",
    };
    static readonly string[] WaveBosses = { "Queen Bee", "Great Bear", "Troll" };
    const float WaveGap = 3f;

    static List<string> waveList = new();
    static int tinyWave, bestTiny;
    static Enemy tinyFoe;
    static bool tinyPending;
    static float tinyNextAt;

    static bool TinyOn => tinyWave > 0;

    // ---- potions: any of ours that falls during a run, one time in four ----

    const float PotionChance = 0.25f;
    static bool potionsHooked;

    static void HookPotions()
    {
        if (potionsHooked) return;
        potionsHooked = true;
        Monsters.Died += m =>
        {
            if (!TinyOn || !Active || !m || Random.value >= PotionChance) return;
            var data = HealthPotion();
            if (!data) return;
            new ItemObject(data, 1, -1).DropToWorld(m.transform.position, 1, 1f);
        };
    }

    /// <summary>The game's health potion (the smallest kind: not Kymia), from the loot catalogue.</summary>
    static ItemData HealthPotion()
    {
        var pool = DungeonLoot.Pool;
        if (pool == null) return null;
        var potions = pool.Where(d => d && d.name.StartsWith("Item_Potion") && d.name.IndexOf("Kymia", System.StringComparison.OrdinalIgnoreCase) < 0).ToList();
        return potions.FirstOrDefault(d => d.name.IndexOf("Small", System.StringComparison.OrdinalIgnoreCase) >= 0) ?? potions.FirstOrDefault();
    }

    static bool kingBattle;

    /// <summary>The King battle: one King of a random colour (his soldiers, his catapults' barrage), the far side of
    /// the arena; over when he's down (or you are).</summary>
    static void StartKing()
    {
        StartTiny();
        if (!TinyOn) return;
        var kings = Monsters.Names().Where(n => n.EndsWith(" King")).ToList();
        if (kings.Count == 0) { EndTiny("stopped (no Kings: MonsterArt.dll)"); return; }
        waveList = new List<string> { kings[Random.Range(0, kings.Count)] };
        kingBattle = true;
        NetLog.Warn($"KING BATTLE: the {waveList[0]}!", toast: true);
    }

    static void StartTiny()
    {
        kingBattle = false;
        HookPotions();
        DungeonLoot.Preload();   // (the catalogue the potions come from)
        EndChickens("stopped");
        EndRush("stopped");
        ClearSpawned();
        var have = Monsters.Names();
        // the troops in colours ("Red Warrior", "Black Monk"): one colour of each, picked at random each run
        string[] colours = { "Black ", "Blue ", "Purple ", "Red ", "Yellow " };
        string Troop(string n) => colours.FirstOrDefault(n.StartsWith) is string c ? n.Substring(c.Length) : null;
        var oneEach = have.Where(n => Troop(n) != null).GroupBy(Troop).Select(g => g.ElementAt(Random.Range(0, g.Count())));
        have = have.Where(n => Troop(n) == null).Concat(oneEach).ToList();
        if (have.Count == 0) { NetLog.Warn("No Tiny Swords creatures: MonsterArt.dll isn't installed", toast: true); return; }
        waveList = WaveOrder.Where(have.Contains)
            .Concat(have.Where(n => !WaveOrder.Contains(n) && !WaveBosses.Contains(n)))
            .Concat(WaveBosses.Where(have.Contains)).ToList();
        tinyWave = 1;
        tinyFoe = null;
        tinyPending = true;
        tinyNextAt = Time.unscaledTime + 2f;
        folded = true;
        NetLog.Warn($"TINY SWORDS WAVES: {waveList.Count} creatures. First one in 2...", toast: true);
    }

    static void EndTiny(string why)
    {
        if (!TinyOn) return;
        int reached = tinyFoe && !tinyFoe.IsDead ? tinyWave - 1 : tinyWave;
        if (why == "won") reached = waveList.Count;
        bestTiny = Mathf.Max(bestTiny, reached);
        NetLog.Info($"Tiny Swords waves {why}: {reached} of {waveList.Count} beaten (best {bestTiny})", toast: true);
        tinyWave = 0;
        tinyFoe = null;
        tinyPending = false;
    }

    /// <summary>Every frame (Arena.Tick): the next creature once the last one is down.</summary>
    static void Tiny()
    {
        if (!TinyOn) return;
        if (!Active) { EndTiny("over (you left)"); return; }
        var me = LocalPlayer.Get();
        if (me && me.IsDead) { if (Integrations.ReallyDead(me)) EndTiny("over: you fell"); return; }
        if (tinyPending)
        {
            if (Time.unscaledTime < tinyNextAt) return;
            tinyPending = false;
            SpawnTiny(me);
            return;
        }
        if (tinyFoe && !tinyFoe.IsDead) return;
        if (tinyWave >= waveList.Count) { EndTiny("won"); NetLog.Success(kingBattle ? $"You beat the {waveList[0]}!" : "You beat every Tiny Swords creature!", toast: true); return; }
        bestTiny = Mathf.Max(bestTiny, tinyWave);
        tinyWave++;
        tinyPending = true;
        tinyNextAt = Time.unscaledTime + WaveGap;
        NetLog.Info($"Wave {tinyWave - 1} down! Next: {waveList[tinyWave - 1]} in {WaveGap:0}...", toast: true);
    }

    static void SpawnTiny(PlayerMaster me)
    {
        string name = waveList[tinyWave - 1];
        // the far side of the arena from you
        var center = Origin + new Vector3(W / 2f, H / 2f, 0f);
        var at = me ? center + (center - me.transform.position).normalized * 7f : center + Vector3.up * 6f;
        at.x = Mathf.Clamp(at.x, Origin.x + 3f, Origin.x + W - 3f);
        at.y = Mathf.Clamp(at.y, Origin.y + 3f, Origin.y + H - 3f);
        tinyFoe = Monsters.Spawn(name, at);
        if (!tinyFoe) { NetLog.Warn($"Tiny Swords: the {name} didn't come; skipping it", toast: false); return; }   // (down counts as gone: the next one)
        tinyFoe.transform.SetParent(root.transform, true);
        spawned.Add(tinyFoe.gameObject);
        var m = tinyFoe.GetComponent<Monster>();
        if (m) m.Hunts = true;
        NetLog.Warn($"Wave {tinyWave}: {name}!", toast: true);
    }

    /// <summary>The panel's row (unfolded): the button, or the wave it's on and Stop.</summary>
    static void DrawTinyRow()
    {
        if (TinyOn)
        {
            ImGui.TextColored(new Vector4(0.55f, 0.9f, 0.45f, 1f), $"Tiny Swords: wave {tinyWave} of {waveList.Count} ({waveList[tinyWave - 1]})");
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop##tiny2")) EndTiny("stopped");
            return;
        }
        if (ImGui.Button("King battle")) StartKing();
        ImGui.SameLine();
        ImGui.TextDisabled("a King of a random colour: his soldiers, his catapults");
        if (ImGui.Button("Tiny Swords waves")) StartTiny();
        ImGui.SameLine();
        ImGui.TextDisabled(bestTiny > 0 ? $"every creature, one at a time (best: wave {bestTiny})" : "every creature, one at a time, bosses last");
    }
}
