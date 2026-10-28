using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace EmbervilleGhost;

/// <summary>
/// A creature of ours (Monsters), on the game's Enemy (its health, hits, knockback, stun, death, XP, loot stay the
/// game's): its mind and its frames. The game's behaviour graph and animator are off.
/// - Wander: near where it appeared, slowly, now and then a new spot.
/// - Chase: you within Sight (and it no further than Leash from home): straight at you, fast.
/// - Wind up: close enough: it stops, faces you, rears back (the attack row's first frames, or its own windup, held
///   as long as it says: the Troll's second, so you see it coming). The game's attack starts.
/// - Strike: a short dash at where you were (a lunger), or where it stands; the attack lands on its frame (the game's
///   own: dodge, block, damage), and with it a bleed if the creature has one (the Giant Bat: 2 a second for 5
///   seconds), or a knock back and dizziness (the Paddle Shark, the Troll: 3 units, 2 seconds), or a web (the Spider:
///   the game's cocoon, at most every 8 seconds and never on top of one), or a breath of fire (the Imp: a second of
///   flame hitting every 0.3, each hit burning you).
/// - Recover: the rest of the attack's frames, a moment, then again; one with its own recovery (the Troll) stands
///   there tired through all of it: open to your hits.
/// - Block (one with a guard stance: the Minotaur, the Panda): hit, it guards (no damage) a moment, then hits
///   back at once; it can't block again for a few seconds: your window.
/// - Throw (one with a bomb: the Bomb Fish): it keeps 2.5-5 away and lobs a bomb at where you are (Bomb).
/// - Shoot (one with a shot: the Gnoll's bones, the Harpoon Shark's harpoon, the Hex Shaman's hex, the Slingshot
///   Gnome's acorns): it keeps its distance too, and its shot flies straight at you (Shot).
/// - Guard (a creature with guard frames: the Turtle): hit, it goes into its shell (once the hit has played) and takes
///   no damage there; it comes out once you're half a screen away, then as before.
/// While the game has it (hit, knocked back, stunned) it waits; dying, it drops and squashes flat (or plays its own
/// death), then fades out.
/// </summary>
internal sealed class Monster : MonoBehaviour
{
    public Monsters.Kind Kind;
    public bool Hunts;   // (knows where you are, however far: the arena's waves)

    const float Sight = 6f, Leash = 12f;
    const float SquashX = 1.3f, SquashY = 0.45f, FadeTime = 1.5f;   // (dead: how flat it goes, how long it takes to fade)
    const float WindUp = 0.45f, DashTime = 0.22f, Recover = 0.7f, Cooldown = 1.1f, Drop = 0.5f;

    // how close it strikes from, and how far its strike reaches: a lunger dives in (and hits close), one that strikes
    // where it stands (the Turtle's neck) comes closer and reaches further; or its own (the Troll's club)
    float Reach => Kind.ThrowRange > 0f ? Kind.ThrowRange : Kind.Reach > 0f ? Kind.Reach : Kind.Lunge > 0f ? 1.7f : 1.1f;
    float Radius => Kind.Radius > 0f ? Kind.Radius : Kind.Lunge > 0f ? 0.9f : 1.4f;

    // its attack in parts: its own windup / recovery rows (the Troll), or cut from the attack row
    bool OwnRows => Kind.Windup != null;
    float WindTime => OwnRows ? (Kind.WindupTime > 0f ? Kind.WindupTime : Length(Kind.Windup)) : WindUp;
    float StrikeTime => Kind.AttackTime > 0f ? Kind.AttackTime : OwnRows ? Length(Kind.Attack) : DashTime;
    float HitAt => OwnRows ? Kind.HitFrame / Mathf.Max(1f, Kind.Attack.Fps) : DashTime * 0.5f;
    float RestTime => Kind.Recovery != null ? Length(Kind.Recovery) : Recover;

    enum Mind { Wander, Chase, WindUp, Sting, Recover, GuardIn, Guard, GuardOut, Block }

    Enemy e;
    NavMeshAgent agent;
    SpriteRenderer body, sr, shadow;
    Transform look;
    Vector3 lookAt;
    readonly List<Renderer> hidden = new();
    Attack sting;
    Mind mind;
    float since, nextWander, readyAt, deadAt = -1f, t;
    bool hit, deathSaid, struck;
    Vector3 home, dashDir;
    Monsters.Anim playing;
    float pathSaidAt, nextHit;

