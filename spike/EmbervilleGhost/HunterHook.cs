using Cysharp.Threading.Tasks;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace EmbervilleGhost;

/// <summary>
/// The Hunter's Hook: the Hollow Hunter's rope-and-hook (AcolyteTracker) for players. A game dagger (or sword) with a
/// mark in its rolled values, like the staffs, so saves keep it. Its normal attack is the weapon's own; right-click
/// throws the hook at the mouse. It hits the first creature on the way, and drags it to your feet, stunned (the game's
/// own knockback, run backwards: it slides along the navmesh, so walls stop it). A guardian is too heavy: the hook
/// pulls YOU to it instead, the way the Hunter pulls you. A wall or prop in the way: it clanks off and comes back.
/// /hook gives you one; descent guardians sometimes drop one.
/// (Connected: only creatures this copy runs can be dragged; the others still take the hit.)
/// </summary>
[HarmonyPatch]
internal static class HunterHook
{
    const ItemRandomValues.Attr Mark = (ItemRandomValues.Attr)77;
    const string MarkValue = "EG_HuntersHook";
    public const string Name = "Hunter's Hook";
    const float Range = 12f, FlySpeed = 24f, ReelSpeed = 30f, DragSpeed = 14f, PullSpeed = 13f, HitRadius = 0.6f, StopShort = 1.1f;

    public static bool IsHook(ItemObject item) =>
        item?.randomValues?.strAttributes != null && item.randomValues.strAttributes.Any(a => a != null && a.Key == Mark && a.Value == MarkValue);

    static PlayerMaster Me => Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
    public static bool HeldBy(PlayerMaster p) => p && IsHook(p.currentWeaponItem);

    // ---- its right-click is the hook: the weapon's own right-click attack never fires (every path to it) ----

    [HarmonyPatch(typeof(PlayerMaster), "StartPressing"), HarmonyPrefix]
    static bool NoAltAttack(PlayerMaster __instance, AttackType attackType) => attackType != AttackType.Secondary || !HeldBy(__instance);

    [HarmonyPatch(typeof(PlayerMaster), "PerformAttack", typeof(InputAction.CallbackContext)), HarmonyPrefix]
    static bool NoAltPerform(PlayerMaster __instance, InputAction.CallbackContext context) => !IsSecondary(__instance, context);

    [HarmonyPatch(typeof(PlayerMaster), "HoldStarted"), HarmonyPrefix]
    static bool NoAltHold(PlayerMaster __instance, InputAction.CallbackContext context) => !IsSecondary(__instance, context);

    static bool IsSecondary(PlayerMaster p, InputAction.CallbackContext context) =>
        HeldBy(p) && p.inputActions != null && context.action == p.inputActions.Player.SecondaryAttack;

    // ---- getting one ----

    public static ItemObject Make(int quality = 3)
    {
        DungeonLoot.Preload();
        var weapons = DungeonLoot.Pool?.OfType<CollectableWeapon>().Where(w => w.weapon != null).ToList();
        if (weapons == null || weapons.Count == 0) return null;
        var pick = weapons.Where(w => w.weapon.weaponCategory == WeaponCategory.Daggers).OrderBy(w => w.name).FirstOrDefault()
                   ?? weapons.Where(w => w.weapon.weaponCategory == WeaponCategory.OneHandedSwords).OrderBy(w => w.name).FirstOrDefault()
                   ?? weapons.OrderBy(w => w.name).First();
        var item = new ItemObject(pick, 1, quality);
        item.randomValues.strAttributes.Add(new ItemRandomValues.StrItemAttribute(Mark, MarkValue));
        return item;
    }

    public static void Give()
    {
        var item = Make();
        var me = LocalPlayer.Get();
        if (item == null || !me || !Singleton<Inventory>.HasMain) { NetLog.Warn("The item list is still loading: try again in a moment", toast: true); return; }
        int rest = Singleton<Inventory>.main.AddItem(item);
        if (rest > 0) item.DropToWorld(me.transform.position, rest, 1f);
        NetLog.Success($"{Name}: equip it and right-click a creature. Get over here!", toast: true);
    }

