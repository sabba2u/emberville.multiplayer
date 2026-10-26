using Newtonsoft.Json.Linq;

namespace BetterStreaming.Core;

/// <summary>The start of a device sign-in: the code to show the streamer and where they enter it.</summary>
public sealed class DeviceStart
{
    public string DeviceCode, UserCode, VerificationUri;
    public int Interval = 5, ExpiresIn = 1800;
}

public enum PollStatus { Pending, SlowDown, Denied, Expired, Success, Error }

/// <summary>One answer while waiting for the streamer to approve (or the tokens once they have).</summary>
public sealed class TokenResult
{
    public PollStatus Status;
    public string Access, Refresh;
    public int ExpiresIn;
}

/// <summary>
/// Reading Twitch's Device Code Grant replies (a public client: no secret). POST /oauth2/device gives the codes; then
/// POST /oauth2/token is asked every Interval seconds: "authorization_pending" until the streamer approves on
/// twitch.tv, "slow_down" (ask less often), a refusal, an expired code, or the tokens.
/// </summary>
public static class DeviceAuth
{
    public static DeviceStart ParseStart(string json)
    {
        try
        {
            var o = JObject.Parse(json ?? "");
            var s = new DeviceStart
            {
                DeviceCode = (string)o["device_code"],
                UserCode = (string)o["user_code"],
                VerificationUri = (string)o["verification_uri"],
                Interval = (int?)o["interval"] ?? 5,
                ExpiresIn = (int?)o["expires_in"] ?? 1800,
            };
            return string.IsNullOrEmpty(s.DeviceCode) || string.IsNullOrEmpty(s.UserCode) ? null : s;
        }
        catch (Newtonsoft.Json.JsonException) { return null; }
    }

    public static TokenResult ParseToken(int httpStatus, string json)
    {
        JObject o;
        try { o = JObject.Parse(json ?? ""); }
        catch (Newtonsoft.Json.JsonException) { return new TokenResult { Status = PollStatus.Error }; }
        if (httpStatus == 200 && o["access_token"] != null)
            return new TokenResult { Status = PollStatus.Success, Access = (string)o["access_token"], Refresh = (string)o["refresh_token"], ExpiresIn = (int?)o["expires_in"] ?? 0 };
        string m = ((string)o["message"] ?? "").ToLowerInvariant();
        var status = m.Contains("authorization_pending") ? PollStatus.Pending
                   : m.Contains("slow_down") ? PollStatus.SlowDown
                   : m.Contains("denied") ? PollStatus.Denied
                   : m.Contains("expired") || m.Contains("invalid device code") ? PollStatus.Expired
                   : PollStatus.Error;
        return new TokenResult { Status = status };
    }
}
