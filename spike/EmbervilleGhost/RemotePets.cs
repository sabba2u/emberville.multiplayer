using System.Collections.Generic;
using BetterPetsAndSummons;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Teammates' pets (Better Pets and Summons installed): their player state says "kind, level, name, colour"; we keep a
/// follower behind each one's stand-in (the pet engine's PetBody, our copy of their creature) and a name tag under it.
/// Only called through Integrations (HasPets).
/// </summary>
internal static class RemotePets
{
    public static Plugin Plugin;

    static readonly Dictionary<int, PetBody> theirs = new Dictionary<int, PetBody>();
    static readonly Dictionary<int, float> retryAt = new Dictionary<int, float>();   // their kind isn't loaded here: look again later

    public static void Tick()
    {
        var gone = new List<int>(theirs.Keys);
        if (Plugin != null && Plugin.Connected)
            foreach (var p in Plugin.Players)
            {
                var avatar = p.Avatar;
                var kind = Pet.KindFromByte(p.PetKind);
                if (kind == null || avatar == null || !avatar.Visible) continue;
                gone.Remove(p.Id);
                var owner = avatar.Root.transform.position;
                var spot = owner + new Vector3(p.Flip ? Pet.FollowGap : -Pet.FollowGap, -0.25f, 0f);
                if (!theirs.TryGetValue(p.Id, out var body) || body == null || !body.Alive || body.Kind != kind)
                {
                    if (body == null && retryAt.TryGetValue(p.Id, out float when) && Time.unscaledTime < when) continue;
                    retryAt[p.Id] = Time.unscaledTime + 2f;
                    body?.Destroy();
                    body = PetBody.Create(kind, spot);
                    theirs[p.Id] = body;
                    if (body == null) continue;   // we haven't got that creature loaded: no pet shown for now
                }
                body.SetTint(p.PetColor);
                float far = Vector2.Distance(body.Position, spot);
                if (far > Pet.TeleportDistance) body.Teleport(spot);
                else if (far > 0.35f) body.MoveToward(spot, far > 3f ? Pet.RunSpeed : Pet.WalkSpeed, Time.deltaTime, 0.2f);
                else body.Stop(owner);
                body.Hover();
            }
        foreach (int id in gone) { theirs[id]?.Destroy(); theirs.Remove(id); retryAt.Remove(id); }
    }

    public static void DrawLabels()
    {
        var cam = Camera.main;
        if (!cam || Plugin == null) return;
        foreach (var p in Plugin.Players)
            if (theirs.TryGetValue(p.Id, out var body) && body != null && body.Alive)
                Pet.Label(cam, body.Position, $"{p.PetName}  Lv{p.PetLevel}", -1f);
    }

    public static void ForgetAll()
    {
        foreach (var b in theirs.Values) b?.Destroy();
        theirs.Clear();
    }
}
