using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;
using Random = UnityEngine.Random;

namespace BetterFishing;

/// <summary>
/// Fishing that pays. In the demo, fishing is just the animation and 1 Fishing XP. Here every catch also brings
/// something up: food or a material from the game's catalogue, or (a quarter of the time) anything at all, up to legendary.
/// It hangs on the Fishing XP the game adds at the end of a catch (PlayerData.AddXP), so the game's own spots (the
/// Vitromotus Crystal's) pay too.
/// And more places to fish: wherever the game has fish shadows in the water ("Fish Shadow Small / Still / Jump / Big":
/// the starting area has eight, and no fishing at all), a spot of ours: walk up to the water near one and the game's
/// fishing hint shows; interact and you fish (the game's own cast, wait, reel and catch, run by us: those scenes don't
/// have the game's FishingManager).
/// The fishing itself is Terraria's: cast, wait for a bite (1-5 s), then left-click within a second (Cast).
/// </summary>
[HarmonyPatch]
internal static class Fishing
{
    public static float RareChance = 0.25f;

    // ---- the catch ----

    [HarmonyPatch(typeof(PlayerData), nameof(PlayerData.AddXP)), HarmonyPostfix]
    static void AfterXp(object key, int points)
    {
        if (!(key is LifeSkillJob job) || job != LifeSkillJob.Fishing || points <= 0) return;
        try { Catch(); }
        catch (Exception e) { Plugin.Logger_.LogWarning($"Catch: {e.Message}"); }
    }

    static void Catch()
    {
        var pool = Catalogue.Pool;
        if (pool == null) { Catalogue.Preload(); owed++; return; }   // (handed over as soon as the item list is in: Tick)
        bool rare = Random.value < RareChance;
        // the usual: food or a material; the rare one: anything at all (not coins or quest keys), up to legendary
        var picks = pool.Where(d => rare ? !(d is CollectableCoin) && d.itemBehaviour != ItemBehaviour.KeyItem
                                         : (d.itemCategory == ItemCategory.Consumable || d.itemCategory == ItemCategory.Material)).ToList();
        if (picks.Count == 0) return;
        var data = picks[Random.Range(0, picks.Count)];
        var item = rare ? new ItemObject(data, 1, RollQuality(data)) : new ItemObject(data, 1);
        var me = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
        if (!Singleton<Inventory>.HasMain) return;
        int rest = Singleton<Inventory>.main.AddItem(item);   // (the game's own "collected" line)
        if (rest > 0 && me) item.DropToWorld(me.transform.position, rest, 1f);
        if (rare) Log.Note("A rare catch!", data.itemIconUI);
    }

    // Broken, Ordinary, Good, Superior, Epic, Legendary: how often each comes up on a rare catch
    static readonly int[] QualityWeights = { 10, 30, 28, 18, 10, 4 };

    /// <summary>A quality for a rare catch (legendary the rarest), within what that item can be.</summary>
    static int RollQuality(ItemData data)
    {
        int total = QualityWeights.Sum(), roll = Random.Range(0, total), q = 1;
        for (int i = 0; i < QualityWeights.Length; i++) { if (roll < QualityWeights[i]) { q = i + 1; break; } roll -= QualityWeights[i]; }
        var range = data.qualityRange;
        return range.y >= range.x && range.y > 0 ? Mathf.Clamp(q, range.x, range.y) : q;   // (outside its range the game would pick at random)
    }

    // ---- our spots, at the fish shadows ----

    static readonly HashSet<Transform> spotted = new HashSet<Transform>();
    static float nextLook;
    public static bool Busy { get; private set; }

    /// <summary>Every frame: new fish shadows (looked for every couple of seconds) get a spot.</summary>
    static int owed;   // catches made before the item list was in
    static float nextCatalogue, arrived;
    static string lookedIn;

