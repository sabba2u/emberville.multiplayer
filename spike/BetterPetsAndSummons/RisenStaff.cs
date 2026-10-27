using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BetterPetsAndSummons;

/// <summary>
/// The Staff of the Risen: a necromancer's staff, made like the Staff of Clucking (a game staff with a mark in its
/// rolled values, so saves keep it). While you hold it, creatures that die near you leave a green glow where they fell
/// for a few seconds; right-click near one and it gets back up on your side: the creature's own art and animations
/// (our pet stand-in), ghostly green, with the health and hitting power it had (its own stats), following you and
/// fighting the nearest foe (its hits are yours). Foes hit back, so the risen don't last forever: 40 s at most, 3 at a
/// time (one more makes the oldest crumble). The staff levels up with souls (raises + their kills): more at once, longer. Mini-boss guardians rise at half strength. Middle-click sends them.
/// /risen gives you one; descent guardians sometimes drop one.
/// (Others see the damage, not your risen, for now.)
/// </summary>
[HarmonyPatch]
public static class RisenStaff
{
    const ItemRandomValues.Attr Mark = (ItemRandomValues.Attr)77;
    const string MarkValue = "EG_StaffOfTheRisen";
    public const string Name = "Staff of the Risen";
    const int BaseRisen = 3;
    const ItemRandomValues.Attr SoulsKey = (ItemRandomValues.Attr)79;   // the staff's own count: raises + kills by the risen

    // ---- the staff grows with use: every creature raised (and every kill a risen makes) gathers souls on THIS staff ----

    /// <summary>Souls to reach a level: 10, 25, 45, 70... (level 1 = new).</summary>
    static int SoulsFor(int level) => level <= 1 ? 0 : 5 * (level - 1) * (level + 2) / 2;

    public static int Souls(ItemObject item)
    {
        var a = item?.randomValues?.strAttributes?.FirstOrDefault(x => x != null && x.Key == SoulsKey);
        return a != null && int.TryParse(a.Value, out int n) ? n : 0;
    }

    public static int Level(ItemObject item)
    {
        int c = Souls(item), lv = 1;
        while (lv < 20 && c >= SoulsFor(lv + 1)) lv++;
        return lv;
    }

    static void AddSouls(ItemObject item, int n)
    {
        if (item?.randomValues == null) return;
        int before = Level(item);
        var list = item.randomValues.strAttributes;
        var a = list.FirstOrDefault(x => x != null && x.Key == SoulsKey);
        if (a == null) list.Add(a = new ItemRandomValues.StrItemAttribute(SoulsKey, "0"));
        a.Value = (Souls(item) + n).ToString();
        int after = Level(item);
        if (after > before) NetLog.Success($"{Name} grew to level {after}! {Perks(after)}", toast: true);
    }

    // what a level gives: more risen at once (3 at level 1, one more every 3 levels: 9 at 19), lasting a bit longer
    static int MaxAt(int lv) => BaseRisen + (lv - 1) / 3;
    static float LifeAt(int lv) => Life + (lv - 1) * 2f;
    static string Perks(int lv) => $"Up to {MaxAt(lv)} risen at once, for {LifeAt(lv):0} s.";

    static string Progress(ItemObject item)
    {
        int lv = Level(item), c = Souls(item);
        string next = lv >= 20 ? "max level" : $"{c - SoulsFor(lv)}/{SoulsFor(lv + 1) - SoulsFor(lv)} souls to level {lv + 1}";
        return $"<color=#C8FFD0>Risen level {lv} ({next})\n{Perks(lv)}</color>";
    }
    const float Life = 40f, CorpseGlow = 8f, RaiseRange = 3f, NoticeRange = 14f, HitEvery = 1.1f, SwatEvery = 1.4f, AggroRange = 9f;
    static readonly Color Ghost = new Color(0.55f, 1f, 0.6f, 0.85f);

