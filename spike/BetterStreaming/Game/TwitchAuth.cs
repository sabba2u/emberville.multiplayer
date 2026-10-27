using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BetterStreaming.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BetterStreaming;

/// <summary>
/// Signing in to Twitch with a device code (no password in the game): Begin asks Twitch for a code, the panel shows it
/// with the link, the streamer approves on twitch.tv, and we get a token (refreshed when it runs out). The token is
/// kept only in BepInEx/config/spike.emberville.betterstreaming.token.json, never logged; Revoke ends it on Twitch's
/// side and deletes the file. Checked with /oauth2/validate when loaded (and hourly, as Twitch asks).
/// </summary>
internal static class TwitchAuth
{
    const string Id = "https://id.twitch.tv/oauth2";
    static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, "spike.emberville.betterstreaming.token.json");

    sealed class Saved { public string Access, Refresh, Login, UserId; public DateTime ExpiresAt; }
    static Saved saved;
    static CancellationTokenSource signing;
    static readonly SemaphoreSlim refreshing = new SemaphoreSlim(1, 1);
    static DateTime nextValidate;

    public static DeviceStart Started { get; private set; }
    public static volatile string Status = "Not connected";
    public static bool SignedIn => saved != null && !string.IsNullOrEmpty(saved.Access);
    public static string Login => saved?.Login ?? "";
    public static string UserId => TwitchConfig.Mock && !SignedIn ? TwitchConfig.MockUserId.Value : saved?.UserId ?? "";
    public static bool NoClientId => string.IsNullOrWhiteSpace(TwitchConfig.ClientId.Value);

    /// <summary>On start: a saved sign-in, checked with Twitch (in the background).</summary>
    public static void LoadSaved()
    {
        try { if (File.Exists(FilePath)) saved = JsonConvert.DeserializeObject<Saved>(File.ReadAllText(FilePath)); }
        catch (Exception e) { Log.Warn($"Twitch: the saved sign-in couldn't be read ({e.GetType().Name})"); saved = null; }
        if (NoClientId) { Status = "Not set up yet: Twitch.ClientId in the config"; return; }
        if (SignedIn) { Status = $"Signed in as {Login}"; Task.Run(Validate); }
    }

    /// <summary>The panel's Connect: a device code to approve on twitch.tv.</summary>
    public static void Begin()
    {
        if (NoClientId) { Status = "Not set up yet: Twitch.ClientId in the config"; return; }
        signing?.Cancel();
        signing = new CancellationTokenSource();
        var stop = signing.Token;
        Task.Run(async () =>
        {
            try
            {
                Status = "Asking Twitch for a code...";
                var (code, body) = await Form(Id + "/device", new Dictionary<string, string> { ["client_id"] = TwitchConfig.ClientId.Value, ["scopes"] = TwitchConfig.Scopes });
                var start = DeviceAuth.ParseStart(body);
                if (start == null) { Status = $"Twitch didn't give a code ({code})"; return; }
                Started = start;
                Status = $"Go to {start.VerificationUri} and enter {start.UserCode}";
                int wait = Math.Max(1, start.Interval);
                var until = DateTime.UtcNow.AddSeconds(start.ExpiresIn);
                while (!stop.IsCancellationRequested && DateTime.UtcNow < until)
                {
                    await Task.Delay(wait * 1000, stop);
                    var (status, reply) = await Form(Id + "/token", new Dictionary<string, string>
                    {
                        ["client_id"] = TwitchConfig.ClientId.Value,
                        ["scopes"] = TwitchConfig.Scopes,
                        ["device_code"] = start.DeviceCode,
                        ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    });
                    var r = DeviceAuth.ParseToken(status, reply);
                    if (r.Status == PollStatus.Pending) continue;
                    if (r.Status == PollStatus.SlowDown) { wait += 5; continue; }
                    Started = null;
                    if (r.Status != PollStatus.Success) { Status = r.Status == PollStatus.Denied ? "Twitch sign-in was declined" : r.Status == PollStatus.Expired ? "The code ran out: Connect again" : $"Twitch sign-in failed ({status})"; return; }
                    saved = new Saved { Access = r.Access, Refresh = r.Refresh, ExpiresAt = DateTime.UtcNow.AddSeconds(r.ExpiresIn) };
                    await Validate();
                    Write();
                    Status = $"Signed in as {Login}";
                    Log.Info($"Twitch: signed in as {Login}");
                    TwitchSocket.Start();
                    return;
                }
                if (!stop.IsCancellationRequested) { Started = null; Status = "The code ran out: Connect again"; }
            }
            catch (TaskCanceledException) { }
            catch (Exception e) { Started = null; Status = $"Twitch sign-in failed ({e.GetType().Name})"; Log.Warn($"Twitch sign-in: {e.Message}"); }
        });
    }

    /// <summary>Sign out: the token ended on Twitch's side, the file gone, the socket closed.</summary>
    public static void Revoke()
    {
        signing?.Cancel();
        Started = null;
        TwitchSocket.Stop();
        var token = saved?.Access;
        saved = null;
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch (Exception e) { Log.Warn($"Twitch: couldn't delete the sign-in file ({e.Message})"); }
        Status = "Signed out";
        if (!string.IsNullOrEmpty(token))
            Task.Run(() => Form(Id + "/revoke", new Dictionary<string, string> { ["client_id"] = TwitchConfig.ClientId.Value, ["token"] = token }));
    }

    /// <summary>Good for 2 more minutes (ExpiresAt can be DateTime.MinValue: "refresh now", so no maths on it).</summary>
    static bool Fresh => saved.ExpiresAt > DateTime.UtcNow.AddMinutes(2);

    /// <summary>A token that works now (refreshed if it's about to run out). Testing against a local mock: a dummy.</summary>
    public static async Task<string> Token()
    {
        if (!SignedIn) return TwitchConfig.Mock ? "mock" : "";
        if (DateTime.UtcNow > nextValidate) _ = Task.Run(Validate);
        if (Fresh) return saved.Access;
        await refreshing.WaitAsync();
        try
        {
            if (Fresh) return saved.Access;
            var (status, body) = await Form(Id + "/token", new Dictionary<string, string>
            {
                ["client_id"] = TwitchConfig.ClientId.Value,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = saved.Refresh,
            });
            var r = DeviceAuth.ParseToken(status, body);
            if (r.Status != PollStatus.Success) { Status = "Twitch sign-in ran out: Connect again"; Log.Warn($"Twitch: couldn't refresh the sign-in ({status})"); return saved.Access; }
            saved.Access = r.Access;
            if (!string.IsNullOrEmpty(r.Refresh)) saved.Refresh = r.Refresh;
            saved.ExpiresAt = DateTime.UtcNow.AddSeconds(r.ExpiresIn);
            Write();
            return saved.Access;
        }
        finally { refreshing.Release(); }
    }

    /// <summary>The permissions this sign-in has (from /oauth2/validate; null: not checked yet).</summary>
    public static System.Collections.Generic.HashSet<string> Granted;

    /// <summary>This sign-in is known to lack that permission (signed in before the mod asked for it).</summary>
    public static bool Lacks(string scope) => SignedIn && Granted != null && !Granted.Contains(scope);

    /// <summary>/oauth2/validate: who this token is (login, user id, permissions); Twitch wants it checked hourly.</summary>
    static async Task Validate()
    {
        nextValidate = DateTime.UtcNow.AddHours(1);
        if (!SignedIn) return;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, Id + "/validate");
            req.Headers.TryAddWithoutValidation("Authorization", "OAuth " + saved.Access);
            using (var res = await http.SendAsync(req))
            {
                string body = await res.Content.ReadAsStringAsync();
                if ((int)res.StatusCode == 401) { await RefreshNow(); return; }
                var o = JObject.Parse(body);
                saved.Login = (string)o["login"] ?? saved.Login;
                saved.UserId = (string)o["user_id"] ?? saved.UserId;
                if (o["scopes"] is JArray granted) Granted = new System.Collections.Generic.HashSet<string>(granted.Select(g => (string)g));
                if ((int?)o["expires_in"] is int left) saved.ExpiresAt = DateTime.UtcNow.AddSeconds(left);
                Write();
            }
        }
        catch (Exception e) { Log.Warn($"Twitch: couldn't check the sign-in ({e.GetType().Name})"); }
    }

    static async Task RefreshNow()
    {
        if (saved == null) return;
        saved.ExpiresAt = DateTime.MinValue;
        await Token();
    }

    static void Write()
    {
        try { File.WriteAllText(FilePath, JsonConvert.SerializeObject(saved)); }
        catch (Exception e) { Log.Warn($"Twitch: couldn't save the sign-in ({e.GetType().Name})"); }
    }

    static async Task<(int, string)> Form(string url, Dictionary<string, string> fields)
    {
        using (var res = await http.PostAsync(url, new FormUrlEncodedContent(fields)))
            return ((int)res.StatusCode, await res.Content.ReadAsStringAsync());
    }
}