    /// <summary>In its shell (going in, in it, coming out): no damage (Monsters.Shell).</summary>
    public bool Guarding => mind == Mind.GuardIn || mind == Mind.Guard || mind == Mind.GuardOut || mind == Mind.Block;

    /// <summary>A hit landed on it (Monsters.Shell): one that can guard goes into its shell once the hit has played.</summary>
    public void Struck()
    {
        if (Kind == null) return;
        if (Kind.GuardIn != null || Kind.Guard != null && Kind.BlockFor > 0f && Time.time >= blockReady) struck = true;
    }

    float blockReady;

    /// <summary>How far you must be for it to come out of its shell: half the screen's width.</summary>
    static float HalfScreen
    {
        get
        {
            var cam = Camera.main;
            return cam && cam.orthographic ? cam.orthographicSize * cam.aspect : 9f;
        }
    }

    void Start()
    {
        e = GetComponent<Enemy>();
        e.DisableBehaviorGraph();
        if (e.animator) e.animator.enabled = false;   // (its frames are ours; its animation events too)
        agent = e.navMeshAgent ? e.navMeshAgent.agent : GetComponent<NavMeshAgent>();
        home = transform.position;
        // the strike: the creature's own first attack (its damage, sound), a close Area hit instead of what it was
        var baseAttack = e.attacks?.Select(a => a.Attack).FirstOrDefault(a => a);
        sting = baseAttack ? Instantiate(baseAttack) : ScriptableObject.CreateInstance<Attack>();
        sting.name = "EG_" + Kind.Name + "_Sting";
        sting.attackHit = AttackHit.Area;
        sting.radius = Radius * Scale;
        sting.projectile = null;
        sting.attackSpawner = null;
        sting.buildupVFX = null;
        sting.hitboxDown = sting.hitboxRight = sting.hitboxUp = null;
        // what its strike leaves on you: a bleed (the game's: damage each second), dizziness (the game's stun: no
        // moving, attacking, dodging), or nothing
        sting.effects = new List<DamageEffect>();
        if (Kind.Poison > 0) sting.effects.Add(new DamageEffect { Effect = EffectType.Poison, Chance = 100f, DamagePerSec = Kind.Poison, Duration = Kind.PoisonFor });
        if (Kind.Burn > 0) sting.effects.Add(new DamageEffect { Effect = EffectType.Burn, Chance = 100f, DamagePerSec = Kind.Burn, Duration = Kind.BurnFor });
        if (Kind.Bleed > 0) sting.effects.Add(new DamageEffect { Effect = EffectType.Bleed, Chance = 100f, DamagePerSec = Kind.Bleed, Duration = Kind.BleedFor });
        if (Kind.Stun > 0f) sting.effects.Add(new DamageEffect { Effect = EffectType.Stun, Chance = 100f, Duration = Kind.Stun });
        // its looks: the creature's body hidden, ours where it was
        var all = GetComponentsInChildren<SpriteRenderer>(true).Where(r => !r.name.ToLowerInvariant().Contains("shadow") && r != e.attackHitboxRenderer).ToList();
        body = all.Where(r => r.sprite).OrderByDescending(r => r.sprite.bounds.size.x * r.sprite.bounds.size.y).FirstOrDefault();
        if (!body) { NetLog.Warn($"Monsters: {name} has no body to dress", toast: false); Destroy(this); return; }
        hidden.AddRange(all);
        shadow = GetComponentsInChildren<SpriteRenderer>(true).FirstOrDefault(r => r.name.ToLowerInvariant().Contains("shadow"));   // (a walker stands over it)
        foreach (var b in GetComponentsInChildren<Behaviour>(true)) if (b.GetType().Name == "Light2D") b.enabled = false;   // (a bee doesn't glow)
        var go = new GameObject("EG_Look");
        go.transform.SetParent(body.transform, false);
        look = go.transform;
        lookAt = body.sprite.bounds.center;   // (where its body was drawn: as high as it flew)
        look.localPosition = lookAt;
        sr = go.AddComponent<SpriteRenderer>();
        NetLog.Info($"Monsters: {name} awake (agent {(agent ? "yes" : "no")}, sting from {(baseAttack ? baseAttack.name : "nothing")}, {Scale:0.##}x; hid {string.Join(", ", hidden.Select(h => h.name))}; shadow {(shadow ? shadow.name : "none")})", toast: false);
    }

