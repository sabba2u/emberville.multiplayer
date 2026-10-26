using System.Linq;
using BetterCompanions.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BetterCompanions;

/// <summary>
/// The companion in the world: out with you or dismissed. Its body is rebuilt from the save when the save changes,
/// and after a change of scene (the body belongs to the scene it was made in). Each frame its brain (Core.Brain)
/// decides: follow, catch up by teleport, go where it was sent, close in, or attack, which weapon to hold and when
/// to dash; this carries it out.
/// </summary>
internal static class Companion
{
    public static CompanionBody Body;
    public static XpLedger Ledger = new();
    static readonly Revive revive = new();   // (kept across rebuilds: downed when the scene changes = up at half)
    static float healthFrac = 1f;
    static CompanionLabel label;
    public static float ReviveProgress;
    public static bool Out => CompanionSave.Has && !CompanionSave.Meta.dismissed;
    public static string State => !CompanionSave.Has ? "none" : CompanionSave.Meta.dismissed ? "dismissed" : Body != null && Body.Unit && Body.Unit.Revive.Down ? "downed" : "with you";

    const float WalkSpeed = 3f, RunSpeed = 5.5f, DashSpeed = 15f, SendFor = 8f, LookEvery = 0.25f;

    static Enemy target;
    static float nextLook, nextAttack, stuckFor, cutOffFor, sinceDash = 10f, sinceSwap = 10f, sentUntil, writeAt;
    static Vector3? sentTo;
    static Enemy cantReach;
    static float cantReachUntil;
    static Vector3? dashTo;
    static Vector3 lastPos;

    static PlayerMaster Me => Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;

    static bool reloadSoon;
    /// <summary>A character was loaded: rebuild next frame (not inside the game's load handlers).</summary>
    public static void ReloadSoon() => reloadSoon = true;

    /// <summary>The save changed (copied, new, loaded, dismissed): rebuild the body from it.</summary>
    public static void Reload()
    {
        RealPlayer.Remove();   // (real mode makes it again from the save, next frame)
        Aggro.ReleaseAll();
        Body?.Destroy();
        Body = null;
        target = null;
        sentTo = null;
        dashTo = null;
        Spawn();
    }

    static void Spawn()
    {
        var me = Me;
        if (!Out || !me) return;
        Body = CompanionBody.Create(CompanionSave.Data, Landing(me));
        if (Body == null) return;
        Body.Fx("Spawn");
        lastPos = Body.Position;
        var unit = Body.Unit;
        unit.Revive = revive;
        unit.Hurt = Hurt;
        if (revive.Down) { revive.SceneChanged(); healthFrac = revive.Health01; }
        unit.Health = Mathf.Clamp(Mathf.RoundToInt(unit.MaxHealth * healthFrac), 1, unit.MaxHealth);
        label = Hooks.DrawsHud ? null : new CompanionLabel(Body.Root);   // (with the multiplayer mod: its HUD draws the tag)
        CompanionMarks.Ring(Body.Root.transform);
    }

    public static string Dismiss()
    {
        if (!CompanionSave.Has) return "Companion: you have none.";
        CompanionSave.Meta.dismissed = true;
        CompanionSave.Write();
        Reload();
        return $"Companion: {CompanionSave.Data.playerName} heads home. /companion call brings them back.";
    }

    public static string Call()
    {
        if (!CompanionSave.Has) return "Companion: you have none. /companion 2 copies save slot 2.";
        CompanionSave.Meta.dismissed = false;
        CompanionSave.Write();
        Reload();
        return $"Companion: {CompanionSave.Data.playerName} is with you.";
    }

    /// <summary>A middle-click order: drop whatever it's doing (a fight too) and go there; then it fights what's there.</summary>
    /// <summary>A drawn line: the real companion walks it; the old body goes to its end.</summary>
    public static void SendPath(System.Collections.Generic.IList<Vector3> points, bool stay = false)
    {
        if (points == null || points.Count == 0) return;
        if (RealBrain.On) RealBrain.SendPath(points, stay);
        else Send(points[points.Count - 1]);
    }

