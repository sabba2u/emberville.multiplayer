using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Creatures (Emberville's Enemy: monsters AND fauna like chickens, which are Enemy with type None).
///
/// Who runs a creature: the player it's fighting. The game's AI only ever chases "the player" (Enemy.Awake:
/// FindGameObjectWithTag("Player"), EnemyGroup sight, PlayerMaster-typed graph nodes), i.e. the local player of the
/// copy it runs on. So instead of teaching the AI about other players, the area owner hands each creature to the
/// player nearest to it (checked twice a second, with a margin so it doesn't flip back and forth): on that player's
/// copy the creature's own AI simply goes for them. Everyone mirrors every creature already, so a handover just turns
/// the AI on there and off here. A creature whose runner isn't in the area any more falls back to the area owner.
///
/// IDs: placed in the scene = path (SceneIds); spawned while playing ("(Clone)" in the path) = given by the area owner,
/// with EnemySpawn telling everyone which prefab to spawn where.
///
/// Runner: streams the creature (position, facing, health, tint, animators) ~10x/sec to the others in the area.
/// Everyone else: AI off (behaviour graph blocked, navmesh off, physics kinematic), the creature follows the stream;
/// its state machine still runs (hit/die states are timers: death animation, corpse fade, hit flash).
/// Hits: a hit by our player on a creature someone else runs is a request to them (EnemyHit); they apply it with the
/// game's own ApplyDamage and broadcast the result (EnemyDamaged); everyone replays it (applyingHost), with the
/// hitter's own player as aggressor so they get the XP. Drops still come from the area owner (it replays the death too).
/// </summary>
[HarmonyPatch]
internal static class EnemySync
{
    public static Plugin Plugin;

    const float SendInterval = 0.1f;
    const float AssignInterval = 0.5f;
    const float HandoverMargin = 2f;       // another player must be this much closer to take a creature over
    const float MissingHideSeconds = 3f;   // a creature we don't run and haven't heard about for this long is gone there

    static float sendTimer, assignTimer;
    static bool applyingHost;      // replaying the runner's result: let ApplyDamage through
    static int hitFrom = -1;       // runner: which player's request is being applied (-1 = none)
    static int replayHitter = -1;  // others: who dealt the hit we're replaying


    static readonly Dictionary<Enemy, Animator[]> animators = new Dictionary<Enemy, Animator[]>();

    class Remote
    {
        public readonly SnapshotBuffer Snapshots = new SnapshotBuffer { Delay = 0.15f };
        public bool Flip, Dead;
        public float Health;
        public Color Tint = Color.white;
        public AnimSync.Frame[] Anim;
        public bool[] Nodes, Sprites;   // its child objects on/off, its sprites visible (the "!", shields, auras...)
        public float LastHeard;
    }
    static readonly Dictionary<uint, Remote> remote = new Dictionary<uint, Remote>();
    static readonly Dictionary<Enemy, RigidbodyType2D> frozen = new Dictionary<Enemy, RigidbodyType2D>();   // AI off here
    static readonly Dictionary<Enemy, float> frozenSince = new Dictionary<Enemy, float>();
    static readonly Dictionary<uint, int> runnerOf = new Dictionary<uint, int>();   // creature -> player running it (area owner decides)

    /// <summary>Someone else runs our area (area-level: breakables, drops, chests, rocks use this).</summary>
    public static bool PuppetMode => Plugin != null && Plugin.Connected && Plugin.MyId >= 0 && !Plugin.IOwnHere;
    public static int Count => frozen.Count;

    // ---- who runs which creature ----

    static bool Connected => Plugin != null && Plugin.Connected && Plugin.MyId >= 0;

    /// <summary>The player running this creature: the one the area owner gave it to, if they're still here, else the area owner.</summary>
    static int RunnerOf(uint id)
    {
        if (runnerOf.TryGetValue(id, out int p) && Present(p)) return p;
        return Plugin.OwnerHere;
    }

    static bool Present(int player)
    {
        if (player == Plugin.MyId) return true;
        foreach (var rp in Plugin.Players) if (rp.Id == player) return rp.SamePlace(LocalPlayer.Scene, LocalPlayer.Area);
        return false;
    }

    /// <summary>Does this copy run the creature's AI? (Not connected: always.)</summary>
    public static bool Runs(Enemy e)
    {
        if (!Connected) return true;
        uint id = IdOf(e);
        return id != 0 && RunnerOf(id) == Plugin.MyId;
    }

    /// <summary>Area owner, twice a second: give each creature to the player nearest to it.</summary>
    static void AssignRunners()
    {
        assignTimer += Time.unscaledDeltaTime;
        if (assignTimer < AssignInterval || !Plugin.IOwnHere || !Plugin.AnyoneElseHere) return;
        assignTimer = 0f;

        var me = LocalPlayer.Get();
        if (!me) return;
        var positions = new List<(int id, Vector3 pos)> { (Plugin.MyId, me.transform.position) };
        foreach (var p in Plugin.Players)
        {
            var t = Plugin.AvatarOf(p.Id);
            if (t) positions.Add((p.Id, t.position));
        }

        foreach (var e in EnemiesManager.AllEnemies)
        {
            if (!e || !e.isActiveAndEnabled || e.IsDead) continue;
            uint id = IdOf(e);
            if (id == 0) continue;
            int current = RunnerOf(id);
            var at = e.transform.position;
            float currentDist = float.MaxValue;
            int best = current;
            float bestDist = float.MaxValue;
            foreach (var (pid, pos) in positions)
            {
                float d = Vector2.Distance(at, pos);
                if (pid == current) currentDist = d;
                if (d < bestDist) { bestDist = d; best = pid; }
            }
            if (best == current || bestDist + HandoverMargin >= currentDist) continue;
            SetRunner(id, best);
            var w = new NetDataWriter();
            w.Put((byte)MsgType.EnemyRunner);
            w.Put(id);
            w.Put(best);
            Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
        }
    }

    static void SetRunner(uint id, int player)
    {
        runnerOf[id] = player;
        remote.Remove(id);   // fresh stream from the new runner
    }

    public static void OnRunner(NetDataReader r)
    {
        uint id = r.GetUInt();
        int player = r.GetInt();
        SetRunner(id, player);
    }

    // ---- ids ----

    const uint RuntimeBit = 0x80000000u;
    static readonly Dictionary<Enemy, uint> runtimeIds = new Dictionary<Enemy, uint>();
    static readonly Dictionary<uint, Enemy> runtimeById = new Dictionary<uint, Enemy>();
    static readonly Dictionary<Enemy, bool> isRuntime = new Dictionary<Enemy, bool>();
    static readonly List<Enemy> announce = new List<Enemy>();   // area owner: new ids to tell the others about
    static uint nextRuntime;

    /// <summary>The network id of a creature (0 = a spawned one we don't know), for other syncs (PlayerHits).</summary>
    public static uint NetId(Enemy e) => e ? IdOf(e) : 0;
    public static Enemy FindById(uint id) => id == 0 ? null : Find(id);

    /// <summary>A new scene: every creature and prefab reference belongs to the old one.</summary>
    public static void SceneChanged()
    {
        runtimeIds.Clear(); runtimeById.Clear(); isRuntime.Clear(); announce.Clear();
        animators.Clear(); woken.Clear(); runnerOf.Clear(); remote.Clear();
        frozen.Clear(); frozenSince.Clear(); parts.Clear();
        prefabs = null;
    }

    static bool IsRuntime(Enemy e)
    {
        if (!isRuntime.TryGetValue(e, out bool r)) isRuntime[e] = r = SceneIds.PathOf(e.transform).Contains("(Clone)");
        return r;
    }

    /// <summary>Our id for this creature; 0 = a spawned one we have no id for (not the area owner's).</summary>
    static uint IdOf(Enemy e)
    {
        if (!IsRuntime(e)) return SceneIds.Of(e);
        if (runtimeIds.TryGetValue(e, out uint id)) return id;
        if (PuppetMode) return 0;
        id = RuntimeBit | ((uint)(Plugin.MyId & 0x7FF) << 20) | (++nextRuntime & 0xFFFFF);
        Map(e, id);
        announce.Add(e);
        return id;
    }

    static void Map(Enemy e, uint id)
    {
        if (runtimeIds.TryGetValue(e, out uint old)) runtimeById.Remove(old);   // pooled instance reused
        runtimeIds[e] = id;
        runtimeById[id] = e;
    }

    static Enemy Find(uint id)
    {
        if ((id & RuntimeBit) == 0) return SceneIds.Find<Enemy>(id);
        return runtimeById.TryGetValue(id, out var e) && e ? e : null;
    }

    static NetDataWriter SpawnMessage(Enemy e, uint id)
    {
        var w = new NetDataWriter();
        w.Put((byte)MsgType.EnemySpawn);
        w.Put(id);
        w.Put(PrefabName(e.gameObject.name));
        var p = e.transform.position;
        w.Put(p.x); w.Put(p.y); w.Put(p.z);
        w.Put(e.IsFlipped);
        return w;
    }

    /// <summary>"Mob_Tick_01(Clone)" / "Mob_Tick_01 (2)" -> "Mob_Tick_01".</summary>
    static string PrefabName(string n)
    {
        n = n.Replace("(Clone)", "").Trim();
        int sp = n.LastIndexOf(" (");
        if (sp > 0 && n.EndsWith(")")) n = n.Substring(0, sp);
        return n;
    }

    static Dictionary<string, GameObject> prefabs;

    /// <summary>The creature prefab with that name among the game's loaded assets (not scene objects).</summary>
    static GameObject FindPrefab(string name)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (prefabs == null || attempt == 1)
            {
                prefabs = new Dictionary<string, GameObject>();
                foreach (var en in Resources.FindObjectsOfTypeAll<Enemy>())
                    if (en && !en.gameObject.scene.IsValid() && !prefabs.ContainsKey(en.gameObject.name)) prefabs[en.gameObject.name] = en.gameObject;
            }
            if (prefabs.TryGetValue(name, out var p) && p) return p;
        }
        return null;
    }

    /// <summary>Area owner: spawned creatures alive here and who runs which, for a player who just arrived.</summary>
    public static void SendSnapshot(int playerId)
    {
        foreach (var e in EnemiesManager.AllEnemies)
        {
            if (!e || !e.isActiveAndEnabled || e.IsDead || !IsRuntime(e)) continue;
            Plugin.SendTo(playerId, SpawnMessage(e, IdOf(e)));
        }
        foreach (var pair in runnerOf)
        {
            if (!Present(pair.Value)) continue;
            var w = new NetDataWriter();
            w.Put((byte)MsgType.EnemyRunner);
            w.Put(pair.Key);
            w.Put(pair.Value);
            Plugin.SendTo(playerId, w);
        }
        announce.Clear();   // they're covered
    }

    /// <summary>The area owner spawned a creature; spawn the same prefab here, the way the game does (its pool).</summary>
    public static void OnSpawn(NetDataReader r)
    {
        uint id = r.GetUInt();
        string name = r.GetString();
        var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
        bool flip = r.GetBool();
        if (runtimeById.TryGetValue(id, out var have) && have && have.isActiveAndEnabled) return;
        var prefab = FindPrefab(name);
        if (!prefab) { NetLog.Warn($"Can't show spawned creature '{name}': no such prefab here"); return; }

        PoolManager.CreatePool(prefab, "EmbervilleGhost_Puppets", 1, disableFirst: true);
        var go = PoolManager.Get(prefab);
        if (!go) return;
        var e = go.GetComponent<Enemy>();
        go.transform.position = pos;
        Map(e, id);
        isRuntime[e] = true;
        e.ForceAlive();
        go.SetActive(true);
        e.IsFlipped = flip;
        // A starting state, so it isn't taken for "missing" before its first EnemyState arrives.
        if (!remote.TryGetValue(id, out var s)) remote[id] = s = new Remote();
        s.LastHeard = Time.unscaledTime;
        s.Flip = flip;
        if (!s.Snapshots.HasData) s.Snapshots.Add(Time.unscaledTimeAsDouble, pos, 0f, Time.unscaledTimeAsDouble);
    }

    static Animator[] AnimatorsOf(Enemy e)
    {
        if (!animators.TryGetValue(e, out var a)) animators[e] = a = AnimSync.Collect(e.gameObject);
        return a;
    }

    public static void Clear()
    {
        foreach (var e in new List<Enemy>(frozen.Keys)) Unfreeze(e);
        animators.Clear(); remote.Clear(); runnerOf.Clear(); frozenSince.Clear(); woken.Clear(); parts.Clear();
        runtimeIds.Clear(); runtimeById.Clear(); isRuntime.Clear(); announce.Clear();
        hitFrom = -1; applyingHost = false;
    }

    // ---- every frame ----

    public static void Tick()
    {
        if (!Connected) return;
        AssignRunners();
        WakeMissing();

        bool send = false;
        sendTimer += Time.unscaledDeltaTime;
        if (sendTimer >= SendInterval) { sendTimer = 0f; send = Plugin.AnyoneElseHere; }

        // Spawned since last time (area owner): tell the others what to spawn, before its first state.
        if (!PuppetMode)
        {
            foreach (var e in EnemiesManager.AllEnemies)
                if (e && e.isActiveAndEnabled && IsRuntime(e)) IdOf(e);
            if (send)
            {
                foreach (var e in announce)
                    if (e && e.isActiveAndEnabled) Plugin.SendToPlace(SpawnMessage(e, runtimeIds[e]), DeliveryMethod.ReliableOrdered);
                announce.Clear();
            }
        }

        double now = Time.unscaledTimeAsDouble;
        hideNow.Clear();
        var w = send ? new NetDataWriter() : null;
        foreach (var e in EnemiesManager.AllEnemies)
        {
            if (!e || !e.isActiveAndEnabled) continue;
            uint id = IdOf(e);
            if (id == 0) { hideNow.Add(e); continue; }   // spawned by our own spawners/waves: only the area owner's spawns count

            if (RunnerOf(id) == Plugin.MyId)
            {
                Unfreeze(e);
                if (send) SendState(w, e, id);
                continue;
            }

            Freeze(e);
            if (!remote.TryGetValue(id, out var s) || Time.unscaledTime - s.LastHeard > MissingHideSeconds)
            {
                // Its runner hasn't got this one (killed before we came, or never spawned there): hide it.
                if (Time.unscaledTime - frozenSince[e] > MissingHideSeconds && !e.IsDead) hideNow.Add(e);
                continue;
            }
            if (s.Dead && !e.IsDead) { hideNow.Add(e); continue; }   // died out of our sight
            if (e.IsDead) continue;   // our own death replay plays out (animation, corpse, fade)
            if (!s.Snapshots.Sample(now, out var pos, out _, out _)) continue;
            e.transform.position = pos;
            e.IsFlipped = s.Flip;
            if (e.fadableRenderers != null && e.fadableRenderers.Length > 0 && e.fadableRenderers[0])
            {
                var c = e.fadableRenderers[0].color;
                if (c.r != s.Tint.r || c.g != s.Tint.g || c.b != s.Tint.b) e.SetSpriteColor(s.Tint);   // keeps each sprite's alpha
            }
            ApplyParts(e, s);   // before the animators: a just-switched-on effect animator gets its state too
            AnimSync.Apply(AnimatorsOf(e), s.Anim);
        }
        foreach (var e in hideNow) if (e) e.gameObject.SetActive(false);
    }

    static void SendState(NetDataWriter w, Enemy e, uint id)
    {
        w.Reset();
        w.Put((byte)MsgType.EnemyState);
        w.Put(id);
        w.Put(Time.unscaledTimeAsDouble);
        var p = e.transform.position;
        w.Put(p.x); w.Put(p.y); w.Put(p.z);
        w.Put(e.IsFlipped);
        w.Put(e.IsDead);
        w.Put(e.CurrentHealth);
        // Tint: the AI recolours creatures (behaviour graph "Tint Enemy" -> SetSpriteColor), and the AI only runs here.
        var tint = e.fadableRenderers != null && e.fadableRenderers.Length > 0 && e.fadableRenderers[0] ? e.fadableRenderers[0].color : Color.white;
        w.Put(tint.r); w.Put(tint.g); w.Put(tint.b);
        // What the AI switches on and off on the creature itself: the "!" when it spots someone, shields, auras,
        // charge-up glows, extra effect animators... = its child objects' on/off and its sprites' visibility.
        var parts = PartsOf(e);
        PutBits(w, parts.nodes.Length, i => parts.nodes[i] && parts.nodes[i].gameObject.activeSelf);
        PutBits(w, parts.sprites.Length, i => parts.sprites[i] && parts.sprites[i].enabled);
        AnimSync.Write(w, AnimatorsOf(e));
        Plugin.SendToPlace(w, DeliveryMethod.Unreliable);
    }

    // ---- the creature's parts (child objects, sprites), mirrored as bits ----

    class Parts { public Transform[] nodes; public SpriteRenderer[] sprites; }
    static readonly Dictionary<Enemy, Parts> parts = new Dictionary<Enemy, Parts>();

    /// <summary>Same order in every copy (same prefab): every child object (not the root) and every sprite.</summary>
    static Parts PartsOf(Enemy e)
    {
        if (parts.TryGetValue(e, out var p)) return p;
        var all = e.GetComponentsInChildren<Transform>(true);
        var nodes = new Transform[all.Length - 1];
        System.Array.Copy(all, 1, nodes, 0, nodes.Length);   // [0] is the root
        parts[e] = p = new Parts { nodes = nodes, sprites = e.GetComponentsInChildren<SpriteRenderer>(true) };
        return p;
    }

    static void PutBits(NetDataWriter w, int count, System.Func<int, bool> on)
    {
        w.Put((ushort)count);
        byte b = 0;
        for (int i = 0; i < count; i++)
        {
            if (on(i)) b |= (byte)(1 << (i & 7));
            if ((i & 7) == 7) { w.Put(b); b = 0; }
        }
        if ((count & 7) != 0) w.Put(b);
    }

    static bool[] GetBits(NetDataReader r)
    {
        var bits = new bool[r.GetUShort()];
        byte b = 0;
        for (int i = 0; i < bits.Length; i++)
        {
            if ((i & 7) == 0) b = r.GetByte();
            bits[i] = (b & (1 << (i & 7))) != 0;
        }
        return bits;
    }

    /// <summary>Puppet: switch its parts like the runner's. Skipped if this copy's creature has a different set of parts.</summary>
    static void ApplyParts(Enemy e, Remote s)
    {
        var p = PartsOf(e);
        if (s.Nodes != null && s.Nodes.Length == p.nodes.Length)
            for (int i = 0; i < p.nodes.Length; i++)
                if (p.nodes[i] && p.nodes[i].gameObject.activeSelf != s.Nodes[i]) p.nodes[i].gameObject.SetActive(s.Nodes[i]);
        if (s.Sprites != null && s.Sprites.Length == p.sprites.Length)
            for (int i = 0; i < p.sprites.Length; i++)
                if (p.sprites[i] && p.sprites[i].enabled != s.Sprites[i]) p.sprites[i].enabled = s.Sprites[i];
    }

    public static void OnState(NetDataReader r)
    {
        uint id = r.GetUInt();
        double t = r.GetDouble();
        var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
        bool flip = r.GetBool();
        bool dead = r.GetBool();
        float health = r.GetFloat();
        var tint = new Color(r.GetFloat(), r.GetFloat(), r.GetFloat(), 1f);
        var nodes = GetBits(r);
        var sprites = GetBits(r);
        var anim = AnimSync.Read(r);
        if (RunnerOf(id) == Plugin.MyId) return;   // stale: we run it now

        if (!remote.TryGetValue(id, out var s)) remote[id] = s = new Remote();
        s.Snapshots.Add(t, pos, 0f, Time.unscaledTimeAsDouble);
        s.Flip = flip; s.Dead = dead; s.Health = health; s.Anim = anim; s.Tint = tint;
        s.Nodes = nodes; s.Sprites = sprites;
        s.LastHeard = Time.unscaledTime;
    }

    static readonly HashSet<uint> woken = new HashSet<uint>();
    static readonly List<Enemy> hideNow = new List<Enemy>();

    /// <summary>
    /// A creature that is switched off here but alive where its runner is, e.g. one that a cutscene or event switched
    /// on there (Mob_Acolyte_Sorcerer_01 under "== Cutscene HummingSphere"). Switch it on. If its group never started
    /// here, take it out of that group first, so we don't start the cutscene/event itself. Once per creature.
    /// </summary>
    static void WakeMissing()
    {
        foreach (var pair in remote)
        {
            var s = pair.Value;
            if (s.Dead || Time.unscaledTime - s.LastHeard > 1f || woken.Contains(pair.Key)) continue;
            var e = Find(pair.Key);
            if (!e || e.gameObject.activeInHierarchy) continue;
            woken.Add(pair.Key);
            var parent = e.transform.parent;
            if (parent && !parent.gameObject.activeInHierarchy) e.transform.SetParent(null, true);
            e.isDead = false;
            e.gameObject.SetActive(true);
            NetLog.Info($"Showing {e.name}: it's out where {Plugin.NameOf(RunnerOf(pair.Key))} is");
        }
    }

    /// <summary>AI and physics off: someone else runs this one.</summary>
    static void Freeze(Enemy e)
    {
        if (frozen.ContainsKey(e)) return;
        var rb = e.GetComponent<Rigidbody2D>();
        frozen[e] = rb ? rb.bodyType : RigidbodyType2D.Dynamic;
        frozenSince[e] = Time.unscaledTime;
        if (rb) { rb.linearVelocity = Vector2.zero; rb.bodyType = RigidbodyType2D.Kinematic; }
        e.behaviorGraphAgent.enabled = false;
        e.navMeshAgent?.Disable();
        e.CancelAttack();
    }

    /// <summary>Back to the game's own AI: we run this one now (handed to us, or we're not connected any more).</summary>
    static void Unfreeze(Enemy e)
    {
        if (!frozen.TryGetValue(e, out var body)) return;
        frozen.Remove(e);
        frozenSince.Remove(e);
        if (!e) return;
        var rb = e.GetComponent<Rigidbody2D>();
        if (rb) rb.bodyType = body;
        if (!e.isActiveAndEnabled || e.IsDead) return;
        // ChangeState ignores the state it's already in, and a frozen creature usually sits in its run-behaviour state
        // with only the graph switched off: then RestartBehavior does nothing and it would stand idle forever.
        if (e.currentState == e.runBehaviorState) e.RestartGraph();
        else e.RestartBehavior();
    }

    // ---- hits ----

    /// <summary>Runner: another player hit one of our creatures; apply it for real.</summary>
    public static void OnHit(NetDataReader r, int fromId)
    {
        uint id = r.GetUInt();
        int amount = r.GetInt();
        var result = (DamageResult)r.GetByte();
        var hitType = (DamageHitType)r.GetByte();
        var knock = (KnockBack)r.GetByte();
        bool noStagger = r.GetBool();
        var e = Find(id);
        if (!e || !e.isActiveAndEnabled || e.IsDead || RunnerOf(id) != Plugin.MyId) return;

        var d = new DamageData(RemoteHitter.For(Plugin, fromId), e, amount, hitType, knock, result: result, noStagger: noStagger);
        ApplyFor(fromId, () => e.ApplyDamage(d));
    }

    /// <summary>The runner's result of a hit; replay it through the game's code.</summary>
    public static void OnDamaged(NetDataReader r)
    {
        uint id = r.GetUInt();
        int amount = r.GetInt();
        var result = (DamageResult)r.GetByte();
        var hitType = (DamageHitType)r.GetByte();
        var knock = (KnockBack)r.GetByte();
        bool noStagger = r.GetBool();
        float healthBefore = r.GetFloat();
        int hitter = r.GetInt();
        var e = Find(id);
        if (!e || !e.isActiveAndEnabled || e.IsDead || e.Group == null || RunnerOf(id) == Plugin.MyId) return;

        // Our own hit: our player is the aggressor (the game gives us the XP). Someone else's: their stand-in (knockback only).
        var d = new DamageData(RemoteHitter.For(Plugin, hitter), e, amount, hitType, knock, result: result, noStagger: noStagger);
        e.currentHealth = healthBefore;
        applyingHost = true;
        replayHitter = hitter;
        try { e.ApplyDamage(d); }
        finally { applyingHost = false; replayHitter = -1; }
    }

    /// <summary>
    /// Shared XP: a teammate killed this creature where we are, so we get the kill's XP too, the way the game gives it
    /// to a killer (Enemy.ApplyDamage: expRange -> PlayerMaster.GainXP -> class, weapon and armour masteries).
    /// The killer's own copy already got theirs from the game. Kills by nobody (hazards...) give nobody XP, like the game.
    /// </summary>
    static void ShareKillXp(Enemy e, int killer)
    {
        if (!Session.SharedXp || killer < 0 || killer == Plugin.MyId) return;   // the host's rule
        var me = LocalPlayer.Get();
        if (!me || me.IsDead) return;
        int xp = e.expRange.Random();
        if (xp <= 0) return;
        xp = me.GainXP(xp);
        Bus<UIDropXPEvent>.Invoke(new UIDropXPEvent(e.transform.position, e.transform.position - me.transform.position, xp));
    }

    /// <summary>Runner: which player dealt this hit (the one whose request we're applying, else us if it was our player, -1 = not a player).</summary>
    public static int HitterId(DamageData d)
    {
        if (hitFrom >= 0) return hitFrom;
        var me = LocalPlayer.Get();
        return me && ReferenceEquals(d.Aggressor, me) ? Plugin.MyId : -1;
    }

    /// <summary>Apply another player's hit with them as the hitter.</summary>
    public static void ApplyFor(int playerId, System.Action apply)
    {
        hitFrom = playerId;
        try { apply(); }
        finally { hitFrom = -1; }
    }

    // ---- patches ----

    /// <summary>Never let the AI start on a creature someone else runs (its states call RestartGraph on their own).</summary>
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.RestartGraph)), HarmonyPrefix]
    static bool NoAiUnlessOurs(Enemy __instance) => Runs(__instance);

    struct Before { public float Health; public bool Dead; }

    /// <summary>
    /// A creature someone else runs: our player's hits become requests to them (every other source is theirs to run).
    /// Our own creature: remember health, so the postfix can broadcast what happened.
    /// </summary>
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.ApplyDamage)), HarmonyPrefix]
    static bool BeforeDamage(Enemy __instance, DamageData damageData, out Before __state)
    {
        __state = new Before { Health = __instance.CurrentHealth, Dead = __instance.IsDead };
        if (!Connected || applyingHost || Runs(__instance)) return true;
        if (__instance.IsDead) return false;

        uint id = IdOf(__instance);
        var me = LocalPlayer.Get();
        if (id != 0 && me && ReferenceEquals(damageData.Aggressor, me))
        {
            var w = new NetDataWriter();
            w.Put((byte)MsgType.EnemyHit);
            w.Put(id);
            w.Put(damageData.Amount);
            w.Put((byte)damageData.Result);
            w.Put((byte)damageData.HitType);
            w.Put((byte)damageData.KnockBackForce);
            w.Put(damageData.NoStagger);
            Plugin.SendTo(RunnerOf(id), w);
        }
        return false;
    }

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.ApplyDamage)), HarmonyPostfix]
    static void AfterDamage(Enemy __instance, DamageData damageData, Before __state)
    {
        if (!Connected || __state.Dead) return;
        bool ours = !applyingHost && Runs(__instance);
        if (__instance.IsDead) ShareKillXp(__instance, ours ? HitterId(damageData) : replayHitter);
        if (!ours || !Plugin.AnyoneElseHere) return;

        var w = new NetDataWriter();
        w.Put((byte)MsgType.EnemyDamaged);
        w.Put(IdOf(__instance));
        w.Put(damageData.Amount);
        w.Put((byte)damageData.Result);
        w.Put((byte)damageData.HitType);
        w.Put((byte)damageData.KnockBackForce);
        w.Put(damageData.NoStagger);
        w.Put(__state.Health);
        w.Put(HitterId(damageData));
        Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
    }
}