    public static void MaybeDrop(Vector3 at, int floor)
    {
        if (Random.value > 0.03f + 0.008f * Mathf.Clamp(floor, 0, 20)) return;
        var item = Make(Mathf.Clamp(1 + floor / 3, 1, 6));
        if (item == null) return;
        item.DropToWorld(at, 1, 1.4f);
        NetLog.Success($"A {Name} dropped!", toast: true);
    }

    [HarmonyPatch(typeof(ItemRandomValues), nameof(ItemRandomValues.CurrentName), MethodType.Getter), HarmonyPostfix]
    static void Named(ItemRandomValues __instance, ref string __result)
    {
        if (__instance.strAttributes != null && __instance.strAttributes.Any(a => a != null && a.Key == Mark && a.Value == MarkValue)) __result = Name;
    }

    [HarmonyPatch(typeof(ItemInfoWindow), nameof(ItemInfoWindow.SetItem)), HarmonyPostfix]
    static void Tooltip(ItemInfoWindow __instance, ItemObject item)
    {
        if (!IsHook(item)) { SideTip.Show(__instance, null); return; }   // another item: no panel
        if (__instance.itemName) __instance.itemName.text = $"<color=#E0A060>{Name}</color>";
        SideTip.Show(__instance, "<color=#E0A060>Right-click: throw the hook (a sixth of your Kymia). A creature it catches is dragged to your feet, stunned. A guardian is too heavy: it pulls you to it instead.</color>");
    }

    // ---- the throw ----

    enum Phase { None, Flying, Dragging, Pulling, Reeling, DraggingPlayer }

    static Phase phase;
    static Vector3 head, dir;
    static float flown, reach, phaseEnds;
    const float Throws = 6f;   // throws in a full Kymia bar
    static Enemy caught;

    public static void Tick()
    {
        var me = Me;
        HookOutfit.Tick(me);   // yellow clothes while it's in hand
        if (!me) { Drop(); return; }
        if (phase == Phase.None)
        {
            if (HeldBy(me) && Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame &&
                !me.IsDead && !ImGuiUnity.Typing && !Chat.IsOpen && !PlayerStatusUtil.LocalBusy && !ImGuiUnity.IsOpen)
            {
                // Kymia (mana) instead of a cooldown: a full bar is six throws
                float cost = me.MaxKymiaPoints / Throws;
                if (me.CurrentKymiaPoints + 0.01f < cost) me.NoKymiaFeedback();
                else if (Throw(me)) me.ConsumeKymia(Mathf.Min(cost, me.CurrentKymiaPoints));
            }
            return;
        }
        if (me.IsDead) { Drop(); return; }
        float dt = Time.deltaTime;
        var hand = Hand(me);
        switch (phase)
        {
            case Phase.Flying:
            {
                float step = FlySpeed * dt;
                var next = head + dir * step;
                var hit = CreatureOn(head, next, me);
                var who = Session.Pvp ? PlayerOn(head, next) : null;   // PvP on: other players can be hooked too
                if (who != null && (!hit || Closer(head, Hand(who), Centre(hit)))) { CaughtPlayer(me, who); break; }
                if (hit) { Caught(me, hit); break; }
                var wall = Blocked(head, next, me);
                if (wall) { NetLog.Info($"Hook: clank on {wall.name} (layer {LayerMask.LayerToName(wall.gameObject.layer)})", toast: false); Clank(next); break; }
                head = next; flown += step;
                if (flown >= reach) phase = Phase.Reeling;   // at the cursor: back it comes
                break;
            }
            case Phase.Dragging:
                if (!caught || caught.IsDead || !caught.isActiveAndEnabled) { phase = Phase.Reeling; break; }
                if (Drag(me, caught, dt) || Time.time >= phaseEnds) { Arrived(caught); phase = Phase.Reeling; break; }
                head = Centre(caught);
                break;
            case Phase.Pulling:
                if (!caught || caught.IsDead || Time.time >= phaseEnds ||
                    ((Vector2)(caught.transform.position - me.transform.position)).magnitude <= StopShort + 0.2f)
                { EndPull(me); break; }
                head = Centre(caught);
                break;
            case Phase.DraggingPlayer:
            {
                // their own copy drags them (HookNet.OnPull); ours just keeps the rope on them until they're here
                var them = Find(caughtPlayer);
                if (them == null || Time.time >= phaseEnds) { phase = Phase.Reeling; break; }
                head = Hand(them);
                if (((Vector2)(them.Avatar.Root.transform.position - me.transform.position)).magnitude <= StopShort + 0.4f) phase = Phase.Reeling;
                break;
            }
            case Phase.Reeling:
            {
                var back = hand - head;
                float d = back.magnitude, step = ReelSpeed * dt;
                if (d <= step) { Drop(); return; }
                head += back / d * step;
                break;
            }
        }
        DrawRope(hand, head);
    }

