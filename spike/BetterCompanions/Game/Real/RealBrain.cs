using System.Linq;
using BetterCompanions.Core;
using UnityEngine;

namespace BetterCompanions;

/// <summary>
/// The real companion's brain: the same decisions as the puppet's (Core.Brain: follow, catch up, go where it was
/// pinged, fight, dash), carried out the way a player would: by pushing its stick (the game moves it: speed from its
/// stats, walls, stairs) and pressing its buttons (the game swings, shoots, combos, spends Kymia and dodges). The
/// patches feed the stick and the aim in (RealPatches.Steer / Aim).
/// </summary>
internal static class RealBrain
{
    const float LookEvery = 0.25f, PressEvery = 0.2f, SendFor = 8f;

    public static Vector2 Stick, Aim;   // what its stick and aim say this frame

    static readonly Revive revive = new();
    static Mover mover;
    static Enemy target, cantReach;
    static float nextLook, nextPress, lastPress, sinceSwap = 10f, nextWeapon, writeAt, stuckFor, cutOffFor, sinceDash = 10f, sentUntil, cantReachUntil, nextSpawnTry;
    static Vector3? sentTo, orderAt;   // (orderAt: a ping on creatures: it fights there until they're gone)
    const float OrderRadius = 2.5f, OrderAreaRadius = 4f;
    /// <summary>Holding a spot (Shift with the order): it stays there instead of following you, and guards it.</summary>
    public static Vector3? Post { get; private set; }
    const float PostGuard = 4.5f, PostChase = 6.5f;   // (however far you go, it stays: only another scene brings it along)
    static Vector3 lastPos;
    static PlayerMaster ringOn;
    public static float ReviveProgress;

    static PlayerMaster Me => Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
    public static bool On => CompanionSave.Has && CompanionSave.Meta.real && !CompanionSave.Meta.dismissed;

    /// <summary>Each frame, in real mode: out if it should be, then think and act.</summary>
    public static void Tick()
    {
        var me = Me;
        var p = RealPlayer.Current;
        Stick = Vector2.zero;
        if (!me) return;
        if (!p)
        {
            if (On && Time.unscaledTime >= nextSpawnTry && Singleton<GameManager>.main.gameState == GameState.Playing)
            {
                nextSpawnTry = Time.unscaledTime + 2f;
                string said = RealPlayer.Spawn(CompanionSave.Data, Landing(me));
                if (RealPlayer.Current) { Arrived(RealPlayer.Current); }
                else Log.Info(said);
            }
            return;
        }
        if (ringOn != p) Arrived(p);

        float dt = Time.deltaTime;
        sinceDash += dt;
        sinceSwap += dt;
        if (CompanionSave.Dirty && Time.unscaledTime >= writeAt) { writeAt = Time.unscaledTime + 20f; CompanionSave.Dirty = false; CompanionSave.Write(); }
        bool down = RealPlayer.Down(p);
        if (down && !revive.Down) revive.Fall();
        Aggro.Tick(p.gameObject, down, me);
        if (down) { ReviveProgress = Revive(me, p, dt); return; }
        ReviveProgress = 0f;

        // a cutscene, a menu, you frozen: it waits too
        if (me.Static || Singleton<GameManager>.main.gameState != GameState.Playing) { Release(p); return; }

        if (sentTo is { } sent && (Time.time > sentUntil || Vector2.Distance(p.transform.position, sent) <= 0.6f))
        {
            if (waypoints.Count > 0 && Time.time <= sentUntil) { sentTo = waypoints.Dequeue(); sentUntil = Time.time + SendFor; }   // (on along the line)
            else { sentTo = null; waypoints.Clear(); nextLook = 0f; }
        }
        // (a fight that's dragged it too far from its post: let it go and walk back)
        if (Post is { } post1 && target && orderAt == null && Vector2.Distance(target.transform.position, post1) > PostChase) { target = null; nextLook = Time.time + 0.5f; }
        if (sentTo != null) target = null;
        else if (orderAt is { } order)
        {
            if (!target || target.IsDead || !target.isActiveAndEnabled) target = NearPoint(order, OrderAreaRadius);   // (the next one there)
            if (!target) { orderAt = null; nextLook = 0f; }
        }
        else if (Time.time >= nextLook) { nextLook = Time.time + LookEvery; target = PickTarget(Post ?? me.transform.position, Post != null ? PostGuard : Rules.Leash, p); }
        if (target && (!target.isActiveAndEnabled || target.IsDead)) target = null;

        var style = FightStyle(p);   // (a staff with no Kymia left for its spell: up close, swinging it)
        // (to the near edge of its body, not its middle: a real body bumps into a big one before reaching its middle)
        var owner = Post ?? me.transform.position;   // (holding a spot: it keeps to that instead of to you)
        var s = new Situation(Flat(p.transform.position), Flat(owner), target ? Flat(Near(target, p.transform.position)) : null,
            sentTo is { } st ? Flat(st) : null, false, style, stuckFor, sinceDash, 10f, cutOffFor);

        if (target && Time.time >= nextWeapon) { nextWeapon = Time.time + 0.3f; Weapons(p, target); }

        if (target && p.currentState != p.stateDodge)
        {
            int crowd = EnemiesManager.AliveEnemies.Count(e => e && !e.IsDead && Vector2.Distance(e.transform.position, p.transform.position) <= 2f);
            if (Brain.DashTo(s, Flat(target.transform.position), crowd, style == Style.Ranged) is { } dash) Dodge(p, new Vector2(dash.x - s.me.x, dash.y - s.me.y));
        }

        try { RealSkills.Tick(p, target, Rules.AttackRange(style), Vector2.Distance(p.transform.position, owner)); }
        catch (System.Exception e) { Log.Warn($"Companion skills: {e.Message}"); }

        var (move, where) = Brain.Decide(s);
        var at = new Vector3(where.x, where.y, 0f);
        switch (move)
        {
            case Move.Idle:
                stuckFor = cutOffFor = 0f;
                Release(p);
                break;
            case Move.Teleport:
                if (Post is { } far) { if (Mover.CanStand(far, far)) Teleport(p, far); }   // (holding: back to its post, never to you)
                else Teleport(p, Landing(me));
                break;
            case Move.Follow:
                Walk(p, Post ?? me.transform.position + Behind(me), Post != null ? 0.4f : 0.3f, dt);
                cutOffFor = mover.CutOff ? cutOffFor + dt : 0f;   // (you're somewhere it can't walk to: it comes to you)
                break;
            case Move.GoTo:
                Walk(p, at, target ? Rules.AttackRange(style) * 0.9f : 0.3f, dt);
                cutOffFor = 0f;
                if (mover.CutOff)
                {
                    if (target) { cantReach = target; cantReachUntil = Time.time + 3f; target = null; orderAt = null; }
                    else sentTo = null;
                }
                break;
            case Move.Attack:
                stuckFor = cutOffFor = 0f;
                if (target) Fight(p, target, style);
                break;
        }
        if (move != Move.Attack && Time.time - lastPress > 0.6f && p.currentState != p.stateAttack && p.nextAttack != AttackType.None) p.ClearComboBuffer();
    }

