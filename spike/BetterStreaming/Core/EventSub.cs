using System.Linq;
using Newtonsoft.Json.Linq;

namespace BetterStreaming.Core;

public enum TwitchKind { Welcome, Keepalive, Reconnect, Revocation, Cheer, Redemption, Chat, PollEnd, Unknown }

/// <summary>One message from Twitch's EventSub socket, with what we use of it (empty strings, not nulls).</summary>
public sealed class TwitchEvent
{
    public TwitchKind Kind = TwitchKind.Unknown;
    public string MessageId = "";
    public string SessionId = "";
    public int KeepaliveSeconds;
    public string ReconnectUrl = "";
    public string User = "";
    public string UserId = "";
    public bool Anonymous;
    public int Bits;
    public string Message = "";
    public string RewardId = "";
    public string RewardTitle = "";
    public string RedemptionId = "";
    public string PollWinner = "";
}

/// <summary>
/// Reading EventSub WebSocket messages (session_welcome, session_keepalive, session_reconnect, revocation, and the
/// notifications we subscribe to: channel.cheer, channel points redemptions, channel.chat.message, channel.poll.end).
/// Never throws: anything it can't read is Unknown.
/// </summary>
public static class EventSub
{
    public static TwitchEvent Parse(string json)
    {
        var e = new TwitchEvent();
        try
        {
            if (string.IsNullOrEmpty(json)) return e;
            var root = JObject.Parse(json);
            var meta = root["metadata"] as JObject;
            var payload = root["payload"] as JObject;
            e.MessageId = S(meta?["message_id"]);
            switch (S(meta?["message_type"]))
            {
                case "session_welcome":
                    e.Kind = TwitchKind.Welcome;
                    e.SessionId = S(payload?["session"]?["id"]);
                    e.KeepaliveSeconds = I(payload?["session"]?["keepalive_timeout_seconds"]);
                    break;
                case "session_keepalive": e.Kind = TwitchKind.Keepalive; break;
                case "session_reconnect":
                    e.Kind = TwitchKind.Reconnect;
                    e.SessionId = S(payload?["session"]?["id"]);
                    e.ReconnectUrl = S(payload?["session"]?["reconnect_url"]);
                    break;
                case "revocation": e.Kind = TwitchKind.Revocation; break;
                case "notification": Notification(e, S(meta?["subscription_type"]), payload?["event"] as JObject); break;
            }
        }
        catch (Newtonsoft.Json.JsonException) { e.Kind = TwitchKind.Unknown; }
        catch (System.InvalidCastException) { e.Kind = TwitchKind.Unknown; }
        return e;
    }

    static void Notification(TwitchEvent e, string type, JObject ev)
    {
        if (ev == null) return;
        switch (type)
        {
            case "channel.cheer":
                e.Kind = TwitchKind.Cheer;
                e.Anonymous = ev["is_anonymous"]?.Type == JTokenType.Boolean && (bool)ev["is_anonymous"];
                e.User = S(ev["user_name"]);
                e.UserId = S(ev["user_id"]);
                e.Bits = I(ev["bits"]);
                e.Message = S(ev["message"]);
                break;
            case "channel.channel_points_custom_reward_redemption.add":
                e.Kind = TwitchKind.Redemption;
                e.RedemptionId = S(ev["id"]);
                e.User = S(ev["user_name"]);
                e.UserId = S(ev["user_id"]);
                e.Message = S(ev["user_input"]);
                e.RewardId = S(ev["reward"]?["id"]);
                e.RewardTitle = S(ev["reward"]?["title"]);
                break;
            case "channel.chat.message":
                e.Kind = TwitchKind.Chat;
                e.User = S(ev["chatter_user_name"]);
                e.UserId = S(ev["chatter_user_id"]);
                e.Message = S(ev["message"]?["text"]);
                break;
            case "channel.poll.end":
                e.Kind = TwitchKind.PollEnd;
                var choices = ev["choices"] as JArray;
                var best = choices?.OfType<JObject>().OrderByDescending(c => I(c["votes"])).FirstOrDefault();
                e.PollWinner = S(best?["title"]);
                break;
        }
    }

    static string S(JToken t) => t == null || t.Type == JTokenType.Null ? "" : (string)t;
    static int I(JToken t) => t == null || t.Type == JTokenType.Null ? 0 : t.Type == JTokenType.Integer ? (int)t : int.TryParse((string)t, out int v) ? v : 0;
}
