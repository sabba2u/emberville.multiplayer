using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Stunned is standing still. The game's stun stops reading your movement keys but never stops your body: whatever
/// speed you had when it hit carried on the whole stun (you kept running). Stopped as it starts, and held still each
/// step (a knock back still moves you: it moves your body itself, not by its speed).
/// </summary>
[HarmonyPatch]
internal static class StunHold
{
    [HarmonyPatch(typeof(PlayerStun), nameof(PlayerStun.EnterState)), HarmonyPostfix]
    static void Stop(PlayerStun __instance)
    {
        Still(__instance.owner);
        if (Mine(__instance.owner)) { stunnedAt = Time.time; NetLog.Info($"Stun: you're stunned for {__instance.duration:0.00} s", toast: false); }
    }

    // (why a stun ends early: how long it lasted, what came after it, and from where)
    static float stunnedAt;

    [HarmonyPatch(typeof(PlayerStun), nameof(PlayerStun.ExitState)), HarmonyPostfix]
    static void Out(PlayerStun __instance, State<PlayerMaster> nextState)
    {
        if (!Mine(__instance.owner)) return;
        float lasted = Time.time - stunnedAt;
        if (lasted + 0.05f >= __instance.duration) { NetLog.Info($"Stun: over after {lasted:0.00} s", toast: false); return; }
        var from = new System.Diagnostics.StackTrace(2, false).GetFrames()?.Take(6).Select(f => f.GetMethod()).Where(m => m != null).Select(m => $"{m.DeclaringType?.Name}.{m.Name}");
        NetLog.Warn($"Stun: cut short after {lasted:0.00} of {__instance.duration:0.00} s, into {nextState?.GetType().Name ?? "nothing"}, by {string.Join(" < ", from ?? new string[0])}", toast: false);
    }

    static bool Mine(PlayerMaster p) => p && p == LocalPlayer.Get();

    /// <summary>A swing you were in when the stun landed ends (its animation's AttackEnd): the game then puts you into
    /// its combo check, out of the stun after a moment (0-0.4 s of 1.5, the log showed). While stunned: nothing.</summary>
    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.AttackEnd)), HarmonyPrefix]
    static bool NotOutOfIt(PlayerMaster __instance) => !__instance.IsStunned;

    [HarmonyPatch(typeof(PlayerStun), nameof(PlayerStun.UpdateState)), HarmonyPostfix]
    static void Hold(PlayerStun __instance)
    {
        var p = __instance.owner;
        if (p && p.IsStunned) Still(p);   // (still stunned: not the step it ended)
    }

    static void Still(PlayerMaster p)
    {
        if (!p || !p.rb) return;
        p.velocity = Vector2.zero;
        p.rb.linearVelocity = Vector2.zero;
    }
}

/// <summary>
/// Backwards: your movement turned round for a few seconds (the King's stunning hit: from the moment the stun wears
/// off). Waits out a stun still on you, then runs its time.
/// </summary>
[HarmonyPatch]
internal static class Backwards
{
    static float pending, until;

    /// <summary>Backwards for that long once you're not stunned (again: the longer of the two).</summary>
    public static void After(float seconds) => pending = Mathf.Max(pending, seconds);

    [HarmonyPatch(typeof(PlayerMaster), "GetInput"), HarmonyPostfix]
    static void Turn(PlayerMaster __instance)
    {
        if (__instance != LocalPlayer.Get()) return;
        if (pending > 0f && !__instance.IsStunned && !__instance.IsDead)
        {
            until = Time.time + pending;
            NetLog.Warn($"Backwards! ({pending:0} s)", toast: true);
            Statuses.Pop("Backwards!");
            pending = 0f;
        }
        if (Time.time < until && !__instance.IsDead) __instance.input = -__instance.input;
    }
}