    public static void Tick()
    {
        if (Catalogue.Pool == null) { if (Time.unscaledTime >= nextCatalogue) { nextCatalogue = Time.unscaledTime + 1f; Catalogue.Preload(); } }   // (at the game's start the catalogue isn't up yet: asked once a second until it is)
        else if (owed > 0) { owed--; Catch(); }
        if (Time.unscaledTime < nextLook) return;
        // (a look at every object in the scene: 10-25 ms, a hitch you see. Often while a new place is still coming in,
        // then seldom: the shadows are part of the place, they don't come and go)
        string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (scene != lookedIn) { lookedIn = scene; arrived = Time.unscaledTime; }
        nextLook = Time.unscaledTime + (Time.unscaledTime - arrived < 12f ? 2f : 20f);
        if (!Singleton<GameManager>.HasMain || !Singleton<GameManager>.main.player) return;
        spotted.RemoveWhere(t => !t);
        foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
        {
            if (!t || !t.name.StartsWith("Fish Shadow") || spotted.Contains(t)) continue;
            if (Singleton<FishingManager>.HasMain && NearGameSpot(t.position)) { spotted.Add(t); continue; }   // the game fishes here already
            spotted.Add(t);
            Make(t);
        }
    }

    static bool NearGameSpot(Vector3 at)
    {
        foreach (var f in UnityEngine.Object.FindObjectsByType<FishingTrigger>(FindObjectsSortMode.None))
            if (f && ((Vector2)(f.transform.position - at)).magnitude < 4f) return true;
        return false;
    }

    static void Make(Transform shadow)
    {
        var go = new GameObject("BF_FishingSpot");
        go.transform.SetParent(shadow.parent, true);   // goes with its scene
        go.transform.position = shadow.position;
        go.layer = InteractLayer();
        var c = go.AddComponent<CircleCollider2D>();
        c.isTrigger = true;
        c.radius = 3.2f;   // from the bank: the shadow is out in the water
        var spot = go.AddComponent<Spot>();
        spot.Water = shadow.position;
        Plugin.Logger_.LogInfo($"Fishing spot at {shadow.name} ({shadow.position.x:0.#}, {shadow.position.y:0.#}) in {SceneManager.GetActiveScene().name}");
    }

    /// <summary>The layer the game's interactables' triggers are on (so the player's "PlayerCenter" meets ours).</summary>
    static int InteractLayer()
    {
        var any = UnityEngine.Object.FindObjectsByType<FishingTrigger>(FindObjectsSortMode.None).FirstOrDefault();
        if (any) return any.gameObject.layer;
        var chest = UnityEngine.Object.FindObjectsByType<Chest>(FindObjectsSortMode.None).FirstOrDefault();
        if (chest)
        {
            var col = chest.GetComponentsInChildren<Collider2D>(true).FirstOrDefault(x => x.isTrigger);
            if (col) return col.gameObject.layer;
        }
        return 0;
    }

    sealed class Spot : MonoBehaviour, IInteractable
    {
        public Vector3 Water;

        void OnTriggerEnter2D(Collider2D other)
        {
            if (other.CompareTag("PlayerCenter") && other.attachedRigidbody && other.attachedRigidbody.TryGetComponent<InteractablesHandler>(out var h))
                h.AddInteractable(transform, this);
        }

        void OnTriggerExit2D(Collider2D other)
        {
            if (other.CompareTag("PlayerCenter") && other.attachedRigidbody && other.attachedRigidbody.TryGetComponent<InteractablesHandler>(out var h))
                h.RemoveInteractable(transform);
        }

        public void OnEnableInteraction(GameObject owner) { var p = Me; if (p && p.fishingIndicator) p.fishingIndicator.SetActive(true); }
        public void OnDisableInteraction(GameObject owner) { var p = Me; if (p && p.fishingIndicator) p.fishingIndicator.SetActive(false); }
        public void OnInteract(GameObject owner) { if (!Busy) Cast(Water).Forget(); }
        public bool CanInteract() => !Busy;
        public bool CanRepeat(out float interval) { interval = 0f; return false; }
    }

    static PlayerMaster Me => Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
    static readonly object holder = new object();   // our hold on the player (the game's "static" stack)

    // ---- the game's own spots (the Vitromotus Crystal's FishingTriggers) play the same game ----

