using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterCompanions.Core;
using UnityEngine;

namespace BetterCompanions;

/// <summary>
/// Your other companions: the ones not out, each kept whole (its levels, classes, stats, gear) in its own file next to
/// the companion's: Saves/companion_slotN.kept-STAMP.evc (+ .json, its notes), per character you play. One is out at a
/// time (companion_slotN.evc, as before): Swap in puts the one out into the roster and that one out. Replacing the one
/// out (a clone, a new one) keeps it here too. Delete asks first (again within 15 s), and leaves a dated .bak.
/// The dated copies replacing used to leave (companion_slotN.evc.replaced-STAMP.bak) come into the roster once: copied,
/// the .bak kept (renamed .imported.bak, so it's taken once).
/// The files are moved and copied as they are (the game's own save coding inside), never rewritten here.
/// </summary>
internal static class CompanionRoster
{
    internal sealed class Kept
    {
        public string Stamp, Name, Class;
        public int Level;
    }

    static string Dir => SaveLoad.GetPath("");
    static string Head(int playing) => Rules.CompanionFile(playing);
    static string KeptFile(int playing, string stamp) => Head(playing) + ".kept-" + stamp + CompanionSave.Extension;
    static string KeptMeta(int playing, string stamp) => Head(playing) + ".kept-" + stamp + ".json";
    static string Path(string name) => SaveLoad.GetPath(name);
    static string Now() => DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");

    static readonly List<Kept> list = new();
    static float listAt = -100f;
    static int listFor = -1;

    /// <summary>The roster (read once; again after a change).</summary>
    public static List<Kept> All()
    {
        int playing = CompanionSave.PlayingSlot;
        if (playing == 0) return new List<Kept>();
        if (listFor == playing && Time.unscaledTime < listAt) return list;
        listFor = playing;
        listAt = float.MaxValue;   // (read again only after a change: Changed)
        list.Clear();
        try
        {
            if (!Directory.Exists(Dir)) return list;
            Import(playing);
            string prefix = Head(playing) + ".kept-";
            foreach (var f in Directory.GetFiles(Dir, prefix + "*" + CompanionSave.Extension))
            {
                string name = System.IO.Path.GetFileName(f);
                string stamp = name.Substring(prefix.Length, name.Length - prefix.Length - CompanionSave.Extension.Length);
                try
                {
                    var d = JsonUtility.FromJson<PlayerData>(SaveLoad.Load(name));
                    if (d == null) continue;
                    int level = 1;
                    try { level = Math.Max(1, d.GetCurrentLevel(PlayerClassType.None)); } catch { }
                    list.Add(new Kept { Stamp = stamp, Name = string.IsNullOrEmpty(d.playerName) ? "(no name)" : d.playerName, Level = level,
                        Class = d.equippedClass == PlayerClassType.None ? "" : d.equippedClass.ToString() });
                }
                catch (Exception e) { Log.Warn($"Companion roster: {name}: {e.Message}"); }
            }
            list.Sort((a, b) => string.CompareOrdinal(b.Stamp, a.Stamp));   // (the newest first)
        }
        catch (Exception e) { Log.Warn($"Companion roster: {e.Message}"); }
        return list;
    }

    public static void Changed() => listAt = -100f;

    /// <summary>The one out now, into the roster (before it's replaced): a copy of its files as they are.</summary>
    public static void Keep()
    {
        int playing = CompanionSave.PlayingSlot;
        if (playing == 0 || !CompanionSave.Has) return;
        CompanionSave.Write();   // (what's in memory first)
        string stamp = Now();
        CopyIf(CompanionFileName(playing), KeptFile(playing, stamp));
        CopyIf(CompanionMetaName(playing), KeptMeta(playing, stamp));
        Log.Info($"Companion roster: {CompanionSave.Data.playerName} kept ({stamp})");
        Changed();
    }

