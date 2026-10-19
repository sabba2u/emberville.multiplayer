using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Recon: does the same world seed always give the same map? (Insert menu > "Map seed test".)
/// The world map is DungeonSettings.CreateDungeon(new GenerationContext(new SeededSystemRandomProvider(seed))), saved as
/// Saves/&lt;settings&gt;S&lt;seed&gt;.dmap.json. But some steps use Unity's global Random instead of the seeded one
/// (GenerationContext's default RandomNeighborSelection, ConnectChunksStep, DungeonEntranceGeneration...), and the game
/// never reseeds it. So we generate the map for our world's seed twice as the game does, twice more with Unity's Random
/// seeded first (a possible fix), and compare them with each other and with the game's cached file.
/// Every result is written to persistentDataPath/EmbervilleGhost_SeedTest so copies / PCs can be diffed too.
/// </summary>
internal static class SeedTest
{
    public static bool Running { get; private set; }
    public static string Result = "";

    public static async UniTaskVoid Run()
    {
        if (Running) return;
        var wm = Singleton<WorldMap>.HasMain ? Singleton<WorldMap>.main : null;
        if (!wm || !GameData.HasWorldData || wm.settings == null) { Result = "Load into a world first (needs the world map)."; NetLog.Warn(Result); return; }
        Running = true;
        Result = "running...";
        try
        {
            int seed = GameData.worldData.MapSeed;
            var settings = wm.settings;
            var ct = wm.destroyCancellationToken;
            string dir = Path.Combine(Application.persistentDataPath, "EmbervilleGhost_SeedTest");
            Directory.CreateDirectory(dir);
            string tag = $"seed{seed}_copy{SaveIsolation.CopyNumber}";

            var t0 = Time.realtimeSinceStartup;
            string a1 = await Generate(settings, seed, false, ct);
            string a2 = await Generate(settings, seed, false, ct);
            string b1 = await Generate(settings, seed, true, ct);
            string b2 = await Generate(settings, seed, true, ct);
            File.WriteAllText(Path.Combine(dir, tag + "_game1.json"), a1);
            File.WriteAllText(Path.Combine(dir, tag + "_game2.json"), a2);
            File.WriteAllText(Path.Combine(dir, tag + "_seeded1.json"), b1);
            File.WriteAllText(Path.Combine(dir, tag + "_seeded2.json"), b2);
            string cached = SaveLoad.Load($"{settings.name}S{seed}.dmap.json", obfuscate: false);

            var sb = new StringBuilder();
            sb.Append($"Seed {seed} ({settings.name}), {Time.realtimeSinceStartup - t0:0.0}s. ");
            sb.Append($"As the game does it: {Same(a1, a2)}. ");
            sb.Append($"With Unity Random seeded: {Same(b1, b2)}. ");
            sb.Append(cached == null ? "No cached map on disk to compare. " : $"vs cached map: game {Same(a1, cached)}, seeded {Same(b1, cached)}. ");
            sb.Append($"Hashes: {Hash(a1)} {Hash(a2)} | {Hash(b1)} {Hash(b2)}. Files in {dir}");
            Result = sb.ToString();
            NetLog.Info("[seed test] " + Result, toast: true);
        }
        catch (Exception e)
        {
            Result = "failed: " + e.Message;
            NetLog.Error("[seed test] " + e);
        }
        finally { Running = false; }
    }

    /// <summary>Generate the map model like WorldMap.CreateAndRenderDungeon and serialize it the way the game saves it.</summary>
    static async UniTask<string> Generate(DungeonSettings settings, int seed, bool seedUnityRandom, System.Threading.CancellationToken ct)
    {
        var saved = UnityEngine.Random.state;
        if (seedUnityRandom) UnityEngine.Random.InitState(seed);
        try
        {
            var ctx = new GenerationContext(new SeededSystemRandomProvider(seed));
            var model = await settings.CreateDungeon(ctx, ct);
            var render = model.GetRender(ctx.RNG);
            return JsonConvert.SerializeObject(render, new DungeonBarrierVariationConverter(null), new BiomeSettingsConverter(null),
                new Vector2IntConverter(), new Vector2Converter(), new ChunkTypeDefConverter(null), new DungeonChunkConverter(null), new ChunkModelConverter(null));
        }
        finally
        {
            if (seedUnityRandom) UnityEngine.Random.state = saved;   // don't leave the game's Random seeded
        }
    }

    static string Same(string x, string y)
    {
        if (x == y) return "SAME";
        int n = Math.Min(x.Length, y.Length), i = 0;
        while (i < n && x[i] == y[i]) i++;
        return $"DIFFERENT (from char {i} of {x.Length}/{y.Length})";
    }

    static string Hash(string s)
    {
        using var sha = SHA1.Create();
        var h = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
        return BitConverter.ToString(h, 0, 4).Replace("-", "");
    }
}