    /// <summary>How much bigger the creature underneath is made (a boss: MiniBoss), for its reach.</summary>
    float Scale => Mathf.Abs(transform.lossyScale.x) > 0.01f ? Mathf.Abs(transform.lossyScale.x) : 1f;

    /// <summary>The game's to move: hit, knocked back, stunned, dying.</summary>
    bool Busy => e.currentState != e.runBehaviorState && e.currentState != e.idleState;

    void Update()
    {
        if (!e || !agent || !sr) return;
        if (e.IsDead) { Die(); return; }
        if (Busy)
        {
            if (mind == Mind.WindUp || mind == Mind.Sting) { e.CancelAttack(); mind = Mind.Recover; since = 0f; readyAt = Time.time + Cooldown; }   // (a hit stops a strike)
            return;
        }
        since += Time.deltaTime;
        var me = LocalPlayer.Get();
        bool alive = me && !PlayerStatusUtil.LocalBusy;
        Vector3 to = alive ? me.transform.position - transform.position : Vector3.zero;
        bool sees = alive && (Hunts || to.magnitude < Sight * Mathf.Max(1f, Kind.Size) && (transform.position - home).magnitude < Leash);
        if (struck)
        {
            struck = false;
            if (!Guarding)
            {
                if (e.IsAttacking) e.CancelAttack();
                Stop();
                mind = Kind.GuardIn != null ? Mind.GuardIn : Mind.Block; since = 0f;
            }
        }
        switch (mind)
        {
            case Mind.Wander:
                agent.speed = Kind.WanderSpeed;
                if (sees) { mind = Mind.Chase; since = 0f; break; }
                if (Time.time >= nextWander)
                {
                    nextWander = Time.time + Random.Range(2f, 4.5f);
                    var spot = home + (Vector3)(Random.insideUnitCircle * 3f);
                    if (NavMesh.SamplePosition(spot, out var h, 1.5f, NavMesh.AllAreas)) Go(h.position);
                }
                Face(agent.velocity);
                break;
            case Mind.Chase:
                agent.speed = Kind.ChaseSpeed;
                if (!sees) { mind = Mind.Wander; nextWander = Time.time + 1f; Go(home); break; }
                // a thrower keeps its distance: too close, it backs off
                if (Kind.ThrowRange > 0f && to.magnitude < Kind.KeepAway) Go(transform.position - to.normalized * 2.5f);
                else Go(me.transform.position);
                Face(to);
                // (a path cut short: you somewhere it can't walk, the stairs say; told once a few seconds)
                if (agent.hasPath && agent.pathStatus != NavMeshPathStatus.PathComplete && Time.time >= pathSaidAt)
                {
                    pathSaidAt = Time.time + 5f;
                    NetLog.Info($"Monsters: {name} can't reach you ({agent.pathStatus}; it at {transform.position.x:0.0}, {transform.position.y:0.0}, you at {me.transform.position.x:0.0}, {me.transform.position.y:0.0}; its areas {agent.areaMask}, type {agent.agentTypeID})", toast: false);
                }
                if (to.magnitude <= Reach && Time.time >= readyAt)
                {
                    Stop();
                    e.PerformAttack(sting, me.gameObject);   // (the game's attack begins: it's attacking now)
                    mind = Mind.WindUp; since = 0f;
                }
                break;
            case Mind.WindUp:
                if (alive) Face(to);
                if (since >= WindTime)
                {
                    dashDir = alive && to.sqrMagnitude > 0.01f ? to.normalized : e.CurrentDirection.normalized;
                    mind = Mind.Sting; since = 0f; hit = false;
                }
                break;
            case Mind.Sting:
                if (agent.isOnNavMesh && Kind.Lunge > 0f) { agent.isStopped = false; agent.velocity = dashDir * Kind.Lunge; }
                if (!hit && since >= HitAt) { hit = true; nextHit = since + Kind.HitEvery; Strike(me); }   // (the strike lands)
                else if (hit && Kind.HitEvery > 0f && since >= nextHit && since < StrikeTime) { nextHit += Kind.HitEvery; Strike(me); }   // (and again: a breath)
                if (since >= StrikeTime)
                {
                    Stop();
                    e.AttackEnd();
                    mind = Mind.Recover; since = 0f;
                    readyAt = Time.time + Cooldown;
                }
                break;
            case Mind.Recover:   // (one with its own recovery stands there tired)
                if (Kind.Recovery != null) Stop();
                if (since >= RestTime) { mind = sees ? Mind.Chase : Mind.Wander; since = 0f; }
                break;
            case Mind.GuardIn:   // (into its shell)
                Stop();
                if (since >= Length(Kind.GuardIn)) { mind = Mind.Guard; since = 0f; }
                break;
            case Mind.Guard:   // (in it, until you're half a screen away)
                Stop();
                if (!alive || to.magnitude > HalfScreen) { mind = Mind.GuardOut; since = 0f; }
                break;
            case Mind.Block:   // (guarding, then straight back at you: the counter)
                Stop();
                if (alive) Face(to);
                if (since >= Kind.BlockFor)
                {
                    blockReady = Time.time + Kind.BlockEvery;
                    readyAt = 0f;
                    if (alive && to.magnitude <= Reach * 1.3f) { e.PerformAttack(sting, me.gameObject); mind = Mind.WindUp; }
                    else mind = sees ? Mind.Chase : Mind.Wander;
                    since = 0f;
                }
                break;
            case Mind.GuardOut:   // (back out: then as it was)
                if (since >= Length(Kind.GuardOut)) { mind = sees ? Mind.Chase : Mind.Wander; since = 0f; nextWander = 0f; }
                break;
        }
    }

