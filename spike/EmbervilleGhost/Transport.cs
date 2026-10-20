using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace EmbervilleGhost;

/// <summary>
/// One connection, whatever carries it (LiteNetLib UDP or Steam relay). All game sync code only sees this,
/// NetDataWriter / NetDataReader for the bytes, and LiteNetLib's DeliveryMethod as "how reliable".
/// </summary>
internal abstract class Conn
{
    public abstract void Send(NetDataWriter data, DeliveryMethod method);
    public abstract int Ping { get; }
    public abstract string Label { get; }
    /// <summary>The other side's address as we see it (IP), for "connect to them instead" (host migration).</summary>
    public abstract string Address { get; }
    /// <summary>Close this connection (host: kick).</summary>
    public abstract void Disconnect();
    public override string ToString() => Label;
}

/// <summary>What a transport tells the game (implemented by Plugin).</summary>
internal interface ITransportListener
{
    /// <summary>Host: someone wants in. False = reject (full).</summary>
    bool AcceptConnection();
    void OnConnected(Conn conn);
    void OnDisconnected(Conn conn, string reason);
    /// <summary>A message arrived. The reader is only valid during this call.</summary>
    void OnReceive(Conn conn, NetDataReader reader, DeliveryMethod method);
}

/// <summary>
/// Swappable transport (roadmap 13d): the same messages run over LiteNetLib (LAN / IP, side-by-side testing)
/// or Steam (lobbies, invites, relay - no IPs or port forwarding). Core Keeper does the same trick.
/// </summary>
internal interface ITransport
{
    string Name { get; }
    bool IsRunning { get; }
    void Poll();
    void Stop();
}

/// <summary>LiteNetLib over UDP: host on a port, join by IP.</summary>
internal sealed class LiteNetTransport : ITransport, INetEventListener
{
    sealed class LiteConn : Conn
    {
        public readonly NetPeer Peer;
        public LiteConn(NetPeer peer) { Peer = peer; }
        public override void Send(NetDataWriter data, DeliveryMethod method) => Peer.Send(data, method);
        public override int Ping => Peer.Ping;
        public override string Label => Peer.ToString();
        public override string Address => Peer.Address.ToString();
        public override void Disconnect() => Peer.Disconnect();
    }

    const string ConnectionKey = "emberville-ghost-spike";

    readonly ITransportListener listener;
    readonly NetManager net;
    readonly Dictionary<NetPeer, LiteConn> conns = new Dictionary<NetPeer, LiteConn>();

    public LiteNetTransport(ITransportListener listener)
    {
        this.listener = listener;
        net = new NetManager(this) { DisconnectTimeout = 10000 };
    }

    public string Name => "LAN";
    public bool IsRunning => net.IsRunning;

    public bool StartHost(int port) => net.Start(port);

    public void StartClient(string address, int port)
    {
        net.Start();
        net.Connect(address, port, ConnectionKey);
    }

    public void Poll() => net.PollEvents();

    public void Stop()
    {
        if (net.IsRunning) net.Stop();
        conns.Clear();
    }

    LiteConn Wrap(NetPeer peer)
    {
        if (!conns.TryGetValue(peer, out var c)) conns[peer] = c = new LiteConn(peer);
        return c;
    }

    public void OnConnectionRequest(ConnectionRequest request)
    {
        if (!listener.AcceptConnection()) { request.Reject(); return; }
        if (request.AcceptIfKey(ConnectionKey) == null)
            NetLog.Warn($"Rejected {request.RemoteEndPoint}: wrong connection key");
    }

    public void OnPeerConnected(NetPeer peer) => listener.OnConnected(Wrap(peer));

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        listener.OnDisconnected(Wrap(peer), info.Reason.ToString());
        conns.Remove(peer);
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        try { listener.OnReceive(Wrap(peer), reader, method); }
        finally { reader.Recycle(); }
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) =>
        NetLog.Error($"Network error {socketError} from {endPoint}");

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
}