    public static bool IsStaff(ItemObject item) =>
        item?.randomValues?.strAttributes != null && item.randomValues.strAttributes.Any(a => a != null && a.Key == Mark && a.Value == MarkValue);

    // ---- getting one ----

    public static ItemObject Make(int quality = 3)
    {
        DungeonLoot.Preload();
        var staffs = DungeonLoot.Pool?.OfType<CollectableWeapon>().Where(w => w.weapon != null && w.weapon.weaponCategory == WeaponCategory.MagicStaffs).OrderBy(w => w.name).ToList();
        if (staffs == null || staffs.Count == 0) return null;
        var staff = staffs[staffs.Count > 1 ? 1 : 0];   // (a different staff from the clucking one, where there is one)
        var item = new ItemObject(staff, 1, quality);
        item.randomValues.strAttributes.Add(new ItemRandomValues.StrItemAttribute(Mark, MarkValue));
        return item;
    }

    public static void Give()
    {
        var item = Make();
        var me = LocalPlayer.Get();
        if (item == null || !me || !Singleton<Inventory>.HasMain)
        {
            // the item list is still loading: given as soon as it's there (Tick)
            if (owed == 0) NetLog.Info("On its way: the item list is still loading", toast: true);
            owed++;
            return;
        }
        int rest = Singleton<Inventory>.main.AddItem(item);
        if (rest > 0) item.DropToWorld(me.transform.position, rest, 1f);
        NetLog.Success($"{Name}: equip it. Fallen creatures glow green: right-click near one to raise it!", toast: true);
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

    /// <summary>The tooltip: our name in the title, what it does and its level in a panel beside it (StaffTip).</summary>
    [HarmonyPatch(typeof(ItemInfoWindow), nameof(ItemInfoWindow.SetItem)), HarmonyPostfix]
    static void Tooltip(ItemInfoWindow __instance, ItemObject item)
    {
        if (!IsStaff(item))
        {
            if (!RisenStaff.IsStaff(item) && !CluckStaff.IsStaff(item)) StaffTip.Show(__instance, null);   // another item: no panel
            return;
        }
        if (__instance.itemName) __instance.itemName.text = $"<color=#8CFF99>{Name}</color>";
        StaffTip.Show(__instance, "<color=#8CFF99>Creatures that fall near you glow green: right-click near one to raise it to fight for you, with its own strength (a fifth of your Kymia). Middle-click to send them. Raising and their kills gather souls: the staff grows.</color>\n\n" + Progress(item));
    }

    /// <summary>
    /// Neither staff fires the staff's own right-click ball: on the Staff of the Risen right-click raises the fallen,
    /// and the Staff of Clucking has its chickens.
    /// </summary>
    [HarmonyPatch(typeof(PlayerMaster), "StartPressing"), HarmonyPrefix]
    static bool NoBall(PlayerMaster __instance, AttackType attackType) =>
        attackType != AttackType.Secondary || !HoldsOurStaff(__instance);

    // the hold (charge) path and the attack itself get there without StartPressing too
    [HarmonyPatch(typeof(PlayerMaster), "PerformAttack", typeof(UnityEngine.InputSystem.InputAction.CallbackContext)), HarmonyPrefix]
    static bool NoBallAttack(PlayerMaster __instance, UnityEngine.InputSystem.InputAction.CallbackContext context) => !IsSecondary(__instance, context);

    [HarmonyPatch(typeof(PlayerMaster), "HoldStarted"), HarmonyPrefix]
    static bool NoBallHold(PlayerMaster __instance, UnityEngine.InputSystem.InputAction.CallbackContext context) => !IsSecondary(__instance, context);

    static bool HoldsOurStaff(PlayerMaster p) => p && (IsStaff(p.currentWeaponItem) || CluckStaff.IsStaff(p.currentWeaponItem));

    static bool IsSecondary(PlayerMaster p, UnityEngine.InputSystem.InputAction.CallbackContext context) =>
        HoldsOurStaff(p) && p.inputActions != null && context.action == p.inputActions.Player.SecondaryAttack;

    // ---- corpses ----

    sealed class Corpse
    {
        public Enemy Source;
        public string Prefab;
        public Vector3 At;
        public float Died, Hp, Damage;
        public bool Boss;
        public GameObject Glow;
    }

    static readonly List<Corpse> corpses = new List<Corpse>();
    static readonly HashSet<Enemy> seenDead = new HashSet<Enemy>();
    static float nextScan;

    static PlayerMaster Me => Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
    static bool Holding(PlayerMaster me) => me && IsStaff(me.currentWeaponItem);
    /// <summary>Is the local player holding the staff right now (a middle click is an order to the risen).</summary>
    public static bool HeldNow => Holding(LocalPlayer.Get());

    /// <summary>A creature's hitting power, from its own stats (the way the game works out its hits).</summary>
    static float HitOf(Enemy e)
    {
        try
        {
            var cs = e.characterStats ?? e.GetComponent<CharacterStats>();
            if (!cs) return 5f;
            float stat = cs.GetSecondaryStatValue(cs.CurrentAttackStat);
            return Mathf.Max(2f, stat * Mathf.Pow(1.015f, cs.CurrentAttackPrimStat.Value));
        }
        catch { return 5f; }
    }

    static string BaseName(string n)
    {
        n = n.Replace("(Clone)", "").Trim();
        int u = n.LastIndexOf('_');
        return u > 0 && int.TryParse(n.Substring(u + 1), out _) ? n.Substring(0, u) : n;
    }

    static void ScanCorpses(PlayerMaster me)
    {
        foreach (var e in EnemiesManager.AllEnemies)
        {
            if (!e) continue;
            if (!e.IsDead) { seenDead.Remove(e); continue; }
            if (!seenDead.Add(e)) continue;   // already counted this death
            if (((Vector2)(e.transform.position - me.transform.position)).magnitude > NoticeRange) continue;
            if (risen.Any(r => r.Source == e)) continue;
            bool boss = MiniBoss.IsBoss(e);
            float hp = Mathf.Max(10f, e.MaxHealth), dmg = HitOf(e);
            if (boss) { hp *= 0.5f; dmg *= 0.5f; }
            var c = new Corpse { Source = e, Prefab = e.name.Replace("(Clone)", "").Trim(), At = e.transform.position, Died = Time.time, Hp = hp, Damage = dmg, Boss = boss };
            c.Glow = MakeGlow(c.At);
            corpses.Add(c);
        }
        for (int i = corpses.Count - 1; i >= 0; i--)
            if (Time.time - corpses[i].Died > CorpseGlow) { if (corpses[i].Glow) Object.Destroy(corpses[i].Glow); corpses.RemoveAt(i); }
    }

    static Sprite glowSprite;

    static GameObject MakeGlow(Vector3 at)
    {
        if (!glowSprite)
        {
            const int n = 48;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = ((y + 0.5f) / n * 2f - 1f) * 2f;   // flat ellipse on the ground
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = r < 1f ? (1f - r) * 0.8f + Mathf.Exp(-Mathf.Pow((r - 0.85f) / 0.08f, 2f)) * 0.6f : 0f;
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255));
                }
            tex.SetPixels32(px); tex.Apply();
            glowSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n / 2.2f);
        }
        var go = new GameObject("EG_RisenGlow");
        go.transform.position = at;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = glowSprite;
        sr.color = new Color(0.4f, 1f, 0.5f, 0.75f);
        var like = Me ? Me.GetComponentInChildren<SpriteRenderer>() : null;
        if (like) { sr.sortingLayerID = like.sortingLayerID; sr.sortingOrder = -400; }
        go.AddComponent<Pulse>();
        return go;
    }

    sealed class Pulse : MonoBehaviour
    {
        void Update() { transform.localScale = Vector3.one * (1f + Mathf.Sin(Time.time * 5f) * 0.1f); }
    }

    // ---- raising ----

    sealed class Risen
    {
        public Enemy Source;
        public PetBody Body;
        public string Label;
        public float Born, Hp, MaxHp, Damage, NextHit, NextSwat, Life = 40f;
        public ushort Id;        // for the summons sync (SummonSync)
        public byte Attacks;
        public ItemObject Staff;
        public Enemy Target;
        public Vector3? RallyTo;
        public float RallyUntil;
        public bool Guardian;    // (Better Streaming's)
    }

    static readonly List<Risen> risen = new List<Risen>();

    /// <summary>A raise costs a fifth of a full Kymia bar (Kymia.Cost).</summary>
    const float RaisesPerBar = 5f;

    /// <summary>Why a right-click raised nothing: on screen and in the log.</summary>
    static void Why(string text)
    {
        NetLog.Info(text, toast: true);   // (the toast, and the log)
    }

    /// <summary>A right-click while holding the staff: the glowing corpse nearest the mouse (close to it) rises.</summary>
    static void TryRaise(PlayerMaster me)
    {
        var mouse = Mouse.current;
        var cam = Camera.main;
        if (mouse == null || !cam) return;
        if (corpses.Count == 0) { Why("Nothing to raise: a creature has to fall near you first (it glows green)"); return; }
        var mp = mouse.position.ReadValue();
        var world = cam.ScreenToWorldPoint(new Vector3(mp.x, mp.y, -cam.transform.position.z));
        Corpse best = null; float bestD = RaiseRange;
        foreach (var c in corpses)
        {
            float d = ((Vector2)(c.At - world)).magnitude;
            if (d < bestD) { bestD = d; best = c; }
        }
        if (best == null) { Why("Right-click closer to a glowing body"); return; }
        float cost = Kymia.Cost(me, RaisesPerBar);
        if (!Kymia.Has(me, cost)) { Why($"Not enough Kymia to raise ({me.CurrentKymiaPoints:0} of {cost:0})"); return; }
        var kind = new PetKind("risen_" + best.Prefab, best.Prefab, best.Prefab, best.Prefab);
        if (!PetBody.Available(kind) && !PetBody.Learn(kind)) { NetLog.Info($"That one can't rise (no {best.Prefab} to copy)", toast: true); return; }
        var body = PetBody.Create(kind, best.At);
        if (body == null) { Why($"The {Pretty(best.Prefab)} couldn't rise (see the log)"); return; }
        Kymia.Take(me, cost);
        body.SetTint(Ghost);
        var staffItem = me.currentWeaponItem;
        int lv = Level(staffItem);
        while (risen.Count >= MaxAt(lv)) { Crumble(risen[0], "crumbles"); risen.RemoveAt(0); }
        string label = Pretty(best.Prefab) + (best.Boss ? " (guardian)" : "");
        risen.Add(new Risen { Source = best.Source, Body = body, Label = label, Born = Time.time, Hp = best.Hp, MaxHp = best.Hp, Damage = best.Damage, NextHit = Time.time + 0.6f, Life = LifeAt(lv), Staff = staffItem, Id = Summoned.NextId() });
        AddSouls(staffItem, 1);
        if (best.Source && best.Source.gameObject.activeSelf && best.Source.IsDead) best.Source.gameObject.SetActive(false);   // the body gets up: no corpse left behind
        if (best.Glow) Object.Destroy(best.Glow);
        corpses.Remove(best);
        Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, "Rise!", best.At, DamageResult.Critical));
    }

    /// <summary>Risen for someone else (Better Streaming's guardians): no corpse, no staff, no Kymia; each of those
    /// creatures rises beside there (ghostly green) and fights for you for that long. How many came.</summary>
    internal static int Guardians(Vector3 at, IEnumerable<string> prefabs, float hp, float damage, float life)
    {
        int made = 0;
        foreach (var prefab in prefabs)
        {
            var kind = new PetKind("risen_" + prefab, prefab, prefab, prefab);
            if (!PetBody.Available(kind) && !PetBody.Learn(kind)) continue;
            var spot = at + (Vector3)(Random.insideUnitCircle.normalized * Random.Range(1.2f, 2.2f));
            var body = PetBody.Create(kind, spot);
            if (body == null) continue;
            body.SetTint(Ghost);
            risen.Add(new Risen { Body = body, Label = Pretty(prefab) + " (guardian)", Born = Time.time, Hp = hp, MaxHp = hp, Damage = damage, NextHit = Time.time + 0.6f, Life = life, Id = Summoned.NextId(), Guardian = true });
            Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, "Rise!", spot, DamageResult.Critical));
            made++;
        }
        return made;
    }

    /// <summary>How many of Better Streaming's guardians are still about.</summary>
    internal static int GuardiansAlive() => risen.Count(r => r.Guardian && r.Body != null && r.Body.Alive && r.Hp > 0f);

    static string Pretty(string prefab) => BaseName(prefab).Replace("Mob_", "").Replace("Acolyte_", "").Replace('_', ' ');

    /// <summary>Our risen, for the summons sync.</summary>
    public static IEnumerable<Summoned.Summon> Summons()
    {
        foreach (var r in risen)
            if (r.Body != null && r.Body.Alive)
                yield return new Summoned.Summon(r.Id, true, r.Body.Kind.Prefab, r.Body.Position, 1f, r.Hp / Mathf.Max(0.01f, r.MaxHp), r.Attacks);
    }

    public static void Rally(Vector3 point)
    {
        foreach (var r in risen) { r.RallyTo = point; r.RallyUntil = Time.time + 10f; r.Target = null; }
    }

    // ---- every frame ----

    static int owed;       // asked for while the item list was loading
    static float owedAt;

    public static void Tick()
    {
        if (owed > 0 && Time.unscaledTime >= owedAt) { owedAt = Time.unscaledTime + 1f; owed--; Give(); }
        var me = Me;
        bool holding = Holding(me);
        if (me && holding && Time.unscaledTime >= nextScan) { nextScan = Time.unscaledTime + 0.2f; ScanCorpses(me); }
        if (!holding && corpses.Count > 0) { foreach (var c in corpses) if (c.Glow) Object.Destroy(c.Glow); corpses.Clear(); }
        if (holding && Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame && !ImGuiUnity.Typing && !Chat.IsOpen && !PlayerStatusUtil.LocalBusy)
            TryRaise(me);

        if (risen.Count == 0) return;
        float now = Time.time, dt = Time.deltaTime;
        for (int i = risen.Count - 1; i >= 0; i--)
        {
            var r = risen[i];
            if (r.Body == null || !r.Body.Alive || !me) { risen.RemoveAt(i); continue; }
            if (r.Hp <= 0f) { Crumble(r, "falls again"); risen.RemoveAt(i); continue; }
            if (now - r.Born > r.Life) { Crumble(r, "crumbles to dust"); risen.RemoveAt(i); continue; }
            if (CluckStaff.CatchUp(r.Body, me, i, r.RallyTo.HasValue && now < r.RallyUntil)) { r.RallyTo = null; r.Target = null; }
            var centre = r.RallyTo.HasValue && now < r.RallyUntil ? r.RallyTo.Value : me.transform.position;
            if (!r.Target || r.Target.IsDead || !r.Target.isActiveAndEnabled) r.Target = Pick(centre, r.Body.Position);
            if (!r.Target)
            {
                if (r.RallyTo.HasValue && now < r.RallyUntil) r.Body.MoveToward(r.RallyTo.Value, 4.5f, dt, 0.8f);
                else r.Body.MoveToward(me.transform.position + new Vector3((i - 1) * 1.2f, -1.2f, 0f), 4.5f, dt, 0.8f);
                continue;
            }
            var tp = r.Target.transform.position;
            float d = ((Vector2)(tp - r.Body.Position)).magnitude;
            if (d > 1.1f) { r.Body.MoveToward(tp, 5f, dt, 0.9f); continue; }
            r.Body.Stop(tp);
            if (now >= r.NextHit) { r.NextHit = now + HitEvery; Hit(me, r); }
            if (now >= r.NextSwat) { r.NextSwat = now + SwatEvery * Random.Range(0.85f, 1.15f); Hurt(r, HitOf(r.Target) * Random.Range(0.8f, 1.2f)); }
        }
    }

    static Enemy Pick(Vector3 around, Vector3 at)
    {
        Enemy best = null; float bestD = float.MaxValue;
        foreach (var e in EnemiesManager.AliveEnemies)
        {
            if (!e || !e.isActiveAndEnabled || e.IsDead || !e.CanTakeDamage || EnemySync.LocalOnly(e)) continue;
            if (((Vector2)(e.transform.position - around)).magnitude > AggroRange) continue;
            float d = ((Vector2)(e.transform.position - at)).magnitude;
            if (d < bestD) { bestD = d; best = e; }
        }
        return best;
    }

    /// <summary>Its hit, with its own strength, as ours (the game's combat: XP, kills, co-op).</summary>
    static void Hit(PlayerMaster me, Risen r)
    {
        r.Body.Attack();
        r.Attacks++;
        int dmg = Mathf.Max(1, Mathf.RoundToInt(r.Damage * Random.Range(0.85f, 1.15f)));
        var target = r.Target;
        CombatSystem.Resolve(new DamageData(me, target, dmg, DamageHitType.Impact, KnockBack.Low));
        if (target && target.IsDead) AddSouls(r.Staff, 2);   // a risen's kill gathers more
    }

    static void Hurt(Risen r, float amount)
    {
        r.Hp -= amount;
        Bus<UIDamageEvent>.Invoke(new UIDamageEvent(Mathf.Round(amount), "", r.Body.Position + Vector3.up * 0.6f, DamageResult.Success));
        r.Body.SetTint(new Color(1f, 0.4f, 0.4f, 0.85f));
        Restore(r).Forget();
    }

    static async Cysharp.Threading.Tasks.UniTaskVoid Restore(Risen r)
    {
        await Cysharp.Threading.Tasks.UniTask.Delay(150);
        if (r.Body != null && r.Body.Alive) r.Body.SetTint(Ghost);
    }

    static void Crumble(Risen r, string how)
    {
        if (r.Body == null || !r.Body.Alive) return;
        Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, how, r.Body.Position, DamageResult.Parry));
        r.Body.Burst();   // (its creature's own gore death)
    }

    /// <summary>From Hud.Draw: each risen's health bar (green, like an ally's; no name).</summary>
    public static void Draw()
    {
        if (risen.Count == 0) return;
        var cam = Camera.main;
        if (!cam) return;
        var dl = ImGuiNET.ImGui.GetBackgroundDrawList();
        foreach (var r in risen)
        {
            if (r.Body == null || !r.Body.Alive) continue;
            var s = cam.WorldToScreenPoint(r.Body.Position + Vector3.up * 1.4f);
            if (s.z < 0) continue;
            float x = s.x, y = Screen.height - s.y, w = 50f, h = 6f;   // an ally's bar (Hud), a little narrower
            dl.AddRectFilled(new ImGuiNET.Vector2(x - w / 2 - 1, y - 1), new ImGuiNET.Vector2(x + w / 2 + 1, y + h + 1), ImGuiNET.ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(0f, 0f, 0f, 0.7f)));
            dl.AddRectFilled(new ImGuiNET.Vector2(x - w / 2, y), new ImGuiNET.Vector2(x - w / 2 + w * Mathf.Clamp01(r.Hp / r.MaxHp), y + h), ImGuiNET.ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(0.25f, 0.85f, 0.35f, 1f)));
        }
    }
}
