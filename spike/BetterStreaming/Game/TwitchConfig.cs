using BepInEx.Configuration;

namespace BetterStreaming;

/// <summary>
/// The Twitch settings. The client ID is the mod's own registered app ("Better Streaming (Emberville Streaming
/// Integration Mod)", a public client: no secret). The URLs are there for testing against the Twitch CLI's mock
/// servers (`twitch event websocket start-server`: ws://127.0.0.1:8080/ws and
/// http://127.0.0.1:8080/eventsub/subscriptions); a local EventSub URL also lets Connect work without signing in.
/// </summary>
internal static class TwitchConfig
{
    public const string Scopes = "bits:read channel:read:redemptions channel:manage:redemptions user:read:chat user:write:chat channel:manage:polls channel:manage:predictions";
    public static ConfigEntry<string> ClientId, EventSubUrl, SubscriptionsUrl, HelixUrl, MockUserId;

    public static void Bind(ConfigFile config)
    {
        ClientId = config.Bind("Twitch", "ClientId", "06ikd61tw9ozf5uoblowxdgc3dak1h", "The Twitch app's client ID (Better Streaming's own; leave it)");
        EventSubUrl = config.Bind("Twitch", "EventSubUrl", "wss://eventsub.wss.twitch.tv/ws", "Testing: the EventSub WebSocket (the Twitch CLI's mock: ws://127.0.0.1:8080/ws)");
        SubscriptionsUrl = config.Bind("Twitch", "SubscriptionsUrl", "https://api.twitch.tv/helix/eventsub/subscriptions", "Testing: where subscriptions are made (the Twitch CLI's mock: http://127.0.0.1:8080/eventsub/subscriptions)");
        HelixUrl = config.Bind("Twitch", "HelixUrl", "https://api.twitch.tv/helix", "Testing: the Twitch API (chat messages)");
        MockUserId = config.Bind("Twitch", "MockUserId", "12345", "Testing: the channel's user ID when using a local mock without signing in");
    }

    /// <summary>Pointed at a local mock server: no real sign-in needed.</summary>
    public static bool Mock => EventSubUrl.Value.StartsWith("ws://127.") || EventSubUrl.Value.StartsWith("ws://localhost");
}