    /// <summary>The game's attack lands (its damage, its effects); if it hit you (hurt or stunned), its knock back.</summary>
    void Strike(PlayerMaster me)
    {
        // a web (the Spider's): only when you're not in one already (another would start your wriggling over), and
        // not too often
        sting.effects.RemoveAll(x => x.Effect == EffectType.Trapped);
        if (Kind.Trap > 0f && me && me.currentState != me.stateTrapped && Time.time >= trapReady)
        {
            sting.effects.Add(new DamageEffect { Effect = EffectType.Trapped, Chance = 100f });
            trapReady = Time.time + Kind.Trap;
        }
        // a thrower: its bomb, at where you are now (it does the damage when it goes off)
        if (Kind.BombSpin != null)
        {
            var from = look.position + new Vector3(0f, 0.3f, 0f);
            var target = me ? me.transform.position : transform.position + e.CurrentDirection.normalized * Kind.ThrowRange;
            Bomb.Throw(this, from, target);
            return;
        }
        // a shooter: its shot, straight at you (it does the damage where it lands)
        if (Kind.Shot != null)
        {
            var from = look.position;
            var dir = me ? me.transform.position - transform.position : e.CurrentDirection;
            dir.z = 0f;
            Shot.Fire(this, sting, from, dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector3.right);
            return;
        }
        int before = me ? me.currentHealth : 0;
        e.PerformAttackHit();
        if (!me || Kind.Knock <= 0f || !(me.IsStunned || me.currentHealth < before)) return;
        var away = me.transform.position - transform.position; away.z = 0f;
        pushDir = away.sqrMagnitude > 0.0001f ? away.normalized : (Vector3)(e.CurrentDirection.normalized);
        pushLeft = Kind.Knock;
        // not off the walkable ground (into water, past a fence): stopped short of where it ends
        var start = me.transform.position;
        if (NavMesh.SamplePosition(start, out var on, 1f, NavMesh.AllAreas) && NavMesh.Raycast(on.position, on.position + pushDir * Kind.Knock, out var edge, NavMesh.AllAreas))
            pushLeft = Mathf.Max(0f, Vector2.Distance(on.position, edge.position) - 0.3f);
    }

    float trapReady;
    Vector3 pushDir;
    float pushLeft;
    const float PushTime = 0.25f;
    static readonly RaycastHit2D[] castHits = new RaycastHit2D[8];

