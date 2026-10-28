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
        "Bumblebee", "Gnome", "Snake", "Giant Bat", "Slingshot Gnome", "Thief", "Spear Goblin", "Torch Goblin", "Lizard",
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

    static void StartTiny()
    {
        EndChickens("stopped");
        EndRush("stopped");
        ClearSpawned();
        var have = Monsters.Names();
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
        if (tinyWave >= waveList.Count) { EndTiny("won"); NetLog.Success("You beat every Tiny Swords creature!", toast: true); return; }
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
        if (ImGui.Button("Tiny Swords waves")) StartTiny();
        ImGui.SameLine();
        ImGui.TextDisabled(bestTiny > 0 ? $"every creature, one at a time (best: wave {bestTiny})" : "every creature, one at a time, bosses last");
    }
}