    static void Arrived(PlayerMaster p)
    {
        ringOn = p;
        mover = new Mover(p.transform);
        lastPos = p.transform.position;
        stuckFor = cutOffFor = 0f;
        target = null;
        sentTo = null; orderAt = null; Follow();
        if (revive.Down) revive.SceneChanged();
        CompanionMarks.Ring(p.transform);
        p.PlayExtraVFX("Spawn");
        try { RealSkills.Load(p); } catch (System.Exception e) { Log.Warn($"Companion skills: {e}"); }
    }

    // ---- moving: its stick ----

    static void Walk(PlayerMaster p, Vector3 to, float stopAt, float dt)
    {
        Stick = mover.Steer(to, stopAt);
        if (Stick != Vector2.zero) Aim = Stick;
        var pos = p.transform.position;
        bool far = Vector2.Distance(pos, to) > stopAt + 0.5f;
        float expected = p.characterStats.CurrentMoveSpeed * dt * 0.2f;
        stuckFor = far && ((Vector2)(pos - lastPos)).sqrMagnitude < expected * expected ? stuckFor + dt : 0f;
        lastPos = pos;
    }

    static void Release(PlayerMaster p) => Stick = Vector2.zero;

    static void Teleport(PlayerMaster p, Vector3 to)
    {
        p.StopVelocity();
        p.transform.position = to;
        if (p.rb) p.rb.position = to;
        mover.Teleport(to);
        lastPos = to;
        stuckFor = cutOffFor = 0f;
        sentTo = null; orderAt = null;
        p.PlayExtraVFX("Teleport");
    }

    // ---- fighting: its buttons ----

    static void Fight(PlayerMaster p, Enemy e, Style style)
    {
        Stick = Vector2.zero;
        Aim = (Vector2)(e.transform.position - p.transform.position);
        if (Time.time < nextPress) return;
        nextPress = Time.time + PressEvery;   // (like someone tapping the button: a press queues the next swing of the combo)
        if (Press(p, ShootingAttack(p))) lastPress = Time.time;
    }