    /// <summary>The knock back: you pushed that far over a moment, in the physics steps; never through a fence or a
    /// wall (it stops at them), never off the walkable ground (Strike shortens it).</summary>
    void FixedUpdate()
    {
        if (pushLeft <= 0f) return;
        var me = LocalPlayer.Get();
        if (!me || !me.rb) { pushLeft = 0f; return; }
        float step = Mathf.Min(pushLeft, Kind.Knock / PushTime * Time.fixedDeltaTime);
        // anything solid in the way (a fence, a wall): up to it, and no further
        int n = me.rb.Cast(pushDir, castHits, step + 0.05f);
        for (int i = 0; i < n; i++)
            if (castHits[i].collider && !castHits[i].collider.isTrigger) { step = Mathf.Max(0f, castHits[i].distance - 0.05f); pushLeft = 0f; break; }
        me.rb.MovePosition(me.transform.position + pushDir * step);
        pushLeft = pushLeft <= 0f ? 0f : pushLeft - step;
    }

    /// <summary>An animation's time, played once (none: a moment).</summary>
    static float Length(Monsters.Anim a) => a == null || a.Fps <= 0f ? 0.3f : a.Frames.Length / a.Fps;

    void Go(Vector3 at)
    {
        if (!agent.isOnNavMesh) return;
        agent.isStopped = false;
        agent.SetDestination(at);
    }

    void Stop()
    {
        if (!agent.isOnNavMesh) return;
        agent.velocity = Vector3.zero;
        agent.ResetPath();
        agent.isStopped = true;
    }

    void Face(Vector3 dir)
    {
        if (dir.sqrMagnitude > 0.01f) e.CurrentDirection = dir;   // (the game flips it: ours with it)
    }

    /// <summary>The game waits for its death animation to end: its own (the Troll's), or a short drop.</summary>
    void Die()
    {
        if (deadAt < 0f) deadAt = Time.time;
        float wait = Kind.Dead != null ? Length(Kind.Dead) : Drop;
        if (!deathSaid && Time.time - deadAt > wait) { deathSaid = true; e.AnimationEndEvent(); }
    }

    void LateUpdate()
    {
        if (!sr || !body) return;
        foreach (var h in hidden) if (h) h.enabled = false;
        // its own size, whatever the creature underneath was made (a boss is made bigger for its body and reach)
        float parent = Mathf.Abs(look.parent.lossyScale.x) > 0.01f ? Mathf.Abs(look.parent.lossyScale.x) : 1f;
        look.localScale = Vector3.one * (Kind.Size / parent);
        if (e.IsDead)
        {
            float k = deadAt < 0f ? 0f : Time.time - deadAt;
            if (Kind.Dead != null)   // (its own fall)
            {
                look.localRotation = Quaternion.identity;
                if (Kind.Flies) look.localPosition = lookAt; else OnGround();
                Show(Kind.Dead, (int)(k * Kind.Dead.Fps));
            }
            else
            {
                // falling to the ground (a flyer), then squashed flat where it lies (wider, its feet on the ground)
                k = Mathf.Clamp01(k / Drop);
                look.localRotation = Quaternion.identity;
                float flat = Mathf.Lerp(1f, SquashY, k);
                look.localScale = new Vector3(look.localScale.x * Mathf.Lerp(1f, SquashX, k), look.localScale.y * flat, 1f);
                if (Kind.Flies) look.localPosition = Vector3.Lerp(lookAt, new Vector3(lookAt.x, 0.15f, lookAt.z), k);
                else OnGround(flat);
                Show(Kind.Idle, 0);
            }
        }
        else
        {
            look.localRotation = Quaternion.identity;
            if (Kind.Flies) look.localPosition = lookAt + new Vector3(0f, Mathf.Sin(Time.time * 5f) * 0.05f, 0f);   // (a hover)
            else OnGround();
            int n = Kind.Attack.Frames.Length, rest = n - Kind.WindFrames - Kind.StrikeFrames;
            if (Kind.Hit != null && e.currentState == e.hitFreezeState) Show(Kind.Hit, (int)(Time.time * Kind.Hit.Fps) % Kind.Hit.Frames.Length);
            else if (mind == Mind.Block) Loop(Kind.Guard);
            else if (mind == Mind.GuardIn) Show(Kind.GuardIn, (int)(since * Kind.GuardIn.Fps));
            else if (mind == Mind.Guard) Show(Kind.GuardIn, Kind.GuardIn.Frames.Length - 1);
            else if (mind == Mind.GuardOut) Show(Kind.GuardOut ?? Kind.GuardIn, Kind.GuardOut != null ? (int)(since * Kind.GuardOut.Fps) : 0);
            else if (OwnRows && mind == Mind.WindUp) Show(Kind.Windup, (int)(since / WindTime * Kind.Windup.Frames.Length));
            else if (OwnRows && mind == Mind.Sting) Show(Kind.Attack, Kind.AttackTime > 0f ? (int)(since * Kind.Attack.Fps) % Kind.Attack.Frames.Length : (int)(since * Kind.Attack.Fps));
            else if (mind == Mind.Recover && Kind.Recovery != null) Show(Kind.Recovery, (int)(since * Kind.Recovery.Fps));
            else if (mind == Mind.WindUp) Show(Kind.Attack, (int)(since / WindUp * Kind.WindFrames));
            else if (mind == Mind.Sting) Show(Kind.Attack, Kind.WindFrames + Mathf.Min(Kind.StrikeFrames - 1, (int)(since / DashTime * Kind.StrikeFrames)));
            else if (mind == Mind.Recover && rest > 0 && since < Recover * 0.6f) Show(Kind.Attack, Kind.WindFrames + Kind.StrikeFrames + Mathf.Min(rest - 1, (int)(since / (Recover * 0.6f) * rest)));
            else Loop(agent && agent.velocity.sqrMagnitude > 0.05f ? Kind.Move : Kind.Idle);
        }
        sr.flipX = body.flipX;
        var c = body.color;   // (its fades: spawning, the corpse going)
        // dead: once it's down (its own fall played, or squashed), it fades out
        if (e.IsDead && deadAt >= 0f)
        {
            float gone = Time.time - deadAt - (Kind.Dead != null ? Length(Kind.Dead) : Drop);
            c.a *= 1f - Mathf.Clamp01(gone / FadeTime);
            c *= new Color(0.8f, 0.8f, 0.8f, 1f);   // (a little darker: no longer a threat)
        }
        if (e.currentState == e.hitFreezeState) c *= new Color(1f, 0.55f, 0.55f, 1f);   // (hit: a red flash)
        sr.color = c;
        sr.sortingLayerID = body.sortingLayerID;
        sr.sortingOrder = body.sortingOrder;
    }

