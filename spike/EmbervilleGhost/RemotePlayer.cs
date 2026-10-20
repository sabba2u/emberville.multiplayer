using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// One other player (host = id 0, clients 1, 2, 3...): what we last heard from them and their stand-in.
/// Played back slightly in the past for smoothness (SnapshotBuffer).
/// </summary>
internal class RemotePlayer
{
    public readonly int Id;
    public readonly SnapshotBuffer Snapshots = new SnapshotBuffer();
    public RemoteAvatar Avatar;
    public AnimSync.Frame[] Anim;
    public bool Flip;
    public string Scene = "", Area = "";
    public int Health, MaxHealth;
    public float Kymia, MaxKymia;
    public bool InWilds;
    public byte PetKind;            // their pet (0 none, 1 chicken), its level and name (Pet)
    public int PetLevel;
    public string PetName = "";
    public UnityEngine.Color PetColor = UnityEngine.Color.white;            // in the generated wilds (world map positions apply: MapMates)   // skill / spell energy; MaxKymia 0 = they have none (no bar)
    public PlayerStatus Status;
    public bool HaveState;
    public Vector3 LastPosition;  // newest position heard (for "teleport to them" when their stand-in isn't showing)
    public byte[] Look;          // LookSync bytes; the host keeps them for players who join later
    public string CharacterName; // from their save, sent with the look
    public readonly System.Collections.Generic.List<string> Effects = new System.Collections.Generic.List<string>();   // status effects on them (PlayerFeel)
    public string Said;          // their last chat line, shown in a bubble over their head for a while
    public float SaidAt;
    string failedAt;

    public RemotePlayer(int id) { Id = id; }

    public string Tag => Id == 0 ? "Host" : $"P{Id + 1}";
    public string Name => string.IsNullOrEmpty(CharacterName) ? Tag : $"{CharacterName} ({Tag})";

    public bool SamePlace(string scene, string area) => HaveState && Scene == scene && Area == area;

    /// <summary>Show / move their stand-in, only while they're in our scene and area.</summary>
    public void Update(string localScene, string localArea)
    {
        var me = LocalPlayer.Get();
        if (!SamePlace(localScene, localArea) || !me)
        {
            Avatar?.SetActive(false);
            return;
        }
        if (!Snapshots.Sample(Time.unscaledTimeAsDouble, out var pos, out _, out _)) return;

        if (Avatar != null && Avatar.Lost) Avatar = null;   // went with the old scene
        if (Avatar == null)
        {
            string here = localScene + "/" + localArea;
            if (failedAt == here) return;   // never retry per frame; try again in the next place
            try { Avatar = RemoteAvatar.Create(me, Id); }
            catch (System.Exception e)
            {
                failedAt = here;
                NetLog.Error($"Couldn't build {Name}'s stand-in: {e.Message}");
                Plugin.Log.LogError(e);
                return;
            }
        }
        Avatar.SetActive(true);
        Avatar.ApplyLook(Look);
        Avatar.Apply(pos, Flip, Anim);
    }

    public void Destroy()
    {
        Avatar?.Destroy();
        Avatar = null;
    }
}