    /// <summary>The weapon's attack that shoots, for a bow or staff (a staff's is its right-click); else the left-click.</summary>
    /// <summary>
    /// Bow (or staff) or sword: the one it holds or its spare, by how far the fight is (Brain.WantRanged), swapped
    /// between swings, not more often than Brain.SwapCooldown.
    /// </summary>
    static void Weapons(PlayerMaster p, Enemy e)
    {
        if (sinceSwap < Brain.SwapCooldown || p.currentState == p.stateAttack || p.currentState == p.stateDodge) return;
        bool rangedNow = StyleOf(p) == Style.Ranged;
        int ranged = Best(true, p), melee = Best(false, p);
        bool want = rangedNow;
        if (rangedNow && !CanShoot(p, p.currentWeapon)) want = false;   // (out of Kymia for its spell: a close-up weapon, if it has one)
        else if (rangedNow ? melee >= 0 : ranged >= 0)
        {
            float d = Vector2.Distance(Near(e, p.transform.position), p.transform.position);
            int crowd = EnemiesManager.AliveEnemies.Count(x => x && !x.IsDead && Vector2.Distance(x.transform.position, p.transform.position) <= 2f);
            want = Brain.WantRanged(d, crowd, hasMelee: true, hasRanged: true, rangedNow);
        }
        int k = want ? ranged : melee;
        if (k < 0) return;
        if (want == rangedNow && Damage(RealGear.Swap(k)) <= Damage(RealGear.Worn(GearSlot.MainHand)) && (!rangedNow || CanShoot(p, p.currentWeapon))) return;   // (what it holds hits hardest of that kind)
        if (RealGear.SwapSpare(k)) { sinceSwap = 0f; p.ClearComboBuffer(); }
    }

    /// <summary>Its hardest-hitting swap weapon of that kind (bow or staff, or not), or -1; a ranged one only if it has the Kymia to shoot it.</summary>
    static int Best(bool ranged, PlayerMaster p)
    {
        int best = -1;
        for (int k = 0; k < RealGear.SwapSlots; k++)
            if (RealGear.Swap(k)?.data is CollectableWeapon w && (Rules.StyleOf((int)w.weapon.weaponCategory) == Style.Ranged) == ranged
                && (!ranged || CanShoot(p, w.weapon))
                && (best < 0 || Damage(RealGear.Swap(k)) > Damage(RealGear.Swap(best))))
                best = k;
        return best;
    }

    /// <summary>A weapon's damage roll, the middle of its range.</summary>
    static float Damage(ItemObject o) => o?.randomValues == null ? 0f : (o.randomValues.damageRange.x + o.randomValues.damageRange.y) / 2f;

    /// <summary>The point of the creature's body nearest to there (its middle if it has no solid body).</summary>
    static Vector3 Near(Enemy e, Vector3 from)
    {
        Vector2 best = e.transform.position;
        float bestD = float.MaxValue;
        foreach (var c in e.GetComponentsInChildren<Collider2D>())
        {
            if (!c || !c.enabled || c.isTrigger) continue;
            var pt = c.ClosestPoint(from);
            float d = (pt - (Vector2)from).sqrMagnitude;
            if (d < bestD) { bestD = d; best = pt; }
        }
        return best;
    }

    static float whyAt;

    /// <summary>It wanted to attack and the game said no: why (once in a while, in the log).</summary>
    static void Why(PlayerMaster p, string what)
    {
        if (Time.time < whyAt) return;
        whyAt = Time.time + 3f;
        Log.Info($"Companion (real): can't attack: {what} (state {p.currentState?.GetType().Name}, weapon {(p.currentWeapon ? p.currentWeapon.name : "none")}, attacks {(p.CanPerfomAttacks ? "on" : "off")}, combo move {p.performingComboMove})");
    }

    static AttackType ShootingAttack(PlayerMaster p) => ShotOf(p.currentWeapon) is { } shot && Affords(p, shot) ? shot : AttackType.Primary;

    /// <summary>A bow's or staff's attack that shoots (a staff's is its right-click), or null.</summary>
    static AttackType? ShotOf(Weapon w)
    {
        if (!w || Rules.StyleOf((int)w.weaponCategory) != Style.Ranged) return null;
        bool primaryShoots = w.primaryAttack != null && w.primaryAttack.Any(a => a && a.projectile);
        bool secondaryShoots = w.secondaryAttack != null && w.secondaryAttack.Any(a => a && a.projectile);
        return !primaryShoots && secondaryShoots ? AttackType.Secondary : primaryShoots ? AttackType.Primary : null;
    }

