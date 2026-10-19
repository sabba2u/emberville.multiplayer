// SPIKE: a second player in Emberville. Patterns carried over from DuckovGhost.
//
// F8  = host on LAN (listen on Port)
// F9  = join (connect to Address:Port)
// F10 = disconnect
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
public class Plugin : BaseUnityPlugin, ITransportListener
{
    internal const string Version = "0.1.2";
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
    internal string Status { get; private set; } = "idle (F8 host, F9 join)";

    void Awake()
    {
        Log = Logger;
        AddressSetting = Config.Bind("Network", "Address", "127.0.0.1", "Host address to join with F9");
        PortSetting = Config.Bind("Network", "Port", 9060, "UDP port to host on / join");
        MaxPlayers = Config.Bind("Network", "MaxPlayers", 4, "Players including the host");
        FreeMouse = Config.Bind("Window", "FreeMouse", true, "Don't let the game lock/confine the mouse to its window (easier with two copies)");
        WindowFix.Bind(Config);
        var harmony = new Harmony("spike.embervilleghost");
        SaveIsolation.Init(Config);
        harmony.PatchAll();
        lan = new LiteNetTransport(this);
        // Keep running while the other copy has focus.
        Application.runInBackground = true;
        Hud.Plugin = this;
        EnemySync.Plugin = this;
        AttackFx.Plugin = this;
        BossSync.Plugin = this;
        DropSync.Plugin = this;
        PropSync.Plugin = this;
        ChestSync.Plugin = this;
        Chat.Plugin = this;
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
        ImGuiUnity.OnRender += Hud.Draw;
        try { ImGuiUnity.Init(Config, System.IO.Path.GetDirectoryName(Info.Location)); }
        catch (Exception e) { Log.LogError($"ImGui init failed, no menu/HUD: {e}"); }
        NetLog.Info($"{Title} loaded. F8 host, F9 join, F10 disconnect, Insert = menu.", toast: true);
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
            if (kb.f8Key.wasPressedThisFrame) HostWindow.Show();   // pick the session rules, then HOST NOW
            if (kb.f9Key.wasPressedThisFrame) Join();
            if (kb.f10Key.wasPressedThisFrame) Stop();
        }

