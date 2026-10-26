using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace BetterCompanions;

/// <summary>
/// Walking the companion (as Better Pets and Summons walks pets): along the walkable map the game's creatures use (its
/// navmesh), so cliffs, pits and water are walked around; where there's no map, straight there, sliding along walls
/// and stopping at closed doors. MoveToward returns the way it went (zero: it didn't move). Straight moves (a dash,
/// walking where there's no route) never leave the walkable map either: some places (the boss arena) have no wall
/// colliders at all, only the map's edge. CutOff: the last route asked for can't reach where it was going (another
/// room, behind a shut door): the companion teleports to you instead of wandering off along a part of the way.
/// </summary>
internal sealed class Mover
{
    readonly Transform root;
    public Mover(Transform root) { this.root = root; }

    public Vector2 MoveToward(Vector3 to, float speed, float dt, float stopAt)
    {
        var from = root.position;
        var d = new Vector2(to.x - from.x, to.y - from.y);
        float dist = d.magnitude;
        if (dist <= stopAt) return Vector2.zero;
        if (Route(from, to)) return FollowRoute(from, speed, dt);
        if (CutOff) return Vector2.zero;
        float step = Mathf.Min(speed * dt, dist - stopAt);
        var dir = d / dist;
        var next = from + new Vector3(dir.x, dir.y, 0f) * step;
        if (Blocked(from, next) && !Blocks(from))
        {
            var alongX = from + new Vector3(dir.x * step, 0f, 0f);
            var alongY = from + new Vector3(0f, dir.y * step, 0f);
            if (Mathf.Abs(dir.x) > 0.1f && !Blocked(from, alongX)) next = alongX;
            else if (Mathf.Abs(dir.y) > 0.1f && !Blocked(from, alongY)) next = alongY;
            else return Vector2.zero;
        }
        root.position = next;
        return dir;
    }

    /// <summary>
    /// For a real player (the game moves it, walls and all): which way to push its stick to get there, along the
    /// walkable map where there is one; zero when it's there, or the way is cut off.
    /// </summary>
    public Vector2 Steer(Vector3 to, float stopAt)
    {
        var from = root.position;
        var d = new Vector2(to.x - from.x, to.y - from.y);
        if (d.magnitude <= stopAt) return Vector2.zero;
        if (Route(from, to))
        {
            while (corner < corners.Length && new Vector2(corners[corner].x - from.x, corners[corner].y - from.y).magnitude < 0.25f) corner++;
            if (corner < corners.Length)
            {
                var c = new Vector2(corners[corner].x - from.x, corners[corner].y - from.y);
                if (c.sqrMagnitude > 1e-6f) return c.normalized;
            }
            return d.normalized;
        }
        return CutOff ? Vector2.zero : d.normalized;
    }

    /// <summary>A straight move (a dash), stopping short of a wall or closed door.</summary>
    public void Slide(Vector3 to, float speed, float dt)
    {
        var from = root.position;
        var d = new Vector2(to.x - from.x, to.y - from.y);
        float dist = d.magnitude;
        if (dist < 0.01f) return;
        var next = from + (Vector3)(d / dist * Mathf.Min(speed * dt, dist));
        if (!Blocked(from, next)) root.position = next;
    }

    public void Teleport(Vector3 to)
    {
        root.position = to;
        hasPath = false;
        CutOff = false;
        pathAt = -10f;
    }

    public static bool Blocks(Vector3 at) => DoorBlocks(at) || WallBlocks(at);

    /// <summary>Can't step from there to here: a wall, a door, or off the edge of the walkable map (when on it now).</summary>
    public static bool Blocked(Vector3 from, Vector3 to) =>
        Blocks(to) || OnMap(from) && NavMesh.Raycast(new Vector3(from.x, from.y, 0f), new Vector3(to.x, to.y, 0f), out _, Filter());

    /// <summary>A spot to appear at, next to someone standing at near: not in a wall or door, and on the walkable map if they are.</summary>
    public static bool CanStand(Vector3 at, Vector3 near) => !Blocks(at) && (!OnMap(near) || !Blocked(near, at));

    static bool OnMap(Vector3 at) => NavMesh.SamplePosition(new Vector3(at.x, at.y, 0f), out _, 0.5f, Filter());

    /// <summary>The last route can't get there (it ends well short): the way is cut off.</summary>
    public bool CutOff { get; private set; }

    // ---- the way there ----

    readonly NavMeshPath path = new();
    Vector3[] corners = new Vector3[0];
    int corner;
    Vector3 pathTo;
    float pathAt = -10f;
    bool hasPath;

    static int agentType;
    static string agentScene;