    /// <summary>Enough Kymia for that attack's first hit (checked quietly: the game's own check shows "No Kymia").</summary>
    static bool Affords(PlayerMaster p, AttackType type, Weapon w = null)
    {
        w = w ? w : p.currentWeapon;
        var list = type == AttackType.Secondary ? w?.secondaryAttack : w?.primaryAttack;
        int cost = list != null && list.Count > 0 && list[0] ? list[0].kymiaCost : 0;
        return cost <= 0 || p.CurrentKymiaPoints >= cost;
    }

    /// <summary>Can it shoot this weapon now (a bow; a staff with the Kymia for its spell)?</summary>
    static bool CanShoot(PlayerMaster p, Weapon w) => w && ShotOf(w) is { } shot && Affords(p, shot, w);

    /// <summary>How it fights now: its weapon's style, but a bow or staff it can't shoot is swung up close.</summary>
    static Style FightStyle(PlayerMaster p)
    {
        var style = StyleOf(p);
        return style == Style.Ranged && !CanShoot(p, p.currentWeapon) ? Style.Melee : style;
    }

    /// <summary>An attack button press (PlayerMaster.PerformAttack, without the mouse and input checks).</summary>
    static bool Press(PlayerMaster p, AttackType type)
    {
        if (!p.currentWeapon) { Why(p, "no weapon"); return false; }
        if (Time.timeScale == 0f || p.currentState == p.stateDodge) return false;
        if (!p.CanPerfomAttacks || p.performingComboMove) { Why(p, "the game has attacks off"); return false; }
        p.nextAttack = type;
        if (p.currentState == p.stateAttack || p.currentState == p.stateComboCheck) return true;   // (queued: the next swing)
        if (!p.UpdateWeaponAttack()) { p.ClearComboBuffer(); Why(p, "the weapon has no such attack"); return false; }
        int cost = Mathf.RoundToInt(p.currentWeapon.CurrentAttack.kymiaCost * p.AttackKymiaConsumptionMultiplier.Value);
        if (cost > 0 && !p.CheckAvailableKymia(cost)) { p.ClearComboBuffer(); Why(p, "not enough Kymia"); return false; }
        p.ConsumeKymia(cost, cost > 0);
        p.currentAttack = p.currentWeapon.CurrentAttack;
        p.ChangeState(p.stateAttack);
        return true;
    }

    /// <summary>The dodge button (PlayerMaster.PerformDodge), that way.</summary>
    static void Dodge(PlayerMaster p, Vector2 dir)
    {
        if (Time.timeScale == 0f || !p.canDodge || p.onDodgeCooldown || p.currentState == p.stateDodge || dir.sqrMagnitude < 0.0001f) return;
        p.input = dir.normalized;   // (the dodge goes the way the stick points)
        Stick = dir.normalized;
        p.currentDodge = p.equippedDodge;
        RealPatches.DodgeCooldown(p);
        p.ChangeState(p.stateDodge);
        sinceDash = 0f;
    }

    // ---- down and up ----

    static float Revive(PlayerMaster me, PlayerMaster p, float dt)
    {
        bool near = Vector2.Distance(me.transform.position, p.transform.position) <= 1.5f;
        bool holding = near && !me.IsDead && !Hooks.Typing() && MainInput.Actions.Shared.Interaction.IsPressed();
        float progress = revive.Hold(holding, dt);
        if (!revive.Down) { RealPlayer.Up(); p.PlayExtraVFX("LifeRecovery"); Hooks.Say($"{CompanionSave.Data.playerName} is back on their feet."); }
        return progress;
    }

    /// <summary>A teammate got it up on their screen (co-op).</summary>
    public static void RevivedByTeammate()
    {
        if (!revive.Down) return;
        while (revive.Down) revive.Hold(true, 0.5f);
        RealPlayer.Up();
    }

    public static bool DownNear(Vector3 at, float reach)
    {
        var p = RealPlayer.Current;
        return p && RealPlayer.Down(p) && Vector2.Distance(at, p.transform.position) <= reach;
    }

    // ---- orders ----

    /// <summary>Back to following you (a middle click on it, a new order without Shift, another scene).</summary>
    public static void Follow()
    {
        Post = null;
        CompanionMarks.Post(null);
    }