/// <summary>
/// Disarmed (the Gnome's bonk): no attacking for a moment. Silenced (the Hex Shaman's hex): no skills. The game's
/// attack and skill buttons each check one switch on you; held off every frame while it lasts, then given back (not
/// while a stun has them: it gives them back itself as it ends).
/// </summary>
[HarmonyLib.HarmonyPatch]
internal static class Statuses
{
    static float disarmedUntil, silencedUntil, rootedUntil;
    static bool disarmed, silenced, rooted;

    public static void Disarm(float seconds) => Start(ref disarmedUntil, seconds, "Disarmed!");
    public static void Silence(float seconds) => Start(ref silencedUntil, seconds, "Silenced!");
    /// <summary>Slowed (the Turtle's bite): you move at that share of your speed (0.2: a fifth).</summary>
    public static void Slow(float to, float seconds, string say = "Slowed!")
    {
        if (seconds <= 0f) return;
        slowTo = to;
        Start(ref slowedUntil, seconds, say);
    }

    // ---- venom (the Snake's): it builds; enough and you're paralyzed ----

    static int venom;
    static float venomAt;

    public static void Venom(int needed, float paralyzed)
    {
        var me = LocalPlayer.Get();
        if (!me || needed <= 0) return;
        if (Time.time - venomAt > 8f) venom = 0;   // (worn off)
        venomAt = Time.time;
        venom++;
        if (venom < needed) { Pop($"Venom {venom}/{needed}"); return; }
        venom = 0;
        me.Stun(paralyzed);
        Pop("Paralyzed!");
    }

    static float slowedUntil, slowTo = 1f;

