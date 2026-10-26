using BetterStreaming.Core;
using Xunit;

public class EventSubTests
{
    const string Welcome = @"{""metadata"":{""message_id"":""96a3f3b5-5dec-4eed-908e-e11ee657416c"",""message_type"":""session_welcome"",""message_timestamp"":""2023-07-19T14:56:51.634234626Z""},""payload"":{""session"":{""id"":""AQoQILE98gtqShGmLD7AM6yJThAB"",""status"":""connected"",""connected_at"":""2023-07-19T14:56:51.616329898Z"",""keepalive_timeout_seconds"":10,""reconnect_url"":null}}}";
    const string Keepalive = @"{""metadata"":{""message_id"":""84c1e79a-2a4b-4c13-ba0b-4312293e9308"",""message_type"":""session_keepalive"",""message_timestamp"":""2023-07-19T10:11:12.634234626Z""},""payload"":{}}";
    const string Reconnect = @"{""metadata"":{""message_id"":""84c1e79a-2a4b-4c13-ba0b-4312293e9309"",""message_type"":""session_reconnect"",""message_timestamp"":""2022-11-18T09:10:11.634234626Z""},""payload"":{""session"":{""id"":""AQoQexAWVYKSTIu4ec_2VAxyuhAB"",""status"":""reconnecting"",""keepalive_timeout_seconds"":null,""reconnect_url"":""wss://eventsub.wss.twitch.tv?..."",""connected_at"":""2022-11-16T10:11:12.634234626Z""}}}";
    const string Revocation = @"{""metadata"":{""message_id"":""84c1e79a-2a4b-4c13-ba0b-4312293e9310"",""message_type"":""revocation"",""message_timestamp"":""2022-11-16T10:11:12.464757833Z"",""subscription_type"":""channel.cheer"",""subscription_version"":""1""},""payload"":{""subscription"":{""id"":""f1c2a387-161a-49f9-a165-0f21d7a4e1c4"",""status"":""authorization_revoked"",""type"":""channel.cheer"",""version"":""1"",""cost"":1,""condition"":{""broadcaster_user_id"":""12826""},""transport"":{""method"":""websocket"",""session_id"":""AQoQexAWVYKSTIu4ec_2VAxyuhAB""},""created_at"":""2022-11-16T10:11:12.464757833Z""}}}";
    const string Cheer = @"{""metadata"":{""message_id"":""befa7b53-d79d-478f-86b9-120f112b044e"",""message_type"":""notification"",""message_timestamp"":""2022-11-16T10:11:12.464757833Z"",""subscription_type"":""channel.cheer"",""subscription_version"":""1""},""payload"":{""subscription"":{""id"":""f1c2a387-161a-49f9-a165-0f21d7a4e1c4"",""type"":""channel.cheer"",""version"":""1"",""status"":""enabled"",""cost"":0,""condition"":{""broadcaster_user_id"":""1337""},""transport"":{""method"":""websocket"",""session_id"":""AQoQexAWVYKSTIu4ec_2VAxyuhAB""},""created_at"":""2019-11-16T10:11:12.634234626Z""},""event"":{""is_anonymous"":false,""user_id"":""1234"",""user_login"":""cool_user"",""user_name"":""Cool_User"",""broadcaster_user_id"":""1337"",""broadcaster_user_login"":""cooler_user"",""broadcaster_user_name"":""Cooler_User"",""message"":""Cheer100 raid pogchamp"",""bits"":100}}}";
    const string AnonCheer = @"{""metadata"":{""message_id"":""a1"",""message_type"":""notification"",""subscription_type"":""channel.cheer""},""payload"":{""event"":{""is_anonymous"":true,""user_id"":null,""user_login"":null,""user_name"":null,""message"":""hi"",""bits"":500}}}";
    const string Redemption = @"{""metadata"":{""message_id"":""a2"",""message_type"":""notification"",""subscription_type"":""channel.channel_points_custom_reward_redemption.add""},""payload"":{""event"":{""id"":""17fa2df1-ad76-4804-bfa5-a40ef63efe63"",""broadcaster_user_id"":""1337"",""user_id"":""9001"",""user_login"":""cooler_user"",""user_name"":""Cooler_User"",""user_input"":""pogchamp"",""status"":""unfulfilled"",""reward"":{""id"":""92af127c-7326-4483-a52b-b0da0be61c01"",""title"":""Raid"",""cost"":100,""prompt"":""reward prompt""},""redeemed_at"":""2020-07-15T17:16:03.17106713Z""}}}";
    const string Chat = @"{""metadata"":{""message_id"":""a3"",""message_type"":""notification"",""subscription_type"":""channel.chat.message""},""payload"":{""event"":{""broadcaster_user_id"":""1971641"",""chatter_user_id"":""4145994"",""chatter_user_login"":""viptest"",""chatter_user_name"":""viptest"",""message_id"":""cc106a89-1814-919d-454c-f4f2f970aae7"",""message"":{""text"":""!effects"",""fragments"":[{""type"":""text"",""text"":""!effects""}]},""color"":""#00FF7F"",""message_type"":""text""}}}";
    const string PollEnd = @"{""metadata"":{""message_id"":""a4"",""message_type"":""notification"",""subscription_type"":""channel.poll.end""},""payload"":{""event"":{""id"":""1243456"",""title"":""Which colour?"",""choices"":[{""id"":""123"",""title"":""Blue"",""votes"":120},{""id"":""124"",""title"":""Yellow"",""votes"":140},{""id"":""125"",""title"":""Green"",""votes"":80}],""status"":""completed""}}}";