    /// <summary>Holding a spot: back to following you.</summary>
    public static void Follow()
    {
        if (RealBrain.On && RealBrain.Post != null) { RealBrain.Follow(); Hooks.Say($"{CompanionSave.Data?.playerName} follows you again."); }
    }

    public static void Send(Vector3 point, bool stay = false)
    {
        if (RealBrain.On) { RealBrain.Send(point, stay); return; }
        if (Body == null || Body.Lost || Body.Unit.Revive.Down) return;
        point.z = 0f;
        sentTo = point;
        sentUntil = Time.time + SendFor;
        target = null;
        dashTo = null;
        nextLook = 0f;
    }

    public static void Tick()
    {
        if (RealBrain.On || RealPlayer.Current)   // (the real companion: a second, real player)
        {
            if (Body != null) { Body.Destroy(); Body = null; label = null; }
            reloadSoon = false;
            if (!RealBrain.On) RealPlayer.Remove();
            else RealBrain.Tick();
            return;
        }
        if (reloadSoon) { reloadSoon = false; Reload(); }
        if (Body != null && Body.Lost) { Body = null; Aggro.ReleaseAll(); }   // its scene went
        var me = Me;
        if (Body == null)
        {
            if (Out && me && Time.frameCount % 30 == 0) Spawn();
            return;
        }
        if (!me) return;
        if (CompanionSave.Dirty && Time.unscaledTime >= writeAt) { writeAt = Time.unscaledTime + 20f; CompanionSave.Dirty = false; CompanionSave.Write(); }
        StandaloneSend();

        float dt = Time.deltaTime;
        sinceDash += dt;
        sinceSwap += dt;
        Arms.Tick(Body);
        var unit = Body.Unit;
        Aggro.Tick(Body.Root, unit.Revive.Down, me);
        float progress = ReviveProgress = Revive(me, dt);
        healthFrac = unit.MaxHealth > 0 ? (float)unit.Health / unit.MaxHealth : 1f;
        label?.Show(CompanionSave.Data.playerName, unit.Health, unit.MaxHealth, revive.Down, progress);
        if (revive.Down) { Body.SetWalk(0f); return; }

        if (dashTo is { } dash)
        {
            Body.Mover.Slide(dash, DashSpeed, dt);
            if (Vector2.Distance(Body.Position, dash) < 0.05f || sinceDash > 0.3f) dashTo = null;
            return;
        }

        // sent somewhere: it goes there first (no fighting on the way), until it's there or gives up
        if (sentTo is { } sent && (Time.time > sentUntil || Vector2.Distance(Body.Position, sent) <= 0.6f)) { sentTo = null; nextLook = 0f; }
        if (sentTo != null) target = null;
        else if (Time.time >= nextLook) { nextLook = Time.time + LookEvery; target = PickTarget(me); }
        if (target && (!target.isActiveAndEnabled || target.IsDead)) target = null;

        var s = new Situation(Flat(Body.Position), Flat(me.transform.position), target ? Flat(target.transform.position) : null,
            sentTo is { } st ? Flat(st) : null, unit.Revive.Down, Arms.StyleNow(Body), stuckFor, sinceDash, sinceSwap, cutOffFor);

        if (target && !unit.Revive.Down) Weapons(s, target);

        var (move, where) = Brain.Decide(s);
        var at = new Vector3(where.x, where.y, 0f);
        switch (move)
        {
            case Move.Idle:
                Body.SetWalk(0f);
                stuckFor = cutOffFor = 0f;
                break;
            case Move.Teleport:
                Body.Mover.Teleport(Landing(me));
                Body.Fx("Teleport");
                stuckFor = cutOffFor = 0f;
                sentTo = null;
                break;
            case Move.Follow:
            {
                float far = Vector2.Distance(Body.Position, me.transform.position);
                Walk(me.transform.position + Behind(me), far > 4f ? RunSpeed : WalkSpeed, dt, 0.3f);
                cutOffFor = Body.Mover.CutOff ? cutOffFor + dt : 0f;   // (you're somewhere it can't walk to: it comes to you)
                break;
            }
            case Move.GoTo:
                Walk(at, RunSpeed, dt, target ? Rules.AttackRange(s.style) * 0.9f : 0.3f);
                cutOffFor = 0f;
                if (Body.Mover.CutOff)   // (can't get there: never mind, and leave that one be for a while)
                {
                    if (target) { cantReach = target; cantReachUntil = Time.time + 3f; target = null; }
                    else sentTo = null;
                }
                break;
            case Move.Attack:
                Body.SetWalk(0f);
                stuckFor = 0f;
                if (target) Body.Face((Vector2)(target.transform.position - Body.Position));
                if (target && Time.time >= nextAttack) { nextAttack = Time.time + Arms.Cooldown(Body); Arms.Strike(Body, target, Ledger); }
                break;
        }
    }