    /// <summary>A walker: its feet where the creature underneath stands (its own body was drawn higher).</summary>
    void OnGround(float flat = 1f)
    {
        float x = shadow && shadow.enabled ? shadow.bounds.center.x : transform.position.x;   // (over its shadow)
        look.position = new Vector3(x, transform.position.y + Kind.Feet * Kind.Size * flat, look.position.z);   // (squashed: still on its feet)
    }

    void Show(Monsters.Anim a, int frame)
    {
        playing = a;
        sr.sprite = a.Frames[Mathf.Clamp(frame, 0, a.Frames.Length - 1)];
    }

    void Loop(Monsters.Anim a)
    {
        if (a != playing) { playing = a; t = 0f; }
        t += Time.deltaTime * a.Fps;
        sr.sprite = a.Frames[(int)t % a.Frames.Length];
    }
}

/// <summary>
/// A thrower's bomb (the Bomb Fish's): it spins through the air in an arc to where you were, lands with its fuse lit
/// (your moment to get away), then blows up: the thrower's own attack (the game's damage, dodge, block) on everything
/// within its blast, then its explosion plays out. It goes off even if the thrower is dead by then.
/// </summary>
internal sealed class Bomb : MonoBehaviour
{
    const float Flight = 0.8f, Arc = 1.6f;

    Monsters.Kind kind;
    Enemy owner;
    Attack attack;
    Vector3 from, to;
    SpriteRenderer sr;
    float since;
    bool blown;

    public static void Throw(Monster thrower, Vector3 from, Vector3 to)
    {
        var e = thrower.GetComponent<Enemy>();
        var go = new GameObject($"EG_{thrower.Kind.Name}_Bomb");
        go.transform.position = from;
        var b = go.AddComponent<Bomb>();
        b.kind = thrower.Kind;
        b.owner = e;
        b.from = from;
        b.to = new Vector3(to.x, to.y, from.z);
        var baseAttack = e.attacks?.Select(a => a.Attack).FirstOrDefault(a => a);
        b.attack = baseAttack ? Object.Instantiate(baseAttack) : ScriptableObject.CreateInstance<Attack>();
        b.attack.attackHit = AttackHit.Area;
        b.attack.projectile = null;
        b.attack.effects = new List<DamageEffect>();
        b.sr = go.AddComponent<SpriteRenderer>();
        var like = thrower.GetComponentsInChildren<SpriteRenderer>().FirstOrDefault(r => r.name == "EG_Look");
        if (like) { b.sr.sortingLayerID = like.sortingLayerID; b.sr.sortingOrder = like.sortingOrder + 1; }
    }