    /// <summary>The game moves you at its speed (PlayerMaster.Move's top speed): slowed, a share of it.</summary>
    [HarmonyLib.HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.Move)), HarmonyLib.HarmonyPrefix]
    static void Slowed(PlayerMaster __instance, ref float maxSpeed)
    {
        if (Time.time < slowedUntil && __instance == LocalPlayer.Get()) maxSpeed *= slowTo;
    }

    // ---- blinded (the Slingshot Gnome's acorn): dark all round, a circle round you clear ----

    static float blindFrom = -10f, blindUntil = -10f;
    const float BlindFade = 0.4f;

    public static void Blind(float seconds)
    {
        if (seconds <= 0f) return;
        if (Time.time >= blindUntil) blindFrom = Time.time;
        Start(ref blindUntil, seconds, "Blinded!");
    }

    /// <summary>Every frame with the HUD: black over everything but a circle round you (an eighth of the screen's height
    /// across each way, a soft edge), fading in and out.</summary>
    public static void DrawBlind()
    {
        float t = Time.time;
        if (t >= blindUntil + BlindFade) return;
        var me = LocalPlayer.Get();
        var cam = Camera.main;
        if (!me || !cam) return;
        float a = Mathf.Clamp01(Mathf.Min((t - blindFrom) / BlindFade, (blindUntil + BlindFade - t) / BlindFade));
        var sp = cam.WorldToScreenPoint(me.transform.position + Vector3.up * 0.6f);
        var c = new ImGuiNET.Vector2(sp.x, Screen.height - sp.y);
        float r = Screen.height * 0.13f, far = (Screen.width + Screen.height) * 1.2f;
        var dl = ImGuiNET.ImGui.GetBackgroundDrawList();
        dl.AddCircle(c, r + 18f + far / 2f, ImGuiNET.ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(0f, 0f, 0f, a)), 96, far);
        for (int i = 0; i < 6; i++)
            dl.AddCircle(c, r + i * 3f + 1.5f, ImGuiNET.ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(0f, 0f, 0f, a * (i + 1) / 7f)), 96, 3f);
    }

    // ---- fists only (the Thief's hit): your weapon knocked from your hand, not from your bag ----

    static float fistsUntil;
    static ItemObject heldBefore;
    static bool fistsSaid;

    /// <summary>Your bare fists for that long (the game's own fallback weapon, as with nothing equipped); your weapon
    /// stays where it is, and comes back after.</summary>
    public static void Fists(float seconds)
    {
        var me = LocalPlayer.Get();
        if (!me || seconds <= 0f) return;
        if (me.currentWeaponItem != null && me.currentWeaponItem != me.fallbackWeaponItem) heldBefore = me.currentWeaponItem;   // (already in fists: keep the one to give back)
        else if (heldBefore == null) { NetLog.Info("Fists: bare-handed already", toast: false); return; }
        fistsUntil = Mathf.Max(fistsUntil, Time.time + seconds);
        ToFists(me);
        NetLog.Info($"Fists: {seconds:0} s with your fists (your {(heldBefore?.data ? heldBefore.data.name : "weapon")} kept); in hand now: {(me.currentWeapon ? me.currentWeapon.name : "nothing")}", toast: false);
        Pop("Fists only!");
    }

    static void ToFists(PlayerMaster me)
    {
        ItemObject none = null;
        try { ((IEquipable)me).EquipPrimaryWeapon(ref none); }
        catch (System.Exception e) { NetLog.Warn($"Fists: {e.Message}", toast: false); }
    }

    /// <summary>While it lasts: any weapon back in your hand goes again (fists for the whole time). Its time up (or you
    /// died): your weapon back, unless you've picked another.</summary>
    static void FistsTick(PlayerMaster me)
    {
        if (heldBefore == null) return;
        if (Time.time < fistsUntil && !me.IsDead)
        {
            // (the game puts your equipped weapon back into your hand as a new copy, seconds later: any weapon there
            // while it lasts goes back to fists)
            if (me.currentWeaponItem != me.fallbackWeaponItem)
            {
                if (!fistsSaid) { fistsSaid = true; NetLog.Info($"Fists: {(me.currentWeaponItem?.data ? me.currentWeaponItem.data.name : "a weapon")} came back into your hand early: fists again", toast: false); }
                ToFists(me);
            }
            return;
        }
        var back = heldBefore;
        heldBefore = null;
        fistsSaid = false;
        if (me.currentWeaponItem != me.fallbackWeaponItem) return;   // (you chose another meanwhile: yours stands)
        try { ((IEquipable)me).EquipPrimaryWeapon(ref back); }
        catch (System.Exception e) { NetLog.Warn($"Fists: your weapon back: {e.Message}", toast: false); }
    }

    // ---- marked (the Bowman's, the Archer's arrow): that one's hits on you do more ----

    public const float MarkedHit = 3f;   // (+200%)
    static Enemy markBy;
    static float markUntil;

    public static void Mark(Enemy by, float seconds)
    {
        if (!by || seconds <= 0f) return;
        markBy = by;
        Start(ref markUntil, seconds, "Marked!");
    }

    /// <summary>Is that creature's mark on you now (its hits: MarkedHit)?</summary>
    public static bool MarkedBy(Enemy e) => e && e == markBy && Time.time < markUntil;

    /// <summary>The mark over your head while it lasts: a red target, pulsing.</summary>
    static void DrawMark()
    {
        if (Time.time >= markUntil || !markBy) return;
        var me = LocalPlayer.Get();
        var cam = Camera.main;
        if (!me || !cam) return;
        var sp = cam.WorldToScreenPoint(me.transform.position + Vector3.up * 2.1f);
        var c = new ImGuiNET.Vector2(sp.x, Screen.height - sp.y);
        float r = 9f + Mathf.Sin(Time.time * 8f) * 1.5f;
        uint red = ImGuiNET.ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(1f, 0.2f, 0.2f, 0.95f));
        var dl = ImGuiNET.ImGui.GetBackgroundDrawList();
        dl.AddCircle(c, r, red, 24, 2.5f);
        dl.AddCircleFilled(c, 2.5f, red, 8);
        foreach (var (dx, dy) in new[] { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f) })
            dl.AddLine(new ImGuiNET.Vector2(c.x + dx * (r - 4f), c.y + dy * (r - 4f)), new ImGuiNET.Vector2(c.x + dx * (r + 5f), c.y + dy * (r + 5f)), red, 2f);
    }

    // ---- polymorphed (the Hex Shaman's hex): a chicken for a few seconds ----

    static float polyUntil;
    static GameObject chicken;
    static Animator chickenAnim;

    /// <summary>You, a chicken (the game's own, its brain and body taken out: just its looks, on you): you can run, but
    /// can't attack or use skills; your own looks hidden till it wears off.</summary>
    public static void Polymorph(float seconds)
    {
        var me = LocalPlayer.Get();
        if (!me || seconds <= 0f || me.IsDead) return;
        if (!chicken && !MakeChicken(me)) return;
        polyUntil = Mathf.Max(polyUntil, Time.time + seconds);
        disarmedUntil = Mathf.Max(disarmedUntil, polyUntil);
        silencedUntil = Mathf.Max(silencedUntil, polyUntil);
        me.ShowPlayer(false);
        chicken.SetActive(true);
        Pop("Polymorphed!");
    }

    static bool MakeChicken(PlayerMaster me)
    {
        var prefab = Arena.Chicken();
        if (!prefab) { NetLog.Warn("Polymorph: no chicken loaded", toast: false); return false; }
        var holder = new GameObject("EG_ChickenHolder");
        holder.SetActive(false);   // (its scripts never wake)
        try
        {
            var copy = Object.Instantiate(prefab.gameObject, holder.transform);
            copy.name = "EG_YouAsAChicken";
            // just its looks: its scripts, physics, walking and hit areas out (scripts first: they need the rest)
            foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true)) if (mb) Object.DestroyImmediate(mb);
            foreach (var c in copy.GetComponentsInChildren<Collider2D>(true)) if (c) Object.DestroyImmediate(c);
            foreach (var a in copy.GetComponentsInChildren<UnityEngine.AI.NavMeshAgent>(true)) if (a) Object.DestroyImmediate(a);
            foreach (var rb in copy.GetComponentsInChildren<Rigidbody2D>(true)) if (rb) Object.DestroyImmediate(rb);
            copy.transform.SetParent(me.transform, false);
            copy.transform.localPosition = Vector3.zero;
            copy.SetActive(false);
            chicken = copy;
            chickenAnim = copy.GetComponentInChildren<Animator>(true);
            NetLog.Info($"Polymorph: the chicken's looks from {prefab.name} (animator parameters: {(chickenAnim && chickenAnim.runtimeAnimatorController ? string.Join(", ", chickenAnim.parameters.Select(p => p.name)) : "none")})", toast: false);
            return true;
        }
        catch (System.Exception e) { NetLog.Warn($"Polymorph: {e.Message}", toast: false); return false; }
        finally { Object.Destroy(holder); }
    }

    /// <summary>Every frame: the chicken facing the way you go, running when you run; worn off (or you died): you back.</summary>
    static void ChickenTick(PlayerMaster me)
    {
        if (!chicken) return;
        if (!chicken.activeSelf) return;
        if (Time.time >= polyUntil || me.IsDead)
        {
            chicken.SetActive(false);
            if (!me.IsDead) me.ShowPlayer(true);
            return;
        }
        float vx = me.rb ? me.rb.linearVelocity.x : 0f;
        if (Mathf.Abs(vx) > 0.05f) { var s = chicken.transform.localScale; s.x = Mathf.Abs(s.x) * (vx < 0f ? -1f : 1f); chicken.transform.localScale = s; }
        if (chickenAnim && chickenAnim.runtimeAnimatorController)
        {
            bool moving = me.rb && me.rb.linearVelocity.sqrMagnitude > 0.05f;
            foreach (var p in chickenAnim.parameters)
            {
                string n = p.name.ToLowerInvariant();
                if (p.type == AnimatorControllerParameterType.Bool && (n.Contains("move") || n.Contains("walk") || n.Contains("run"))) chickenAnim.SetBool(p.name, moving);
                if (p.type == AnimatorControllerParameterType.Float && (n.Contains("speed") || n.Contains("velocity"))) chickenAnim.SetFloat(p.name, moving ? 1f : 0f);
            }
        }
    }

    /// <summary>Every frame with the HUD: the mark, the blind.</summary>
    public static void DrawAll()
    {
        DrawBlind();
        DrawMark();
    }

    /// <summary>Rooted (the Lizard's frill scare): no walking or dodging; attacking still.</summary>
    public static void Root(float seconds) => Start(ref rootedUntil, seconds, "Rooted!");

    static void Start(ref float until, float seconds, string say)
    {
        var me = LocalPlayer.Get();
        if (!me || seconds <= 0f) return;
        until = Mathf.Max(until, Time.time + seconds);
        Pop(say);
    }

    /// <summary>A word over your head (as the game's own "Stunned", "Bleed"...).</summary>
    public static void Pop(string say)
    {
        var me = LocalPlayer.Get();
        if (me) Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, say, me.transform.position + Vector3.up * 1.2f, DamageResult.None));
    }

    // ---- trying to attack when you can't: told so ----

    static float toldAt;

    /// <summary>Every frame: the attack button pressed while disarmed or a chicken: why nothing happens, over your head.</summary>
    static void TellWhyNot(PlayerMaster me)
    {
        bool cant = Time.time < disarmedUntil || chicken && chicken.activeSelf;
        if (!cant || Time.time - toldAt < 0.6f || ImGuiUnity.Typing) return;
        var mouse = UnityEngine.InputSystem.Mouse.current;
        var pad = UnityEngine.InputSystem.Gamepad.current;
        bool pressed = mouse != null && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame) ||
                       pad != null && (pad.buttonWest.wasPressedThisFrame || pad.rightTrigger.wasPressedThisFrame);
        if (!pressed) return;
        toldAt = Time.time;
        Pop(chicken && chicken.activeSelf ? "You're a chicken!" : "Disarmed: can't attack!");
    }

    /// <summary>Every frame (Plugin.Update).</summary>
    static bool wasWebbed;

    public static void Tick()
    {
        var me = LocalPlayer.Get();
        if (!me) { disarmed = silenced = rooted = false; return; }
        FistsTick(me);
        ChickenTick(me);
        TellWhyNot(me);
        // webbed (the game's Trapped effect: the Spider's web): no swinging, no spells, no rolling till you've wriggled
        // free, even if a hit or a stun knocked you out of the game's trapped state meanwhile (its exit gave them back)
        bool webbed = !me.IsDead && me.EffectsHandler != null && me.EffectsHandler.HasEffect(EffectType.Trapped);
        if (webbed) { me.canAttack = false; me.canSkill = false; me.CanDodge = false; wasWebbed = true; }
        else if (wasWebbed) { wasWebbed = false; if (!me.IsStunned) { me.canAttack = true; me.canSkill = true; me.CanDodge = true; } }
        bool d = Time.time < disarmedUntil && !me.IsDead, s = Time.time < silencedUntil && !me.IsDead;
        if (d) me.canAttack = false;
        else if (disarmed && !me.IsStunned && !webbed) me.canAttack = true;
        if (s) me.canSkill = false;
        else if (silenced && !me.IsStunned && !webbed) me.canSkill = true;
        bool r = Time.time < rootedUntil && !me.IsDead;
        if (r)
        {
            me.canMove = false;
            me.canDodge = false;
            me.velocity = Vector2.zero;
            if (me.rb) me.rb.linearVelocity = Vector2.zero;
        }
        else if (rooted && !me.IsStunned) { me.canMove = true; me.canDodge = true; }
        disarmed = d;
        silenced = s;
        rooted = r;
    }
}