    [HarmonyPatch(typeof(FishingManager), nameof(FishingManager.StartFishing)), HarmonyPrefix]
    static bool GameSpot(Direction directionToLook, Transform spot)
    {
        var p = Me;
        if (!p || Busy) return false;
        if (spot) p.myTransform.position = spot.position;   // (as the game does: onto the spot)
        Cast(p.transform.position + directionToLook.ToVector() * 2f).Forget();
        return false;
    }

    // ---- the cast: wait for a bite, click in time ----

    const float BiteWindow = 1.0f;   // seconds to click once it bites

    /// <summary>
    /// Terraria-style: the game's cast (FishStart: throw, then its waiting pose, held as long as we like), 1-5 s, a bite
    /// (a splash at the line's end, a sound, "!"), and a second to left-click: in time, the game's reel-in (FishEnd), its
    /// catch sparkle, the Fishing XP and (AfterXp) the catch. Too early or too late: reeled in empty.
    /// The line's end: one step out in the direction you face (where the game itself puts its catch effect).
    /// </summary>
    static async UniTaskVoid Cast(Vector3 water)
    {
        var p = Me;
        if (!p || p.IsDead) return;
        Busy = true;
        bool couldAttack = p.CanPerfomAttacks;
        Direction dir = Direction.Down;
        try
        {
            var d = water - p.transform.position;
            dir = Mathf.Abs(d.x) > Mathf.Abs(d.y) ? (d.x > 0 ? Direction.Right : Direction.Left) : (d.y > 0 ? Direction.Up : Direction.Down);
            var lineEnd = p.transform.position + dir.ToVector();
            if (p.fishingIndicator) p.fishingIndicator.SetActive(false);
            p.AddStaticStack(holder);
            p.CanPerfomAttacks = false;   // the click is for the fish, not a swing
            p.Invincible = true;
            p.ForceDirection = dir;
            p.animator.SetTrigger("FishStart");
            p.sfxComponent.PlaySfx("FishStart");
            await UniTask.Delay(600);   // the throw (a click now is just the cast settling)

            // waiting for a bite: a click now is too soon
            float biteAt = Time.time + Random.Range(1f, 5f);
            while (Time.time < biteAt)
            {
                await UniTask.NextFrame();
                if (!p || p.IsDead) return;
                if (Clicked()) { Popup(p, "Too soon!"); await Reel(p, dir, caught: false); return; }
            }

            // a bite!
            Splash(lineEnd);
            p.sfxComponent.PlaySfx("FishLoop");
            Popup(p, "!");
            float until = Time.time + BiteWindow;
            bool hooked = false;
            while (Time.time < until)
            {
                await UniTask.NextFrame();
                if (!p || p.IsDead) return;
                if (Clicked()) { hooked = true; break; }
            }
            if (!hooked) Popup(p, "It got away...");
            await Reel(p, dir, hooked);
        }
        catch (Exception e) { Plugin.Logger_.LogWarning($"Fishing: {e.Message}"); }
        finally
        {
            if (p)
            {
                p.RemoveStaticStack(holder);
                p.CanPerfomAttacks = couldAttack;
                p.Invincible = false;
                if (p.extraFxAnimator) p.extraFxAnimator.transform.localPosition = Vector3.zero;
            }
            Busy = false;
        }
    }

    /// <summary>The game's reel-in; caught: its catch sparkle, the Fishing XP and the catch.</summary>
    static async UniTask Reel(PlayerMaster p, Direction dir, bool caught)
    {
        p.animator.SetTrigger("FishEnd");
        await UniTask.Delay(500);
        if (!p) return;
        if (caught)
        {
            if (p.extraFxAnimator)
            {
                p.extraFxAnimator.transform.localPosition = dir.ToVector();
                p.extraFxAnimator.SetTrigger("FishRare");
            }
            if (Singleton<FishingManager>.HasMain) Singleton<FishingManager>.main.sfxCatch?.Post(p.gameObject);
        }
        await UniTask.Delay(800);
        if (!p || !caught) return;
        GameData.playerData.AddXP(LifeSkillJob.Fishing, 1);   // the game's XP, and (AfterXp) the catch
    }

    static bool Clicked() => UnityEngine.InputSystem.Mouse.current != null && UnityEngine.InputSystem.Mouse.current.leftButton.wasPressedThisFrame && Application.isFocused;

