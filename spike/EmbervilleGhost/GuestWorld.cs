using System.IO;
using System.IO.Compression;
using Cysharp.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// "You bring your character, the host brings the world."
///
/// Emberville keeps them in separate save files: player_slotN.evp is the character (look, inventory, XP, masteries);
/// a world is &lt;prefix&gt;.evw (WorldData: seed, chests, recipes, story flags, last scene/area/spot),
/// &lt;prefix&gt;.eve (estate: farm, buildings, soil, chopped trees) and &lt;prefix&gt;.evm (explored map). The map
/// itself is generated from WorldData.MapSeed and cached as &lt;settings&gt;S&lt;seed&gt;.dmap, but generation is not
/// fully deterministic (some steps use Unity's global Random), so we send that file too and the client never generates.
///
/// Join: the client asks (WorldRequest); the host collects its live world in memory, like a save without touching its
/// disk, sets the spawn point to where the host stands now, and sends it gzipped (WorldFiles). The client stores it as
/// a guest world in its own folder (SaveIsolation redirects the "ghostguest" prefix; never its own worlds, never in its
/// world list). At the main menu, after picking a character, the world screen is skipped: the guest world loads
/// (MainMenuManager.LoadWorldSave), so the client spawns next to the host. Its character keeps saving to its own file.
/// </summary>
internal static class GuestWorld
{
    public static Plugin Plugin;

    public const string Prefix = "ghostguest_world";

    public enum State { None, Downloading, NoWorld, Ready, Loading }
    public static State Status { get; private set; }
    public static bool Ready => Status == State.Ready;
    public static string WorldName = "";
    public static int Seed, SizeKB;
    public static bool GotMap;
    static bool loading;

    public static void Clear()
    {
        Status = State.None;
        loading = false;
    }

    // ---- client ----

    public static void Request()
    {
        var w = new NetDataWriter();
        w.Put((byte)MsgType.WorldRequest);
        Plugin.SendTo(0, w);
        Status = State.Downloading;
        NetLog.Info("Asking the host for its world...");
    }

    public static void OnWorld(NetDataReader r)
    {
        if (!r.GetBool())
        {
            Status = State.NoWorld;
            NetLog.Warn("The host isn't in a world yet: load one there, then join again.", toast: true);
            return;
        }
        var data = Unzip(r.GetBytesWithLength());
        var d = new NetDataReader(data);
        string worldName = d.GetString();
        string evw = d.GetString(), eve = d.GetString(), evm = d.GetString();
        string mapName = d.GetString(), map = d.GetString();
        WorldName = worldName;
        SizeKB = Mathf.Max(1, data.Length / 1024);
        GotMap = map.Length > 0;
        var wd = JsonUtility.FromJson<WorldData>(evw);
        Seed = wd != null ? wd.MapSeed : 0;

        SaveLoad.Save(Prefix + ".evw", evw);
        SaveLoad.Save(Prefix + ".eve", eve);
        if (evm.Length > 0) SaveLoad.Save(Prefix + ".evm", evm); else SaveLoad.Delete(Prefix + ".evm", showWarning: false);
        if (map.Length > 0) SaveLoad.Save(mapName, map);   // the generated map, so we never generate it ourselves
        Status = State.Ready;
        loading = false;
        NetLog.Success($"Got the host's world \"{worldName}\" ({SizeKB} KB). Pick a character and press Connect now.", toast: true);
    }

