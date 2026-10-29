using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace EmbervilleGhost;

/// <summary>
/// /door: to the nearest doorway (any of the game's teleporters: the glowing doors), standing Away tiles in front of
/// it. In front is the way its art faces into the room (the way you face arriving through it); if that side isn't
/// ground you can walk to from here (a door set the other way round: the throne room's cave doors), the other sides
/// are tried, so you never land behind it.
/// </summary>
internal static class DoorJump
{
    const float Away = 5f;

    public static void Command()
    {
        var me = LocalPlayer.Get();
        var gm = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main : null;
        if (!me || !gm) return;
        var here = me.transform.position;
        var door = Object.FindObjectsByType<Teleporter>(FindObjectsSortMode.None)
            .Where(t => t && t.isActiveAndEnabled)
            .OrderBy(t => ((Vector2)(t.transform.position - here)).sqrMagnitude).FirstOrDefault();
        if (!door) { Chat.System("No doorway here."); return; }
        NavMesh.SamplePosition(here, out var mine, 3f, NavMesh.AllAreas);
        var d = door.SpawnDirection;
        Vector2 Way(Direction x) => x == Direction.Up ? Vector2.up : x == Direction.Down ? Vector2.down : x == Direction.Left ? Vector2.left : Vector2.right;
        var order = new[] { d, Opposite(d), Direction.Up, Direction.Down, Direction.Left, Direction.Right }.Distinct();
        foreach (var dir in order)
        {
            var want = door.transform.position + (Vector3)(Way(dir) * Away);
            if (!NavMesh.SamplePosition(want, out var h, 1.5f, NavMesh.AllAreas)) continue;
            var path = new NavMeshPath();   // (ground you could walk to from here: not behind a wall)
            if (mine.hit && (!NavMesh.CalculatePath(mine.position, h.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)) continue;
            var at = new Vector3(h.position.x, h.position.y, here.z);
            me.transform.position = at;
            if (me.rb) me.rb.position = at;
            Chat.System($"To {door.name}: {Away:0} tiles {dir.ToString().ToLowerInvariant()} of it ({((Vector2)(door.transform.position - here)).magnitude:0} away before).");
            return;
        }
        Chat.System($"The nearest doorway ({door.name}) has no ground you can walk to {Away:0} tiles in front of it.");
    }

    static Direction Opposite(Direction d) => d == Direction.Up ? Direction.Down : d == Direction.Down ? Direction.Up : d == Direction.Left ? Direction.Right : Direction.Left;
}