    [Fact]
    public void WelcomeHasSession()
    {
        var e = EventSub.Parse(Welcome);
        Assert.Equal(TwitchKind.Welcome, e.Kind);
        Assert.Equal("AQoQILE98gtqShGmLD7AM6yJThAB", e.SessionId);
        Assert.Equal(10, e.KeepaliveSeconds);
        Assert.Equal("96a3f3b5-5dec-4eed-908e-e11ee657416c", e.MessageId);
    }

    [Fact] public void KeepaliveKind() => Assert.Equal(TwitchKind.Keepalive, EventSub.Parse(Keepalive).Kind);

    [Fact]
    public void ReconnectCarriesUrl()
    {
        var e = EventSub.Parse(Reconnect);
        Assert.Equal(TwitchKind.Reconnect, e.Kind);
        Assert.Equal("wss://eventsub.wss.twitch.tv?...", e.ReconnectUrl);
    }

    [Fact] public void RevocationKind() => Assert.Equal(TwitchKind.Revocation, EventSub.Parse(Revocation).Kind);

    [Fact]
    public void CheerFields()
    {
        var e = EventSub.Parse(Cheer);
        Assert.Equal(TwitchKind.Cheer, e.Kind);
        Assert.Equal("Cool_User", e.User);
        Assert.Equal("1234", e.UserId);
        Assert.False(e.Anonymous);
        Assert.Equal(100, e.Bits);
        Assert.Equal("Cheer100 raid pogchamp", e.Message);
    }

    [Fact]
    public void AnonymousCheer()
    {
        var e = EventSub.Parse(AnonCheer);
        Assert.True(e.Anonymous);
        Assert.Equal("", e.User);
        Assert.Equal(500, e.Bits);
    }

    [Fact]
    public void RedemptionFields()
    {
        var e = EventSub.Parse(Redemption);
        Assert.Equal(TwitchKind.Redemption, e.Kind);
        Assert.Equal("92af127c-7326-4483-a52b-b0da0be61c01", e.RewardId);
        Assert.Equal("Raid", e.RewardTitle);
        Assert.Equal("17fa2df1-ad76-4804-bfa5-a40ef63efe63", e.RedemptionId);
        Assert.Equal("Cooler_User", e.User);
        Assert.Equal("pogchamp", e.Message);
    }

    [Fact]
    public void ChatFields()
    {
        var e = EventSub.Parse(Chat);
        Assert.Equal(TwitchKind.Chat, e.Kind);
        Assert.Equal("viptest", e.User);
        Assert.Equal("!effects", e.Message);
    }

    [Fact]
    public void PollWinnerByVotes()
    {
        var e = EventSub.Parse(PollEnd);
        Assert.Equal(TwitchKind.PollEnd, e.Kind);
        Assert.Equal("Yellow", e.PollWinner);
    }

    [Fact] public void GarbageIsUnknown() => Assert.Equal(TwitchKind.Unknown, EventSub.Parse("{not json").Kind);
    [Fact] public void NullIsUnknown() => Assert.Equal(TwitchKind.Unknown, EventSub.Parse(null).Kind);
    [Fact] public void OtherNotificationIsUnknown() => Assert.Equal(TwitchKind.Unknown, EventSub.Parse(@"{""metadata"":{""message_type"":""notification"",""subscription_type"":""channel.follow""},""payload"":{""event"":{}}}").Kind);
}