    void Update()
    {
        since += Time.deltaTime;
        if (since < Flight)   // (in the air: an arc, spinning)
        {
            float k = since / Flight;
            transform.position = Vector3.Lerp(from, to, k) + new Vector3(0f, Arc * 4f * k * (1f - k), 0f);
            Frame(kind.BombSpin, since);
        }
        else if (since < Flight + kind.Fuse)   // (on the ground, its fuse burning)
        {
            transform.position = to;
            Frame(kind.BombFuse ?? kind.BombSpin, since - Flight);
        }
        else
        {
            if (!blown) { blown = true; Blow(); gameFx = GameBlast(transform.position); }
            float t = since - Flight - kind.Fuse;
            if (gameFx) { sr.enabled = false; if (t >= 1f) Destroy(gameObject); return; }   // (the game's own blast and smoke play it out)
            if (kind.Boom == null || t >= kind.Boom.Frames.Length / kind.Boom.Fps) { Destroy(gameObject); return; }
            sr.sprite = kind.Boom.Frames[Mathf.Min(kind.Boom.Frames.Length - 1, (int)(t * kind.Boom.Fps))];
        }
    }

    void Frame(Monsters.Anim a, float t)
    {
        if (a != null) sr.sprite = a.Frames[(int)(t * a.Fps) % a.Frames.Length];
    }

    bool gameFx;
    static GameObject blastFx, smokeFx;
    static bool fxLooked;

    /// <summary>The game's own explosion and smoke, there (just their looks: the damage is ours). False: none loaded
    /// (the pack's explosion frames play instead).</summary>
    static bool GameBlast(Vector3 at)
    {
        if (!fxLooked)
        {
            fxLooked = true;
            // its explosions (what its exploding creatures and spells spawn), the most bomb-like first
            var blasts = Resources.FindObjectsOfTypeAll<Explosion>().Where(x => x && !x.gameObject.scene.IsValid()).Select(x => x.gameObject).ToList();
            string[] best = { "bomb", "kamikaze", "explosion" };
            blastFx = blasts.OrderBy(g => { int i = System.Array.FindIndex(best, b => g.name.ToLowerInvariant().Contains(b)); return i < 0 ? 99 : i; }).FirstOrDefault();
            smokeFx = Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(g => g && !g.scene.IsValid() && (g.name == "VFX_OuterSmoke" || g.name == "Smoke"));
            NetLog.Info($"Monsters: bomb blasts: {(blasts.Count > 0 ? string.Join(", ", blasts.Select(g => g.name)) : "none loaded")}; using {(blastFx ? blastFx.name : "the pack's")}, smoke {(smokeFx ? smokeFx.name : "none")}", toast: false);
        }
        bool any = false;
        if (blastFx)
        {
            try
            {
                var go = Object.Instantiate(blastFx, at, Quaternion.identity);
                go.AddComponent<BlastLooks>();   // (Monsters.HarmlessBlast: its looks and shake, never its damage)
                var x = go.GetComponent<Explosion>();
                if (x) { x.range = 0f; x.Owner = null; }
                go.SetActive(true);
                if (x) x.Trigger();
                Object.Destroy(go, 3f);
                any = true;
            }
            catch (System.Exception e) { NetLog.Warn($"Monsters: the game's blast: {e.Message}", toast: false); }
        }
        if (smokeFx)
        {
            try
            {
                var go = Object.Instantiate(smokeFx, at, Quaternion.identity);
                go.SetActive(true);
                Object.Destroy(go, 2.5f);
            }
            catch (System.Exception e) { NetLog.Warn($"Monsters: the game's smoke: {e.Message}", toast: false); }
        }
        return any;
    }

    /// <summary>The blast: the thrower's attack, as the game lands one (its damage worked out, then everything in
    /// reach takes it), from where the bomb lies.</summary>
    void Blow()
    {
        if (!owner) return;
        try
        {
            var data = new DamageData { Aggressor = owner, Attack = attack, Processors = new List<DamageDataProcessor> { new EnemyDamageDifficultyModifier() } };
            CombatSystem.ProcessAttackDamageData(ref data);
            var hits = Physics2D.OverlapCircleAll(transform.position, kind.Blast).ToList();
            owner.ProcessColliders(hits, data, false);
        }
        catch (System.Exception x) { NetLog.Warn($"Monsters: a bomb went off wrong: {x.Message}", toast: false); }
    }
}

/// <summary>On our copy of one of the game's explosions (a bomb's blast): only its looks (Monsters.HarmlessBlast).</summary>
internal sealed class BlastLooks : MonoBehaviour { }

/// <summary>
/// A shooter's shot (a bone, a harpoon, a hex, an acorn): it flies straight on until it hits you (the shooter's attack,
/// as the game lands one: damage, dodge, block, and what it leaves on you: the hex's poison), something solid (a
/// wall, a fence), or it has flown its range; then what it lands as plays out (the hex's burst), if it has one.
/// </summary>
internal sealed class Shot : MonoBehaviour
{
    const float Size = 0.3f;

