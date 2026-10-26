using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BetterStreaming.Core;

namespace BetterStreaming;

/// <summary>
/// Twitch's EventSub over a WebSocket, on a background task: connect, and on the welcome subscribe this session to
/// cheers, channel-point redemptions and chat. Notifications go into Events for the main thread (Streamer drains them);
/// a message id seen before is dropped (Twitch may resend). No message for keepalive + 5 s: the connection is dead,
/// start again. session_reconnect: go to the URL Twitch gives (the subscriptions come along). Any other drop: start
/// again after 2, 4, 8 ... 60 s, with fresh subscriptions.
/// </summary>
internal static class TwitchSocket
{
    public static readonly ConcurrentQueue<TwitchEvent> Events = new ConcurrentQueue<TwitchEvent>();
    public static volatile string Status = "Not connected";
    public static volatile bool Connected;

    static CancellationTokenSource running;
    static readonly Queue<string> seenOrder = new Queue<string>();
    static readonly HashSet<string> seen = new HashSet<string>();

    public static void Start()
    {
        if (running != null && !running.IsCancellationRequested) return;
        running = new CancellationTokenSource();
        var stop = running.Token;
        Task.Run(() => Loop(stop));
    }

    public static void Stop()
    {
        running?.Cancel();
        running = null;
        Connected = false;
        Status = "Not connected";
    }

    static async Task Loop(CancellationToken stop)
    {
        string url = TwitchConfig.EventSubUrl.Value;
        ClientWebSocket handed = null;   // (a reconnect: the new socket, already welcomed; the subscriptions came with it)
        string handedWelcome = null;
        int backoff = 2;
        while (!stop.IsCancellationRequested)
        {
            ClientWebSocket ws = handed;
            string first = handedWelcome;
            handed = null; handedWelcome = null;
            try
            {
                if (ws == null)
                {
                    ws = new ClientWebSocket();
                    Status = "Connecting to Twitch...";
                    await ws.ConnectAsync(new Uri(url), stop);
                }
                (handed, handedWelcome) = await Session(ws, first, carried: first != null, stop, () => backoff = 2);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e) { Log.Warn($"Twitch: connection lost ({e.GetType().Name}: {e.Message})"); }
            finally { try { ws?.Dispose(); } catch { } }
            Connected = handed != null;
            if (stop.IsCancellationRequested) break;
            if (handed != null) continue;
            url = TwitchConfig.EventSubUrl.Value;
            Status = $"Reconnecting in {backoff} s";
            try { await Task.Delay(backoff * 1000, stop); } catch (OperationCanceledException) { break; }
            backoff = Math.Min(60, backoff * 2);
        }
        try { handed?.Dispose(); } catch { }
        Connected = false;
    }

    /// <summary>
    /// One connection, until it ends (null, null). On session_reconnect: the new socket is opened while this one keeps
    /// delivering, and handed back with its welcome once it comes (Twitch's way: nothing is missed in between; a
    /// message both send is dropped by its id).
    /// </summary>
    static async Task<(ClientWebSocket, string)> Session(ClientWebSocket ws, string first, bool carried, CancellationToken stop, Action welcomed)
    {
        int keepalive = 10;
        var buffer = new byte[16 * 1024];
        while (ws.State == WebSocketState.Open && !stop.IsCancellationRequested)
        {
            string text = first;
            first = null;
            if (text == null)
            {
                using (var watchdog = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    watchdog.CancelAfter(TimeSpan.FromSeconds(keepalive + 5));
                    try { text = await Receive(ws, buffer, watchdog.Token); }
                    catch (OperationCanceledException) when (!stop.IsCancellationRequested) { Log.Warn("Twitch: no word from Twitch in a while; reconnecting"); return (null, null); }
                }
            }
            if (text == null) return (null, null);   // closed
            var e = EventSub.Parse(text);
            switch (e.Kind)
            {
                case TwitchKind.Welcome:
                    keepalive = e.KeepaliveSeconds > 0 ? e.KeepaliveSeconds : 10;
                    welcomed();
                    if (!carried) await SubscribeAll(e.SessionId);
                    Connected = true;
                    Status = "Connected: listening for cheers";
                    break;
                case TwitchKind.Reconnect:
                    if (string.IsNullOrEmpty(e.ReconnectUrl)) return (null, null);
                    return await Overlap(ws, buffer, e.ReconnectUrl, stop);
                default:
                    Deliver(e);
                    break;
            }
        }
        return (null, null);
    }

    /// <summary>The new socket's welcome awaited while the old one goes on delivering.</summary>
    static async Task<(ClientWebSocket, string)> Overlap(ClientWebSocket old, byte[] buffer, string url, CancellationToken stop)
    {
        Status = "Reconnecting...";
        var next = new ClientWebSocket();
        try
        {
            await next.ConnectAsync(new Uri(url), stop);
            var welcome = Receive(next, new byte[16 * 1024], stop);
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                limit.CancelAfter(TimeSpan.FromSeconds(30));
                Task<string> fromOld = old.State == WebSocketState.Open ? Receive(old, buffer, limit.Token) : null;
                while (true)
                {
                    var done = fromOld == null ? await Task.WhenAny(welcome) : await Task.WhenAny(welcome, fromOld);
                    if (done == welcome) return (next, await welcome);
                    string text = null;
                    try { text = await fromOld; } catch (Exception) { }
                    if (text != null) Deliver(EventSub.Parse(text));
                    fromOld = text != null && old.State == WebSocketState.Open ? Receive(old, buffer, limit.Token) : null;
                }
            }
        }
        catch (Exception)
        {
            try { next.Dispose(); } catch { }
            throw;
        }
    }

    static void Deliver(TwitchEvent e)
    {
        switch (e.Kind)
        {
            case TwitchKind.Revocation:
                Log.Warn("Twitch: a subscription was revoked (signed out on twitch.tv, or a permission removed)");
                Status = "Twitch stopped a subscription: sign out and connect again";
                break;
            case TwitchKind.Cheer:
            case TwitchKind.Redemption:
            case TwitchKind.Chat:
            case TwitchKind.PollEnd:
                if (Fresh(e.MessageId)) Events.Enqueue(e);
                break;
        }
    }

    static async Task SubscribeAll(string session)
    {
        string me = TwitchAuth.UserId;
        bool cheers = await TwitchHelix.Subscribe("channel.cheer", "1", new { broadcaster_user_id = me }, session);
        await TwitchHelix.Subscribe("channel.channel_points_custom_reward_redemption.add", "1", new { broadcaster_user_id = me }, session);
        await TwitchHelix.Subscribe("channel.chat.message", "1", new { broadcaster_user_id = me, user_id = me }, session);
        if (!cheers) Log.Info("Twitch: Bits need an Affiliate or Partner channel; chat commands work for anyone", toast: true);
    }

    static async Task<string> Receive(ClientWebSocket ws, byte[] buffer, CancellationToken token)
    {
        using (var ms = new MemoryStream())
        {
            while (true)
            {
                var r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buffer, 0, r.Count);
                if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }

    /// <summary>Not seen before (the last 200 message ids are kept).</summary>
    static bool Fresh(string id)
    {
        if (string.IsNullOrEmpty(id)) return true;
        lock (seen)
        {
            if (!seen.Add(id)) return false;
            seenOrder.Enqueue(id);
            if (seenOrder.Count > 200) seen.Remove(seenOrder.Dequeue());
            return true;
        }
    }
}