    static NavMeshQueryFilter Filter()
    {
        string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (scene != agentScene)
        {
            var agent = Object.FindAnyObjectByType<NavMeshAgent>();
            if (!agent) return new NavMeshQueryFilter { agentTypeID = 0, areaMask = NavMesh.AllAreas };
            agentScene = scene;
            agentType = agent.agentTypeID;
        }
        return new NavMeshQueryFilter { agentTypeID = agentType, areaMask = NavMesh.AllAreas };
    }

    bool Route(Vector3 from, Vector3 to)
    {
        if (Time.time - pathAt < 0.4f && ((Vector2)(to - pathTo)).sqrMagnitude < 0.25f) return hasPath;
        pathAt = Time.time;
        pathTo = to;
        hasPath = false;
        CutOff = false;
        var filter = Filter();
        if (!NavMesh.SamplePosition(new Vector3(from.x, from.y, 0f), out var start, 1f, filter)) return false;
        if (!NavMesh.SamplePosition(new Vector3(to.x, to.y, 0f), out var end, 6f, filter)) return false;
        if (!NavMesh.CalculatePath(start.position, end.position, filter, path) || path.status == NavMeshPathStatus.PathInvalid) return false;
        if (path.status == NavMeshPathStatus.PathPartial && path.corners.Length > 0
            && ((Vector2)(path.corners[path.corners.Length - 1] - new Vector3(to.x, to.y, 0f))).magnitude > 2f)
        { CutOff = true; return false; }   // (it would only get part of the way, and wander there)
        corners = path.corners;
        corner = corners.Length > 1 ? 1 : corners.Length;
        hasPath = true;
        return true;
    }

    Vector2 FollowRoute(Vector3 from, float speed, float dt)
    {
        var pos = from;
        float left = speed * dt;
        var moved = Vector2.zero;
        while (corner < corners.Length && left > 0f)
        {
            var c = corners[corner];
            var d = new Vector2(c.x - pos.x, c.y - pos.y);
            float m = d.magnitude;
            if (m <= left) { pos.x = c.x; pos.y = c.y; left -= m; corner++; moved += d; }
            else { var s = d / m * left; pos.x += s.x; pos.y += s.y; moved += s; left = 0f; }
        }
        if (moved.sqrMagnitude < 1e-8f || DoorBlocks(pos) && !DoorBlocks(from)) return Vector2.zero;
        root.position = pos;
        return moved.normalized;
    }

    // ---- walls: whatever would stop the player there ----

    const float WallRadius = 0.2f;
    static int wallMask;
    static float maskAt = -1f;

    static bool WallBlocks(Vector3 at)
    {
        if (Time.unscaledTime >= maskAt)
        {
            maskAt = Time.unscaledTime + 2f;
            var me = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
            var col = me && me.rb ? System.Linq.Enumerable.FirstOrDefault(me.rb.GetComponents<Collider2D>(), c => c && !c.isTrigger) : null;
            wallMask = me ? Physics2D.GetLayerCollisionMask(col ? col.gameObject.layer : me.gameObject.layer) : 0;
        }
        if (wallMask == 0) return false;
        foreach (var c in Physics2D.OverlapCircleAll(new Vector2(at.x, at.y), WallRadius, wallMask))
        {
            if (!c || c.isTrigger) continue;
            bool mapWall = c is UnityEngine.Tilemaps.TilemapCollider2D || c is CompositeCollider2D;
            if (!mapWall && c.attachedRigidbody && c.attachedRigidbody.bodyType != RigidbodyType2D.Static) continue;
            if (c.GetComponentInParent<Enemy>() || c.GetComponentInParent<PlayerMaster>() || c.GetComponentInParent<CompanionUnit>()) continue;
            return true;
        }
        return false;
    }

    // ---- closed doors ----

    static readonly List<Collider2D> closedDoors = new();
    static float doorsAt = -1f;
    const float DoorRadius = 0.3f;

    static bool DoorBlocks(Vector3 at)
    {
        if (Time.unscaledTime >= doorsAt)
        {
            doorsAt = Time.unscaledTime + 0.5f;
            closedDoors.Clear();
            foreach (var door in Object.FindObjectsByType<GateDoor>(FindObjectsSortMode.None))
            {
                if (!door || door.state == DoorState.Opened || !door.closedColliders || !door.closedColliders.activeInHierarchy) continue;
                foreach (var c in door.closedColliders.GetComponentsInChildren<Collider2D>())
                    if (c && c.enabled && !c.isTrigger) closedDoors.Add(c);
            }
        }
        var p = new Vector2(at.x, at.y);
        foreach (var c in closedDoors)
            if (c && (c.ClosestPoint(p) - p).sqrMagnitude < DoorRadius * DoorRadius) return true;
        return false;
    }
}