    // ---- hurt, down, up ----

    /// <summary>An enemy's hit (already through its defence and block, CombatSystem.GetDamageData).</summary>
    static void Hurt(DamageData d)
    {
        if (Body == null || revive.Down) return;
        if (d.Aggressor is PlayerMaster || d.Aggressor is CompanionUnit) return;   // (no friendly fire)
        var unit = Body.Unit;
        var at = Body.Position;
        if (d.Result == DamageResult.Parry) Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, "Dodged", at, DamageResult.Parry));
        if (d.Result == DamageResult.Block) Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, "Blocked", at, DamageResult.Block));
        if (d.Effects != null && unit.Effects) foreach (var effect in d.Effects) unit.Effects.HandleEffect(effect);
        if (d.Amount <= 0) return;
        unit.Health -= d.Amount;
        Bus<UIDamageEvent>.Invoke(new UIDamageEvent(d.Amount, "", at, DamageResult.Success));
        if (d.Aggressor is Enemy e) Aggro.LastHitByCompanion = e;   // (it's fighting us: it keeps at it)
        if (unit.Health > 0) { Body.Play("Damage"); return; }
        unit.Health = 0;
        revive.Fall();
        Body.Play("Dead");
        Aggro.ReleaseAll();
        target = null;
        sentTo = null;
        dashTo = null;
        Hooks.Say($"{CompanionSave.Data.playerName} is down! Hold F next to them to revive.");
    }

    /// <summary>Downed: someone holding F next to it brings it back (2 s). The progress, 0..1.</summary>
    static float Revive(PlayerMaster me, float dt)
    {
        if (!revive.Down) return 0f;
        bool near = Vector2.Distance(me.transform.position, Body.Position) <= 1.5f;
        bool holding = near && !me.IsDead && !Hooks.Typing() && MainInput.Actions.Shared.Interaction.IsPressed();
        float p = revive.Hold(holding, dt);
        if (!revive.Down) GetUp();
        return p;
    }

    /// <summary>A teammate held F at it on their screen (co-op): up it gets.</summary>
    public static void RevivedByTeammate()
    {
        if (RealBrain.On) { RealBrain.RevivedByTeammate(); return; }
        if (!revive.Down) return;
        while (revive.Down) revive.Hold(true, 0.5f);
        healthFrac = revive.Health01;   // (if the body is between scenes, it comes back at that)
        GetUp();
    }

    public static void GetUp()
    {
        if (Body == null) return;
        var unit = Body.Unit;
        unit.Health = Mathf.Max(1, Mathf.RoundToInt(unit.MaxHealth * revive.Health01));
        Body.Rise();
        Body.Fx("LifeRecovery");
        Hooks.Say($"{CompanionSave.Data.playerName} is back on their feet.");
    }

    /// <summary>Which favourite to hold, and whether to dash (toward a fleeing target, or out of a crowd).</summary>
    static void Weapons(Situation s, Enemy e)
    {
        float d = Vector2.Distance(Body.Position, e.transform.position);
        int crowd = EnemiesManager.AliveEnemies.Count(x => x && !x.IsDead && x.isActiveAndEnabled && Vector2.Distance(x.transform.position, Body.Position) <= 2f);
        bool rangedNow = s.style == Style.Ranged;
        bool want = Brain.WantRanged(d, crowd, Arms.Has(Body, Style.Melee), Arms.Has(Body, Style.Ranged), rangedNow);
        if (want != rangedNow && sinceSwap >= Brain.SwapCooldown && Arms.Swap(Body, want ? Style.Ranged : Style.Melee))
        {
            sinceSwap = 0f;
            nextAttack = Mathf.Max(nextAttack, Time.time + 0.3f);
        }
        if (Brain.DashTo(s, Flat(e.transform.position), crowd, Arms.StyleNow(Body) == Style.Ranged) is { } p)
        {
            dashTo = new Vector3(p.x, p.y, 0f);
            sinceDash = 0f;
            Body.Face((Vector2)(dashTo.Value - Body.Position));
            Body.Play("Dash");
        }
    }

    static void Walk(Vector3 to, float speed, float dt, float stopAt)
    {
        var dir = Body.Mover.MoveToward(to, speed, dt, stopAt);
        if (dir == Vector2.zero) { Body.SetWalk(0f); }
        else { Body.Face(dir); Body.SetWalk(speed); }
        var pos = Body.Position;
        bool far = Vector2.Distance(pos, to) > stopAt + 0.5f;
        stuckFor = far && ((Vector2)(pos - lastPos)).sqrMagnitude < (speed * dt * 0.2f) * (speed * dt * 0.2f) ? stuckFor + dt : 0f;
        lastPos = pos;
    }

    /// <summary>The nearest creature worth fighting near the companion (the player's lock-on first).</summary>
    static Enemy PickTarget(PlayerMaster me)
    {
        var locked = Hooks.LockTarget?.Invoke();
        if (locked && !locked.IsDead && locked.isActiveAndEnabled) return locked;
        float range = Rules.AttackRange(Arms.StyleNow(Body)) + 6f;
        Enemy best = null;
        float bestD = float.MaxValue;
        foreach (var e in EnemiesManager.AliveEnemies)
        {
            if (e == cantReach && Time.time < cantReachUntil) continue;
            if (!e || !e.isActiveAndEnabled || e.IsDead || !e.CanTakeDamage || Hooks.Ignore(e)) continue;
            if (e.enemyType == EnemyType.None) continue;   // (fauna: chickens and the like are enemies too)
            float d = Vector2.Distance(e.transform.position, Body.Position);
            if (d > range || Vector2.Distance(e.transform.position, me.transform.position) > Rules.Leash) continue;
            if (d < bestD) { bestD = d; best = e; }
        }
        return best;
    }

    /// <summary>On its own (no multiplayer mod to hand us its middle clicks): a middle click sends the companion.</summary>
    static void StandaloneSend()
    {
        if (Hooks.HasRally || Mouse.current == null || !Mouse.current.middleButton.wasPressedThisFrame || Hooks.Busy() || Hooks.Typing()) return;
        var cam = Camera.main;
        if (!cam) return;
        var p = cam.ScreenToWorldPoint(Mouse.current.position.ReadValue());
        var at = new Vector3(p.x, p.y, 0f);
        var root = Plugin.CompanionRoot;
        if (root && Vector2.Distance(root.transform.position, at) <= 0.8f) { Follow(); return; }   // (a click on it: follow me)
        bool shift = Keyboard.current != null && Keyboard.current.shiftKey.isPressed;
        Send(at, shift);
    }

    static Vector3 Behind(PlayerMaster me) => new(me.isFliped ? Rules.FollowGap * 0.8f : -Rules.FollowGap * 0.8f, -0.3f, 0f);

    /// <summary>Where to appear next to you (spawn, teleport): behind you, else in front, else right where you stand, never inside a wall or door.</summary>
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