    /// <summary>The pull on us runs in physics steps (as the Hunter's does: rb.MovePosition).</summary>
    public static void FixedTick()
    {
        PulledTick();
        if (phase != Phase.Pulling) return;
        var me = Me;
        if (!me || !caught || !me.rb) return;
        var to = caught.transform.position - me.transform.position; to.z = 0f;
        float d = to.magnitude;
        if (d <= StopShort) return;
        me.rb.MovePosition(me.transform.position + to / d * Mathf.Min(PullSpeed * Time.fixedDeltaTime, d - StopShort));
    }

    static bool Throw(PlayerMaster me)
    {
        var cam = Camera.main;
        if (!cam) return false;
        var mp = Mouse.current.position.ReadValue();
        var aim = cam.ScreenToWorldPoint(new Vector3(mp.x, mp.y, -cam.transform.position.z)); aim.z = 0f;
        var hand = Hand(me);
        dir = aim - hand; dir.z = 0f;
        if (dir.sqrMagnitude < 0.01f) return false;
        reach = Mathf.Min(Range, dir.magnitude);   // to where you clicked (12 at most)
        dir.Normalize();
        head = hand; flown = 0f; caught = null; caughtPlayer = -1;
        phase = Phase.Flying;
        HookNet.SendThrow(HookSounds.Thrown(me.gameObject));   // the sound here, and on everyone's copy
        return true;
    }

    // ---- PvP: hooking another player ----

    static int caughtPlayer = -1;

    static RemotePlayer Find(int id)
    {
        if (id < 0 || HookNet.Plugin == null) return null;
        foreach (var p in HookNet.Plugin.Players)
            if (p.Id == id && p.Avatar != null && p.Avatar.Visible && p.SamePlace(LocalPlayer.Scene, LocalPlayer.Area)) return p;
        return null;
    }

    static Vector3 Hand(RemotePlayer p) { var v = p.Avatar.Root.transform.position + Vector3.up * 0.55f; v.z = 0f; return v; }

    static bool Closer(Vector3 from, Vector3 a, Vector3 b) => ((Vector2)(a - from)).sqrMagnitude <= ((Vector2)(b - from)).sqrMagnitude;

    /// <summary>Another player (in our area, alive) the hook passes this step.</summary>
    static RemotePlayer PlayerOn(Vector3 from, Vector3 to)
    {
        if (HookNet.Plugin == null || !HookNet.Plugin.Connected) return null;
        RemotePlayer best = null; float bestT = float.MaxValue;
        var seg = to - from;
        float len2 = Mathf.Max(1e-6f, seg.sqrMagnitude);
        foreach (var p in HookNet.Plugin.Players)
        {
            if (p.Avatar == null || !p.Avatar.Visible || p.Status == PlayerStatus.Dead || !p.SamePlace(LocalPlayer.Scene, LocalPlayer.Area)) continue;
            var c = Hand(p);
            float t = Mathf.Clamp01(Vector3.Dot(c - from, seg) / len2);
            if (((Vector2)(c - (from + seg * t))).magnitude < HitRadius && t < bestT) { bestT = t; best = p; }
        }
        return best;
    }

