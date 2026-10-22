using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// The multiplayer mod's door for other mods (everything else in it is internal). Soft-depend on
/// "spike.embervilleghost" and call these from a method that's only reached when it's installed.
/// </summary>
public static class Api
{
    /// <summary>Any chat command, as if typed after "/" ("tp sabba", "summon all", "dungeon", "respec"...). True if it was one.</summary>
    public static bool Command(string cmd) => Chat.Run(cmd);

    /// <summary>Playing together right now.</summary>
    public static bool Online => TownPortal.Plugin != null && TownPortal.Plugin.Connected;

    /// <summary>The other players' names.</summary>
    public static List<string> Players() => TownPortal.Plugin != null ? TownPortal.Plugin.Players.Select(p => p.Name).ToList() : new List<string>();

    /// <summary>Go to that player (a part of the name will do), dungeon floor and all.</summary>
    public static void TeleportTo(string name) => Travel.TeleportTo(Travel.Find(name));

    /// <summary>Bring that player to us, now (no asking).</summary>
    public static void Summon(string name) => Travel.Summon(Travel.Find(name));

    /// <summary>Bring everyone to us.</summary>
    public static void SummonAll() => Travel.SummonAll();

    /// <summary>Move the local player to a spot in the area they're in (the game's fade).</summary>
    public static void Teleport(Vector3 at)
    {
        if (!LocalPlayer.Get() || !Singleton<GameManager>.HasMain) return;
        Singleton<GameManager>.main.TeleportPlayerFade(at, Direction.Down).Forget();
    }
}
