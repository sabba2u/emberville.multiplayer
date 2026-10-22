using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Better Weather, together: one weather for everyone (clear, rain or snow) (whoever switches it, everyone gets it; the host tells each player
/// who joins how it is), and each game's lightning strikes shown on the others' screens too (to their place). The
/// wind needs nothing: it's a function of the clock, the same everywhere. Every call into Better Weather sits behind
/// Integrations.HasWeather, in NoInlining methods.
/// </summary>
internal static class WeatherSync
{
    public static Plugin Plugin;
    static readonly HashSet<int> sentTo = new();

    static bool Up => Plugin != null && Plugin.Connected && Plugin.MyId >= 0;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Hook()
    {
        BetterWeather.Hooks.Say = Chat.System;
        BetterWeather.Hooks.Underground = () => Dungeon.Active;
        BetterWeather.Hooks.SendWeather = (kind, options) => { if (Up) Plugin.SendToAll(Storm(kind, options), DeliveryMethod.ReliableOrdered); };
        BetterWeather.Hooks.SendStrike = at =>
        {
            if (!Up) return;
            var w = new NetDataWriter();
            w.Put((byte)MsgType.WeatherStrike);
            w.Put(at.x); w.Put(at.y);
            Plugin.SendToPlace(w, DeliveryMethod.ReliableUnordered);
        };
    }

    static NetDataWriter Storm(int kind, BetterWeather.WeatherOptions options)
    {
        var w = new NetDataWriter();
        w.Put((byte)MsgType.WeatherStorm);
        w.Put((byte)kind);
        foreach (var f in (options ?? new BetterWeather.WeatherOptions()).ToArray()) w.Put(f);   // (its own settings: NaN = the sliders')
        w.Put(BetterWeather.Plugin.SnowDepth);   // (how much snow lies already: a player who joins gets the same ground)
        return w;
    }

    /// <summary>The host: the storm as it is, to each player who just joined.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Tick()
    {
        if (Plugin == null || !Plugin.IsHost) { sentTo.Clear(); return; }
        var ids = Plugin.Players.Select(p => p.Id).ToList();
        sentTo.RemoveWhere(id => !ids.Contains(id));
        foreach (var id in ids) if (sentTo.Add(id)) Plugin.SendTo(id, Storm(BetterWeather.Plugin.WeatherNow, BetterWeather.Plugin.CurrentOptions));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OnStorm(NetDataReader r)
    {
        int kind = r.GetByte();
        var f = new float[] { float.NaN, float.NaN, float.NaN, float.NaN, float.NaN };   // (not sent: the sliders', never 0)
        for (int i = 0; i < f.Length && r.AvailableBytes >= 4; i++) f[i] = r.GetFloat();
        BetterWeather.Plugin.OnWeather(kind, BetterWeather.WeatherOptions.FromArray(f));
        if (r.AvailableBytes >= 4) { float depth = r.GetFloat(); if (!float.IsNaN(depth)) BetterWeather.Plugin.SnowDepth = depth; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OnStrike(NetDataReader r) => BetterWeather.Plugin.OnStrike(new Vector3(r.GetFloat(), r.GetFloat(), 0f));
}