    static void Popup(PlayerMaster p, string text) =>
        Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, text, p.transform.position + Vector3.up * 1.6f, text == "!" ? DamageResult.Critical : DamageResult.Parry));

    // ---- the bite's splash: the game's own water splash, or a ring of our own ----

    static GameObject splashTemplate;

    static void Splash(Vector3 at)
    {
        if (!splashTemplate)
            splashTemplate = Resources.FindObjectsOfTypeAll<Transform>()
                .Where(t => t && t.name.StartsWith("VFX_WaterSplash")).OrderBy(t => t.gameObject.scene.IsValid() ? 1 : 0)
                .Select(t => t.gameObject).FirstOrDefault();
        if (splashTemplate)
        {
            var go = UnityEngine.Object.Instantiate(splashTemplate);
            go.name = "BF_BiteSplash";
            go.transform.position = at;
            go.SetActive(true);
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true)) ps.Play(true);
            foreach (var an in go.GetComponentsInChildren<Animator>(true)) { an.Rebind(); an.Update(0f); }
            UnityEngine.Object.Destroy(go, 3f);
        }
        Ripple.At(at);   // and rings spreading out, so it reads even where the splash is small
    }

    /// <summary>Two pale rings spreading out on the water and fading.</summary>
    sealed class Ripple : MonoBehaviour
    {
        static Sprite ring;
        float born, delay;
        SpriteRenderer sr;

        public static void At(Vector3 at)
        {
            for (int i = 0; i < 2; i++)
            {
                var go = new GameObject("BF_Ripple");
                go.transform.position = at;
                var r = go.AddComponent<Ripple>();
                r.delay = i * 0.25f;
                r.sr = go.AddComponent<SpriteRenderer>();
                r.sr.sprite = Ring();
                var like = Me ? Me.GetComponentInChildren<SpriteRenderer>() : null;
                if (like) { r.sr.sortingLayerID = like.sortingLayerID; r.sr.sortingOrder = like.sortingOrder + 50; }
                r.sr.color = new Color(1f, 1f, 1f, 0f);
                r.born = Time.time;
                Destroy(go, 1.5f + r.delay);
            }
        }

        void Update()
        {
            float t = (Time.time - born - delay) / 1.2f;
            if (t < 0f) return;
            transform.localScale = new Vector3(1f, 0.55f, 1f) * Mathf.Lerp(0.2f, 1.4f, t);   // flat: lying on the water
            sr.color = new Color(0.85f, 0.95f, 1f, Mathf.Clamp01(1f - t) * 0.9f);
        }

        static Sprite Ring()
        {
            if (ring) return ring;
            const int n = 64;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    px[y * n + x] = r > 0.82f && r < 0.95f ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 0);
                }
            tex.SetPixels32(px); tex.Apply();
            ring = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 32f);
            return ring;
        }
    }
}

/// <summary>The game's item catalogue (every ItemData, loaded once in the background), for the catches.</summary>
internal static class Catalogue
{
    static List<ItemData> pool;
    internal static IReadOnlyList<ItemData> Pool => pool;
    static bool loading;

    public static void Preload()
    {
        if (pool != null || loading) return;
        loading = true;
        var keys = new HashSet<string>();
        foreach (var locator in Addressables.ResourceLocators)
            foreach (var key in locator.Keys)
                if (key is string s && locator.Locate(s, typeof(ItemData), out var locs))
                    foreach (var l in locs) keys.Add(l.PrimaryKey);
        var found = new List<ItemData>();
        int pending = keys.Count;
        if (pending == 0) { loading = false; return; }   // the catalogue isn't up yet: asked again at the next catch
        foreach (var key in keys)
            Addressables.LoadAssetAsync<ItemData>(key).Completed += h =>
            {
                if (h.Status == AsyncOperationStatus.Succeeded && h.Result && (h.Result.itemIconUI || h.Result.itemIconWorld)) found.Add(h.Result);
                if (--pending == 0) { pool = found.Distinct().OrderBy(d => d.addressKey).ToList(); loading = false; }
            };
    }
}