    Monsters.Kind kind;
    Enemy owner;
    Attack attack;
    Vector3 dir;
    float range, flown, since, landedAt = -1f;
    SpriteRenderer sr;

    public static void Fire(Monster shooter, Attack sting, Vector3 from, Vector3 dir)
    {
        var go = new GameObject($"EG_{shooter.Kind.Name}_Shot");
        go.transform.position = from;
        var s = go.AddComponent<Shot>();
        s.kind = shooter.Kind;
        s.owner = shooter.GetComponent<Enemy>();
        s.attack = Object.Instantiate(sting);   // (its damage, and what it leaves on you)
        s.dir = dir;
        s.range = shooter.Kind.ThrowRange + 3f;
        s.sr = go.AddComponent<SpriteRenderer>();
        var like = shooter.GetComponentsInChildren<SpriteRenderer>().FirstOrDefault(r => r.name == "EG_Look");
        if (like) { s.sr.sortingLayerID = like.sortingLayerID; s.sr.sortingOrder = like.sortingOrder + 1; }
        if (shooter.Kind.ShotAim) go.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - shooter.Kind.ShotAngle);
        else s.sr.flipX = dir.x < 0f;
    }

    void Update()
    {
        since += Time.deltaTime;
        if (landedAt >= 0f)   // (landed: what it lands as, then gone)
        {
            float t = since - landedAt;
            if (kind.ShotHit == null || t >= kind.ShotHit.Frames.Length / kind.ShotHit.Fps) { Destroy(gameObject); return; }
            sr.sprite = kind.ShotHit.Frames[Mathf.Min(kind.ShotHit.Frames.Length - 1, (int)(t * kind.ShotHit.Fps))];
            return;
        }
        sr.sprite = kind.Shot.Frames[(int)(since * kind.Shot.Fps) % kind.Shot.Frames.Length];
        float step = kind.ShotSpeed * Time.deltaTime;
        transform.position += dir * step;
        flown += step;
        // what's here: you (or anything that takes hits but its own side), or something solid
        bool hitSomething = false, solid = false;
        var here = Physics2D.OverlapCircleAll(transform.position, Size);
        foreach (var c in here)
        {
            if (!c || owner && c.transform.IsChildOf(owner.transform)) continue;
            if (c.CompareTag("Enemy") || c.CompareTag("HitBox")) continue;
            if (c.GetComponent<IDamageable>() != null) hitSomething = true;
            else if (!c.isTrigger) solid = true;
        }
        if (hitSomething) Land(here, hurt: true);
        else if (solid || flown >= range) Land(here, hurt: false);
    }

    void Land(Collider2D[] here, bool hurt)
    {
        landedAt = since;
        transform.rotation = Quaternion.identity;
        if (hurt && owner)
        {
            try
            {
                var data = new DamageData { Aggressor = owner, Attack = attack, Processors = new List<DamageDataProcessor> { new EnemyDamageDifficultyModifier() } };
                CombatSystem.ProcessAttackDamageData(ref data);
                owner.ProcessColliders(here.ToList(), data, false);
            }
            catch (System.Exception x) { NetLog.Warn($"Monsters: a shot landed wrong: {x.Message}", toast: false); }
        }
        if (kind.ShotHit == null) Destroy(gameObject);
    }
}
