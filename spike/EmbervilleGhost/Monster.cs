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
/// - Ambush (the Barrel Goblin): hidden in its barrel (no damage) till you come near, then out, at you, and it
///   blows itself up (Kamikaze: its blast where it stands, then it's dead).
/// - Heal (the Monk): never hits you; it keeps its distance and heals the most hurt creature near it.
/// - Barrage (the King): every 15 s, spots around you marked red (2, then 2 more each time, up to 30); then stones from
///   off the screen crash on them.
/// - Summon (the King): stands back; when it sees you, and every 20 s, it calls in a Monk and a random soldier, each in
///   the teleport flash. Hit, it teleports away (at most every 5 s), then flashes rainbow, dashes at you almost at
///   once and strikes (a stun), and leaps a few squares back.
/// - Spin (the Berserker): its attack loops 3 s while it goes after you, hitting every half second; nothing hurts it
///   meanwhile.
/// - Charge (the Lancer): from 5 away, a quick windup, a rush at you that hits once when it gets there.
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
    public bool Hunts;
    public bool Small;   // (a split-off one: its parent's split size and strength; it doesn't split again)
    public float SizeMul = 1f, HealthMul = 1f, DamageMul = 1f;
    bool smallSet, splitDone;
    float SizeNow => Kind.Size * Monsters.ScaleAll * SizeMul;   // (knows where you are, however far: the arena's waves)

    const float Sight = 6f, Leash = 12f;
    const float SquashX = 1.3f, SquashY = 0.45f, FadeTime = 1.5f;   // (dead: how flat it goes, how long it takes to fade)
    const float WindUp = 0.45f, DashTime = 0.22f, Recover = 0.7f, Cooldown = 1.1f, Drop = 0.5f;

    // how close it strikes from, and how far its strike reaches: a lunger dives in (and hits close), one that strikes
    // where it stands (the Turtle's neck) comes closer and reaches further; or its own (the Troll's club)
    float Reach => Kind.ThrowRange > 0f ? Kind.ThrowRange : Kind.Reach > 0f ? Kind.Reach : Kind.Lunge > 0f ? 1.7f : 1.1f;
    float Radius => Kind.Radius > 0f ? Kind.Radius : Kind.Lunge > 0f ? 0.9f : 1.4f;

    // its attack in parts: its own windup / recovery rows (the Troll), or cut from the attack row
    bool OwnRows => Kind.Windup != null;
    float WindTime => Kind.ChargeTime > 0f ? Kind.ChargeWindup : OwnRows ? (Kind.WindupTime > 0f ? Kind.WindupTime : Length(Kind.Windup)) : WindUp;
    bool ChargeOn => Kind.ChargeTime > 0f || strongNow;
    float ChargeT => strongNow ? Kind.StrongTime : Kind.ChargeTime;
    float ChargeV => strongNow ? Kind.StrongSpeed : Kind.ChargeSpeed;
    /// <summary>Is its next attack its strong one (the Spear Goblin's every 3rd)?</summary>
    bool StrongNext => Kind.StrongEvery > 0 && (attacks + 1) % Kind.StrongEvery == 0;

    /// <summary>This blow's damage on top (a strong attack's).</summary>
    public float HitMul => strongNow ? Kind.StrongDamage : 1f;
    bool strongNow, bashNow;
    int attacks;
    float nextDust;
    int puffs;
    float StrikeTime => ChargeOn ? ChargeT : Kind.AttackTime > 0f ? Kind.AttackTime : OwnRows ? Length(Kind.Attack) : DashTime;
    float HitAt => OwnRows ? Kind.HitFrame / Mathf.Max(1f, Kind.Attack.Fps) : DashTime * 0.5f;
    float RestTime => Kind.Recovery != null ? Length(Kind.Recovery) : Recover;

    enum Mind { Wander, Chase, WindUp, Sting, Recover, GuardIn, Guard, GuardOut, Block, Dash, PullBack, Stuck, Dazed }

    Enemy e;
    NavMeshAgent agent;
    SpriteRenderer body, sr, shadow;
    Transform look;
    Vector3 lookAt;
    readonly List<Renderer> hidden = new();
    Attack sting;
    Mind mind;
    float since, nextWander, readyAt, deadAt = -1f, t;
    bool hit, deathSaid, struck, thrusted, lungeDone;
    float lungeSpeed;
    float shadowAlpha = -1f, stealthA = 1f;
    /// <summary>Faded out (the Thief): its health bar kept off (Monsters.NoBarUnseen).</summary>
    public bool Unseen { get; private set; }
    const float StealthLow = 0.08f;
    Vector3 home, dashDir;
    Monsters.Anim playing;
    float pathSaidAt, nextHit;

    /// <summary>In its shell (going in, in it, coming out): no damage (Monsters.Shell).</summary>
    public bool Guarding => mind == Mind.GuardIn || mind == Mind.Guard || mind == Mind.GuardOut || mind == Mind.Block || Kind != null && Kind.Spin && mind == Mind.Sting;   // (a Berserker spinning too)

    /// <summary>A hit landed on it (Monsters.Shell): one that can guard goes into its shell once the hit has played.</summary>
    public void Struck()
    {
        if (Kind == null) return;
        if (Kind.Blink > 0f && Time.time >= blinkReady && mind != Mind.Stuck && mind != Mind.Dazed) blinkNow = true;   // (the King: away, once the hit has landed; not while he's open)
        if (Kind.Ambush > 0f) return;   // (one that lies in wait doesn't duck back when hit)
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
        if (Kind.Ambush > 0f && Kind.GuardIn != null) mind = Mind.Guard;   // (hidden from the start: the Barrel Goblin)
        NetLog.Info($"Monsters: {name} awake (agent {(agent ? "yes" : "no")}, sting from {(baseAttack ? baseAttack.name : "nothing")}, {Scale:0.##}x; hid {string.Join(", ", hidden.Select(h => h.name))}; shadow {(shadow ? shadow.name : "none")})", toast: false);
    }

    /// <summary>How much bigger the creature underneath is made (a boss: MiniBoss), for its reach.</summary>
    float Scale => Mathf.Abs(transform.lossyScale.x) > 0.01f ? Mathf.Abs(transform.lossyScale.x) : 1f;

    /// <summary>The game's to move: hit, knocked back, stunned, dying.</summary>
    bool Busy => e.currentState != e.runBehaviorState && e.currentState != e.idleState;

    void Update()
    {
        if (!e || !agent || !sr) return;
        if (Small && !smallSet && e.initHealth > 0f) { smallSet = true; e.initHealth *= HealthMul; e.currentHealth = Mathf.Min(e.currentHealth, e.initHealth); }   // (a split-off one: weaker)
        if (e.IsDead) { Die(); return; }
        if (Busy)
        {
            if (mind == Mind.WindUp || mind == Mind.Sting || mind == Mind.Dash) { e.CancelAttack(); mind = Mind.Recover; since = 0f; readyAt = Time.time + Cooldown; dashed = false; strongNow = false; DashDone(); }   // (a hit stops a strike, a dash)
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
        if (blinkNow) { blinkNow = false; if (alive) Blink(me); }
        if (Kind.SummonEvery > 0f && sees && Time.time >= summonAt) Summon();
        if (Kind.BarrageEvery > 0f && sees && me) { if (barrageAt <= 0f) barrageAt = Time.time + 6f; else if (Time.time >= barrageAt) Barrage(me); }
        switch (mind)
        {
            case Mind.Wander:
                agent.speed = Kind.WanderSpeed;
                if (sees) { mind = Mind.Chase; since = 0f; break; }
                if (Kind.Heal > 0f && Time.time >= readyAt && Hurt()) { Stop(); mind = Mind.WindUp; since = 0f; break; }   // (healing needs no you)
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
                // a thrower keeps its distance (and one that stands back: the King): too close, it backs off
                if ((Kind.ThrowRange > 0f || Kind.SummonEvery > 0f) && to.magnitude < Kind.KeepAway) Go(transform.position - to.normalized * 2.5f);
                else Go(me.transform.position);
                Face(to);
                // (a path cut short: you somewhere it can't walk, the stairs say; told once a few seconds)
                if (agent.hasPath && agent.pathStatus != NavMeshPathStatus.PathComplete && Time.time >= pathSaidAt)
                {
                    pathSaidAt = Time.time + 5f;
                    NetLog.Info($"Monsters: {name} can't reach you ({agent.pathStatus}; it at {transform.position.x:0.0}, {transform.position.y:0.0}, you at {me.transform.position.x:0.0}, {me.transform.position.y:0.0}; its areas {agent.areaMask}, type {agent.agentTypeID})", toast: false);
                }
                if (Kind.Heal > 0f) { if (Time.time >= readyAt && Hurt()) { Stop(); mind = Mind.WindUp; since = 0f; } }   // (a healer: a hurt friend, not you)
                else if (to.magnitude <= (StrongNext ? Kind.StrongReach : Reach) && Time.time >= readyAt)
                {
                    Stop();
                    strongNow = StrongNext;
                    attacks++;
                    bashNow = Kind.BashEvery > 0 && attacks % Kind.BashEvery == 0;
                    e.PerformAttack(sting, me.gameObject);   // (the game's attack begins: it's attacking now)
                    mind = Mind.WindUp; since = 0f;
                }
                break;
            case Mind.WindUp:
                if (alive) Face(to);
                if (since >= WindTime)
                {
                    dashDir = alive && to.sqrMagnitude > 0.01f ? to.normalized : e.CurrentDirection.normalized;
                    mind = Mind.Sting; since = 0f; hit = false; thrusted = false; lungeDone = false;
                }
                break;
            case Mind.Sting:
                // a lunge: at you, only as far as you are (close, it doesn't fly past); it hits on reaching you, and stops
                if (agent.isOnNavMesh && Kind.Lunge > 0f && !ChargeOn && !lungeDone)
                {
                    if (since <= Time.deltaTime + 0.0001f) lungeSpeed = Mathf.Min(Kind.Lunge, (alive ? to.magnitude : 1f) / DashTime * 1.15f);
                    agent.isStopped = false; agent.velocity = dashDir * lungeSpeed;
                    if (!hit && alive && to.magnitude <= Radius * Scale) { hit = true; nextHit = since + Kind.HitEvery; Strike(me); lungeDone = true; Stop(); }
                }
                if (Kind.Spin && alive) { agent.speed = Kind.ChaseSpeed; Go(me.transform.position); }   // (spinning after you)
                if (ChargeOn)   // (a rush: straight on; the hit when it reaches you, and the rush is over, or on through you: trampling)
                {
                    if (agent.isOnNavMesh) { agent.isStopped = false; agent.ResetPath(); agent.velocity = dashDir * ChargeV; }
                    if (!hit && alive && to.magnitude <= Radius * Scale) { hit = true; Strike(me); if (!Kind.Trample) since = StrikeTime; }
                    else if (since >= StrikeTime) hit = true;   // (missed: no hit at the end)
                    if (Kind.Dust != null && Time.time >= nextDust)   // (dust kicked up behind)
                    {
                        nextDust = Time.time + 0.06f;
                        bool big = Kind.Dust2 != null && ++puffs % 3 == 0;   // (every third a big cloud, the rest small puffs)
                        Fx.Play(big ? Kind.Dust2 : Kind.Dust, transform.position - dashDir * 0.5f + (Vector3)(Random.insideUnitCircle * 0.2f), sr, 1.4f, -1);
                    }
                }
                if (!hit && since >= HitAt && !ChargeOn && (Kind.Lunge <= 0f || since >= StrikeTime * 0.9f)) { hit = true; nextHit = since + Kind.HitEvery; Strike(me); }   // (the strike lands; a lunger's at its end if it never reached you: a swing at the air)
                else if (hit && Kind.HitEvery > 0f && since >= nextHit && since < StrikeTime) { nextHit += Kind.HitEvery; Strike(me); }   // (and again: a breath)
                if (since >= StrikeTime)
                {
                    Stop();
                    e.AttackEnd();
                    mind = Kind.StuckFor > 0f ? Mind.Stuck : dashed ? Mind.PullBack : Mind.Recover; since = 0f;
                    strongNow = false;
                    readyAt = Mathf.Max(readyAt, Time.time + Cooldown);   // (a healer's longer wait kept)
                    if (Kind.StuckFor > 0f) { pullAfter = dashed; NetLog.Info($"Monsters: {name}'s weapon is stuck in the ground", toast: false); }
                    dashed = false;
                }
                break;
            case Mind.Dash:   // (the King, after a teleport: a moment's flash, then at you almost at once, and the strike)
                if (!alive || since > DashGiveUp) { Stop(); DashDone(); mind = Mind.Chase; since = 0f; break; }
                if (since < DashPause) { Face(to); Stop(); dashLine = to.sqrMagnitude > 0.01f ? to.normalized : (Vector3)(Vector2)e.CurrentDirection.normalized; dashFrom = transform.position; break; }
                if (Kind.WallDaze > 0f)   // (straight on, at where you were: a wall in the way dazes it; past you too far, it stops)
                {
                    if (NavMesh.Raycast(transform.position, transform.position + dashLine * 0.7f, out _, NavMesh.AllAreas))
                    {
                        Stop(); DashDone();
                        mind = Mind.Dazed; since = 0f;
                        Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, "Stunned", e.CenterOfMass.position, DamageResult.None));
                        NetLog.Info($"Monsters: {name} ran into a wall: dazed", toast: false);
                        break;
                    }
                    if ((transform.position - dashFrom).magnitude > DashMax) { Stop(); DashDone(); mind = Mind.Chase; since = 0f; break; }
                }
                else Face(to);
                if (to.magnitude <= Reach)
                {
                    Stop();
                    DashDone();
                    e.PerformAttack(sting, me.gameObject);
                    dashed = true;
                    mind = Mind.Sting; since = 0f; hit = false;   // (no windup: the dash was it)
                    dashDir = to.normalized;
                    break;
                }
                if (agent.isOnNavMesh && Kind.WallDaze > 0f) { agent.isStopped = false; agent.ResetPath(); agent.speed = DashSpeed; agent.acceleration = 200f; agent.velocity = dashLine * DashSpeed; Face(dashLine); }
                else if (agent.isOnNavMesh) { agent.isStopped = false; agent.speed = DashSpeed; agent.acceleration = 200f; Go(me.transform.position); agent.velocity = to.normalized * DashSpeed; }
                break;
            case Mind.Stuck:   // (its weapon in the ground: standing there, not turning; its back open)
                Stop();
                if (since >= Kind.StuckFor) { mind = pullAfter ? Mind.PullBack : Mind.Recover; since = 0f; pullAfter = false; }
                break;
            case Mind.Dazed:   // (ran into a wall: reeling, open all round)
                Stop();
                if (since >= Kind.WallDaze) { mind = Mind.Chase; since = 0f; }
                break;
            case Mind.PullBack:   // (then a few squares back, quickly: and he stands back again)
                if (since == 0f || pullTo == Vector3.zero)
                {
                    var away = alive && to.sqrMagnitude > 0.01f ? -to.normalized : -(Vector3)(Vector2)e.CurrentDirection.normalized;
                    pullTo = NavMesh.SamplePosition(transform.position + away * PullBackBy, out var ph, 1.5f, NavMesh.AllAreas) ? ph.position : transform.position;
                }
                agent.speed = Kind.ChaseSpeed * 2.5f;
                Go(pullTo);
                if (alive) Face(to);
                if (since >= PullBackFor || (transform.position - pullTo).magnitude < 0.3f) { Stop(); pullTo = Vector3.zero; mind = Mind.Chase; since = 0f; }
                break;
            case Mind.Recover:   // (one with its own recovery stands there tired)
                if (Kind.Recovery != null) Stop();
                if (Kind.Thrust > 0f && !thrusted && hit && since >= Kind.Thrust)   // (the Swordsman's second blow: the thrust)
                {
                    thrusted = true;
                    e.PerformAttack(sting, me ? me.gameObject : null);
                    Strike(me);
                    e.AttackEnd();
                }
                if (since >= RestTime) { mind = sees ? Mind.Chase : Mind.Wander; since = 0f; }
                break;
            case Mind.GuardIn:   // (into its shell)
                Stop();
                if (since >= Length(Kind.GuardIn)) { mind = Mind.Guard; since = 0f; }
                break;
            case Mind.Guard:   // (in it, until you're half a screen away; one lying in wait: until you're near)
                Stop();
                if (Kind.Ambush > 0f ? alive && to.magnitude < Kind.Ambush : !alive || to.magnitude > HalfScreen) { mind = Mind.GuardOut; since = 0f; }
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
        // a healer: its friend healed (a share of its health), the heal showing on it
        if (Kind.Heal > 0f)
        {
            if (healing && !healing.IsDead && healing.initHealth > 0f)
            {
                bool boss = MiniBoss.Is(healing);
                healing.currentHealth = Mathf.Min(healing.initHealth, healing.currentHealth + healing.initHealth * Kind.Heal * (boss ? 0.5f : 1f));   // (a boss: half)
                readyAt = Time.time + Mathf.Max(Cooldown, Kind.HealEvery);   // (not again for a while)
                if (Kind.HealFx != null) Fx.Play(Kind.HealFx, healing.transform.position + new Vector3(0f, 0.5f, 0f), sr);
                NetLog.Info($"Monsters: {name} healed {healing.name} ({healing.currentHealth:0}/{healing.initHealth:0})", toast: false);
            }
            healing = null;
            return;
        }
        // a web (the Spider's): only when you're not in one already (another would start your wriggling over), and
        // not too often
        sting.effects.RemoveAll(x => x.Effect == EffectType.Trapped);
        if (Kind.Trap > 0f && me && me.currentState != me.stateTrapped && Time.time >= trapReady)
        {
            sting.effects.Add(new DamageEffect { Effect = EffectType.Trapped, Chance = 100f });
            trapReady = Time.time + Kind.Trap;
        }
        // blowing itself up (the Barrel Goblin): its blast where it stands, and it's dead
        if (Kind.Kamikaze)
        {
            Bomb.Throw(this, transform.position, transform.position, now: true);
            e.currentHealth = 0f;
            e.dieState.IsGore = false;
            e.ChangeState(e.dieState);   // (as the game's own death: its loot, its corpse)
            e.SetCollisionEnabled(false);
            e.SetKilled();   // (dead to the game too: without it IsDead stays false, our fall and fade never start, and it stands there for good)
            return;
        }
        // a thrower: its bomb, at where you are now (it does the damage when it goes off)
        if (Kind.BombSpin != null)
        {
            var from = look.position + new Vector3(0f, 0.3f, 0f);
            var target = me ? me.transform.position : transform.position + e.CurrentDirection.normalized * Kind.ThrowRange;
            var first = Bomb.Throw(this, from, target);
            // a fan of them (the TNT Goblin's 3 sticks): the others turned to each side of the first, as far
            if (Kind.Spread > 1)
            {
                var toIt = target - transform.position; toIt.z = 0f;
                for (int i = 1; i < Kind.Spread; i++)
                {
                    float turn = Kind.SpreadAngle * ((i + 1) / 2) * (i % 2 == 1 ? 1f : -1f);   // (+a, -a, +2a, -2a...)
                    Bomb.Throw(this, from, transform.position + Quaternion.Euler(0f, 0f, turn) * toIt).Wick(Bomb.WickSpread);
                }
                first.Wick(Bomb.WickSpread);   // (each its own wick: they don't all go off at once)
            }
            return;
        }
        // a shooter: its shot, straight at you (it does the damage where it lands)
        if (Kind.Shot != null)
        {
            var dir = me ? me.transform.position - transform.position : e.CurrentDirection;
            dir.z = 0f;
            var first = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector3.right;
            Shot.Fire(this, sting, transform.position, look.position - transform.position, first);
            for (int i = 1; i < Kind.Spread; i++)   // (a fan of them: the Gnoll's 3 bones)
            {
                float turn = Kind.SpreadAngle * ((i + 1) / 2) * (i % 2 == 1 ? 1f : -1f);
                Shot.Fire(this, sting, transform.position, look.position - transform.position, Quaternion.Euler(0f, 0f, turn) * first);
            }
            return;
        }
        int before = me ? me.currentHealth : 0;
        if (dashed) NetLog.Info($"Monsters: {name}'s dash strike lands {(me && (me.transform.position - transform.position).magnitude <= sting.radius ? "on you" : "short")}", toast: false);
        e.PerformAttackHit();
        if (Kind.Backwards > 0f && me && me.currentHealth < before) Backwards.After(Kind.Backwards);   // (backwards: at once, or once a stun's over: the King's, the Minotaur's)
        if (me && me.currentHealth < before) { Statuses.Disarm(Kind.Disarm); Statuses.Silence(Kind.Silence); Statuses.Root(Kind.Root); Statuses.Slow(Kind.SlowTo, Kind.SlowFor, Kind.Splash != null ? "Drenched!" : "Slowed!"); Statuses.Blind(Kind.Blind); Statuses.Fists(Kind.Fists); Statuses.Mark(e, Kind.Mark); Statuses.Polymorph(Kind.Polymorph); }   // (the Gnome's, the Hex Shaman's, the Lizard's, the Turtle's)   // (the King's: backwards after the stun)
        if (me && me.currentHealth < before)
        {
            if (Kind.Venom > 0) Statuses.Venom(Kind.Venom, Kind.VenomStun);   // (the Snake's)
            if (bashNow && Kind.BashStun > 0f) { me.Stun(Kind.BashStun); Statuses.Pop("Shield bash!"); }   // (the Warrior's, the Knight's)
            if (Kind.Splash != null) Fx.Play(Kind.Splash, me.transform.position + new Vector3(0f, 0.4f, 0f), sr, 1.3f);   // (the Paddle Shark's)
        }
        if (Kind.Pound && Kind.PoundFx != null)   // (the Great Bear's ground pound: dust in a ring, the ground shaking)
        {
            float r = Radius * Scale;
            Fx.Play(Kind.PoundFx, transform.position, sr, 3f);
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.PI / 4f;
                Fx.Play(Kind.PoundFx, transform.position + new Vector3(Mathf.Cos(a), Mathf.Sin(a) * 0.6f, 0f) * r, sr, 1.6f);
            }
            try { CameraShake.Play(ShakeSize.Medium); } catch { }
        }
        if (!me || Kind.Knock <= 0f || !(me.IsStunned || me.currentHealth < before)) return;
        var away = me.transform.position - transform.position; away.z = 0f;
        pushDir = away.sqrMagnitude > 0.0001f ? away.normalized : (Vector3)(e.CurrentDirection.normalized);
        pushLeft = Kind.Knock;
        pushSpeed = Mathf.Min(Kind.Knock / PushTime, PushSpeedMax);
        // not off the walkable ground (into water, past a fence): stopped short of where it ends
        var start = me.transform.position;
        if (NavMesh.SamplePosition(start, out var on, 1f, NavMesh.AllAreas) && NavMesh.Raycast(on.position, on.position + pushDir * Kind.Knock, out var edge, NavMesh.AllAreas))
            pushLeft = Mathf.Max(0f, Vector2.Distance(on.position, edge.position) - 0.3f);
    }

    // ---- the catapults' barrage (the King): spots marked around you, stones from off the screen ----

    const float MarkFor = 0.2f, StoneGap = 0.2f;   // (the marks show the stones' whole flight too)
    const int MostStones = 30;
    float barrageAt;
    int barrages;   // (how many it has called: each one BarrageCount more stones than the last)

    /// <summary>BarrageCount spots around you (apart, on walkable ground), each marked; a stone lands on each after
    /// MarkFor, one after another (its colour's Catapult's stones: their damage the King's, hurting anyone there).</summary>
    void Barrage(PlayerMaster me)
    {
        barrageAt = Time.time + Kind.BarrageEvery;
        string colour = Kind.Name.Contains(' ') ? Kind.Name.Substring(0, Kind.Name.IndexOf(' ') + 1) : "";
        var stone = Monsters.Load(colour + "Catapult") ?? Monsters.Names().Where(n => n.EndsWith(" Catapult")).Select(Monsters.Load).FirstOrDefault(k => k != null);
        if (stone == null || stone.BombSpin == null) return;
        barrages++;
        int count = Mathf.Min(MostStones, Kind.BarrageCount * barrages);   // (2, 4, 6...)
        float spread = 2f + 0.3f * count;   // (more stones, a wider area: room for each ring)
        var spots = new List<Vector3>();
        for (int tries = 0; tries < count * 10 && spots.Count < count; tries++)
        {
            var p = me.transform.position + (Vector3)(Random.insideUnitCircle * spread);
            if (spots.Count == 0) p = me.transform.position;   // (one right where you stand)
            if (!NavMesh.SamplePosition(p, out var h, 0.6f, NavMesh.AllAreas) || spots.Any(o => (o - h.position).magnitude < 1.3f)) continue;
            spots.Add(h.position);
        }
        var marks = new List<SpriteRenderer>();
        for (int i = 0; i < spots.Count; i++) { var m = Bomb.FromAfar(this, stone, spots[i], MarkFor + i * StoneGap); if (m) marks.Add(m); }
        // the rings just over the flat ground there (floor, rugs; under what stands on it), as the dungeon pads' are
        if (marks.Count > 0)
        {
            // over everything on the creatures' layer (the floor under it hid them): a warning may show over whoever
            // stands in it
            marks[0].sortingLayerID = sr.sortingLayerID;
            marks[0].sortingOrder = 30000;
            foreach (var m in marks) { m.sortingLayerID = marks[0].sortingLayerID; m.sortingOrder = marks[0].sortingOrder; }
            if (!markSaid)   // (once: the floor's layers there, to see why the floor check missed)
                NetLog.Info("Monsters: tilemaps at the first ring: " + string.Join("; ", FindObjectsByType<UnityEngine.Tilemaps.TilemapRenderer>(FindObjectsSortMode.None)
                    .Where(r => r && r.gameObject.activeInHierarchy && r.bounds.Contains(new Vector3(spots[0].x, spots[0].y, r.bounds.center.z)))
                    .Select(r => { var tm = r.GetComponent<UnityEngine.Tilemaps.Tilemap>(); return $"{r.name} {SortingLayer.IDToName(r.sortingLayerID)} {r.sortingOrder} tile {(tm && tm.HasTile(tm.WorldToCell(spots[0])) ? "yes" : "no")} collider {(r.GetComponent<Collider2D>() ? "yes" : "no")} on {(r.enabled ? "yes" : "no")}"; })), toast: false);
            if (!markSaid)
            {
                markSaid = true;
                var k = marks[0];
                NetLog.Info($"Monsters: a catapult ring: {SortingLayer.IDToName(k.sortingLayerID)} {k.sortingOrder}, at {k.transform.position}, {k.transform.lossyScale.x:0.0} across, " +
                            $"material {(k.sharedMaterial ? k.sharedMaterial.shader.name : "none")}, sprite {(k.sprite ? "yes" : "no")}, enabled {k.enabled}, layer {k.gameObject.layer}; " +
                            $"the King's: {SortingLayer.IDToName(sr.sortingLayerID)} {sr.sortingOrder} at {transform.position}", toast: false);
            }
        }
        NetLog.Info($"Monsters: {name} calls the catapults on {spots.Count} spots", toast: false);
    }

    static bool ringSaid, markSaid;

    /// <summary>Just over the floor tiles there (the topmost tilemap there without a collider: the arena's floor, a
    /// dungeon's merged ground, the map's): false if none.</summary>
    static bool OverFloorTiles(SpriteRenderer sr, Vector3 at)
    {
        UnityEngine.Tilemaps.TilemapRenderer best = null;
        foreach (var r in FindObjectsByType<UnityEngine.Tilemaps.TilemapRenderer>(FindObjectsSortMode.None))
        {
            if (!r || !r.enabled || !r.gameObject.activeInHierarchy || r.GetComponent<Collider2D>()) continue;
            var tm = r.GetComponent<UnityEngine.Tilemaps.Tilemap>();
            if (!tm || !tm.HasTile(tm.WorldToCell(at))) continue;   // (a tile right there)
            if (best && (SortingLayer.GetLayerValueFromID(r.sortingLayerID) < SortingLayer.GetLayerValueFromID(best.sortingLayerID) ||
                         r.sortingLayerID == best.sortingLayerID && r.sortingOrder <= best.sortingOrder)) continue;
            best = r;
        }
        if (!best) return false;
        sr.sortingLayerID = best.sortingLayerID;
        sr.sortingOrder = best.sortingOrder + 1;
        if (!ringSaid) { ringSaid = true; NetLog.Info($"Monsters: the catapults' rings over {best.name} ({SortingLayer.IDToName(best.sortingLayerID)} {best.sortingOrder})", toast: false); }
        return true;
    }

    // ---- teleporting away when hit (the King) ----

    const float BlinkNear = 5f, BlinkFar = 7f;
    float blinkReady;
    bool blinkNow;

    /// <summary>Gone in the teleport flash, back in another BlinkNear-BlinkFar from you on walkable ground.</summary>
    void Blink(PlayerMaster me)
    {
        blinkReady = Time.time + Kind.Blink;
        // (from the walkable ground under it: a boss made bigger can stand a little off it)
        var from = NavMesh.SamplePosition(transform.position, out var under, 2f, NavMesh.AllAreas) ? under.position : transform.position;
        for (int tries = 0; tries < 12; tries++)
        {
            var want = me.transform.position + (Vector3)(Random.insideUnitCircle.normalized * Random.Range(BlinkNear, BlinkFar));
            if (!NavMesh.SamplePosition(want, out var h, 1f, NavMesh.AllAreas)) continue;
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(from, h.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete) continue;   // (not onto a patch cut off)
            if (e.IsAttacking) e.CancelAttack();
            Stop();
            StreamRaids.Poof(transform.position);
            e.TeleportTo(new Vector3(h.position.x, h.position.y, transform.position.z));
            StreamRaids.Poof(transform.position);
            mind = Mind.Dash; since = 0f;   // (and back at you in a flash: the dash, the strike, then back off)
            if (accelWas <= 0f) accelWas = agent.acceleration;
            NetLog.Info($"Monsters: {name} teleports to {transform.position.x:0.0}, {transform.position.y:0.0} and dashes", toast: false);
            return;
        }
        NetLog.Info($"Monsters: {name} found nowhere to teleport to", toast: false);
    }

    const float DashPause = 0.3f, DashSpeed = 22f, DashGiveUp = 2f, PullBackBy = 3f, PullBackFor = 0.8f, DashMax = 14f;
    bool dashed;   // (this strike came from a dash: then the pull back)
    bool pullAfter;   // (stuck after a dash strike: the pull back once it's free)
    Vector3 dashLine, dashFrom;
    const float BackHit = 1.5f;

    /// <summary>Its armour (the King's): only Kind.Armour of a hit gets through; with its weapon stuck, a hit on its back
    /// does BackHit; dazed (it ran into a wall), every hit gets through in full.</summary>
    public void Armour(ref DamageData d)
    {
        if (Kind == null || Kind.Armour <= 0f || d.Amount <= 0) return;
        if (mind == Mind.Dazed) return;
        if (mind == Mind.Stuck && d.Aggressor != null)
        {
            var from = d.Aggressor.GetTransform().position - transform.position;
            if (Vector2.Dot(e.CurrentDirection, from) < 0f)   // (behind it: its back)
            {
                d.Amount = Mathf.RoundToInt(d.Amount * BackHit);
                Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, "Weak spot!", e.CenterOfMass.position, DamageResult.Critical));
                return;
            }
        }
        d.Amount = Mathf.Max(1, Mathf.RoundToInt(d.Amount * Kind.Armour));
    }
    Vector3 pullTo;
    float accelWas;

    /// <summary>Its own acceleration back (a dash's is near instant).</summary>
    void DashDone() { if (accelWas > 0f && agent) agent.acceleration = accelWas; }

    /// <summary>Dashing (and its moment before): its colours running through the rainbow.</summary>
    bool Rainbow => mind == Mind.Dash || dashed && mind == Mind.Sting;

    // ---- splitting (the Gnome): killed, it comes apart into smaller ones ----

    /// <summary>Its death: Kind.Split smaller ones of it (Kind.SplitSize the size, Kind.SplitStats the health and damage),
    /// beside where it fell, straight at you; they don't split.
    /// (Only the one who runs the place makes them: its creatures are spawned there.)</summary>
    void SplitUp()
    {
        if (Kind.Split <= 0 || Small || splitDone) return;
        splitDone = true;
        if (MiniBoss.Plugin != null && MiniBoss.Plugin.IsClient) return;
        for (int i = 0; i < Kind.Split; i++)
        {
            var at = transform.position + (Vector3)(Random.insideUnitCircle.normalized * 0.7f);
            var s = Monsters.Spawn(Kind.Name, at);
            if (!s) continue;
            if (transform.parent) s.transform.SetParent(transform.parent, true);
            var m = s.GetComponent<Monster>();
            if (m) { m.Small = true; m.Hunts = true; m.SizeMul = Kind.SplitSize; m.HealthMul = m.DamageMul = Kind.SplitStats; }
            StreamRaids.Poof(s.transform.position);
        }
        NetLog.Info($"Monsters: {name} split into {Kind.Split}", toast: false);
    }

    // ---- a summoner (the King): its soldiers, a few at a time ----

    const int MostSummoned = 24;   // (alive at once; the next call waits)
    float summonAt;
    readonly List<Enemy> summoned = new();

    /// <summary>SummonCount of its colour's troops (picked at random) beside it, each in a puff, all after you.</summary>
    void Summon()
    {
        summonAt = Time.time + Kind.SummonEvery;
        summoned.RemoveAll(x => !x || x.IsDead);
        if (Kind.Summons == null || Kind.Summons.Length == 0 || summoned.Count >= MostSummoned) return;
        string first = Kind.Name.Contains(' ') ? Kind.Name.Substring(0, Kind.Name.IndexOf(' ')) : "";
        string colour = Monsters.Colours.Contains(first) ? first + " " : "";   // (a troop's colour; the Queen Bee's Bumblebees have none)
        var called = new List<string>();
        // the ones it always calls (the King: his Monk and a Bowman), then SummonCount picked at random
        var who = (Kind.SummonsAlways ?? new string[0]).Concat(Enumerable.Range(0, Kind.SummonCount).Select(_ => Kind.Summons[Random.Range(0, Kind.Summons.Length)])).ToList();
        foreach (var pick in who)
        {
            if (summoned.Count >= MostSummoned) break;
            string troop = colour + pick;
            var at = transform.position + (Vector3)(Random.insideUnitCircle.normalized * 1.5f);
            var s = Monsters.Spawn(troop, at);
            if (!s) continue;
            if (transform.parent) s.transform.SetParent(transform.parent, true);   // (the arena's: gone with it)
            var m = s.GetComponent<Monster>();
            if (m) m.Hunts = true;
            summoned.Add(s);
            called.Add(troop);
            StreamRaids.Poof(s.transform.position);   // (arriving in the teleport flash)
        }
        if (called.Count > 0) NetLog.Info($"Monsters: {name} calls in {string.Join(" and ", called)}", toast: false);
    }

    Enemy healing;
    float lookedForHurt;

    /// <summary>A healer: the most hurt creature of ours within its range (itself too), into healing; none: false.</summary>
    bool Hurt()
    {
        if (Time.time < lookedForHurt) return false;
        lookedForHurt = Time.time + 0.5f;
        healing = null;
        float worst = 0.95f;
        foreach (var m in FindObjectsByType<Monster>(FindObjectsSortMode.None))
        {
            var o = m ? m.GetComponent<Enemy>() : null;
            if (!o || o.IsDead || o.initHealth <= 0f || (o.transform.position - transform.position).magnitude > Kind.HealRange) continue;
            float part = o.currentHealth / o.initHealth;
            if (part < worst) { worst = part; healing = o; }
        }
        if (healing) Face(healing.transform.position - transform.position);
        return healing;
    }

    float trapReady;
    Vector3 pushDir;
    float pushLeft, pushSpeed;

    /// <summary>Reeled in (the Harpoon Shark's harpoon): pulled to it, stopped a little short, never through a wall.</summary>
    public void PullIn(PlayerMaster me)
    {
        if (!me) return;
        var to = transform.position - me.transform.position; to.z = 0f;
        if (to.magnitude < 1.6f) return;
        pushDir = to.normalized;
        pushLeft = to.magnitude - 1.3f;
        pushSpeed = 14f;
        if (NavMesh.SamplePosition(me.transform.position, out var on, 1f, NavMesh.AllAreas) && NavMesh.Raycast(on.position, on.position + pushDir * pushLeft, out var edge, NavMesh.AllAreas))
            pushLeft = Mathf.Max(0f, Vector2.Distance(on.position, edge.position) - 0.3f);
        Statuses.Pop("Hooked!");
    }
    const float PushTime = 0.25f, PushSpeedMax = 16f;   // (a short knock back takes PushTime; a long one (the King's, to the wall) at most this fast)
    static readonly RaycastHit2D[] castHits = new RaycastHit2D[8];

    /// <summary>The knock back: you pushed that far over a moment, in the physics steps; never through a fence or a
    /// wall (it stops at them), never off the walkable ground (Strike shortens it).</summary>
    void FixedUpdate()
    {
        if (pushLeft <= 0f) return;
        var me = LocalPlayer.Get();
        if (!me || !me.rb) { pushLeft = 0f; return; }
        float step = Mathf.Min(pushLeft, pushSpeed * Time.fixedDeltaTime);   // (a long one: flying back, not a jump)
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
        if (deadAt < 0f)
        {
            deadAt = Time.time;
            Monsters.Died?.Invoke(this);   // (the arena's waves: a potion, maybe)
            SplitUp();
            if (Kind.DeathBomb > 0f && Kind.BombSpin != null && !(MiniBoss.Plugin != null && MiniBoss.Plugin.IsClient)) Bomb.Drop(this, Kind.DeathBomb);   // (the Bomb Fish: its last bomb)
        }
        float wait = Kind.Dead != null ? Length(Kind.Dead) : Drop;
        if (!deathSaid && Time.time - deadAt > wait) { deathSaid = true; e.AnimationEndEvent(); }
    }

    void LateUpdate()
    {
        if (!sr || !body) return;
        foreach (var h in hidden) if (h) h.enabled = false;
        // its own size, whatever the creature underneath was made (a boss is made bigger for its body and reach)
        float parent = Mathf.Abs(look.parent.lossyScale.x) > 0.01f ? Mathf.Abs(look.parent.lossyScale.x) : 1f;
        look.localScale = Vector3.one * (SizeNow / parent);
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
            else if (mind == Mind.Stuck) Show(Kind.Attack, Kind.Attack.Frames.Length - 1);   // (its weapon down, in the ground)
            else if (mind == Mind.Dazed) Show(Kind.Idle, 0);
            else if (mind == Mind.Block) Loop(Kind.Guard);
            else if (mind == Mind.GuardIn) Show(Kind.GuardIn, (int)(since * Kind.GuardIn.Fps));
            else if (mind == Mind.Guard) Show(Kind.GuardIn, Kind.GuardIn.Frames.Length - 1);
            else if (mind == Mind.GuardOut) Show(Kind.GuardOut ?? Kind.GuardIn, Kind.GuardOut != null ? (int)(since * Kind.GuardOut.Fps) : 0);
            else if (strongNow && Kind.Strong != null && (mind == Mind.WindUp || mind == Mind.Sting))
            {
                int half = Kind.Strong.Frames.Length / 2;
                Show(Kind.Strong, mind == Mind.WindUp ? (int)(since / Mathf.Max(0.05f, WindTime) * half) : half + Mathf.Min(Kind.Strong.Frames.Length - half - 1, (int)(since / Mathf.Max(0.05f, StrikeTime) * (Kind.Strong.Frames.Length - half))));
            }
            else if (OwnRows && mind == Mind.WindUp) Show(Kind.Windup, (int)(since / WindTime * Kind.Windup.Frames.Length));
            else if (OwnRows && mind == Mind.Sting) Show(Kind.Attack, Kind.AttackTime > 0f ? (int)(since * Kind.Attack.Fps) % Kind.Attack.Frames.Length : (int)(since * Kind.Attack.Fps));
            else if (mind == Mind.Recover && Kind.Recovery != null) Show(Kind.Recovery, (int)(since * Kind.Recovery.Fps));
            else if (mind == Mind.WindUp) Show(Kind.Attack, (int)(since / WindUp * Kind.WindFrames));
            else if (mind == Mind.Sting && Kind.AttackTime > 0f) Show(Kind.Attack, (int)(since * Kind.Attack.Fps) % Kind.Attack.Frames.Length);   // (a long one: its row looping, the Berserker's spin)
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
            if (shadow)   // (its shadow fades with it, then goes: it stayed on the ground)
            {
                if (shadowAlpha < 0f) shadowAlpha = shadow.color.a;
                var sc = shadow.color;
                sc.a = shadowAlpha * (1f - Mathf.Clamp01(gone / FadeTime));
                shadow.color = sc;
                if (gone >= FadeTime) shadow.enabled = false;
            }
            c *= new Color(0.8f, 0.8f, 0.8f, 1f);   // (a little darker: no longer a threat)
        }
        if (e.currentState == e.hitFreezeState) c *= new Color(1f, 0.55f, 0.55f, 1f);   // (hit: a red flash)
        // unseen (the Thief): faded to a shimmer, but there when it strikes (its windup on) and when hit
        if (Kind.Stealth && !e.IsDead)
        {
            bool shown = mind == Mind.WindUp || mind == Mind.Sting || mind == Mind.Recover && since < 0.6f || e.currentState == e.hitFreezeState;
            stealthA = Mathf.MoveTowards(stealthA, shown ? 1f : StealthLow, Time.deltaTime * (shown ? 8f : 1.5f));
            // its health bar with it: gone as it fades, back (if it's been hurt) as it shows
            bool unseen = stealthA < 0.5f;
            if (unseen != Unseen)
            {
                Unseen = unseen;
                if (unseen) Bus<UIFloatingBarEvent>.Invoke(new UIFloatingBarEvent(transform, 1f, 1f, hide: true));
                else if (e.initHealth > 0f && e.currentHealth < e.initHealth)
                    Bus<UIFloatingBarEvent>.Invoke(new UIFloatingBarEvent(transform, e.currentHealth / e.initHealth, e.currentHealth / e.initHealth, hide: false, autoHide: false));
            }
            c.a *= stealthA;
            if (shadow) { var sc = shadow.color; sc.a = stealthA * 0.5f; shadow.color = sc; }
        }
        if (mind == Mind.Dazed) look.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(Time.time * 9f) * 12f);   // (reeling)
        if (Rainbow) { var h = Color.HSVToRGB(Time.time * 4f % 1f, 0.55f, 1f); c = new Color(c.r * h.r, c.g * h.g, c.b * h.b, c.a); }   // (the King's dash)
        sr.color = c;
        sr.sortingLayerID = body.sortingLayerID;
        sr.sortingOrder = body.sortingOrder;
    }

    /// <summary>A walker: its feet where the creature underneath stands (its own body was drawn higher).</summary>
    void OnGround(float flat = 1f)
    {
        float x = shadow && shadow.enabled ? shadow.bounds.center.x : transform.position.x;   // (over its shadow)
        look.position = new Vector3(x, transform.position.y + Kind.Feet * SizeNow * flat, look.position.z);   // (squashed: still on its feet)
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
    const float SpinTurns = 1.25f;   // (how far it tumbles in the air)
    const float BlastDamage = 3f, BlastStun = 3f;   // (a blast: that many times the thrower's hit; you're stunned that many seconds)

    Monsters.Kind kind;
    Enemy owner;
    Attack attack;
    Vector3 from, to;
    SpriteRenderer sr;
    float since;
    bool blown;
    float flight = Flight, arc = Arc, big = 1f, fuse;
    int hitFor;   // (0: the thrower's attack's damage)   // (a thrown bomb's; a catapult stone from afar flies longer, higher, bigger)

    /// <summary>A bomb thrown from there to there; now: no flight or fuse, it goes off at once (a kamikaze's).</summary>
    public static Bomb Throw(Monster thrower, Vector3 from, Vector3 to, bool now = false)
    {
        var e = thrower.GetComponent<Enemy>();
        var go = new GameObject($"EG_{thrower.Kind.Name}_Bomb");
        go.transform.position = from;
        var b = go.AddComponent<Bomb>();
        b.kind = thrower.Kind;
        b.fuse = thrower.Kind.Fuse;
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
        if (like) { b.sr.sortingLayerID = like.sortingLayerID; b.sr.sortingOrder = 30001; }   // (over the floor's traps and props)
        if (now) b.since = Flight + b.fuse;
        return b;
    }

    SpriteRenderer mark;
    static Sprite ring;

    /// <summary>A stone from off the screen onto that spot (its looks: stone's kind; its damage: the caller's), the spot
    /// marked on the ground until it lands, that long from now. Its mark (to be drawn over the ground).</summary>
    public static SpriteRenderer FromAfar(Monster caller, Monsters.Kind stone, Vector3 at, float after)
    {
        var e = caller.GetComponent<Enemy>();
        var cam = Camera.main;
        float off = cam && cam.orthographic ? cam.orthographicSize * cam.aspect + 2f : 12f;
        var from = at + new Vector3(Random.value < 0.5f ? -off : off, Random.Range(0f, 2f), 0f);   // (from beyond a side of the screen)
        var go = new GameObject($"EG_{caller.Kind.Name}_Stone");
        go.transform.position = from;
        var b = go.AddComponent<Bomb>();
        b.kind = stone;
        b.fuse = stone.Fuse;
        b.owner = e;
        b.from = from;
        b.to = new Vector3(at.x, at.y, from.z);
        var baseAttack = e.attacks?.Select(a => a.Attack).FirstOrDefault(a => a);
        b.attack = baseAttack ? Object.Instantiate(baseAttack) : ScriptableObject.CreateInstance<Attack>();
        b.attack.attackHit = AttackHit.Area;
        b.attack.projectile = null;
        b.attack.effects = new List<DamageEffect>();
        b.sr = go.AddComponent<SpriteRenderer>();
        var like = caller.GetComponentsInChildren<SpriteRenderer>().FirstOrDefault(r => r.name == "EG_Look");
        if (like) { b.sr.sortingLayerID = like.sortingLayerID; b.sr.sortingOrder = 30001; }   // (a stone in the air: over everything, props too)
        b.sr.enabled = false;
        b.since = -after;   // (waiting: the mark shows)
        b.flight = StoneFlight;   // (a catapult's lob: slow, high, coming up over the edge of the screen and down)
        b.arc = StoneArc;
        b.big = StoneSize;
        b.hitFor = Random.Range(StoneHitLow, StoneHitHigh + 1);   // (70-140, each stone its own)
        // the mark: a red ring on the ground, as big as the stone's reach
        var m = new GameObject("EG_StoneMark");
        m.transform.position = b.to;
        m.transform.localScale = Vector3.one * stone.Blast * 2f;
        b.mark = m.AddComponent<SpriteRenderer>();
        b.mark.sprite = Ring();
        if (Unlit()) b.mark.sharedMaterial = Unlit();
        if (like) { b.mark.sortingLayerID = like.sortingLayerID; b.mark.sortingOrder = like.sortingOrder - 5; }
        return b.mark;
    }

    const float StoneFlight = 1.7f, StoneArc = 7f, StoneSize = 1.8f;
    const int StoneHitLow = 70, StoneHitHigh = 140;
    static Material unlit;

    /// <summary>Unlit (the floor's lights don't reach our layer: a lit ring came out dark), as the pads' glow.</summary>
    static Material Unlit()
    {
        if (unlit) return unlit;
        var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default") ?? Shader.Find("Sprites/Default");
        if (shader) unlit = new Material(shader) { name = "EG_StoneMark" };
        return unlit;
    }

    /// <summary>A ring, 1 unit across (made once).</summary>
    static Sprite Ring()
    {
        if (ring) return ring;
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = new Vector2(x - n / 2f + 0.5f, y - n / 2f + 0.5f).magnitude / (n / 2f);
                byte a = d > 1f ? (byte)0 : d > 0.86f ? (byte)230 : (byte)(70 * d);   // (a solid edge, a faint fill)
                px[y * n + x] = new Color32(230, 40, 30, a);
            }
        tex.SetPixels32(px);
        tex.Apply();
        Object.DontDestroyOnLoad(tex);
        ring = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        Object.DontDestroyOnLoad(ring);
        return ring;
    }

    void OnDestroy() { if (mark) Destroy(mark.gameObject); }

    /// <summary>Its wick a little longer or shorter (up to that many seconds either way).</summary>
    public Bomb Wick(float by)
    {
        fuse = Mathf.Max(0.2f, fuse + Random.Range(-by, by));
        return this;
    }

    /// <summary>A bomb let fall where it stands (the Bomb Fish's, as it dies): a short drop, then lit that long.</summary>
    public static void Drop(Monster from, float lit)
    {
        var at = from.transform.position;
        var b = Throw(from, at + new Vector3(0f, 0.6f, 0f), at);
        b.flight = 0.25f;
        b.arc = 0.3f;
        b.fuse = lit;
    }

    // ---- kicked: you swing at a bomb on the ground, it slides off the way you aim and goes off where it stops ----

    public const float WickSpread = 0.3f;
    const float KickReach = 1.5f, KickSpeed = 10f, KickFriction = 14f;
    Vector3 slide;
    float kickedAt = -10f;

    /// <summary>Your swing near it (in reach, before you): sliding the way you aim; sliding, slowing, stopped by a wall.</summary>
    void Kick()
    {
        bool kickable = hitFor == 0 && kind.BombSpin != null;   // (a thrown bomb: not a catapult's stone)
        var me = LocalPlayer.Get();
        if (kickable && me && me.currentState == me.stateAttack && Time.time - kickedAt > 0.4f)
        {
            var aim = me.lookDir.sqrMagnitude > 0.01f ? (Vector3)me.lookDir.normalized : me.direction.ToVector().normalized;
            var toBomb = to - me.transform.position; toBomb.z = 0f;
            if (toBomb.magnitude <= KickReach && (toBomb.sqrMagnitude < 0.04f || Vector3.Dot(aim, toBomb.normalized) > 0.2f))
            {
                kickedAt = Time.time;
                slide = new Vector3(aim.x, aim.y, 0f) * KickSpeed;
                NetLog.Info($"Monsters: you kicked a {kind.Name} bomb", toast: false);
            }
        }
        if (slide.sqrMagnitude < 0.01f) return;
        float step = slide.magnitude * Time.deltaTime;
        // (a wall or a fence in the way: it stops against it; creatures and you it slides past)
        var hits = Physics2D.RaycastAll(to, slide.normalized, step + 0.15f);
        foreach (var h in hits)
            if (h.collider && !h.collider.isTrigger && !h.collider.GetComponentInParent<PlayerMaster>() && !h.collider.GetComponentInParent<Enemy>())
            {
                to += slide.normalized * Mathf.Max(0f, h.distance - 0.15f);
                slide = Vector3.zero;
                return;
            }
        to += slide * Time.deltaTime;
        slide = Vector3.MoveTowards(slide, Vector3.zero, KickFriction * Time.deltaTime);
    }

    void Update()
    {
        since += Time.deltaTime;
        if (since < 0f)   // (not yet: its spot marked, pulsing)
        {
            if (mark) mark.color = new Color(1f, 1f, 1f, 0.55f + 0.45f * Mathf.Sin(Time.time * 12f));
            return;
        }
        if (!sr.enabled && !blown) sr.enabled = true;
        if (mark && since >= flight) Destroy(mark.gameObject);   // (landed: the mark gone)
        if (since < flight)   // (in the air: an arc, spinning; a big one swelling toward the top of its arc)
        {
            float k = since / flight;
            transform.position = Vector3.Lerp(from, to, k) + new Vector3(0f, arc * 4f * k * (1f - k), 0f);
            transform.localScale = Vector3.one * big * (1f + (big > 1f ? 0.35f * 4f * k * (1f - k) : 0f));
            // tumbling end over end (its frames only flicker the fuse): rolling toward where it's going, upright as it lands
            transform.localRotation = Quaternion.Euler(0f, 0f, (to.x >= from.x ? -1f : 1f) * SpinTurns * 360f * k);
            Frame(kind.BombSpin, since);
        }
        else if (since < flight + fuse)   // (on the ground, its fuse burning; a swing sends it sliding)
        {
            Kick();
            transform.localRotation = Quaternion.identity;
            transform.position = to;
            transform.localScale = Vector3.one * big;
            Frame(kind.BombFuse ?? kind.BombSpin, since - flight);
        }
        else
        {
            transform.localScale = Vector3.one * big;
            transform.localRotation = Quaternion.identity;
            if (!blown) { blown = true; Blow(); gameFx = !kind.OwnBoom && GameBlast(transform.position); }
            float t = since - flight - fuse;
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
            if (hitFor > 0) data.Amount = hitFor;   // (a catapult stone from afar: its own heavy hit)
            else data.Amount = Mathf.RoundToInt(data.Amount * BlastDamage);   // (a blast hits far harder than the thrower's own swing would)
            // and it knocks you senseless: stunned (the game's stun: you, or a creature caught in it)
            data.Effects ??= new List<DamageEffect>();
            data.Effects.Add(new DamageEffect { Effect = EffectType.Stun, Chance = 100f, Duration = BlastStun });
            // everyone in its reach: you, other players' bodies, creatures of either side (friendly fire on; the
            // thrower itself is left out by the game)
            var hits = Physics2D.OverlapCircleAll(transform.position, kind.Blast).ToList();
            owner.ProcessColliders(hits, data, true);
            // the thrower too, if it's in the blast (its bomb kicked back at it, a catapult stone on the King who called
            // it in): the game leaves the attacker out, so it's hit here
            if (!owner.IsDead && owner.iDamageableCollider && hits.Contains(owner.iDamageableCollider))
            {
                var self = data;
                self.Target = owner;
                CombatSystem.Resolve(self, out _);   // (the game's hit on him: his guard, his health; no counter back)
            }
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
    Vector3 dir, ground, lift;   // (it flies over the ground, drawn at the height it left the shooter's hand)
    float range, flown, since, landedAt = -1f;
    SpriteRenderer sr;
    const float Clear = 0.5f;   // (walls ignored this far out: the shooter's own spot, a stair's edge it stands on)
    static readonly HashSet<string> said = new();

    /// <summary>A shot: leaving the shooter's feet (ground), drawn lift above them, flying dir.</summary>
    public static void Fire(Monster shooter, Attack sting, Vector3 ground, Vector3 lift, Vector3 dir)
    {
        var go = new GameObject($"EG_{shooter.Kind.Name}_Shot");
        go.transform.position = ground + lift;
        var s = go.AddComponent<Shot>();
        s.ground = ground;
        s.lift = lift;
        s.kind = shooter.Kind;
        s.owner = shooter.GetComponent<Enemy>();
        s.attack = Object.Instantiate(sting);   // (its damage, and what it leaves on you)
        s.dir = dir;
        s.range = shooter.Kind.ThrowRange + 3f;
        s.sr = go.AddComponent<SpriteRenderer>();
        var like = shooter.GetComponentsInChildren<SpriteRenderer>().FirstOrDefault(r => r.name == "EG_Look");
        if (like) { s.sr.sortingLayerID = like.sortingLayerID; s.sr.sortingOrder = 30001; }   // (in flight: over the floor's traps and props)
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
        // a bouncing one (the Gnoll's bones): a wall ahead turns it, as a ball off it
        if (kind.ShotBounce > 0f && flown > Clear)
            foreach (var h in Physics2D.RaycastAll(ground, dir, step + Size))
            {
                var c = h.collider;
                if (!c || c.isTrigger || c.GetComponentInParent<PlayerMaster>() || c.CompareTag("Enemy") || c.CompareTag("HitBox") || Floorish(c) || owner && c.transform.IsChildOf(owner.transform)) continue;
                dir = ((Vector3)Vector2.Reflect(dir, h.normal)).normalized;
                if (kind.ShotAim) transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - kind.ShotAngle);
                else sr.flipX = dir.x < 0f;
                break;
            }
        ground += dir * step;
        transform.position = ground + lift;
        flown += step;
        // what's here: you (or anything that takes hits but its own side) where it's drawn or over the ground below
        // it; something solid only on the ground (walls stand on it), and not before it's clear of the shooter
        Collider2D wall = null, target = null;
        var up = Physics2D.OverlapCircleAll(transform.position, Size);
        var low = Physics2D.OverlapCircleAll(ground, Size);
        var here = up.Concat(low).Where(c => c).Distinct().ToArray();
        foreach (var c in here)
        {
            if (owner && c.transform.IsChildOf(owner.transform)) continue;
            if (c.CompareTag("Enemy") || c.CompareTag("HitBox")) continue;
            // you (or another player, a companion): hit; traps and things on the floor (the arena's spike plates, pots):
            // flown over; walls and solid props: stopped
            if (c.GetComponentInParent<PlayerMaster>()) { if (c.GetComponent<IDamageable>() != null) target ??= c; continue; }
            if (Floorish(c)) continue;
            if (!c.isTrigger && flown > Clear && low.Contains(c)) wall ??= c;
        }
        if (target) Land(here, hurt: true, target.name);
        else if (wall && kind.ShotBounce <= 0f) Land(here, hurt: false, $"a wall ({wall.name})");
        else if (kind.ShotBounce > 0f ? since >= kind.ShotBounce : flown >= range) Land(here, hurt: false, kind.ShotBounce > 0f ? "its time" : "its range");
    }

    /// <summary>A trap or something lying on the floor (the arena's spikes and arrow traps, the game's floor traps):
    /// shots fly over it.</summary>
    static bool Floorish(Collider2D c)
    {
        for (var t = c.transform; t; t = t.parent)
        {
            string n = t.name.ToLowerInvariant();
            if (n.Contains("trap") || n.Contains("spike")) return true;
        }
        return false;
    }

    void Land(Collider2D[] here, bool hurt, string on)
    {
        landedAt = since;
        if (said.Add(kind.Name)) NetLog.Info($"Monsters: the {kind.Name}'s first shot landed on {on} after {flown:0.0}", toast: false);
        transform.rotation = Quaternion.identity;
        if (hurt && owner)
        {
            try
            {
                var data = new DamageData { Aggressor = owner, Attack = attack, Processors = new List<DamageDataProcessor> { new EnemyDamageDifficultyModifier() } };
                CombatSystem.ProcessAttackDamageData(ref data);
                var me = LocalPlayer.Get();
                int before = me ? me.currentHealth : 0;
                owner.ProcessColliders(here.ToList(), data, false);
                if (me && me.currentHealth < before) { Statuses.Disarm(kind.Disarm); Statuses.Silence(kind.Silence); Statuses.Blind(kind.Blind); Statuses.Mark(owner, kind.Mark); Statuses.Polymorph(kind.Polymorph); }   // (the acorn: blinded; an arrow: marked; the hex: a chicken)
                if (me && me.currentHealth < before && kind.Pull) owner.GetComponent<Monster>()?.PullIn(me);   // (the harpoon: reeled in)
            }
            catch (System.Exception x) { NetLog.Warn($"Monsters: a shot landed wrong: {x.Message}", toast: false); }
        }
        if (kind.ShotHit == null) Destroy(gameObject);
    }
}

