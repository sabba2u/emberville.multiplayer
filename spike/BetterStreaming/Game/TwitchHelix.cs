using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace BetterStreaming;

/// <summary>The Twitch API calls we make (off the main thread): EventSub subscriptions, and a line in chat.</summary>
internal static class TwitchHelix
{
    static readonly HttpClient http = new HttpClient { Timeout = System.TimeSpan.FromSeconds(15) };

    static async Task<(int status, string body)> Post(string url, object json)
    {
        string token = await TwitchAuth.Token();
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(JsonConvert.SerializeObject(json), Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        req.Headers.TryAddWithoutValidation("Client-Id", TwitchConfig.ClientId.Value);
        using (var res = await http.SendAsync(req))
            return ((int)res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    /// <summary>Subscribe this socket session to an event type. False (logged) if Twitch said no.</summary>
    public static async Task<bool> Subscribe(string type, string version, object condition, string sessionId)
    {
        var (status, body) = await Post(TwitchConfig.SubscriptionsUrl.Value, new
        {
            type,
            version,
            condition,
            transport = new { method = "websocket", session_id = sessionId },
        });
        if (status >= 200 && status < 300) { Log.Info($"Twitch: listening for {type}"); return true; }
        Log.Warn($"Twitch: couldn't listen for {type} ({status}): {Short(body)}");
        return false;
    }

    /// <summary>A line in the streamer's chat, as them.</summary>
    public static async Task Chat(string text)
    {
        if (string.IsNullOrEmpty(TwitchAuth.UserId) || TwitchConfig.Mock) { Log.Info($"Twitch chat (not sent): {text}"); return; }
        var (status, body) = await Post(TwitchConfig.HelixUrl.Value.TrimEnd('/') + "/chat/messages",
            new { broadcaster_id = TwitchAuth.UserId, sender_id = TwitchAuth.UserId, message = text.Length > 480 ? text.Substring(0, 480) : text });
        if (status < 200 || status >= 300) Log.Warn($"Twitch: couldn't send to chat ({status}): {Short(body)}");
    }

    static string Short(string s) => s == null ? "" : s.Length > 200 ? s.Substring(0, 200) : s;
}