    static void CaughtPlayer(PlayerMaster me, RemotePlayer p)
    {
        caughtPlayer = p.Id;
        head = Hand(p);
        // the hit: through their stand-in's hurtbox, which sends it to them as a PvP hit (PlayerHits)
        var target = p.Avatar.Root.GetComponentInChildren<StandInTarget>(true);
        if (target)
        {
            int dmg = 4;
            try { var b = CombatSystem.GetBaseDamage(me.currentWeaponItem, me); dmg = Mathf.Max(2, Mathf.RoundToInt((b.x + b.y) * 0.5f * 0.6f)); } catch { }
            CombatSystem.Resolve(new DamageData(me, target, dmg, DamageHitType.Impact, KnockBack.None));
        }
        HookNet.Popup("Get over here!", head + Vector3.up * 0.4f, DamageResult.Critical);
        float d = ((Vector2)(p.Avatar.Root.transform.position - me.transform.position)).magnitude;
        float time = Mathf.Max(0.05f, (d - StopShort) / PullSpeed);
        HookNet.SendPull(p.Id, time + 1f);   // their copy moves them
        phase = Phase.DraggingPlayer;
        phaseEnds = Time.time + time + 1.5f;
    }

    // ---- PvP: being hooked (from HookNet.OnPull: someone's hook caught us) ----

    static int pulledBy = -1;
    static float pulledUntil;

    public static void Pulled(int by, float seconds)
    {
        var me = Me;
        if (!me || me.IsDead || !Session.Pvp) return;
        pulledBy = by;
        pulledUntil = Time.time + Mathf.Clamp(seconds, 0.2f, 3f);
        me.Static = true;   // held, as the Hunter's hook holds you
    }

    static void PulledTick()
    {
        if (pulledBy < 0) return;
        var me = Me;
        var them = Find(pulledBy);
        if (!me || me.IsDead || them == null || !me.rb || Time.time >= pulledUntil) { EndPulled(me, false); return; }
        var to = them.Avatar.Root.transform.position - me.transform.position; to.z = 0f;
        float d = to.magnitude;
        if (d <= StopShort + 0.05f) { EndPulled(me, true); return; }
        me.rb.MovePosition(me.transform.position + to / d * Mathf.Min(PullSpeed * Time.fixedDeltaTime, d - StopShort));
    }

    static void EndPulled(PlayerMaster me, bool arrived)
    {
        pulledBy = -1;
        if (!me || me.IsDead) return;
        if (me.Static) me.Static = false;
        if (arrived)
        {
            me.Stun(1f);
            HookNet.Popup("Dizzy!", me.transform.position + Vector3.up * 1.6f, DamageResult.Parry);
        }
    }

    static void Caught(PlayerMaster me, Enemy e)
    {
        caught = e;
        head = Centre(e);
        int dmg = 4;
        try { var b = CombatSystem.GetBaseDamage(me.currentWeaponItem, me); dmg = Mathf.Max(2, Mathf.RoundToInt((b.x + b.y) * 0.5f * 0.6f)); } catch { }
        CombatSystem.Resolve(new DamageData(me, e, dmg, DamageHitType.Impact, KnockBack.None));
        if (e.IsDead) { phase = Phase.Reeling; return; }
        HookNet.Popup("Get over here!", e.transform.position + Vector3.up * 0.8f, DamageResult.Critical);

        var gap = me.transform.position - e.transform.position; gap.z = 0f;
        float d = gap.magnitude;
        if (MiniBoss.IsBoss(e))
        {
            // too heavy: we go to it, held still (the game's Static state), as the Hunter's pull holds you
            me.Static = true;
            phase = Phase.Pulling;
            phaseEnds = Time.time + d / PullSpeed + 0.5f;
            return;
        }
        if (!EnemySync.Runs(e)) { phase = Phase.Reeling; return; }   // another copy runs it: just the hit
        float time = Mathf.Max(0.05f, (d - StopShort) / DragSpeed);
        e.Stun(time + 2f);   // held still on the way in (the arrival gives it its own dizzy spell)
        dragAgent = e.GetComponentInChildren<UnityEngine.AI.NavMeshAgent>();
        phase = Phase.Dragging;
        phaseEnds = Time.time + time + 1.5f;   // (stuck on a corner: it gives up after this)
    }

    static UnityEngine.AI.NavMeshAgent dragAgent;

