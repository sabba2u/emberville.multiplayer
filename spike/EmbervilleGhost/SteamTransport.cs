using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LiteNetLib;
using LiteNetLib.Utils;
using Steamworks;

namespace EmbervilleGhost;

/// <summary>
/// Steam P2P over Valve's relay network (SteamNetworkingSockets / SDR): connect by Steam ID, no IP addresses,
/// no port forwarding, NAT and firewalls handled. Runs in the Steam session the mod starts itself (SteamBoot, as the
/// Emberville demo, app 5355880), whose callbacks SteamBoot pumps every frame. Reliability comes from Steam:
/// our reliable messages use k_nSteamNetworkingSend_Reliable, the rest unreliable.
/// </summary>
internal sealed class SteamTransport : ITransport
{
    sealed class SteamConn : Conn
    {
        public readonly HSteamNetConnection Handle;
        public readonly CSteamID Who;
        public SteamConn(HSteamNetConnection handle, CSteamID who) { Handle = handle; Who = who; }

        public override void Send(NetDataWriter data, DeliveryMethod method)
        {
            int flags = method == DeliveryMethod.Unreliable || method == DeliveryMethod.Sequenced
                ? Constants.k_nSteamNetworkingSend_UnreliableNoNagle
                : Constants.k_nSteamNetworkingSend_Reliable;
            var pin = GCHandle.Alloc(data.Data, GCHandleType.Pinned);
            try { SteamNetworkingSockets.SendMessageToConnection(Handle, pin.AddrOfPinnedObject(), (uint)data.Length, flags, out _); }
            finally { pin.Free(); }
        }

        public override int Ping
        {
            get
            {
                var status = default(SteamNetConnectionRealTimeStatus_t);
                var lanes = default(SteamNetConnectionRealTimeLaneStatus_t);
                return SteamNetworkingSockets.GetConnectionRealTimeStatus(Handle, ref status, 0, ref lanes) == EResult.k_EResultOK ? status.m_nPing : -1;
            }
        }

        public override string Label => SteamFriends.GetFriendPersonaName(Who);
        /// <summary>Their Steam ID: on Steam that's the address (host migration connects to it).</summary>
        public override string Address => Who.m_SteamID.ToString();
        public override void Disconnect() => SteamNetworkingSockets.CloseConnection(Handle, 0, "removed by the host", true);
    }

    readonly ITransportListener listener;
    readonly Dictionary<HSteamNetConnection, SteamConn> conns = new Dictionary<HSteamNetConnection, SteamConn>();
    readonly IntPtr[] inbox = new IntPtr[64];
    Callback<SteamNetConnectionStatusChangedCallback_t> statusCallback;
    HSteamListenSocket listenSocket = HSteamListenSocket.Invalid;
    bool running;

    public SteamTransport(ITransportListener listener) { this.listener = listener; }

    public string Name => "Steam";
    public bool IsRunning => running;

    public static bool SteamReady => SteamBoot.Ready;

    void Begin()
    {
        SteamNetworkingUtils.InitRelayNetworkAccess();   // warm up the relay network early
        statusCallback ??= Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatusChanged);
        running = true;
    }

    /// <summary>Host: accept P2P connections from anyone who knows our Steam ID (our lobby members).</summary>
    public void StartHost()
    {
        Begin();
        listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, null);
    }

    /// <summary>Client: connect to the host's Steam ID (read from the lobby).</summary>
    public void StartClient(CSteamID host)
    {
        Begin();
        var identity = new SteamNetworkingIdentity();
        identity.SetSteamID(host);
        var handle = SteamNetworkingSockets.ConnectP2P(ref identity, 0, 0, null);
        conns[handle] = new SteamConn(handle, host);
    }

    void OnStatusChanged(SteamNetConnectionStatusChangedCallback_t e)
    {
        if (!running) return;
        var state = e.m_info.m_eState;
        var who = e.m_info.m_identityRemote.GetSteamID();

        switch (state)
        {
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                // Incoming (host side): it arrives through our listen socket.
                if (e.m_info.m_hListenSocket != HSteamListenSocket.Invalid)
                {
                    if (!listener.AcceptConnection()) { SteamNetworkingSockets.CloseConnection(e.m_hConn, 0, "full", false); return; }
                    conns[e.m_hConn] = new SteamConn(e.m_hConn, who);
                    SteamNetworkingSockets.AcceptConnection(e.m_hConn);
                }
                break;

            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                if (!conns.TryGetValue(e.m_hConn, out var c)) conns[e.m_hConn] = c = new SteamConn(e.m_hConn, who);
                listener.OnConnected(c);
                break;

            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                if (conns.TryGetValue(e.m_hConn, out var gone))
                {
                    conns.Remove(e.m_hConn);
                    bool wasConnected = e.m_eOldState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected;
                    // Same words as LiteNetLib's DisconnectReason: the host closing on purpose is "RemoteConnectionClose"
                    // (others take over), anything else is a dropped connection.
                    string reason = !wasConnected ? $"couldn't connect ({e.m_info.m_szEndDebug})"
                        : state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer ? "RemoteConnectionClose"
                        : $"Timeout ({e.m_info.m_szEndDebug})";
                    listener.OnDisconnected(gone, reason);
                }
                SteamNetworkingSockets.CloseConnection(e.m_hConn, 0, null, false);
                break;
        }
    }

    public void Poll()
    {
        if (!running) return;
        foreach (var conn in new List<SteamConn>(conns.Values))
        {
            int n;
            while ((n = SteamNetworkingSockets.ReceiveMessagesOnConnection(conn.Handle, inbox, inbox.Length)) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    var msg = SteamNetworkingMessage_t.FromIntPtr(inbox[i]);
                    var bytes = new byte[msg.m_cbSize];
                    Marshal.Copy(msg.m_pData, bytes, 0, msg.m_cbSize);
                    bool reliable = (msg.m_nFlags & Constants.k_nSteamNetworkingSend_Reliable) != 0;
                    SteamNetworkingMessage_t.Release(inbox[i]);
                    try { listener.OnReceive(conn, new NetDataReader(bytes), reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable); }
                    catch (Exception ex) { NetLog.Error($"Steam message: {ex.Message}"); }
                }
                if (!conns.ContainsKey(conn.Handle)) break;   // disconnected while handling
            }
        }
    }

    public void Stop()
    {
        if (!running) return;
        running = false;
        foreach (var conn in conns.Values) SteamNetworkingSockets.CloseConnection(conn.Handle, 0, "bye", true);
        conns.Clear();
        if (listenSocket != HSteamListenSocket.Invalid) SteamNetworkingSockets.CloseListenSocket(listenSocket);
        listenSocket = HSteamListenSocket.Invalid;
    }
}
