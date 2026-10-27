using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BetterPetsAndSummons;

/// <summary>
/// The Staff of Clucking: one of the game's magic staffs with a mark in its rolled values (a text attribute the game
/// never reads; saves keep it, so it's still the Staff of Clucking after loading). Its name and tooltip say so. Every
/// attack with it summons a battle chicken where you aim (3 at most at first, more as the staff levels; a new one replaces the oldest): our pet stand-in
/// with its own little brain, pecking the nearest creature (the hit is ours: XP, kills, co-op sync). Creatures swat
/// back, so chickens in a fight don't last; the rest go after 20 s. A middle-click ping sends the flock there.
/// /clucking gives you one; descent guardians sometimes drop one.
/// (Others don't see your chickens yet, only the damage they do.)
/// </summary>
[HarmonyPatch]
public static class CluckStaff
{
    const ItemRandomValues.Attr Mark = (ItemRandomValues.Attr)77;
    const string MarkValue = "EG_StaffOfClucking";
    public const string Name = "Staff of Clucking";
    const int BaseChickens = 3;
    const ItemRandomValues.Attr ClucksKey = (ItemRandomValues.Attr)78;   // the staff's own count of summons + chicken kills

    // ---- the staff grows with use: every chicken summoned (and every kill a chicken makes) counts on THIS staff ----

    /// <summary>Clucks to reach a level: 20, 50, 90, 140... (level 1 = new).</summary>
    static int ClucksFor(int level) => level <= 1 ? 0 : 10 * (level - 1) * (level + 2) / 2;

    public static int Clucks(ItemObject item)
    {
        var a = item?.randomValues?.strAttributes?.FirstOrDefault(x => x != null && x.Key == ClucksKey);
        return a != null && int.TryParse(a.Value, out int n) ? n : 0;
    }

    public static int Level(ItemObject item)
    {
        int c = Clucks(item), lv = 1;
        while (lv < 20 && c >= ClucksFor(lv + 1)) lv++;
        return lv;
    }

    static void AddClucks(ItemObject item, int n)
    {
        if (item?.randomValues == null) return;
        int before = Level(item);
        var list = item.randomValues.strAttributes;
        var a = list.FirstOrDefault(x => x != null && x.Key == ClucksKey);
        if (a == null) list.Add(a = new ItemRandomValues.StrItemAttribute(ClucksKey, "0"));
        a.Value = (Clucks(item) + n).ToString();
        int after = Level(item);
        if (after > before) NetLog.Success($"{Name} grew to level {after}! {Perks(after)}", toast: true);
    }

    // what a level gives: tougher, harder-hitting, bigger, longer-lived chickens, and more of them
    static float HpAt(int lv) => 3f + (lv - 1) * 1f;
    static float DamageMulAt(int lv) => 1f + (lv - 1) * 0.15f;
    static float SizeAt(int lv) => 1f + Mathf.Min(0.6f, (lv - 1) * 0.04f);
    static float LifeAt(int lv) => Life + (lv - 1) * 2f;
    static int MaxAt(int lv) => BaseChickens + (lv - 1) / 3;

    static string Perks(int lv) => $"Chickens: {HpAt(lv):0} hits of health, {DamageMulAt(lv):0.##}x pecks, up to {MaxAt(lv)} at once, {LifeAt(lv):0} s.";

    static int MaxChickens => MaxAt(Level(Me ? Me.currentWeaponItem : null));
    const float Life = 20f, PeckEvery = 0.7f, SwatEvery = 1.3f, AggroRange = 9f;

    public static bool IsStaff(ItemObject item) =>
        item?.randomValues?.strAttributes != null && item.randomValues.strAttributes.Any(a => a != null && a.Key == Mark && a.Value == MarkValue);

    // ---- getting one ----

    /// <summary>A Staff of Clucking item: a magic staff from the catalogue, marked. Null if none is loaded yet.</summary>
    public static ItemObject Make(int quality = 3)
    {
        DungeonLoot.Preload();
        var staff = DungeonLoot.Pool?.OfType<CollectableWeapon>().Where(w => w.weapon != null && w.weapon.weaponCategory == WeaponCategory.MagicStaffs).OrderBy(w => w.name).FirstOrDefault();
        if (staff == null) return null;
        var item = new ItemObject(staff, 1, quality);
        item.randomValues.strAttributes.Add(new ItemRandomValues.StrItemAttribute(Mark, MarkValue));
        return item;
    }