    /// <summary>
    /// Connect now: load the host's world with this character, from the main menu or from inside a game (our current
    /// game is saved first). The same steps as MainMenuManager.LoadWorldSave: set the save prefixes, load the data,
    /// start from the saved spot (next to the host), load its scene.
    /// </summary>
    public static void Load(string playerFile)
    {
        if (!Ready || string.IsNullOrEmpty(playerFile) || !Singleton<SceneLoader>.HasMain) return;
        if (LocalPlayer.Get() && GameData.HasWorldData && GameData.GetWorldPrefix != Prefix) GameData.SaveData();   // leaving our own game
        Status = State.Loading;
        loading = true;
        GameData.NewData();
        GameData.SetPlayerPrefix(playerFile);
        GameData.SetWorldPrefix(Prefix);
        GameData.LoadData();
        string scene = string.IsNullOrEmpty(GameData.worldData.lastActiveScene) ? "TutorialArea" : GameData.worldData.lastActiveScene;
        GameData.worldData.startFromSavedData = true;
        MainInput.Actions.UI.Disable();
        NetLog.Info($"Joining the host's world \"{WorldName}\"...", toast: true);
        Singleton<SceneLoader>.main.CallScene(scene, null).Forget();
    }

    /// <summary>Every frame: at the main menu, once a character is picked, load the host's world instead of showing ours.</summary>
    public static void Tick()
    {
        if (!Ready || loading || Plugin == null || !Plugin.IsClient) return;
        var menu = Object.FindAnyObjectByType<MainMenuManager>();
        if (!menu || menu.isBusy || menu.state != MainMenuState.WorldSelection) return;
        loading = true;
        Status = State.Loading;
        NetLog.Info("Loading the host's world...", toast: true);
        menu.LoadWorldSave(Prefix);
    }

    // ---- host ----

    /// <summary>Host: a client wants our world. Collect it in memory (no save to our disk) and send it.</summary>
    public static void OnRequest(int fromId)
    {
        var w = new NetDataWriter();
        w.Put((byte)MsgType.WorldFiles);
        if (!GameData.HasWorldData || !LocalPlayer.Get())
        {
            w.Put(false);
            Plugin.SendTo(fromId, w);
            return;
        }

        // What GameData.SaveData collects, without writing files: every save listener fills in its part.
        GameData.CallSaveListeners();
        GameData.worldData.bools = GameData.gameBools.ToGameBoolList();
        var world = JsonUtility.FromJson<WorldData>(JsonUtility.ToJson(GameData.worldData));   // a copy we can change
        var me = LocalPlayer.Get();
        world.lastActiveScene = LocalPlayer.Scene;   // they arrive next to us
        world.lastGameArea = LocalPlayer.Area;
        world.lastSpawnPosition = me.transform.position + new Vector3(me.isFliped ? -0.8f : 0.8f, 0f, 0f);   // beside us, not on top
        world.lastSpawnDirection = me.direction;

        string evm = "", mapName = "", map = "";
        if (Singleton<WorldMap>.HasMain)
        {
            var wm = Singleton<WorldMap>.main;
            var saved = wm.WorldMapState.GetSavedData();
            if (saved != null) evm = JsonUtility.ToJson(saved);
            if (wm.settings != null)
            {
                mapName = $"{wm.settings.name}S{GameData.worldData.MapSeed}.dmap";
                map = SaveLoad.Load(mapName) ?? "";
            }
        }

        var d = new NetDataWriter();
        d.Put(world.Name ?? "");
        d.Put(JsonUtility.ToJson(world));
        d.Put(JsonUtility.ToJson(GameData.playerEstateData));
        d.Put(evm);
        d.Put(mapName);
        d.Put(map);
        var zipped = Zip(d.CopyData());
        w.Put(true);
        w.PutBytesWithLength(zipped);
        Plugin.SendTo(fromId, w);
        NetLog.Info($"Sent our world to {Plugin.NameOf(fromId)} ({zipped.Length / 1024} KB zipped{(map.Length == 0 ? ", no generated map yet" : "")})");
    }

    // ---- gzip ----

    static byte[] Zip(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, System.IO.Compression.CompressionLevel.Optimal)) gz.Write(raw, 0, raw.Length);
        return output.ToArray();
    }

    static byte[] Unzip(byte[] zipped)
    {
        using var input = new GZipStream(new MemoryStream(zipped), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