        CoopDeath.Tick();   // before the game's frame logic: last frame's death reset (if any) is over
        if (transport != null && transport.IsRunning) NetTick();
        Chat.Tick();

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
        if (LocalPlayer.Scene != lastScene) { lastScene = LocalPlayer.Scene; SceneIds.Clear(); EnemySync.SceneChanged(); FxSync.Clear(); AttackFx.Clear(); BossSync.Clear(); }   // ids belong to a scene

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
            DropSync.SendSnapshot(p.Id);
            PropSync.SendSnapshot(p.Id);
            ChestSync.SendSnapshot(p.Id);
            GatherSync.SendSnapshot(p.Id);
            DoorSync.SendSnapshot(p.Id);
        }
    }

    void OnDestroy() => Stop();

    internal void Host()
    {
        Stop();
        transport = lan;
        if (lan.StartHost(PortSetting.Value))
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
        lan.StartClient(AddressSetting.Value, PortSetting.Value);
        Status = $"connecting to {AddressSetting.Value}:{PortSetting.Value}...";
        JoinWindow.Show();
        NetLog.Info($"Connecting to {AddressSetting.Value}:{PortSetting.Value}...", toast: true);
    }

    internal void Stop()
    {
        if (transport != null && transport.IsRunning)
        {
            transport.Stop();
            NetLog.Info($"Network stopped ({transport.Name})");
        }
        transport = null;
        ForgetEveryone();
        IsHost = false;
        MyId = -1;
        Status = "idle (F8 host, F9 join)";
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

        int id = nextPlayerId++;
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
        NetLog.Success($"You are P{MyId + 1}");
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
                return;
            }
            ForgetEveryone();
            MyId = -1;
            Status = $"disconnected ({reason})";
            NetLog.Warn($"Host disconnected: {reason}");
            return;
        }

        if (!peerIds.TryGetValue(peer, out int id)) return;
        peerIds.Remove(peer);
        idPeers.Remove(id);
        RemovePlayer(id);
        ChestSync.Unlock(id);
        Status = peerIds.Count > 0 ? $"hosting, {PlayerCount} players" : $"hosting on {PortSetting.Value}, waiting...";
        NetLog.Warn($"P{id + 1} left ({reason})");

        var w = new NetDataWriter();
        w.Put((byte)MsgType.PlayerLeft);
        w.Put(id);
        SendToAllClients(w, DeliveryMethod.ReliableOrdered);
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
                case MsgType.PlayerLeft: if (!IsHost) { int id = reader.GetInt(); RemovePlayer(id); NetLog.Warn($"P{id + 1} left"); } break;
                case MsgType.AreaOwners: if (!IsHost) OnAreaOwners(reader); break;
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
            case MsgType.ChestState: if (!owner) ChestSync.OnState(reader); break;
            case MsgType.GatherDamaged: if (!owner) GatherSync.OnDamaged(reader); break;
            case MsgType.GatherState: if (!owner) GatherSync.OnState(reader); break;
            // from whoever used it (doors / levers: the user's copy runs it, everyone in the area mirrors it)
            case MsgType.DoorChange: DoorSync.OnDoor(reader); break;
            case MsgType.SwitchChange: DoorSync.OnSwitch(reader); break;
            case MsgType.DoorSnapshot: if (!owner) DoorSync.OnSnapshot(reader); break;
            case MsgType.ProjectileFx: FxSync.OnProjectileFx(reader); break;
            case MsgType.AttackFx: AttackFx.OnAttackFx(reader); break;
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
        var status = (PlayerStatus)reader.GetByte();
        var anim = AnimSync.Read(reader);
        if (id == MyId || id < 0) return;

        var p = GetPlayer(id);
        p.Status = status;
        p.Flip = flip;
        p.Health = health;
        p.MaxHealth = maxHealth;
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
    ChestOpen = 60,    // puppet -> owner, reliable: chest id (path hash). May I open it?
    ChestContents = 61,// owner -> requester: chest id, items (key, amount, values, slot). It's locked to you; these are the real contents
    ChestDenied = 62,  // owner -> requester: chest id, player id who has it open
    ChestClose = 63,   // puppet -> owner: chest id, final items. The owner takes them and unlocks
    ChestState = 64,   // owner -> puppets: chest id, items (after every close, so whoever runs the area next has them)
    GatherHit = 70,    // puppet -> owner, reliable: rock/tree id (path hash). One swing of our gather animation
    GatherDamaged = 71,// owner -> puppets, reliable: id, health before, gather type, who swung (replayed: shake, break, tree fall; XP only for the swinger)
    GatherState = 72,  // owner -> one arriving player: count, [id, health, gather type] of rocks/trees already hit here
    DoorChange = 80,   // whoever used it / the owner's events -> everyone in the area: door id, op (open/close/change/force), state, side it swings to
    SwitchChange = 81, // whoever used it / the owner's scripts -> everyone in the area: lever/switch id, state (On/Off/Disabled)
    DoorSnapshot = 82, // owner -> one arriving player: [door id, state]..., [switch id, state]... (applied without animation or events)
    ProjectileFx = 90, // shooter's copy (owner for creatures, the player for theirs) -> everyone in the area, reliable unordered: prefab name, position, direction, range (harmless copy)
    AttackFx = 91,     // creature's runner -> area, reliable unordered: id, kind (hit/buildup/cancel), ends buildup, count x (prefab, x, y, angle, scale x, trigger) (harmless copies)
    EggSpawn = 100,    // boss runner -> area, reliable: egg id (owner << 24 | n), x, y
    EggHit = 101,      // player -> egg owner, reliable: id, amount, hit type, knockback
    EggDamaged = 102,  // egg owner -> area, reliable: id, amount, hit type, knockback, health before, hitter
    EggDestroy = 103,  // egg owner -> area, reliable: id, index (0 = broken, >0 = hatch that many spiders)
    PlayerTrapped = 104, // trapped player -> area, reliable: on, x, y (the boss's runner reacts like for its own player)
}