    static void Hold(Vector3 at)
    {
        at.z = 0f;
        bool was = Post != null;
        Post = at;
        CompanionMarks.Post(at);
        if (!was) Hooks.Say($"{CompanionSave.Data?.playerName} holds there (fights what comes near). Middle-click it to have it follow you again.");
    }

    /// <summary>A ping (middle click): drop everything and go there; on creatures, fight them (and the rest around there).
    /// With Shift (<paramref name="stay"/>): then stay there and guard it.</summary>
    public static void Send(Vector3 point, bool stay = false)
    {
        var p = RealPlayer.Current;
        if (!p || RealPlayer.Down(p)) return;
        if (stay) Hold(point); else Follow();
        waypoints.Clear();
        point.z = 0f;
        orderAt = null;
        target = null;
        var there = NearPoint(point, OrderRadius);
        if (there) { orderAt = point; target = there; sentTo = null; return; }   // creatures there: fight them
        sentTo = point;
        sentUntil = Time.time + SendFor;
    }

    /// <summary>The creature nearest a point (fauna too: a ping on chickens means them), if one's that close.</summary>
    static Enemy NearPoint(Vector3 point, float within)
    {
        Enemy best = null;
        float bestD = within;
        foreach (var e in EnemiesManager.AliveEnemies)
        {
            if (!e || !e.isActiveAndEnabled || e.IsDead || !e.CanTakeDamage || Hooks.Ignore(e)) continue;
            float d = Vector2.Distance(e.transform.position, point);
            if (d <= bestD) { bestD = d; best = e; }
        }
        return best;
    }

    static readonly System.Collections.Generic.Queue<Vector3> waypoints = new();
    const float WaypointSpacing = 1.2f;

    /// <summary>A drawn line (middle mouse held and dragged): it walks it point by point, breaking off any fight.
    /// With Shift (<paramref name="stay"/>): it stays at the end and guards it.</summary>
    public static void SendPath(System.Collections.Generic.IList<Vector3> points, bool stay = false)
    {
        var p = RealPlayer.Current;
        if (!p || RealPlayer.Down(p) || points == null || points.Count == 0) return;
        if (stay) Hold(points[points.Count - 1]); else Follow();
        orderAt = null;
        waypoints.Clear();
        var last = points[0];
        waypoints.Enqueue(new Vector3(last.x, last.y, 0f));
        for (int i = 1; i < points.Count; i++)
        {
            bool end = i == points.Count - 1;
            if (!end && Vector2.Distance(points[i], last) < WaypointSpacing) continue;   // (a point every step or so)
            last = points[i];
            waypoints.Enqueue(new Vector3(last.x, last.y, 0f));
        }
        sentTo = waypoints.Dequeue();
        sentUntil = Time.time + SendFor;
        target = null;
        orderAt = null;
    }

    /// <summary>The creature to fight: the nearest within its reach that's within <paramref name="leash"/> of <paramref name="owner"/> (you, or the spot it holds).</summary>
    static Enemy PickTarget(Vector3 owner, float leash, PlayerMaster p)
    {
        var locked = Hooks.LockTarget?.Invoke();
        if (locked && !locked.IsDead && locked.isActiveAndEnabled) return locked;
        float range = Rules.AttackRange(StyleOf(p)) + 6f;
        Enemy best = null;
        float bestD = float.MaxValue;
        foreach (var e in EnemiesManager.AliveEnemies)
        {
            if (e == cantReach && Time.time < cantReachUntil) continue;
            if (!e || !e.isActiveAndEnabled || e.IsDead || !e.CanTakeDamage || Hooks.Ignore(e)) continue;
            if (e.enemyType == EnemyType.None) continue;   // (fauna)
            float d = Vector2.Distance(e.transform.position, p.transform.position);
            if (d > range || Vector2.Distance(e.transform.position, owner) > leash) continue;
            if (d < bestD) { bestD = d; best = e; }
        }
        return best;
    }

    static Style StyleOf(PlayerMaster p) => Rules.StyleOf(p.currentWeapon ? (int)p.currentWeapon.weaponCategory : 14);

    static Vector3 Behind(PlayerMaster me) => new(me.isFliped ? Rules.FollowGap * 0.8f : -Rules.FollowGap * 0.8f, -0.3f, 0f);

    static Vector3 Landing(PlayerMaster me)
    {
        var at = me.transform.position;
        var back = Behind(me);
        foreach (var off in new[] { back, new Vector3(-back.x, back.y, 0f) })
            if (Mover.CanStand(at + off, at)) return at + off;
        return at;
    }

    static (float x, float y) Flat(Vector3 v) => (v.x, v.y);
}