    /// <summary>/clucking: one into your bag (at your feet if it's full).</summary>
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
        NetLog.Success($"{Name}: equip it and attack. Every attack summons a battle chicken!", toast: true);
    }

    /// <summary>A guardian's death: a small chance (better deeper down) of a Staff of Clucking among its loot.</summary>
    public static void MaybeDrop(Vector3 at, int floor)
    {
        if (Random.value > 0.04f + 0.01f * Mathf.Clamp(floor, 0, 20)) return;
        var item = Make(Mathf.Clamp(1 + floor / 3, 1, 6));
        if (item == null) return;
        item.DropToWorld(at, 1, 1.4f);
        NetLog.Success($"A {Name} dropped!", toast: true);
    }

    // ---- its name and tooltip ----

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
        if (__instance.itemName) __instance.itemName.text = $"<color=#FFD24A>{Name}</color>";
        StaffTip.Show(__instance, "<color=#FFD24A>Right-click summons a battle chicken (a tenth of your Kymia). They peck the nearest foe for you; middle-click to send them somewhere.</color>\n\n" + Progress(item));
    }

    static string Progress(ItemObject item)
    {
        int lv = Level(item), c = Clucks(item);
        string next = lv >= 20 ? "max level" : $"{c - ClucksFor(lv)}/{ClucksFor(lv + 1) - ClucksFor(lv)} clucks to level {lv + 1}";
        return $"<color=#FFE9A0>Clucking level {lv} ({next})\n{Perks(lv)}</color>";
    }

    // ---- summoning ----

    sealed class Chicken
    {
        public PetBody Body;
        public float Born, NextPeck, NextSwat, Hp = 3f, MaxHp = 3f, Life = 20f, DamageMul = 1f, Size = 1f;
        public ushort Id;        // for the summons sync (SummonSync)
        public byte Attacks;     // pecks so far (others play the attack when it goes up)
        public ItemObject Staff;
        public Enemy Target;
        public Vector3? RallyTo;
        public float RallyUntil;
        public string Tag;       // (someone else's: Better Streaming's "chickens" / "golden")
    }

    static readonly List<Chicken> flock = new List<Chicken>();
    static float lastSummon;

    static PlayerMaster Me => Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;

    static bool Holding(PlayerMaster me) => me && IsStaff(me.currentWeaponItem);
    /// <summary>Is the local player holding the staff right now (a middle click is an order to the flock).</summary>
    public static bool HeldNow => Holding(LocalPlayer.Get());

    /// <summary>A chicken costs a tenth of a full Kymia bar (Kymia.Cost).</summary>
    const float ChickensPerBar = 10f;

    /// <summary>Right-click with the staff: a chicken where we aim (if there's the Kymia for it). Left-click is the
    /// staff's own attack; its right-click ball never fires (RisenStaff).</summary>
    static void Summon(PlayerMaster me)
    {
        if (!Holding(me) || Time.unscaledTime - lastSummon < 0.3f) return;
        lastSummon = Time.unscaledTime;
        var kind = Pet.Kinds[0];   // the chicken
        float cost = Kymia.Cost(me, ChickensPerBar);
        if (!Kymia.Has(me, cost)) return;   // (the swing still happens; no chicken)
        if (!PetBody.Available(kind) && !PetBody.Learn(kind)) { NetLog.Info("The chicken couldn't be loaded here (see the log)", toast: true); return; }
        var aim = me.lastLookDir.sqrMagnitude > 0.01f ? (Vector3)me.lastLookDir.normalized : Vector3.right;
        var at = me.transform.position + aim * 1.8f;
        var body = PetBody.Create(kind, at);
        if (body == null) return;
        Kymia.Take(me, cost);
        var staffItem = me.currentWeaponItem;
        int lv = Level(staffItem);
        if (flock.Count >= MaxAt(lv)) { flock[0].Body?.Burst(); flock.RemoveAt(0); }
        body.Scale(SizeAt(lv));
        flock.Add(new Chicken { Body = body, Born = Time.time, NextPeck = Time.time + 0.4f, Hp = HpAt(lv), MaxHp = HpAt(lv), Life = LifeAt(lv), DamageMul = DamageMulAt(lv), Staff = staffItem, Size = SizeAt(lv), Id = Summoned.NextId() });
        AddClucks(staffItem, 1);
        Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, "Cluck!", at, DamageResult.Critical));
    }

    /// <summary>Chickens for someone else (Better Streaming's battle chickens): no staff, no Kymia; as a staff of that
    /// level makes them, for that long, around there (a tint: golden ones). How many came.</summary>
    internal static int Flock(Vector3 at, int count, int level, float life, Color? tint = null, float size = 0f, string tag = null)
    {
        var kind = Pet.Kinds[0];
        if (!PetBody.Available(kind) && !PetBody.Learn(kind)) return 0;
        int made = 0;
        for (int i = 0; i < count; i++)
        {
            var body = PetBody.Create(kind, at + (Vector3)(Random.insideUnitCircle * 1.5f));
            if (body == null) continue;
            float s = size > 0f ? size : SizeAt(level);
            body.Scale(s);
            if (tint.HasValue) body.SetTint(tint.Value);
            flock.Add(new Chicken { Body = body, Born = Time.time, NextPeck = Time.time + 0.4f, Hp = HpAt(level), MaxHp = HpAt(level), Life = life, DamageMul = DamageMulAt(level), Size = s, Id = Summoned.NextId(), Tag = tag });
            made++;
        }
        return made;
    }

    /// <summary>How many chickens with that tag are still about.</summary>
    internal static int Alive(string tag) => flock.Count(c => c.Tag == tag && c.Body != null && c.Body.Alive && c.Hp > 0f);

    /// <summary>Our chickens, for the summons sync.</summary>
    public static IEnumerable<Summoned.Summon> Summons()
    {
        foreach (var c in flock)
            if (c.Body != null && c.Body.Alive)
                yield return new Summoned.Summon(c.Id, false, c.Body.Kind.Prefab, c.Body.Position, c.Size, c.Hp / Mathf.Max(0.01f, c.MaxHp), c.Attacks);
    }

    /// <summary>A middle-click ping of ours: the flock runs there and fights whatever's near it for a while.</summary>
    public static void Rally(Vector3 point)
    {
        foreach (var c in flock) { c.RallyTo = point; c.RallyUntil = Time.time + 10f; c.Target = null; }
    }

    static int owed;       // asked for while the item list was loading
    static float owedAt;

    public static void Tick()
    {
        if (owed > 0 && Time.unscaledTime >= owedAt) { owedAt = Time.unscaledTime + 1f; owed--; Give(); }
        var me = Me;
        // (not while typing, in chat, busy, or with a game screen up: the game's controls are off then)
        if (me && Holding(me) && Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame && !ImGuiUnity.Typing && !Chat.IsOpen &&
            !PlayerStatusUtil.LocalBusy && MainInput.Actions.Player.enabled && !UIPanels.HasOpenedPanel)
            Summon(me);
        if (flock.Count == 0) return;
        float now = Time.time, dt = Time.deltaTime;
        for (int i = flock.Count - 1; i >= 0; i--)
        {
            var c = flock[i];
            if (c.Body == null || !c.Body.Alive || !me) { flock.RemoveAt(i); continue; }
            if (now - c.Born > c.Life || c.Hp <= 0f) { Poof(c); flock.RemoveAt(i); continue; }
            if (CatchUp(c.Body, me, i, c.RallyTo.HasValue && now < c.RallyUntil)) { c.RallyTo = null; c.Target = null; }

            // a target: near the rally point while rallying, else near us / the chicken
            var centre = c.RallyTo.HasValue && now < c.RallyUntil ? c.RallyTo.Value : me.transform.position;
            if (!c.Target || c.Target.IsDead || !c.Target.isActiveAndEnabled) c.Target = Pick(centre, c.Body.Position);
            if (!c.Target)
            {
                if (c.RallyTo.HasValue && now < c.RallyUntil) c.Body.MoveToward(c.RallyTo.Value, 5.5f, dt, 0.6f);
                else c.Body.MoveToward(me.transform.position + new Vector3((i % 3 - 1) * 0.8f, -0.6f - (i / 3) * 0.6f, 0f), 5.5f, dt, 0.5f);
                continue;
            }
            var tp = c.Target.transform.position;
            float d = ((Vector2)(tp - c.Body.Position)).magnitude;
            if (d > 0.9f) { c.Body.MoveToward(tp, 6f, dt, 0.7f); continue; }
            c.Body.Stop(tp);
            if (now >= c.NextPeck) { c.NextPeck = now + PeckEvery; Peck(me, c); }
            // the creature swats back
            if (now >= c.NextSwat) { c.NextSwat = now + SwatEvery * Random.Range(0.8f, 1.2f); if (c.NextSwat > now && Random.value < 0.75f) Hurt(c); }
        }
    }

    static Enemy Pick(Vector3 around, Vector3 chickenAt)
    {
        Enemy best = null;
        float bestD = float.MaxValue;
        foreach (var e in EnemiesManager.AliveEnemies)
        {
            if (!e || !e.isActiveAndEnabled || e.IsDead || !e.CanTakeDamage || EnemySync.LocalOnly(e)) continue;
            float d = ((Vector2)(e.transform.position - around)).magnitude;
            if (d > AggroRange) continue;
            float dc = ((Vector2)(e.transform.position - chickenAt)).magnitude;
            if (dc < bestD) { bestD = dc; best = e; }
        }
        return best;
    }

    /// <summary>A peck: the game's own hit with us as the attacker (it counts as ours), sized from the staff's damage.</summary>
    static void Peck(PlayerMaster me, Chicken c)
    {
        c.Body.Attack();
        c.Attacks++;
        int damage = 3;
        try
        {
            var dmg = CombatSystem.GetBaseDamage(me.currentWeaponItem ?? Make(), me);
            damage = Mathf.Max(2, Mathf.RoundToInt((dmg.x + dmg.y) * 0.5f * 0.35f * c.DamageMul));
        }
        catch { }
        var target = c.Target;
        CombatSystem.Resolve(new DamageData(me, target, damage, DamageHitType.Impact, KnockBack.Low));
        if (target && target.IsDead) AddClucks(c.Staff, 3);   // a chicken's kill counts extra
    }

    /// <summary>
    /// Left far behind (through a door to another area, where the old spot is somewhere else entirely): it appears
    /// beside us, like the pet does. True = it was moved (its old orders don't apply here).
    /// </summary>
    public static bool CatchUp(PetBody body, PlayerMaster me, int index, bool rallying)
    {
        float far = ((Vector2)(body.Position - me.transform.position)).magnitude;
        if (far <= (rallying ? 30f : 14f)) return false;
        float a = index * 2.4f;
        body.Teleport(me.transform.position + new Vector3(Mathf.Cos(a), Mathf.Sin(a) * 0.6f, 0f) * 1.2f);
        return true;
    }

    static void Hurt(Chicken c)
    {
        c.Hp -= 1f;
        c.Body.SetTint(new Color(1f, 0.45f, 0.45f));
        Bus<UIDamageEvent>.Invoke(new UIDamageEvent(1f, "", c.Body.Position + Vector3.up * 0.5f, DamageResult.Success));
        if (c.Hp > 0f) Restore(c).Forget();
    }

    static async Cysharp.Threading.Tasks.UniTaskVoid Restore(Chicken c)
    {
        await Cysharp.Threading.Tasks.UniTask.Delay(150);
        if (c.Body != null && c.Body.Alive) c.Body.SetTint(Color.white);
    }

    static void Poof(Chicken c)
    {
        if (c.Body == null || !c.Body.Alive) return;
        Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, c.Hp <= 0f ? "Bawk..." : "Poof", c.Body.Position, DamageResult.Parry));
        c.Body.Burst();   // (the chicken's own gore death)
    }

    /// <summary>From Hud.Draw: a small health bar over each chicken.</summary>
    public static void Draw()
    {
        if (flock.Count == 0) return;
        var cam = Camera.main;
        if (!cam) return;
        var dl = ImGuiNET.ImGui.GetBackgroundDrawList();
        foreach (var c in flock)
        {
            if (c.Body == null || !c.Body.Alive) continue;
            var s = cam.WorldToScreenPoint(c.Body.Position + Vector3.up * 1.0f);
            if (s.z < 0) continue;
            float x = s.x, y = Screen.height - s.y, w = 26f;
            dl.AddRectFilled(new ImGuiNET.Vector2(x - w / 2, y), new ImGuiNET.Vector2(x + w / 2, y + 4), ImGuiNET.ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(0f, 0f, 0f, 0.6f)));
            dl.AddRectFilled(new ImGuiNET.Vector2(x - w / 2, y), new ImGuiNET.Vector2(x - w / 2 + w * Mathf.Clamp01(c.Hp / c.MaxHp), y + 4), ImGuiNET.ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(1f, 0.85f, 0.3f, 1f)));
        }
    }

    public static void Clear()
    {
        foreach (var c in flock) c.Body?.Destroy();
        flock.Clear();
    }
}
