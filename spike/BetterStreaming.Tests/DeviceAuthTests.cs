using BetterStreaming.Core;
using Xunit;

public class DeviceAuthTests
{
    [Fact]
    public void StartFields()
    {
        var s = DeviceAuth.ParseStart(@"{""device_code"":""ike3GM8QIdYZs43KdrWPIO36LofILoCyFEzjlQ91"",""expires_in"":1800,""interval"":5,""user_code"":""ABCDEFGH"",""verification_uri"":""https://www.twitch.tv/activate?public=true&device-code=ABCDEFGH""}");
        Assert.Equal("ike3GM8QIdYZs43KdrWPIO36LofILoCyFEzjlQ91", s.DeviceCode);
        Assert.Equal("ABCDEFGH", s.UserCode);
        Assert.Equal("https://www.twitch.tv/activate?public=true&device-code=ABCDEFGH", s.VerificationUri);
        Assert.Equal(5, s.Interval);
        Assert.Equal(1800, s.ExpiresIn);
    }

    [Fact] public void StartGarbageIsNull() => Assert.Null(DeviceAuth.ParseStart("<html>"));

    [Theory]
    [InlineData(400, @"{""status"":400,""message"":""authorization_pending""}", PollStatus.Pending)]
    [InlineData(400, @"{""status"":400,""message"":""slow_down""}", PollStatus.SlowDown)]
    [InlineData(400, @"{""status"":400,""message"":""access_denied""}", PollStatus.Denied)]
    [InlineData(400, @"{""status"":400,""message"":""expired_token""}", PollStatus.Expired)]
    [InlineData(400, @"{""status"":400,""message"":""invalid device code""}", PollStatus.Expired)]
    [InlineData(500, "oops", PollStatus.Error)]
    public void PollStates(int http, string body, PollStatus want) => Assert.Equal(want, DeviceAuth.ParseToken(http, body).Status);

    [Fact]
    public void Success()
    {
        var r = DeviceAuth.ParseToken(200, @"{""access_token"":""rfx2uswqe8l4g1mkagrvg5tv0ks3"",""expires_in"":14124,""refresh_token"":""5b93chm6hdve3mycz05zfzatkfdenfspp1h1ar2xxdalen01"",""scope"":[""bits:read""],""token_type"":""bearer""}");
        Assert.Equal(PollStatus.Success, r.Status);
        Assert.Equal("rfx2uswqe8l4g1mkagrvg5tv0ks3", r.Access);
        Assert.Equal("5b93chm6hdve3mycz05zfzatkfdenfspp1h1ar2xxdalen01", r.Refresh);
        Assert.Equal(14124, r.ExpiresIn);
    }
}
