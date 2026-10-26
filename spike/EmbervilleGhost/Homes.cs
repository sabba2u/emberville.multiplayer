using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Your own named places: /sethome (here is "home"), /sethome mansion (here is "mansion"), /tp mansion (go there),
/// /home (go home), /homes (the list), /delhome mansion. Each character has its own (by save slot), kept in
/// BepInEx/config/spike.embervilleghost.homes.json. "home" also moves the Town Portal's far end: the button opens your
/// portal pair to there instead of town. Not on a dungeon or descent floor, nor in the boss arena (they're built
/// again each time; nothing there stays put). Each home leaves a portal on the ground (green, its name over it): a
/// marker, not a door.
/// </summary>
internal static class Homes
{
    [System.Serializable]
    sealed class Place { public string name, scene, area; public float x, y; }

    [System.Serializable]
    sealed class Character { public string key; public List<Place> places = new(); }

    [System.Serializable]
    sealed class File { public List<Character> characters = new(); }

    static File file;
    static string Path => System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "spike.embervilleghost.homes.json");
    static string Me => GameData.playerFilePrefix ?? "";

    static File Load()
    {
        if (file != null) return file;
        try { file = System.IO.File.Exists(Path) ? JsonUtility.FromJson<File>(System.IO.File.ReadAllText(Path)) : null; }
        catch (System.Exception e) { NetLog.Warn($"Homes: couldn't read them ({e.Message})", toast: true); }
        return file ??= new File();
    }

    static void Save()
    {
        try
        {
            if (System.IO.File.Exists(Path)) System.IO.File.Copy(Path, Path + ".bak", overwrite: true);   // (the last good copy)
            System.IO.File.WriteAllText(Path, JsonUtility.ToJson(file, prettyPrint: true));
        }
        catch (System.Exception e) { NetLog.Warn($"Homes: couldn't save them ({e.Message})", toast: true); }
    }

    static List<Place> Mine(bool make = false)
    {
        var c = Load().characters.Find(x => x.key == Me);
        if (c == null && make) { c = new Character { key = Me }; file.characters.Add(c); }
        return c?.places ?? new List<Place>();
    }

    static string Clean(string name) => string.IsNullOrWhiteSpace(name) ? "home" : name.Trim().ToLowerInvariant();

    /// <summary>A home by name (null: none such).</summary>
    public static Travel.Spot? Get(string name)
    {
        var p = Mine().Find(x => x.name == Clean(name));
        return p == null ? null : new Travel.Spot { Scene = p.scene, Area = p.area, At = new Vector3(p.x, p.y, 0f) };
    }

    public static bool Has(string name) => Get(name).HasValue;

    /// <summary>Your homes' names ("home" first).</summary>
    public static List<string> Names() => Mine().Select(x => x.name).OrderBy(n => n != "home").ThenBy(n => n).ToList();

    /// <summary>/sethome [name]: here.</summary>
    public static void Set(string name)
    {
        name = Clean(name);
        if (string.IsNullOrEmpty(Me) || !(Travel.Here() is Travel.Spot here)) { NetLog.Info("Load a character first", toast: true); return; }
        if (here.InDungeon || Arena.Active) { NetLog.Info("Not here: dungeon floors and the arena are built again each time", toast: true); return; }
        var list = Mine(make: true);
        list.RemoveAll(x => x.name == name);
        list.Add(new Place { name = name, scene = here.Scene, area = here.Area, x = here.At.x, y = here.At.y });
        Save();
        NetLog.Success(name == "home"
            ? "Home set here. /home (or /tp home) comes back; the Town Portal opens to here now."
            : $"\"{name}\" set here. /tp {name} comes back.", toast: true);
    }

    /// <summary>/delhome name.</summary>
    public static void Delete(string name)
    {
        name = Clean(name);
        var list = Mine();
        if (list.RemoveAll(x => x.name == name) == 0) { NetLog.Info($"No home called \"{name}\" (/homes lists them)", toast: true); return; }
        Save();
        NetLog.Info(name == "home" ? "Home forgotten: the Town Portal goes to town again." : $"\"{name}\" forgotten.", toast: true);
    }

    /// <summary>/homes.</summary>
    public static void List()
    {
        var list = Mine();
        Chat.System(list.Count == 0 ? "No homes yet: /sethome (or /sethome mansion) where you stand."
            : "Your homes: " + string.Join(", ", list.OrderBy(x => x.name != "home").ThenBy(x => x.name).Select(x => $"{x.name} ({x.area})")) + ".  /tp name goes there.");
    }

    // ---- each home's portal on the ground (where you are: that scene and area), with its name over it ----

    static readonly Dictionary<string, GameObject> shown = new();
    static readonly Color Tint = new Color(0.55f, 1f, 0.75f);
    static float nextLook;

    /// <summary>A few times a second: the portals of the homes here (made, moved, gone as they change).</summary>
    public static void Tick()
    {
        if (Time.unscaledTime < nextLook) return;
        nextLook = Time.unscaledTime + 0.3f;
        var want = new Dictionary<string, Vector3>();
        if (LocalPlayer.Get() && !Dungeon.Active && !string.IsNullOrEmpty(Me))
            foreach (var p in Mine())
                if (p.scene == LocalPlayer.Scene && p.area == LocalPlayer.Area) want[p.name] = new Vector3(p.x, p.y, 0f);
        foreach (var name in shown.Keys.ToList())
            if (!want.ContainsKey(name) || !shown[name] || shown[name].transform.position != want[name])
            { if (shown[name]) Object.Destroy(shown[name]); shown.Remove(name); }
        foreach (var kv in want)
        {
            if (shown.ContainsKey(kv.Key)) continue;
            var go = new GameObject("EG_Home_" + kv.Key);
            go.transform.position = kv.Value;
            TownPortal.Look(go.transform, kv.Value, Tint);
            shown[kv.Key] = go;
        }
    }

    /// <summary>Their names over them (ImGui, with the HUD).</summary>
    public static void Draw()
    {
        var cam = Camera.main;
        if (!cam || shown.Count == 0) return;
        var dl = ImGuiNET.ImGui.GetBackgroundDrawList();
        foreach (var kv in shown)
        {
            if (!kv.Value) continue;
            var sp = cam.WorldToScreenPoint(kv.Value.transform.position + new Vector3(0f, 1.6f, 0f));
            if (sp.z < 0f) continue;
            string label = kv.Key == "home" ? "Home" : kv.Key;
            var size = ImGuiNET.ImGui.CalcTextSize(label);
            var pos = new ImGuiNET.Vector2(sp.x - size.x / 2f, Screen.height - sp.y);
            dl.AddText(new ImGuiNET.Vector2(pos.x + 1f, pos.y + 1f), ImGuiNET.ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(0f, 0f, 0f, 0.8f)), label);
            dl.AddText(pos, ImGuiNET.ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(Tint.r, Tint.g, Tint.b, 1f)), label);
        }
    }

    /// <summary>/tp name, /home: there. False: no home by that name (so /tp looks for a player).</summary>
    public static bool Go(string name)
    {
        var to = Get(name);
        if (!to.HasValue) return false;
        if (Travel.Go(to.Value)) NetLog.Info($"To {Clean(name)}...", toast: true);
        return true;
    }
}
