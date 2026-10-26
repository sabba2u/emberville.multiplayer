namespace BetterCompanions.Core;

/// <summary>
/// Downed and getting up: at zero health the companion falls and stays down until someone holds the interact key
/// next to it for ReviveHold seconds (letting go starts over), or the scene changes; either way it gets up at
/// ReviveHealth of its health.
/// </summary>
public class Revive
{
    public bool Down { get; private set; }
    public float Health01 { get; private set; } = 1f;
    float held;

    public void Fall() { Down = true; held = 0f; Health01 = 0f; }

    /// <summary>One frame of holding (or not); the progress 0..1. At 1 it's up.</summary>
    public float Hold(bool holding, float dt)
    {
        if (!Down) return 0f;
        held = holding ? held + dt : 0f;
        float progress = held / Rules.ReviveHold;
        if (progress >= 1f - 1e-4f) { GetUp(); return 1f; }
        return progress;
    }

    public void SceneChanged() { if (Down) GetUp(); }

    void GetUp() { Down = false; held = 0f; Health01 = Rules.ReviveHealth; }
}