/// <summary>A look that plays once where it's put, then goes (a Monk's heal on the one it heals).</summary>
internal sealed class Fx : MonoBehaviour
{
    Monsters.Anim anim;
    SpriteRenderer sr;
    float since, pace = 1f;
    static bool groupSaid;

    /// <summary>An effect played once there; pace: its speed (0.5: twice as long).</summary>
    public static void Play(Monsters.Anim a, Vector3 at, SpriteRenderer like, float size = 1f, int order = 2, float pace = 1f)
    {
        if (a == null || a.Frames == null || a.Frames.Length == 0) return;
        var go = new GameObject("EG_Fx");
        go.transform.position = at;
        go.transform.localScale = Vector3.one * size;
        var f = go.AddComponent<Fx>();
        f.anim = a;
        f.pace = pace;
        f.sr = go.AddComponent<SpriteRenderer>();
        if (like)
        {
            // drawn by the creature's sorting group if it's in one (its renderer's order only counts inside the group:
            // outside, "just behind it" was under the floor tiles, and the charge dust never showed)
            var group = like.GetComponentInParent<UnityEngine.Rendering.SortingGroup>();
            f.sr.sortingLayerID = group ? group.sortingLayerID : like.sortingLayerID;
            f.sr.sortingOrder = (group ? group.sortingOrder : like.sortingOrder) + order;
            if (!groupSaid) { groupSaid = true; NetLog.Info($"Monsters: effects drawn on {SortingLayer.IDToName(f.sr.sortingLayerID)} {f.sr.sortingOrder - order}{(group ? $" (its sorting group {group.name})" : "")}", toast: false); }
        }
        f.sr.sprite = a.Frames[0];
    }

    void Update()
    {
        since += Time.deltaTime * pace;
        int i = (int)(since * anim.Fps);
        if (i >= anim.Frames.Length) { Destroy(gameObject); return; }
        sr.sprite = anim.Frames[i];
    }
}
