// SPIKE: a second player in Emberville. Patterns carried over from DuckovGhost.
//
// F9  = host (host window: LAN / Steam)
// F10 = join (connect to Address:Port)
// F10 = disconnect (when connected)
// Insert = open the Dear ImGui menu (drawn by Unity inside the game frame)
//
// Star topology: clients only talk to the host; the host relays player messages to the other clients.
// Each player sends its own character's position, facing and animators ~20x/sec (Unreliable).
// Everyone else shows a stand-in there (RemotePlayer + RemoteAvatar), played back slightly in the past (SnapshotBuffer).
// Step 1 is "see each other walk around"; the world (time, crops, chests, enemies) is still each copy's own.

using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.InputSystem;

namespace EmbervilleGhost;

[BepInPlugin("spike.embervilleghost", "EmbervilleGhost", Version)]
[BepInDependency("spike.emberville.betterchests", BepInDependency.DependencyFlags.SoftDependency)]   // loads first if installed
[BepInDependency(Integrations.CombatGuid, BepInDependency.DependencyFlags.SoftDependency)]   // the split-out mods too (Integrations)
[BepInDependency(Integrations.LootingGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(Integrations.PetsGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(Integrations.NpcsGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(Integrations.WeatherGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(Integrations.CompanionGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("spike.emberville.bettertranslations", BepInDependency.DependencyFlags.SoftDependency)]   // (Better Translations: /lang)
public class Plugin : BaseUnityPlugin, ITransportListener
{
    internal const string Version = "0.4.0";
    internal const string ModName = "Sabba's Emberville Multiplayer Mod";
    internal static string Title => $"{ModName} v{Version}";

    internal static ManualLogSource Log;

    const float SendInterval = 0.05f;
    const float LookCheckInterval = 0.5f;

    internal ConfigEntry<string> AddressSetting;
    internal ConfigEntry<int> PortSetting;
    ConfigEntry<bool> FreeMouse;
    ConfigEntry<int> MaxPlayers;

    ITransport transport;
    LiteNetTransport lan;
    SteamTransport steam;
    /// <summary>Connected (or connecting) through Steam rather than by IP.</summary>
    internal bool OnSteam => transport != null && transport == steam;
    readonly NetDataWriter writer = new NetDataWriter();
    float sendTimer, lookTimer;
    byte[] myLook;   // last look we sent (LookSync); null = send on the next check

    // ---- who's who ----
    Conn hostPeer;                                                          // client: our connection to the host
    readonly Dictionary<Conn, int> peerIds = new Dictionary<Conn, int>();   // host: client connection -> player id
    readonly Dictionary<int, Conn> idPeers = new Dictionary<int, Conn>();
    readonly Dictionary<int, RemotePlayer> players = new Dictionary<int, RemotePlayer>();   // everyone but us
    readonly HashSet<int> snapshotSentFor = new HashSet<int>();   // owner: players that got our area's snapshot
    string snapshotPlace = "", lastScene = "";
    int nextPlayerId = 1;

    /// <summary>Our player id: host 0, clients get theirs from the host (Welcome). -1 = not yet.</summary>
    internal int MyId { get; private set; } = -1;
    internal bool IsHost { get; private set; }
    /// <summary>Client: the host. Host: any client (null if nobody is connected). Non-null = we have company.</summary>
    internal Conn Remote => IsHost ? (peerIds.Count > 0 ? FirstClient() : null) : hostPeer;
    internal bool Connected => Remote != null;
    internal bool IsClient => !IsHost && hostPeer != null;
    internal IEnumerable<RemotePlayer> Players => players.Values;
    internal int PlayerCount => players.Count + 1;
    internal string Status { get; private set; } = "idle (F9 host, F10 join)";

    void Awake()
    {
        Log = Logger;
        AddressSetting = Config.Bind("Network", "Address", "127.0.0.1", "Host address to join with F10");
        if (string.IsNullOrWhiteSpace(AddressSetting.Value)) AddressSetting.Value = "127.0.0.1";   // cleared: this PC
        PortSetting = Config.Bind("Network", "Port", 9060, "UDP port to host on / join");
        MaxPlayers = Config.Bind("Network", "MaxPlayers", 16, "Players including the host (also the Steam lobby size)");
        if (MaxPlayers.Value == 4) MaxPlayers.Value = 16;   // the old default
        FreeMouse = Config.Bind("Window", "FreeMouse", true, "Don't let the game lock/confine the mouse to its window (easier with two copies)");
        WindowFix.Bind(Config);
        var harmony = new Harmony("spike.embervilleghost");
        SaveIsolation.Init(Config);
        harmony.PatchAll();
        ChestSync.HookBetterChests(harmony);   // X at a chest (Better Chests mod), if installed
        lan = new LiteNetTransport(this);
        steam = new SteamTransport(this);
        SteamLobby.Plugin = this;
        SteamBoot.Init();
        // Keep running while the other copy has focus.
        Application.runInBackground = true;
        Hud.Plugin = this;
        EnemySync.Plugin = this;
        AttackFx.Plugin = this;
        TimeSync.Plugin = this;
        PlayerFeel.Plugin = this;
        StationSync.Plugin = this;
        FarmSync.Plugin = this;
        PlaceSync.Plugin = this;
        GroundSync.Plugin = this;
        InteractSync.Plugin = this;
        CutsceneSync.Plugin = this;
        Diag.Plugin = this;
        DesyncCheck.Plugin = this;
        Dungeon.Plugin = this;
        MiniBoss.Plugin = this;
        Arena.Init();
        Delve.Plugin = this;
        Dungeon.Init();
        StorySync.Plugin = this;
        TeamBuffs.Plugin = this;
        Pings.Plugin = this;
        HookNet.Plugin = this;
        SummonSync.Plugin = this;
        CompanionSync.Plugin = this;
        RemotePets.Plugin = this;
        ArenaTraps.Plugin = this;
        BossSync.Plugin = this;
        DropSync.Plugin = this;
        PropSync.Plugin = this;
        ChestSync.Plugin = this;
        NpcSync.Plugin = this;
        NpcLooks.Plugin = this;
        WeatherSync.Plugin = this;
        TownPortal.Plugin = this;
        Travel.Plugin = this;
        Chat.Plugin = this;
        HookSounds.Init(Config);
        Chat.Lift = Config.Bind("Chat", "HeightAboveBottom", 0.25f, "Chat's bottom edge, as a fraction of the window height from the bottom (clears the game's health bar and hotbar)");
        PlayerHits.Plugin = this;
        GatherSync.Plugin = this;
        GuestWorld.Plugin = this;
        DoorSync.Plugin = this;
        FxSync.Plugin = this;
        JoinWindow.Plugin = this;
        MenuBanner.Plugin = this;
        CoopDeath.Plugin = this;
        Session.Plugin = this;
        HostWindow.Plugin = this;
        Session.Bind(Config);
        OnlinePlayers.Bind(Config);
        StoryText.Bind(Config);
        Arena.BindChickens(Config);
        TiltView.Bind(Config);
        Walls3D.Bind(Config);
        TiltShift.Bind(Config);
        Bestiary.Bind(Config);
        BuildMode.Bind(Config);
        TeamHud.Bind(Config);
        Hud.ShowStatus = Config.Bind("Hud", "ShowStatus", false, "The status line top-left (version, connection, fps); Delete shows / hides it");
        DemoEnd.Bind(Config);
        MouseWalk.Bind(Config);
        Diag.Bind(Config);
        DesyncCheck.Bind(Config);
        Delve.Bind(Config);
        Integrations.Init();   // Better Combat / Looting / Pets and Summons, if installed
        TeamHud.Plugin = this;
        MapMates.Plugin = this;
        Trade.Plugin = this;
        OnlinePlayers.Plugin = this;
        ImGuiUnity.OnRender += Hud.Draw;
        try { GameConsole.Init(Config); ImGuiUnity.OnRender += GameConsole.Draw; } catch (System.Exception e) { Log.LogWarning($"Console: {e}"); }
        try { ImGuiUnity.Init(Config, System.IO.Path.GetDirectoryName(Info.Location)); }
        catch (Exception e) { Log.LogError($"ImGui init failed, no menu/HUD: {e}"); }
        NetLog.Info($"{Title} loaded. F9 host, F10 join / disconnect, Insert = menu, Delete = status line.", toast: true);
    }

    void LateUpdate()
    {
        if (FreeMouse.Value && Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
    }

    // Perf readout for the status line: frame rate and how long our own code takes per frame.
    readonly System.Diagnostics.Stopwatch modWatch = new System.Diagnostics.Stopwatch();
    float fpsTimer; int fpsFrames;
    internal float Fps { get; private set; }
    internal float ModMs { get; private set; }
    internal int StateBytes { get; private set; }

    void FixedUpdate() => HunterHook.FixedTick();   // the hook's pull on us (physics steps)

    void Update()
    {
        modWatch.Restart();
        fpsFrames++;
        fpsTimer += Time.unscaledDeltaTime;
        if (fpsTimer >= 0.5f) { Fps = fpsFrames / fpsTimer; fpsFrames = 0; fpsTimer = 0f; }

        WindowFix.Tick();
        var kb = Keyboard.current;
        if (kb != null && Application.isFocused)
        {
            if (kb.f9Key.wasPressedThisFrame) HostWindow.Show();   // pick the session rules, then HOST NOW
            if (kb.f10Key.wasPressedThisFrame) { if (Connected) Stop(); else Join(); }   // join, or leave when connected
            if (kb.deleteKey.wasPressedThisFrame && !ImGuiUnity.Typing && !Chat.IsOpen) Hud.ShowStatus.Value = !Hud.ShowStatus.Value;   // the status line top-left
            if (Hud.ShowStatus.Value && !ImGuiUnity.Typing && !Chat.IsOpen)   // with it up: Home / End copy where you stand / where the mouse points
            {
                if (kb.homeKey.wasPressedThisFrame) Hud.CopySpot(mouse: false);
                if (kb.endKey.wasPressedThisFrame) Hud.CopySpot(mouse: true);
            }
        }

        SteamBoot.Tick();
        CharacterSafety.Tick();
        DemoEnd.Tick();
        Stockpile.Tick();
        Arsenal.Tick();
        MenuLightWatch.Tick();
        GroundSync.Tick();
        MouseWalk.Tick();
        Diag.Tick();
        DesyncCheck.Tick();
        DungeonInvite.Tick();   // a teammate's dungeon: the Join / Not now card
        Delve.Tick();   // the descent: entrance pad, stronger creatures on deeper floors
        MiniBoss.Tick();   // our mini-bosses: health, colour, the boss bar, loot
        Arena.Tick();
        ArenaTraps.Tick();   // the arena's traps: in step with whoever switched them
        Rory.Tick();
        NpcLooks.Tick();   // the host's NPCs (and Rory) look the same on every game
        GearDye.Tick();
        TownPortal.Tick();
        Homes.Tick();   // your /sethome places' portals
        DeathGuard.Tick();   // a death a stun interrupted: die properly
        ModsButton.Tick();   // the MODS buttons (main menu, Esc menu): the Insert menu
        StoryText.Tick();   // bigger story text (dialogue, bubbles, the quest tracker)
        TiltView.Tick();    // the tilted 2.5D view (/tilt)
        TiltShift.Tick();   // the miniature look (/tiltshift)
        Travel.Tick();   // arriving in another scene: on to the exact spot   // the town portal button, its channel, everyone's portal pairs   // dyed gear (and the Hunter's Hook's yellow on every armour piece)
        Dungeon.Tick();      // creatures off the navigation mesh put back on it
        DungeonMap.Tick();   // the dungeon minimap (top left; ~ hides it)
        NoClip.Tick();       // /noclip stays on through deaths and respawns
        HunterHook.Tick();   // the Hunter's Hook: the throw, the rope, the drag
        HookNet.Tick();      // teammates' hooks: their ropes
        if (Integrations.HasWeather) WeatherSync.Tick();   // Better Weather: the storm for each player who joins
        Integrations.Tick();   // teammates' pets, chickens and risen (Better Pets and Summons)
        if (Integrations.HasNpcs) NpcSync.Tick();   // Better NPCs and Quests: the host's files and story for everyone
        SteamLobby.Tick();
        CheckLeftWorld();
        DevMode.Tick();
        Pings.Tick();
        OnlinePlayers.Tick();
        Bestiary.Tick();   // N: the Bestiary
        if (BuildMode.Enabled)
        {
            BuildMode.Tick();   // B: build mode anywhere
            BuildSave.Tick();   // what B built: saved per world, put back per scene
        }
        Trade.Tick();
        TeamHud.Tick();   // right-click a party card: teleport / trade / mute
        TeamHud.RenderPortraits();
        TickKicks();
        TickMigration();
        CoopDeath.Tick();   // before the game's frame logic: last frame's death reset (if any) is over
        TimeSync.Tick();
        if (transport != null && transport.IsRunning) NetTick();
        Chat.Tick();
        GameConsole.Tick();

        Perf.Begin("4 imgui");
        try { ImGuiUnity.Update(); }
        catch (Exception e) { Log.LogError($"imgui frame: {e}"); }
        Perf.End("4 imgui");
        ModMs = Mathf.Lerp(ModMs, (float)modWatch.Elapsed.TotalMilliseconds, 0.1f);
    }

    // ImGui is drawn here, after the game's cameras, so it sits on top of the frame.
    void OnGUI()
    {
        try { ImGuiUnity.Render(); }
        catch (Exception e) { Log.LogError($"imgui render: {e}"); }
    }

    void NetTick()
    {
        if (LocalPlayer.Scene != lastScene) { lastScene = LocalPlayer.Scene; SceneIds.Clear(); EnemySync.SceneChanged(); FxSync.Clear(); AttackFx.Clear(); BossSync.Clear(); StationSync.Clear(); FarmSync.Clear(); Pings.Clear(); HookNet.Clear(); Integrations.ForgetTeammates(); GroundSync.Clear(); InteractSync.Clear(); CutsceneSync.Clear(); DoorSync.Clear(); }   // ids belong to a scene

        Perf.Begin("1 receive"); transport.Poll(); Perf.End("1 receive");

        sendTimer += Time.unscaledDeltaTime;
        if (Connected && MyId >= 0 && sendTimer >= SendInterval)
        {
            sendTimer = 0f;
            Perf.Begin("2 send"); SendLocalState(); Perf.End("2 send");
        }

        lookTimer += Time.unscaledDeltaTime;
        if (Connected && MyId >= 0 && lookTimer >= LookCheckInterval)
        {
            lookTimer = 0f;
            Perf.Begin("2 look"); SendLookIfChanged(); Perf.End("2 look");
        }

        if (IsHost) UpdateOwners();
        NoticeOwnerChange();
        GuestWorld.Tick();
        PlaceSync.Tick();
        StorySync.Tick();

        Perf.Begin("2 world");
        if (MyId >= 0)
        {
            DropSync.Tick();
            EnemySync.Tick();
            if (IOwnHere) SendArrivalSnapshots();
        }
        Perf.End("2 world");

        Perf.Begin("3 teammates");
        string scene = LocalPlayer.Scene, area = LocalPlayer.Area;
        foreach (var p in players.Values) p.Update(scene, area);
        Perf.End("3 teammates");
    }

    /// <summary>
    /// Owner of our area: a player who just arrived gets what it can't know: drops lying here, things already broken,
    /// chest contents. (Creatures stream continuously, so they need nothing.) Only to that player.
    /// </summary>
    void SendArrivalSnapshots()
    {
        string here = Place;
        if (here != snapshotPlace) { snapshotPlace = here; snapshotSentFor.Clear(); }   // we moved: everyone counts as arriving
        foreach (var p in players.Values)
        {
            bool there = p.SamePlace(LocalPlayer.Scene, LocalPlayer.Area);
            if (!there) { snapshotSentFor.Remove(p.Id); continue; }
            if (!snapshotSentFor.Add(p.Id)) continue;
            EnemySync.SendSnapshot(p.Id);
            MiniBoss.SendSnapshot(p.Id);   // bosses' names, colours, sizes
            DropSync.SendSnapshot(p.Id);
            PropSync.SendSnapshot(p.Id);
            ChestSync.SendSnapshot(p.Id);
            ChestSync.SendLooksSnapshot(p.Id);   // chest names and colours (Better Chests)
            GatherSync.SendSnapshot(p.Id);
            DoorSync.SendSnapshot(p.Id);
            InteractSync.SendSnapshot(p.Id);
            CutsceneSync.SendSnapshot(p.Id);
            StationSync.SendSnapshot(p.Id);
            FarmSync.SendSnapshot(p.Id);
        }
    }

    void OnDestroy() => Stop();

    void OnApplicationQuit()
    {
        CharacterSafety.Release();
        HookOutfit.Undress();   // (the clothes back to your colour)
        Stop();
        SteamBoot.Shutdown();
    }

    /// <summary>Host through Steam: a friends-only lobby, friends join from the friends list or an invite (no IPs).</summary>
    internal void HostSteam()
    {
        if (!SteamBoot.Ready) { NetLog.Error("Steam isn't running (start the game from Steam)", toast: true); return; }
        Stop();
        transport = steam;
        steam.StartHost();
        IsHost = true;
        MyId = 0;
        nextPlayerId = 1;
        Status = "creating a Steam lobby...";
        SteamLobby.Host(MaxPlayers.Value);
    }

    /// <summary>SteamLobby: our lobby exists, friends can come.</summary>
    internal void SteamHostReady() => Status = $"hosting through Steam (up to {MaxPlayers.Value} players)";

    /// <summary>SteamLobby: we're in a friend's lobby; connect to its owner through Steam.</summary>
    internal void JoinSteam(Steamworks.CSteamID host)
    {
        Stop(leaveLobby: false);
        transport = steam;
        steam.StartClient(host);
        hostAddress = host.m_SteamID.ToString();
        Status = $"connecting to {Steamworks.SteamFriends.GetFriendPersonaName(host)} through Steam...";
        JoinWindow.Show();
    }

    internal void Host() => Host(PortSetting.Value);

    internal void Host(int port)
    {
        Stop();
        transport = lan;
        if (lan.StartHost(port))
        {
            IsHost = true;
            MyId = 0;
            nextPlayerId = 1;
            Status = $"hosting on {PortSetting.Value}, waiting...";
            NetLog.Info($"Hosting on port {PortSetting.Value} (up to {MaxPlayers.Value} players)", toast: true);
        }
        else
        {
            Status = $"failed to listen on {PortSetting.Value}";
            NetLog.Error($"Couldn't listen on port {PortSetting.Value} (already in use?)");
        }
    }

    internal void Join()
    {
        Stop();
        transport = lan;
        if (string.IsNullOrWhiteSpace(AddressSetting.Value)) AddressSetting.Value = "127.0.0.1";   // blank = this PC
        AddressSetting.Value = AddressSetting.Value.Trim();
        lan.StartClient(AddressSetting.Value, PortSetting.Value);
        hostAddress = AddressSetting.Value;
        Status = $"connecting to {AddressSetting.Value}:{PortSetting.Value}...";
        JoinWindow.Show();
        NetLog.Info($"Connecting to {AddressSetting.Value}:{PortSetting.Value}...", toast: true);
    }

    internal void Stop() => Stop(leaveLobby: true);

    internal void Stop(bool leaveLobby)
    {
        if (leaveLobby && OnSteam) SteamLobby.Leave();
        if (transport != null && transport.IsRunning)
        {
            transport.Stop();
            NetLog.Info($"Network stopped ({transport.Name})");
        }
        transport = null;
        ForgetEveryone();
        // A session ended: nothing of a host migration in progress or of the old session's world-leaving check survives.
        roster.Clear();
        guids.Clear();
        pendingKicks.Clear();
        kicked = false;
        rejoining = migrateHost = false;
        migrateAt = -1f;
        hadWorld = false;
        menuCheck = 0f;
        IsHost = false;
        MyId = -1;
        Status = "idle (F9 host, F10 join)";
    }

    void ForgetEveryone()
    {
        myLook = null;
        hostPeer = null;
        peerIds.Clear();
        idPeers.Clear();
        foreach (var p in players.Values) p.Destroy();
        players.Clear();
        snapshotSentFor.Clear();
        snapshotPlace = "";
        areaOwners.Clear();
        arrivals.Clear();
        ownersSig = "";
        lastOwnerHere = -2;
        EnemySync.Clear();
        DropSync.Clear();
        PropSync.Clear();
        ChestSync.Clear();
        GatherSync.Clear();
        GuestWorld.Clear();
        PlaceSync.Clear();
        TeamBuffs.ForgetAll();
        TimeSync.Clear();
        Integrations.ForgetTeammates();
        SceneIds.Clear();
    }

    Conn FirstClient()
    {
        foreach (var peer in peerIds.Keys) return peer;
        return null;
    }

    RemotePlayer GetPlayer(int id)
    {
        if (!players.TryGetValue(id, out var p)) players[id] = p = new RemotePlayer(id);
        return p;
    }

    // ---- sending ----

    /// <summary>Host: to every client wherever they are, never to <paramref name="except"/>.</summary>
    internal void SendToAllClients(NetDataWriter data, DeliveryMethod method, Conn except = null)
    {
        foreach (var peer in peerIds.Keys)
            if (peer != except) peer.Send(data, method);
    }

    // ---- area ownership ----
    //
    // Whoever has been in an area (scene + GameArea) the longest runs it: its creatures, breakables, drops, chests.
    // Everyone else there is a puppet of that player's world. The host only decides who that is (it sees everyone's
    // place) and carries the messages; it is just another player otherwise. Like our BDSP mod's "longest-present
    // player in the area hosts its NPCs". Handover is safe because puppets mirror the owner's world as it happens.

    readonly Dictionary<string, int> areaOwners = new Dictionary<string, int>();                     // place -> owner id (host decides)
    readonly Dictionary<int, (string place, double since)> arrivals = new Dictionary<int, (string, double)>();   // host
    string ownersSig = "";
    int lastOwnerHere = -2;

    internal static string PlaceOf(string scene, string area) => scene + "/" + area;
    internal string Place => PlaceOf(LocalPlayer.Scene, LocalPlayer.Area);

    /// <summary>
    /// Who runs the area we're in (us when alone or not connected). Just after we walk in, the host's AreaOwners list
    /// may not have arrived yet: then whoever is already here runs it (they were here first), never us by default.
    /// </summary>
    internal int OwnerHere
    {
        get
        {
            if (!Connected) return MyId;
            if (areaOwners.TryGetValue(Place, out var o) && (o == MyId || PlayerHere(o))) return o;
            int first = int.MaxValue;
            string scene = LocalPlayer.Scene, area = LocalPlayer.Area;
            foreach (var p in players.Values) if (p.SamePlace(scene, area) && p.Id < first) first = p.Id;
            return first == int.MaxValue ? MyId : first;
        }
    }

    bool PlayerHere(int id) => players.TryGetValue(id, out var p) && p.SamePlace(LocalPlayer.Scene, LocalPlayer.Area);
    /// <summary>We decide what happens in our area (not connected counts as yes).</summary>
    internal bool IOwnHere => !Connected || OwnerHere == MyId;
    /// <summary>Who runs that player's area, -1 if unknown.</summary>
    internal int OwnerOf(RemotePlayer p) => areaOwners.TryGetValue(PlaceOf(p.Scene, p.Area), out var o) ? o : -1;

    /// <summary>Someone else is in our scene and area.</summary>
    internal bool AnyoneElseHere
    {
        get
        {
            string scene = LocalPlayer.Scene, area = LocalPlayer.Area;
            foreach (var p in players.Values) if (p.SamePlace(scene, area)) return true;
            return false;
        }
    }

    /// <summary>Host, every tick: the longest-present player in each occupied place owns it; tell everyone when that changes.</summary>
    void UpdateOwners()
    {
        double now = Time.unscaledTimeAsDouble;
        Arrive(0, Place, now);
        foreach (var p in players.Values)
            if (p.HaveState && idPeers.ContainsKey(p.Id)) Arrive(p.Id, PlaceOf(p.Scene, p.Area), now);
        var gone = new List<int>();
        foreach (var id in arrivals.Keys) if (id != 0 && !idPeers.ContainsKey(id)) gone.Add(id);
        foreach (var id in gone) arrivals.Remove(id);

        var best = new Dictionary<string, (int id, double since)>();
        foreach (var pair in arrivals)
        {
            var (place, since) = pair.Value;
            if (!best.TryGetValue(place, out var b) || since < b.since || (since == b.since && pair.Key < b.id)) best[place] = (pair.Key, since);
        }
        var keys = new List<string>(best.Keys);
        keys.Sort(string.CompareOrdinal);
        string sig = "";
        foreach (var k in keys) sig += k + "=" + best[k].id + ";";
        if (sig == ownersSig) return;
        ownersSig = sig;

        areaOwners.Clear();
        foreach (var k in keys) areaOwners[k] = best[k].id;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.AreaOwners);
        w.Put(keys.Count);
        foreach (var k in keys) { w.Put(k); w.Put(best[k].id); }
        SendToAllClients(w, DeliveryMethod.ReliableOrdered);
    }

    void Arrive(int id, string place, double now)
    {
        if (!arrivals.TryGetValue(id, out var a) || a.place != place) arrivals[id] = (place, now);
    }

    void OnAreaOwners(NetDataReader r)
    {
        areaOwners.Clear();
        int n = r.GetInt();
        for (int i = 0; i < n; i++) areaOwners[r.GetString()] = r.GetInt();
    }

    /// <summary>Tell the player when the area they're in changes hands.</summary>
    void NoticeOwnerChange()
    {
        int owner = Connected ? OwnerHere : -2;
        if (owner == lastOwnerHere) return;
        bool first = lastOwnerHere == -2;
        lastOwnerHere = owner;
        if (!Connected || first && owner == MyId) return;
        if (owner == MyId) NetLog.Info("You run this area now", toast: true);
        else NetLog.Info($"{NameOf(owner)} runs this area", toast: true);
    }

    internal string NameOf(int id) => id == MyId ? "You" : players.TryGetValue(id, out var p) ? p.Name : (id == 0 ? "Host" : $"P{id + 1}");

    // ---- routing: to one player, to the owner, to everyone in our area (clients go through the host) ----

    /// <summary>To one player wherever they are. Clients reach other clients through the host (ToPlayer envelope).</summary>
    internal void SendTo(int id, NetDataWriter data, DeliveryMethod method = DeliveryMethod.ReliableOrdered)
    {
        if (id == MyId || id < 0) return;
        if (IsHost) { if (idPeers.TryGetValue(id, out var peer)) peer.Send(data, method); return; }
        if (id == 0) { hostPeer?.Send(data, method); return; }
        var w = new NetDataWriter();
        w.Put((byte)MsgType.ToPlayer);
        w.Put(id);
        w.Put(data.Data, 0, data.Length);
        hostPeer?.Send(w, method);
    }

    /// <summary>A request to whoever runs our area.</summary>
    internal void SendToOwner(NetDataWriter data, DeliveryMethod method) => SendTo(OwnerHere, data, method);

    /// <summary>To everyone else in our scene+area (what the area's owner tells its puppets).</summary>
    internal void SendToPlace(NetDataWriter data, DeliveryMethod method)
    {
        if (IsHost) { SendToPlaceFrom(0, LocalPlayer.Scene, LocalPlayer.Area, data.Data, 0, data.Length, method); return; }
        var w = new NetDataWriter();
        w.Put((byte)MsgType.ToPlace);
        w.Put(LocalPlayer.Scene);
        w.Put(LocalPlayer.Area);
        w.Put(data.Data, 0, data.Length);
        hostPeer?.Send(w, method);
    }

    /// <summary>To every other player, wherever they are (things that outlive a visit: buildings on the estate).</summary>
    internal void SendToAll(NetDataWriter data, DeliveryMethod method)
    {
        if (IsHost) { SendToAllClients(data, method); return; }
        var w = new NetDataWriter();
        w.Put((byte)MsgType.ToAll);
        w.Put(data.Data, 0, data.Length);
        hostPeer?.Send(w, method);
    }

    /// <summary>Host: deliver a message from <paramref name="from"/> to every client in that place except the sender.</summary>
    void SendToPlaceFrom(int from, string scene, string area, byte[] data, int offset, int length, DeliveryMethod method)
    {
        NetDataWriter w = null;
        foreach (var pair in peerIds)
        {
            if (pair.Value == from || !players.TryGetValue(pair.Value, out var p) || !p.SamePlace(scene, area)) continue;
            if (w == null)
            {
                w = new NetDataWriter();
                if (from == 0) w.Put(data, offset, length);
                else { w.Put((byte)MsgType.FromPlayer); w.Put(from); w.Put(data, offset, length); }
            }
            pair.Key.Send(w, method);
        }
    }

    /// <summary>That player's stand-in in our world, if it's showing.</summary>
    internal Transform AvatarOf(int id) =>
        players.TryGetValue(id, out var p) && p.Avatar != null && p.Avatar.Visible ? p.Avatar.Root.transform : null;

    void SendLocalState()
    {
        var me = LocalPlayer.Get();
        if (!me) return;

        writer.Reset();
        writer.Put((byte)MsgType.PlayerState);
        writer.Put(MyId);
        writer.Put(Time.unscaledTimeAsDouble);   // sender clock, for the receiver's snapshot buffer
        writer.Put(LocalPlayer.Scene);
        writer.Put(LocalPlayer.Area);
        var p = me.transform.position;
        writer.Put(p.x); writer.Put(p.y); writer.Put(p.z);
        writer.Put(me.isFliped);
        writer.Put(me.CurrentHealthPoints);
        writer.Put(me.characterStats ? me.MaxHealthPoints : 0);
        writer.Put(me.characterStats ? Mathf.Max(me.CurrentKymiaPoints, 0f) : 0f);   // Kymia: what skills and spells cost
        writer.Put(me.characterStats ? me.MaxKymiaPoints : 0f);
        writer.Put(MapMates.InWilds);   // in the generated wilds: our spot goes on everyone's world map
        writer.Put(me.currentWeaponItem?.data != null ? me.currentWeaponItem.data.addressKey ?? "" : "");   // the party strip shows it
        writer.Put(Integrations.PetKindByte);   // our pet (0 none, else which kind), so theirs follows our stand-in
        writer.Put((byte)Mathf.Min(Integrations.PetLevel, 255));
        writer.Put(Integrations.PetName);
        Color32 petTint = Integrations.PetTint;
        writer.Put(petTint.r); writer.Put(petTint.g); writer.Put(petTint.b);
        writer.Put((byte)PlayerStatusUtil.Local());
        AnimSync.Write(writer, LocalPlayer.Animators(me));
        StateBytes = writer.Length;   // unreliable packets must fit one MTU (~1.4 KB)

        if (IsHost) SendToAllClients(writer, DeliveryMethod.Unreliable);
        else hostPeer?.Send(writer, DeliveryMethod.Unreliable);
    }

    /// <summary>Our hair, clothes, gear, colours and name: sent (reliable) only when they change.</summary>
    void SendLookIfChanged()
    {
        var me = LocalPlayer.Get();
        if (!me) return;
        string name = GameData.HasPlayerData ? GameData.playerData.playerName : "";
        var look = LookSync.Capture(me.gameObject, name);
        if (myLook != null && SameBytes(look, myLook)) return;
        myLook = look;
        var w = LookMessage(MyId, look);
        if (IsHost) SendToAllClients(w, DeliveryMethod.ReliableOrdered);
        else hostPeer?.Send(w, DeliveryMethod.ReliableOrdered);
    }

    static NetDataWriter LookMessage(int id, byte[] look)
    {
        var w = new NetDataWriter();
        w.Put((byte)MsgType.Look);
        w.Put(id);
        w.PutBytesWithLength(look);
        return w;
    }

    static bool SameBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    void ReadLook(NetDataReader reader, int id)
    {
        var look = reader.GetBytesWithLength();
        if (id == MyId || id < 0) return;
        var p = GetPlayer(id);
        bool first = p.Look == null;
        p.Look = look;
        p.CharacterName = LookSync.ReadName(look);
        if (first) NetLog.Info($"Got {p.Name}'s look ({look.Length} bytes)");
    }

    /// <summary>Host: pass a client's player message on to everyone else, unchanged.</summary>
    void Relay(NetDataReader reader, Conn from, DeliveryMethod method)
    {
        var copy = new NetDataWriter();
        copy.Put(reader.RawData, reader.UserDataOffset, reader.UserDataSize);
        SendToAllClients(copy, method, except: from);
    }

    // ---- transport callbacks (run inside transport.Poll, on the main thread) ----

    bool ITransportListener.AcceptConnection()
    {
        if (!IsHost) return false;
        if (PlayerCount < MaxPlayers.Value) return true;
        NetLog.Warn($"Rejected a player: full ({MaxPlayers.Value} players)");
        return false;
    }

    void ITransportListener.OnConnected(Conn peer)
    {
        if (!IsHost)
        {
            hostPeer = peer;
            Status = $"connected to host {peer}";
            NetLog.Success($"Connected to host {peer}, waiting for a player id...");
            return;
        }

        // The lowest free number (P2, P3...): someone who left frees theirs, so a cancelled join doesn't push the next
        // player up to P5, P6...
        int id = 1;
        while (idPeers.ContainsKey(id) || players.ContainsKey(id)) id++;
        peerIds[peer] = id;
        idPeers[id] = peer;
        GetPlayer(id);
        Status = $"hosting, {PlayerCount} players";
        NetLog.Success($"P{id + 1} joined from {peer}");

        var w = new NetDataWriter();
        w.Put((byte)MsgType.Welcome);
        w.Put(id);
        peer.Send(w, DeliveryMethod.ReliableOrdered);
        Session.SendTo(id);   // the host's rules (shared XP, instanced loot)
        PlaceSync.SendBuiltTo(id);   // what's been built anywhere (B)
        SendRoster();
        ownersSig = "";       // re-send who runs which area, now including them

        // The looks of everyone already here (host + other clients); later changes arrive as they happen.
        if (myLook != null) peer.Send(LookMessage(0, myLook), DeliveryMethod.ReliableOrdered);
        foreach (var p in players.Values)
            if (p.Id != id && p.Look != null) peer.Send(LookMessage(p.Id, p.Look), DeliveryMethod.ReliableOrdered);
    }

    void OnWelcome(NetDataReader reader)
    {
        MyId = reader.GetInt();
        Status = $"connected as P{MyId + 1}";
        var who = new NetDataWriter();
        who.Put((byte)MsgType.Identity);
        who.Put(OnlinePlayers.MyGuid);   // the host's bans follow this, not our name
        hostPeer?.Send(who, DeliveryMethod.ReliableOrdered);
        NetLog.Success($"You are P{MyId + 1}");
        if (rejoining)
        {
            // Reconnected to the player who took over from a host that left: we're already in that world.
            rejoining = false;
            NetLog.Success("Back together with the new host", toast: true);
            return;
        }
        GuestWorld.Request();   // the host brings the world; we bring our character
    }

    void ITransportListener.OnDisconnected(Conn peer, string reason)
    {
        if (!IsHost)
        {
            if (peer != hostPeer)
            {
                NetLog.Warn($"Couldn't connect: {reason}");
                Status = $"failed ({reason})";
                if (rejoining)
                {
                    rejoining = false;
                    NetLog.Warn("Couldn't reach the new host (on the internet they need port " + migratePort + " forwarded). You're playing on your own.", toast: true);
                }
                return;
            }
            int me = MyId;
            var (next, nextName, others) = Successor(me);
            ForgetEveryone();
            MyId = -1;
            Status = $"disconnected ({reason})";
            NetLog.Warn($"Host disconnected: {reason}");
            // Only when the host really left (closed the connection: quit to menu, quit the game, F10). A timeout can
            // be our own network blipping while the host carries on: taking over then would split the session.
            if (me > 0 && LocalPlayer.Get() && reason == "RemoteConnectionClose" && !kicked) { migrateSteam = OnSteam; TakeOver(me, next, nextName, others); }
            return;
        }

        if (!peerIds.TryGetValue(peer, out int id)) return;
        peerIds.Remove(peer);
        idPeers.Remove(id);
        RemovePlayer(id);
        snapshotSentFor.Remove(id);   // their number may be given out again: the next one is a fresh arrival
        arrivals.Remove(id);
        ChestSync.Unlock(id);
        TimeSync.Forget(id);
        TeamBuffs.Forget(id);
        Status = peerIds.Count > 0 ? $"hosting, {PlayerCount} players" : $"hosting on {PortSetting.Value}, waiting...";
        NetLog.Warn($"P{id + 1} left ({reason})");

        var w = new NetDataWriter();
        w.Put((byte)MsgType.PlayerLeft);
        w.Put(id);
        SendToAllClients(w, DeliveryMethod.ReliableOrdered);
        SendRoster();
    }

    // ---- the Online Players screen's moderation (host): kick, ban; ping ----

    readonly Dictionary<int, string> guids = new Dictionary<int, string>();   // host: player id -> their player ID (bans)
    readonly List<(int id, float at)> pendingKicks = new List<(int, float)>();
    bool kicked;   // client: the host threw us out (no taking over)

    /// <summary>Round trip to that player in ms (host: anyone; client: only the host), -1 unknown.</summary>
    internal int PingOf(int id)
    {
        if (IsHost) return idPeers.TryGetValue(id, out var peer) ? peer.Ping : -1;
        return id == 0 && hostPeer != null ? hostPeer.Ping : -1;
    }

    void OnIdentity(NetDataReader r, int from)
    {
        string guid = r.GetString();
        guids[from] = guid;
        if (!OnlinePlayers.IsBanned(guid)) return;
        NetLog.Warn($"A banned player tried to join ({NameOf(from)}): sent away");
        Kick(from, ban: false, silent: true, why: "You're banned from this host's games.");
    }

    /// <summary>Host: tell them why, then close their connection a moment later (so the message gets there).</summary>
    internal void Kick(int id, bool ban, bool silent = false, string why = null)
    {
        if (!IsHost || id == 0 || !idPeers.TryGetValue(id, out var peer)) return;
        string name = NameOf(id);
        if (ban) OnlinePlayers.AddBan(guids.TryGetValue(id, out var g) && !string.IsNullOrEmpty(g) ? g : "name:" + name, name);
        var w = new NetDataWriter();
        w.Put((byte)MsgType.Kicked);
        w.Put(ban);
        w.Put(why ?? (ban ? "The host banned you." : "The host removed you from the game."));
        peer.Send(w, DeliveryMethod.ReliableOrdered);
        pendingKicks.Add((id, Time.unscaledTime + 0.5f));
        if (!silent) NetLog.Warn($"{name} was {(ban ? "banned" : "kicked")}", toast: true);
    }

    void TickKicks()
    {
        for (int i = 0; i < pendingKicks.Count; i++)
        {
            if (Time.unscaledTime < pendingKicks[i].at) continue;
            if (idPeers.TryGetValue(pendingKicks[i].id, out var peer)) peer.Disconnect();
            pendingKicks.RemoveAt(i--);
        }
    }

    void OnKicked(NetDataReader r)
    {
        bool ban = r.GetBool();
        string why = r.GetString();
        kicked = true;
        NetLog.Warn(why + (ban ? " (banned)" : ""), toast: true);
        GameUi.Message(ban ? "Banned" : "Removed from the game", why);   // the game's own bordered box
    }

    // ---- the host leaving: the next player takes over (todo #10) ----
    //
    // When the host goes back to the main menu (or quits, or crashes), the clients don't stay attached to it: the
    // player who joined first (lowest id) starts hosting right where they are, in the world they're already in, and
    // the others reconnect to them without loading anything. The host keeps everyone's address (as it sees them) in a
    // roster it sends to all, so every client knows the same successor and where to find them. On a LAN that just
    // works; over the internet the new host needs the port forwarded (Steam relay will remove that, next version).

    readonly Dictionary<int, string> roster = new Dictionary<int, string>();   // client: player id -> address (host's view)
    int rosterPort;
    bool rejoining;              // reconnecting to a new host from inside its world: skip the world download
    string hostAddress = "";     // client: the address we reached the host at
    bool migrateHost;            // we're the one taking over: start hosting
    bool migrateSteam;           // the session was on Steam: take over / reconnect through Steam (addresses are Steam IDs)
    float migrateAt = -1f;
    string migrateTo = "";
    int migratePort;
    bool hadWorld;
    float menuCheck;

    void SendRoster()
    {
        var w = new NetDataWriter();
        w.Put((byte)MsgType.Roster);
        w.Put(PortSetting.Value);
        w.Put(idPeers.Count);
        foreach (var pair in idPeers) { w.Put(pair.Key); w.Put(pair.Value.Address); }
        SendToAllClients(w, DeliveryMethod.ReliableOrdered);
    }

    void OnRoster(NetDataReader r)
    {
        roster.Clear();
        rosterPort = r.GetInt();
        int n = r.GetInt();
        for (int i = 0; i < n; i++) roster[r.GetInt()] = r.GetString();
    }

    /// <summary>Who takes over when the host is gone: the client who joined first. Also their name, and how many others there are.</summary>
    (int id, string name, int others) Successor(int me)
    {
        int next = me;
        foreach (int id in roster.Keys) if (id > 0 && id < next) next = id;
        int others = 0;
        foreach (int id in roster.Keys) if (id > 0 && id != me) others++;
        return (next, NameOf(next), others);
    }

    void TakeOver(int me, int next, string nextName, int others)
    {
        if (next == me)
        {
            // Not here: we're inside the transport's event loop. Next frame (TickMigration).
            migrateHost = true;
            migratePort = rosterPort > 0 ? rosterPort : PortSetting.Value;   // the old host's port: where the others will look
            migrateAt = Time.unscaledTime;
            NetLog.Success(others > 0
                ? $"The host left. You're the host now; {others} other player{(others == 1 ? "" : "s")} will reconnect to you."
                : "The host left. You're hosting now (the world carries on here; others can join you).", toast: true);
            return;
        }
        if (!roster.TryGetValue(next, out string address)) return;
        // The host saw them on its own machine (127.0.0.1): for us that machine is the host's address.
        if (address == "127.0.0.1" || address == "::1" || address.StartsWith("::ffff:127.")) address = hostAddress;
        migrateTo = address;
        migratePort = rosterPort > 0 ? rosterPort : PortSetting.Value;
        migrateAt = Time.unscaledTime + 2f;   // give them a moment to start hosting
        NetLog.Info($"The host left. {nextName} takes over; reconnecting to them...", toast: true);
    }

    void TickMigration()
    {
        if (migrateAt < 0f || Time.unscaledTime < migrateAt) return;
        migrateAt = -1f;
        if (!LocalPlayer.Get()) { migrateHost = false; return; }   // went to the menu meanwhile
        if (migrateHost) { migrateHost = false; if (migrateSteam) HostSteam(); else Host(migratePort); return; }
        if (migrateSteam && ulong.TryParse(migrateTo, out ulong steamId))
        {
            Stop(leaveLobby: true);
            transport = steam;
            rejoining = true;
            steam.StartClient(new Steamworks.CSteamID(steamId));
            hostAddress = migrateTo;
            Status = "reconnecting to the new host through Steam...";
            return;
        }
        Stop();
        transport = lan;
        rejoining = true;
        hostAddress = migrateTo;
        lan.StartClient(migrateTo, migratePort);
        Status = $"reconnecting to the new host {migrateTo}:{migratePort}...";
    }

    /// <summary>Back at the main menu from a world: we left the session (as host, the next player takes over).</summary>
    void CheckLeftWorld()
    {
        if (LocalPlayer.Get()) { hadWorld = Connected; return; }   // in a world as part of a session
        if (!hadWorld || !Connected) return;
        menuCheck += Time.unscaledDeltaTime;
        if (menuCheck < 1f) return;
        menuCheck = 0f;
        if (!UnityEngine.Object.FindAnyObjectByType<MainMenuManager>()) return;   // just a scene change inside the world
        hadWorld = false;
        bool wasHost = IsHost;
        Stop();
        NetLog.Info(wasHost ? "You left the world: hosting stopped, the next player takes over." : "You left the world: disconnected.", toast: true);
    }

    void RemovePlayer(int id)
    {
        if (!players.TryGetValue(id, out var p)) return;
        p.Destroy();
        players.Remove(id);
    }

    void ITransportListener.OnReceive(Conn peer, NetDataReader reader, DeliveryMethod method)
    {
        try
        {
            var type = (MsgType)reader.GetByte();
            // Host: trust the connection, not the packet, for who sent it. Client: plain messages come from the host.
            int from = IsHost ? (peerIds.TryGetValue(peer, out int pid) ? pid : -1) : 0;
            switch (type)
            {
                case MsgType.Welcome: if (!IsHost) OnWelcome(reader); break;
                case MsgType.PlayerLeft: if (!IsHost) { int id = reader.GetInt(); RemovePlayer(id); TeamBuffs.Forget(id); NetLog.Warn($"P{id + 1} left"); } break;
                case MsgType.AreaOwners: if (!IsHost) OnAreaOwners(reader); break;
                case MsgType.Roster: if (!IsHost) OnRoster(reader); break;
                case MsgType.Identity: if (IsHost) OnIdentity(reader, from); break;
                case MsgType.Kicked: if (!IsHost) OnKicked(reader); break;
                case MsgType.PlayerState:
                    if (IsHost) Relay(reader, peer, method);
                    { int id = reader.GetInt(); ReadPlayerState(reader, IsHost ? from : id); }
                    break;
                case MsgType.Look:
                    if (IsHost) Relay(reader, peer, DeliveryMethod.ReliableOrdered);
                    { int id = reader.GetInt(); ReadLook(reader, IsHost ? from : id); }
                    break;
                case MsgType.Chat:
                    if (IsHost) Relay(reader, peer, DeliveryMethod.ReliableOrdered);
                    { int id = reader.GetInt(); Chat.OnChat(IsHost ? from : id, reader.GetString()); }
                    break;
                case MsgType.PlayerDied:
                    if (IsHost) Relay(reader, peer, DeliveryMethod.ReliableOrdered);
                    {
                        int id = IsHost ? from : reader.GetInt();
                        if (IsHost) reader.GetInt();
                        string cause = reader.GetString();
                        bool gore = reader.GetBool();
                        Chat.Died(id, cause);
                        if (players.TryGetValue(id, out var dead) && dead.Avatar != null && dead.Avatar.Visible)
                            dead.Avatar.PlayDeath(LocalPlayer.Get(), gore);   // their death animation + scorch / gore mark here too
                    }
                    break;

                // Envelopes: client -> host -> another client / everyone in a place.
                case MsgType.ToPlayer:
                    if (IsHost && from > 0)
                    {
                        int target = reader.GetInt();
                        if (target == 0) Dispatch(Inner(reader), from);
                        else if (idPeers.TryGetValue(target, out var to))
                        {
                            var w = new NetDataWriter();
                            w.Put((byte)MsgType.FromPlayer); w.Put(from);
                            w.Put(reader.RawData, reader.Position, reader.AvailableBytes);
                            to.Send(w, method);
                        }
                    }
                    break;
                case MsgType.ToPlace:
                    if (IsHost && from > 0)
                    {
                        string scene = reader.GetString(), area = reader.GetString();
                        int at = reader.Position, len = reader.AvailableBytes;
                        SendToPlaceFrom(from, scene, area, reader.RawData, at, len, method);
                        if (LocalPlayer.Scene == scene && LocalPlayer.Area == area) Dispatch(Inner(reader), from);
                    }
                    break;
                case MsgType.ToAll:
                    if (IsHost && from > 0)
                    {
                        var w = new NetDataWriter();
                        w.Put((byte)MsgType.FromPlayer); w.Put(from);
                        w.Put(reader.RawData, reader.Position, reader.AvailableBytes);
                        SendToAllClients(w, method, except: peer);
                        Dispatch(Inner(reader), from);
                    }
                    break;
                case MsgType.FromPlayer:
                    if (!IsHost) { int sender = reader.GetInt(); Dispatch(Inner(reader), sender); }
                    break;

                default: Dispatch(reader, from, type); break;
            }
        }
        catch (Exception e)
        {
            NetLog.Error($"Bad packet: {e.Message}");
        }
    }

    /// <summary>The message wrapped inside an envelope, as its own reader.</summary>
    static NetDataReader Inner(NetDataReader reader) => new NetDataReader(reader.GetRemainingBytes());

    void Dispatch(NetDataReader reader, int from) => Dispatch(reader, from, (MsgType)reader.GetByte());

    /// <summary>
    /// World messages, whoever relayed them. Requests are only answered by whoever runs our area; results are only
    /// taken by puppets (we don't run this area). Replies always go to the one who asked.
    /// </summary>
    void Dispatch(NetDataReader reader, int from, MsgType type)
    {
        bool owner = IOwnHere;
        switch (type)
        {
            // requests -> the area's owner
            case MsgType.EnemyHit: EnemySync.OnHit(reader, from); break;   // creatures: whoever runs that one decides (EnemySync checks)
            case MsgType.EnemyHitRefused: EnemySync.OnHitRefused(reader); break;
            case MsgType.PropHit: if (owner) PropSync.OnHit(reader, from); break;
            case MsgType.DropRequest: if (owner) DropSync.OnDropRequest(reader); break;
            case MsgType.DropTake: if (owner) DropSync.OnTake(reader, from); break;
            case MsgType.ChestOpen: if (owner) ChestSync.OnOpenRequest(reader, from); break;
            case MsgType.ChestClose: if (owner) ChestSync.OnClose(reader, from); break;
            case MsgType.GatherHit: if (owner) GatherSync.OnHit(reader, from); break;
            // results -> puppets
            case MsgType.EnemyState: EnemySync.OnState(reader); break;
            case MsgType.EnemyDamaged: EnemySync.OnDamaged(reader); break;
            case MsgType.EnemySpawn: EnemySync.OnSpawn(reader); break;   // the area owner's spawns, or a creature someone claimed (a hatchling of their boss's egg)
            case MsgType.EnemyRunner: if (!owner) EnemySync.OnRunner(reader); break;
            case MsgType.PropDamaged: if (!owner) PropSync.OnDamaged(reader); break;
            case MsgType.PropBroken: if (!owner) PropSync.OnBroken(reader); break;
            case MsgType.DropSpawn: if (from != MyId) DropSync.OnSpawn(reader); break;   // only the area owner sends these; never filter on our maybe-stale idea of who that is
            case MsgType.DropRemoved: if (!owner) DropSync.OnRemoved(reader); break;
            case MsgType.DropTakenBy: DropSync.OnTakenBy(reader); break;
            case MsgType.SessionSettings: if (!IsHost) Session.OnSettings(reader); break;
            case MsgType.TimeSync: if (!IsHost) TimeSync.OnTime(reader); break;
            case MsgType.SleepReady: if (IsHost) TimeSync.OnSleepReady(reader, from); break;
            case MsgType.SleepState: if (!IsHost) TimeSync.OnSleepState(reader); break;
            case MsgType.ChestState: if (!owner) ChestSync.OnState(reader); break;
            case MsgType.GatherDamaged: if (!owner) GatherSync.OnDamaged(reader); break;
            case MsgType.GatherState: if (!owner) GatherSync.OnState(reader); break;
            // from whoever used it (doors / levers: the user's copy runs it, everyone in the area mirrors it)
            case MsgType.DoorChange: DoorSync.OnDoor(reader); break;
            case MsgType.SwitchChange: DoorSync.OnSwitch(reader); break;
            case MsgType.DoorSnapshot: if (!owner) DoorSync.OnSnapshot(reader); break;
            case MsgType.ProjectileFx: FxSync.OnProjectileFx(reader); break;
            case MsgType.AttackFx: AttackFx.OnAttackFx(reader); break;
            case MsgType.PlayerHurt: PlayerFeel.OnHurt(reader, from); break;
            case MsgType.PlayerEffect: PlayerFeel.OnEffect(reader, from); break;
            case MsgType.TeamBuff: TeamBuffs.OnBuff(reader, from); break;
            case MsgType.TeamSkill: TeamBuffs.OnSkill(reader, from); break;
            case MsgType.TradeInvite: Trade.OnInvite(from); break;   // trading: between the two players only
            case MsgType.TradeAnswer: Trade.OnAnswer(reader, from); break;
            case MsgType.TradeOffer: Trade.OnOffer(reader, from); break;
            case MsgType.TradeLock: Trade.OnLock(reader, from); break;
            case MsgType.TradeCancel: Trade.OnCancel(reader, from); break;
            case MsgType.Ping: Pings.OnPing(reader, from); break;
            case MsgType.HookRope: HookNet.OnRope(reader, from); break;
            case MsgType.HookThrow: HookNet.OnThrow(reader, from); break;
            case MsgType.HookText: HookNet.OnText(reader, from); break;
            case MsgType.HookPull: HookNet.OnPull(reader, from); break;
            case MsgType.HookBlood: HookNet.OnBlood(reader, from); break;
            case MsgType.TownPortal: TownPortal.OnMessage(reader); break;
            case MsgType.TpAsk: Travel.OnTpAsk(from); break;
            case MsgType.SummonTo: Travel.OnSummon(reader); break;
            case MsgType.WeatherStorm: if (Integrations.HasWeather) WeatherSync.OnStorm(reader); break;
            case MsgType.WeatherStrike: if (Integrations.HasWeather) WeatherSync.OnStrike(reader); break;
            case MsgType.Summons: if (Integrations.HasPets) SummonSync.OnSummons(reader, from); break;
            case MsgType.CompanionState: if (Integrations.HasCompanion) CompanionSync.OnState(reader, from); break;
            case MsgType.CompanionLook: if (Integrations.HasCompanion) CompanionSync.OnLook(reader, from); break;
            case MsgType.CompanionRevive: if (Integrations.HasCompanion) CompanionSync.OnRevive(from); break;
            case MsgType.PathDraw: Pings.OnPath(reader, from); break;
            case MsgType.ChestTake: if (owner) ChestSync.OnTake(reader, from); break;   // shared chests: the area owner's are real
            case MsgType.ChestShared: if (!owner) ChestSync.OnShared(reader); break;
            case MsgType.ChestShort: ChestSync.OnShort(reader); break;
            case MsgType.StationEnqueue: if (owner) StationSync.OnEnqueue(reader, from); break;   // stations: the area owner's copy cooks
            case MsgType.StationCollect: if (owner) StationSync.OnCollect(reader, from); break;
            case MsgType.StationGive: StationSync.OnGive(reader); break;
            case MsgType.StationState: if (!owner) StationSync.OnState(reader); break;
            case MsgType.StationXp: StationSync.OnXp(reader); break;
            case MsgType.SoilState: FarmSync.OnState(reader); break;   // farm plots: whoever farms sends the plot
            case MsgType.PlaceChange: PlaceSync.OnChange(reader); break;   // building: whoever builds sends it
            case MsgType.BuiltList: if (!IsHost) PlaceSync.OnBuiltList(reader); break;   // the host's list of what's built anywhere
            case MsgType.GroundPress: GroundSync.OnPress(reader, from); break;
            case MsgType.LogBatch: if (IsHost) Diag.OnBatch(reader, from); break;   // diagnostics: everyone's lines in one log
            case MsgType.StateDigest: DesyncCheck.OnDigest(reader, from); break;   // diagnostics: the mismatch checker
            case MsgType.DungeonOpen: Dungeon.OnOpen(reader, from); break;
            case MsgType.ArenaOpen: Dungeon.OnArena(reader, from); break;
            case MsgType.ArenaTraps: ArenaTraps.OnState(reader); break;
            case MsgType.ChestLook: ChestSync.OnLook(reader); break;
            case MsgType.NpcFiles: if (Integrations.HasNpcs && !IsHost) NpcSync.OnFiles(reader); break;
            case MsgType.NpcBook: if (Integrations.HasNpcs && !IsHost) NpcSync.OnBook(reader); break;
            case MsgType.NpcAsk: if (Integrations.HasNpcs && IsHost) NpcSync.OnAsk(reader); break;
            case MsgType.NpcWander: if (Integrations.HasNpcs) NpcSync.OnWander(reader); break;
            case MsgType.NpcLook: if (!IsHost) NpcLooks.OnLooks(reader); break;
            case MsgType.BossLook: MiniBoss.OnLook(reader); break;   // a mini-boss's size, colour, name, toughness   // generated dungeons: someone went in, the others may join
            case MsgType.CutsceneStart: CutsceneSync.OnStart(reader, from); break;   // story cutscenes: everyone in the area watches
            case MsgType.CutsceneSnapshot: if (!owner) CutsceneSync.OnSnapshot(reader); break;   // story cutscenes: everyone in the area watches
            case MsgType.ItemUnlocked: InteractSync.OnItemUnlocked(reader); break;   // key altars: whoever has the item opens it for all
            case MsgType.Repaired: InteractSync.OnRepaired(reader); break;   // repairs: the repairer pays, everyone sees it
            case MsgType.InteractSnapshot: if (!owner) InteractSync.OnSnapshot(reader); break;   // floor buttons: down while anyone stands on one   // building: whoever builds sends it
            case MsgType.EstateUnlock: if (from == 0) DebugPanel.UnlockEstate(); break;   // the host's call
            case MsgType.StoryFlag: StorySync.OnFlag(reader, from); break;   // story progress: anyone's counts for all
            case MsgType.EggSpawn: BossSync.OnEggSpawn(reader, from); break;   // eggs: whoever's boss laid it owns it
            case MsgType.EggHit: BossSync.OnEggHit(reader, from); break;
            case MsgType.EggDamaged: BossSync.OnEggDamaged(reader); break;
            case MsgType.EggDestroy: BossSync.OnEggDestroy(reader); break;
            case MsgType.PlayerTrapped: BossSync.OnPlayerTrapped(reader, from); break;
            // to one player
            case MsgType.PlayerHit: PlayerHits.OnPlayerHit(reader); break;
            case MsgType.WorldRequest: if (IsHost) GuestWorld.OnRequest(from); break;
            case MsgType.WorldFiles: if (!IsHost) GuestWorld.OnWorld(reader); break;
            // replies -> the one who asked
            case MsgType.DropTakeOk: DropSync.OnTakeOk(reader); break;
            case MsgType.DropDenied: DropSync.OnDenied(reader); break;
            case MsgType.ChestContents: ChestSync.OnContents(reader); break;
            case MsgType.ChestDenied: ChestSync.OnDenied(reader); break;
            default: NetLog.Warn($"Unknown message type {(byte)type}"); break;
        }
    }

    void ReadPlayerState(NetDataReader reader, int id)
    {
        double senderTime = reader.GetDouble();
        string scene = reader.GetString();
        string area = reader.GetString();
        var pos = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        bool flip = reader.GetBool();
        int health = reader.GetInt();
        int maxHealth = reader.GetInt();
        float kymia = reader.GetFloat();
        float maxKymia = reader.GetFloat();
        bool inWilds = reader.GetBool();
        string weapon = reader.GetString();
        byte petKind = reader.GetByte();
        int petLevel = reader.GetByte();
        string petName = reader.GetString();
        var petColor = new Color32(reader.GetByte(), reader.GetByte(), reader.GetByte(), 255);
        var status = (PlayerStatus)reader.GetByte();
        var anim = AnimSync.Read(reader);
        if (id == MyId || id < 0) return;

        var p = GetPlayer(id);
        p.Status = status;
        p.Flip = flip;
        p.Health = health;
        p.MaxHealth = maxHealth;
        p.Kymia = kymia;
        p.MaxKymia = maxKymia;
        p.InWilds = inWilds;
        p.Weapon = weapon;
        p.PetKind = petKind;
        p.PetLevel = petLevel;
        p.PetName = petName;
        p.PetColor = petColor;
        p.Anim = anim;

        // A new scene or area means a teleport: don't blend from the old position.
        if (scene != p.Scene || area != p.Area)
        {
            p.Snapshots.Clear();
            bool same = scene == LocalPlayer.Scene && area == LocalPlayer.Area;
            NetLog.Info($"{p.Name} is now in {scene} / {area}" + (same ? " (same as you)" : ""), toast: true);
        }
        p.Snapshots.Add(senderTime, pos, 0f, Time.unscaledTimeAsDouble);
        p.LastPosition = pos;
        p.Scene = scene;
        p.Area = area;
        p.HaveState = true;
    }
}

/// <summary>First byte of every packet.</summary>
internal enum MsgType : byte
{
    PlayerState = 1,   // player -> host -> others, unreliable ~20 Hz: player id, sender time, scene, area, position, flip, health, max health, status, animators
    Look = 2,          // player -> host -> others, reliable, when it changes: player id, LookSync bytes (name, parts on/off, material colours)
    Welcome = 3,       // host -> new client: your player id
    PlayerLeft = 4,    // host -> clients: player id
    ToPlayer = 5,      // client -> host envelope: target player id, message (host forwards it as FromPlayer, or handles it if target 0)
    ToPlace = 6,       // client -> host envelope: scene, area, message (host forwards to everyone else there as FromPlayer, handles it if it's there)
    FromPlayer = 7,    // host -> client envelope: sender player id, message
    AreaOwners = 8,    // host -> all clients, reliable, when it changes: count, [place "scene/area", owner player id] (longest present runs it)
    Chat = 9,          // player -> host -> everyone else, reliable: player id, text (Enter opens the chat box)
    PlayerDied = 10,   // player -> host -> everyone else, reliable: player id, what killed them ("" = unknown), gore death
    WorldRequest = 11, // client -> host on joining: send me your world
    WorldFiles = 12,   // host -> that client: has world, gzip [name, .evw, .eve, .evm, map file name, generated map] (GuestWorld)

    // World messages: "owner" = the player running that area (EmbervilleGhost's authority), "puppets" = everyone else there.
    EnemyState = 30,   // owner -> puppets, unreliable ~10 Hz, one per creature: id (path hash), time, position, flip, dead, health, tint rgb, animators
    EnemySpawn = 31,   // owner -> puppets (or one arriving player), reliable: runtime id (top bit set), prefab name, position, flip (a creature spawned while playing)
    EnemyRunner = 32,  // area owner -> everyone in the area (or one arriving player): creature id, player id that runs it now (the nearest; its AI chases them)
    EnemyHit = 33,     // puppet -> runner, reliable: id, amount, result, hit type, knockback, no-stagger, attacker player id, hops (passed on if it changed hands)
    EnemyHitRefused = 35, // runner -> hitter, reliable: id (it's immune there, past the grace; show "Immune")
    EnemyDamaged = 34, //owner -> puppets, reliable: id, amount, result, hit type, knockback, no-stagger, health before, hitter player id (-1 none)
    PropHit = 40,      // puppet -> owner, reliable: breakable id (path hash), amount, hit type, knockback
    PropDamaged = 41,  // owner -> puppets, reliable: id, amount, hit type, knockback, health before, hitter player id
    PropBroken = 42,   // owner -> one arriving player: count, ids already broken in this scene
    PlayerHit = 45,    // attacker's copy -> victim: creature id (0 = none), attacking player id (PvP, -1 = creature), attacker position, amount, crit chance, effect chance, hit type, knockback, no-stagger, Attack name (unresolved; the victim's copy resolves it)
    DropSpawn = 50,    // owner -> puppets (or one arriving player), reliable: drop id, from, to, item key, amount, random values JSON
    DropRequest = 51,  // puppet -> owner, reliable: item key, amount, values, position, radius (our overflow / dropped item; the owner drops it)
    DropTake = 52,     // puppet -> owner, reliable: drop id (may I pick it up?)
    DropTakeOk = 53,   // owner -> requester: it's yours (the owner already removed it)
    DropDenied = 54,   // owner -> requester: someone was faster
    DropRemoved = 55,  // owner -> puppets: drop id is gone (picked up)
    DropTakenBy = 56,  // owner -> everyone in the area (or an arriving player): instanced drop id, player who took their copy
    SessionSettings = 13, // host -> clients (on joining and on change): shared XP, instanced loot
    TimeSync = 14,     // host -> clients, sequenced, every second: day, time of day (hours)
    Roster = 15,       // host -> clients, on every join / leave: host port, count x (player id, address as the host sees it) (host migration)
    ToAll = 16,        // client -> host: envelope, deliver to everyone (host relays as FromPlayer)
    EstateUnlock = 17, // host -> everyone: turn on estate building + repairs (story flags the demo never sets)
    StoryFlag = 18,    // player -> everyone (ToAll), reliable: BoolID, value (story progress is shared)
    SleepReady = 19,   // client -> host: ready to sleep or not (/sleep)
    SleepState = 20,   // host -> clients: who, ready, ready count, players, everyone slept (the clock skipped to 6:00)
    Identity = 21,     // client -> host after Welcome: player ID (random, kept in its config; bans follow it)
    Kicked = 22,       // host -> client: banned?, reason (the connection closes right after)
    TradeInvite = 23,  // player -> player: want to trade?
    TradeAnswer = 24,  // player -> player: yes/no, reason ("busy")
    TradeOffer = 25,   // player -> player: their whole offer (JSON, the game's SavedItem list)
    TradeLock = 26,    // player -> player: ready = the hash of the offer they accept ("" = not ready)
    TradeCancel = 27,  // player -> player: reason
    ChestOpen = 60,    // puppet -> owner, reliable: chest id (path hash). May I open it?
    ChestContents = 61,// owner -> requester: chest id, items (key, amount, values, slot). It's locked to you; these are the real contents
    ChestDenied = 62,  // owner -> requester: chest id, player id who has it open
    ChestClose = 63,   // puppet -> owner: chest id, final items. The owner takes them and unlocks
    ChestState = 64,   // owner -> puppets: chest id, items (after every close, so whoever runs the area next has them)
    ChestTake = 65,    // puppet -> area owner, reliable: chest id (string), count x (item key, amount) taken from a shared chest without opening it
    ChestShared = 66,  // area owner -> area / newcomer, reliable: chest id (string), items (a shared chest's contents)
    ChestShort = 67,   // area owner -> taker, reliable: item key, how many were already gone (they pay from their bag)
    GatherHit = 70,    // puppet -> owner, reliable: rock/tree id (path hash). One swing of our gather animation
    GatherDamaged = 71,// owner -> puppets, reliable: id, health before, gather type, who swung (replayed: shake, break, tree fall; XP only for the swinger)
    GatherState = 72,  // owner -> one arriving player: count, [id, health, gather type] of rocks/trees already hit here
    DoorChange = 80,   // whoever used it / the owner's events -> everyone in the area: door id, op (open/close/change/force), state, side it swings to
    SwitchChange = 81, // whoever used it / the owner's scripts -> everyone in the area: lever/switch id, state (On/Off/Disabled)
    DoorSnapshot = 82, // owner -> one arriving player: [door id, state]..., [switch id, state]... (applied without animation or events)
    ProjectileFx = 90, // shooter's copy (owner for creatures, the player for theirs) -> everyone in the area, reliable unordered: prefab name, position, direction, range (harmless copy)
    AttackFx = 91,     // attacker's copy (creature runner / player) -> area, reliable: from player?, id, kind (hit/buildup/cancel/release), ends buildup, aim, count x (prefab, x, y, angle, scale x, trigger, hold id) (harmless copies)
    PlayerHurt = 92,   // victim -> area, reliable unordered: health lost, result (numbers + flash on their stand-in)
    PlayerEffect = 93, // player -> area, reliable: on/off, effect name, raw effect name (burning, poisoned... under their name)
    TeamBuff = 94,     // caster -> area, reliable: attack name, skill level (its self-buffs, for teammates nearby)
    TeamSkill = 95,    // caster -> area, reliable: skill name, on/off, skill level, x, y (its stat modifiers, for teammates nearby)
    Ping = 96,         // player -> area, reliable unordered: x, y (middle-click marker)
    PathDraw = 97,     // player -> area, reliable: count x (x, y) (a line drawn on the ground)
    EggSpawn = 100,    // boss runner -> area, reliable: egg id (owner << 24 | n), x, y
    EggHit = 101,      // player -> egg owner, reliable: id, amount, hit type, knockback
    EggDamaged = 102,  // egg owner -> area, reliable: id, amount, hit type, knockback, health before, hitter
    EggDestroy = 103,  // egg owner -> area, reliable: id, index (0 = broken, >0 = hatch that many spiders)
    PlayerTrapped = 104, // trapped player -> area, reliable: on, x, y (the boss's runner reacts like for its own player)
    StationEnqueue = 110, // puppet -> area owner, reliable: station key, recipe id, crafting time (ingredients already paid)
    StationCollect = 111, // puppet -> area owner, reliable: station key, item index (-1 = all)
    StationGive = 112,    // area owner -> collector, reliable: station key, items (JSON, the game's SavedItem)
    StationState = 113,   // area owner -> area / newcomer, reliable: station key, SavedCraftingStation JSON
    StationXp = 114,      // area owner -> whoever queued it, reliable: recipe id, life skill, xp
    SoilState = 120,      // farmer -> area / owner -> newcomer, reliable: plot save id, SavedSoilState JSON
    ItemUnlocked = 123, // user -> their place, reliable: item lock (ItemRequester) id; it opens on every copy
    Repaired = 124,     // repairer -> their place, reliable: kind (building / portal), id
    InteractSnapshot = 125, // area owner -> arriving player: repaired buildings, portals, opened item locks
    LogBatch = 128,     // client -> host, reliable: dropped count, lines (kind, text) for the host's session log
    StateDigest = 129,  // player -> area owner, every 3 s: scene, area, (category, id, value) of what their game sees
    HookRope = 140,     // thrower -> their place: out?, then (unreliable ~20 Hz) the hook head's x, y; when it's gone (reliable) just false
    HookText = 142,     // thrower -> their place, reliable unordered: text, x, y, kind (the hook's pop-up words)
    HookPull = 143,     // thrower -> the player it caught (PvP), reliable: seconds (their copy drags them to the thrower)
    Summons = 144,      // summoner -> their place, ~10 Hz unreliable (empty list: reliable): count x (id, risen?, creature, x, y, size, health 0-255, attack count)
    ArenaOpen = 145,    // player -> everyone (ToAll), reliable: scene, boss rush? (they went into the boss arena / started a rush)
    ArenaTraps = 146,   // whoever switched them -> their place, reliable (again every 3 s): spikes on, arrows on (the boss arena's traps)
    NpcFiles = 148,     // host -> a player who joined (and everyone after a reload), reliable: Better NPCs and Quests' files (packed: count, path + bytes)
    NpcBook = 149,      // host -> everyone, reliable: the story (flags + quest progress, its saved text as UTF-8 bytes)
    NpcAsk = 150,       // guest -> host, reliable: an action ("start q", "set $x to 1"), "handed q step" (turned in from their own bag, for that step), "event kill Mob_Glowbat"
    NpcLook = 153,
    HookBlood = 154,
    WeatherStorm = 155, // whoever changed it -> everyone (host -> each player who joins), reliable: kind (0 clear, 1 rain, 2 snow: Better Weather), its own settings (amount, darkness, settle, lightning every, wind: NaN = the sliders')
    CompanionState = 160, // owner -> their place, ~10 Hz unreliable (gone: reliable): out?, then x, y, flip, health 0-255, downed, animators (Better Companions)
    CompanionLook = 161,  // owner -> their place, reliable, when it changes: LookSync bytes (name, parts, colours) of their companion
    CompanionRevive = 162, // reviver -> the companion's owner, reliable: (nothing) they held F at it for 2 s
    TpAsk = 158,        // player -> the player they want to go to, reliable: (nothing; they answer with SummonTo)
    SummonTo = 159,     // player -> the player they bring over, reliable: their name, Travel.Spot (scene, area, dungeon floor, x, y)
    TownPortal = 157,   // its owner -> everyone (host -> each player who joins), reliable: owner, name, scene, area, in a dungeon?, settings, seed, region, floor, x, y (their town portal pair)
    WeatherStrike = 156, // the game it struck on -> their place, reliable unordered: x, y (a lightning strike, to show if in view)    // thrower -> their place, reliable unordered: x, y, direction x, y, sorting layer id (blood where the hook bit)      // host -> everyone (and each player who joins), reliable: count x (npc id, LookSync look): how the host's NPCs and Rory look
    NpcWander = 151,    // host -> their place, reliable: npc id, x, y (where that NPC walks to next / stops)
    BuiltList = 152,    // host -> a player who joined, reliable: everything built anywhere (B), BuildList's saved text
    ChestLook = 147,    // editor -> everyone (ToAll) / area owner -> arriving player, reliable: chest id, name, colour hex, what it takes (ItemCategory bits) (Better Chests' V)
    HookThrow = 141,    // thrower -> their place, reliable unordered: shouts? (the hook's sound at their stand-in)
    BossLook = 131,     // boss spawner -> their place, reliable: creature id, name, base, tint rgb, scale, health x, damage x
    DungeonOpen = 130,  // player -> everyone (ToAll), reliable: scene, dungeon settings name, seed, region (they went into a generated dungeon)
    CutsceneSnapshot = 127, // area owner -> arriving player: cutscenes already played here (index, id), skipped to their end
    CutsceneStart = 126, // whoever set it off -> their place, reliable: entry index, cutscene id; everyone there plays it
    GroundPress = 122, // anyone -> their place, reliable: floor button id, down (repeated every 2 s while standing on it)
    PlaceChange = 121, // builder -> everyone (ToAll), reliable: op (place / remove object / remove tile), scene, then placeable id, x, y, direction, GUID, built anywhere? | id, x, y | layer, x, y, z
}