    /// <summary>
    /// One frame of the drag: toward the spot just in front of us (where we are NOW), along the navmesh (Move keeps it
    /// on the walkable ground, so walls stop it). True = it's there.
    /// </summary>
    static bool Drag(PlayerMaster me, Enemy e, float dt)
    {
        var to = me.transform.position - e.transform.position; to.z = 0f;
        float d = to.magnitude;
        if (d <= StopShort + 0.05f) return true;
        var step = to / d * Mathf.Min(DragSpeed * dt, d - StopShort);
        if (dragAgent && dragAgent.isActiveAndEnabled && dragAgent.isOnNavMesh)
        {
            var before = dragAgent.transform.position;
            dragAgent.velocity = Vector3.zero;
            dragAgent.Move(step);
            if (((Vector2)(dragAgent.transform.position - before)).sqrMagnitude < step.sqrMagnitude * 0.01f) return true;   // against a wall: as far as it goes
        }
        else e.transform.position += step;
        return false;
    }

    /// <summary>At our feet: stopped dead, dizzy for a moment (the game's stun, its stars and all).</summary>
    static void Arrived(Enemy e)
    {
        if (!e || e.IsDead) return;
        if (dragAgent && dragAgent.isActiveAndEnabled && dragAgent.isOnNavMesh) { dragAgent.velocity = Vector3.zero; dragAgent.ResetPath(); }
        e.Stun(1.5f);
        HookNet.Popup("Dizzy!", e.transform.position + Vector3.up * 0.8f, DamageResult.Parry);
    }

    static void EndPull(PlayerMaster me)
    {
        if (me && !me.IsDead && me.Static) me.Static = false;
        phase = Phase.Reeling;
    }

    static void Clank(Vector3 at)
    {
        HookNet.Popup("clank", at, DamageResult.Parry);
        phase = Phase.Reeling;
    }

    static void Drop()
    {
        if (phase == Phase.Pulling) EndPull(Me);
        phase = Phase.None;
        caught = null;
        mine.Hide();
        HookNet.SendGone();
    }

    // ---- what the hook meets ----

    static Enemy CreatureOn(Vector3 from, Vector3 to, PlayerMaster me)
    {
        Enemy best = null; float bestT = float.MaxValue;
        var seg = to - from;
        float len2 = Mathf.Max(1e-6f, seg.sqrMagnitude);
        foreach (var e in EnemiesManager.AliveEnemies)
        {
            if (!e || !e.isActiveAndEnabled || e.IsDead || !e.CanTakeDamage || EnemySync.LocalOnly(e)) continue;
            var c = Centre(e);
            float t = Mathf.Clamp01(Vector3.Dot(c - from, seg) / len2);
            var p = from + seg * t;
            if (((Vector2)(c - p)).magnitude < HitRadius && t < bestT) { bestT = t; best = e; }
        }
        return best;
    }

    /// <summary>What stops the hook: only what would stop us walking (our body's collision layers), not the game's
    /// other invisible shapes (camera bounds, sight blockers, room areas...).</summary>
    static Collider2D Blocked(Vector3 from, Vector3 to, PlayerMaster me)
    {
        int mask = ~0;
        if (me.rb)
        {
            var body = me.rb.GetComponents<Collider2D>().FirstOrDefault(c => c && !c.isTrigger) ?? me.GetComponentsInChildren<Collider2D>().FirstOrDefault(c => c && !c.isTrigger && c.attachedRigidbody == me.rb);
            mask = Physics2D.GetLayerCollisionMask(body ? body.gameObject.layer : me.gameObject.layer);
        }
        foreach (var h in Physics2D.LinecastAll(from, to, mask))
        {
            var c = h.collider;
            if (!c || c.isTrigger) continue;
            if (me.rb && c.attachedRigidbody == me.rb) continue;
            if (c.GetComponentInParent<Enemy>()) continue;   // creatures are CreatureOn's
            if (c.GetComponentInParent<PlayerMaster>()) continue;
            return c;
        }
        return null;
    }

    static Vector3 Centre(Enemy e)
    {
        var p = e.transform.position + Vector3.up * 0.4f; p.z = 0f;
        return p;
    }

    static Vector3 Hand(PlayerMaster me)
    {
        var p = me.CenterOfMass ? me.CenterOfMass.position : me.transform.position + Vector3.up * 0.5f;
        p.z = 0f;
        return p;
    }

    // ---- the rope (ours; teammates' come through HookNet) ----

    static readonly RopeView mine = new RopeView();

    static void DrawRope(Vector3 from, Vector3 to)
    {
        mine.Draw(from, to);
        HookNet.SendHead(to);
    }
}