    /// <summary>That one out (the one out now into the roster). Empty: done; else why not.</summary>
    public static string SwapIn(string stamp)
    {
        int playing = CompanionSave.PlayingSlot;
        if (playing == 0) return "Companion: load a character first.";
        string from = KeptFile(playing, stamp), fromMeta = KeptMeta(playing, stamp);
        if (!File.Exists(Path(from))) { Changed(); return "Companion: that companion isn't there any more."; }
        string was = CompanionSave.Data?.playerName;
        try
        {
            Keep();   // (the one out: kept as it is)
            File.Copy(Path(from), Path(CompanionFileName(playing)), overwrite: true);
            if (File.Exists(Path(fromMeta))) File.Copy(Path(fromMeta), Path(CompanionMetaName(playing)), overwrite: true);
            else if (File.Exists(Path(CompanionMetaName(playing)))) File.Delete(Path(CompanionMetaName(playing)));
            File.Delete(Path(from));
            if (File.Exists(Path(fromMeta))) File.Delete(Path(fromMeta));
        }
        catch (Exception e) { Changed(); return $"Companion: couldn't swap ({e.Message}); nothing was lost (see the Saves folder)."; }
        Changed();
        CompanionSave.Reread();
        if (CompanionSave.Has) { CompanionSave.Meta.dismissed = false; CompanionSave.Write(); }
        Companion.Reload();
        Log.Info($"Companion roster: {CompanionSave.Data?.playerName} out ({stamp}){(was != null ? $", {was} kept" : "")}");
        return $"Companion: {CompanionSave.Data?.playerName} (level {CompanionSave.Level}) joins you" + (was != null ? $"; {was} waits in your other companions." : ".");
    }

    /// <summary>Gone from the roster: its files renamed .deleted-STAMP.bak (still in the Saves folder).</summary>
    public static string Delete(string stamp)
    {
        int playing = CompanionSave.PlayingSlot;
        var k = list.Find(x => x.Stamp == stamp);
        string tag = ".deleted-" + Now() + ".bak";
        try
        {
            foreach (var n in new[] { KeptFile(playing, stamp), KeptMeta(playing, stamp) })
                if (File.Exists(Path(n))) File.Move(Path(n), Path(n) + tag);
        }
        catch (Exception e) { Changed(); return $"Companion: couldn't delete ({e.Message})."; }
        Changed();
        Log.Info($"Companion roster: {k?.Name} deleted ({stamp})");
        return $"Companion: {k?.Name ?? "that companion"} is gone (a copy is kept in the Saves folder, ending {tag}).";
    }

    /// <summary>The dated copies replacing used to leave: into the roster, once.</summary>
    static void Import(int playing)
    {
        string evc = CompanionFileName(playing) + ".replaced-";
        foreach (var f in Directory.GetFiles(Dir, evc + "*.bak"))
        {
            string name = System.IO.Path.GetFileName(f);
            if (name.EndsWith(".imported.bak")) continue;
            string stamp = name.Substring(evc.Length, name.Length - evc.Length - ".bak".Length);
            try
            {
                if (!File.Exists(Path(KeptFile(playing, stamp)))) File.Copy(f, Path(KeptFile(playing, stamp)));
                string meta = Path(CompanionMetaName(playing) + ".replaced-" + stamp + ".bak");
                if (File.Exists(meta))
                {
                    if (!File.Exists(Path(KeptMeta(playing, stamp)))) File.Copy(meta, Path(KeptMeta(playing, stamp)));
                    File.Move(meta, meta.Substring(0, meta.Length - ".bak".Length) + ".imported.bak");
                }
                File.Move(f, f.Substring(0, f.Length - ".bak".Length) + ".imported.bak");
                Log.Info($"Companion roster: {name} brought into the roster");
            }
            catch (Exception e) { Log.Warn($"Companion roster: {name}: {e.Message}"); }
        }
    }

    static string CompanionFileName(int playing) => Head(playing) + CompanionSave.Extension;
    static string CompanionMetaName(int playing) => Head(playing) + ".json";

    static void CopyIf(string from, string to)
    {
        if (File.Exists(Path(from))) File.Copy(Path(from), Path(to), overwrite: true);
    }
}
