using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// The Hunter's Hook for everyone in the area: the thrower sends where the hook's head is (~20x a second while it's
/// out, unreliable: HookRope) and when it's gone (reliable), plus one reliable HookThrow per throw (its sound, and
/// whether this one shouts). Each teammate's copy draws the same rope from the thrower's stand-in to that point
/// (smoothed), and plays the sounds there. The drag and the hits need nothing here: creature positions, the thrower's
/// position and damage are synced already.
/// </summary>
internal static class HookNet
{
    public static Plugin Plugin;
    const float SendEvery = 0.05f, StaleAfter = 0.5f;
    static float nextSend;
    static bool sentOut;

    static bool Online => Plugin != null && Plugin.Connected && Plugin.MyId >= 0;

    // ---- ours, out ----

    public static void SendHead(Vector3 head)
    {
        if (!Online || Time.unscaledTime < nextSend) return;
        nextSend = Time.unscaledTime + SendEvery;
        sentOut = true;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.HookRope);
        w.Put(true);
        w.Put(head.x); w.Put(head.y);
        Plugin.SendToPlace(w, DeliveryMethod.Unreliable);
    }

    public static void SendGone()
    {
        if (!sentOut || !Online) { sentOut = false; return; }
        sentOut = false;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.HookRope);
        w.Put(false);
        Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
    }

    public static void SendThrow(bool shout)
    {
        if (!Online) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.HookThrow);
        w.Put(shout);
        Plugin.SendToPlace(w, DeliveryMethod.ReliableUnordered);
    }

    /// <summary>A pop-up word over the world (Get over here! / Dizzy! / clank): here, and on everyone's copy.</summary>
    public static void Popup(string text, Vector3 at, DamageResult kind)
    {
        Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, text, at, kind));
        if (!Online) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.HookText);
        w.Put(text);
        w.Put(at.x); w.Put(at.y);
        w.Put((byte)kind);
        Plugin.SendToPlace(w, DeliveryMethod.ReliableUnordered);
    }

    public static void OnText(NetDataReader r, int from)
    {
        string text = r.GetString();
        var at = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
        var kind = (DamageResult)r.GetByte();
        if (text.Length > 40) return;
        Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, text, at, kind));
    }

    /// <summary>PvP: our hook caught that player; their copy drags them to us (and holds them while it does).</summary>
    public static void SendPull(int victim, float seconds)
    {
        if (!Online) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.HookPull);
        w.Put(seconds);
        Plugin.SendTo(victim, w);
    }

    public static void OnPull(NetDataReader r, int from)
    {
        float seconds = r.GetFloat();
        HunterHook.Pulled(from, seconds);
    }

    // ---- theirs, in ----

    sealed class Theirs
    {
        public readonly RopeView View = new RopeView();
        public Vector3 Head, Shown;
        public float HeardAt;
        public bool Out;
    }

    static readonly Dictionary<int, Theirs> ropes = new Dictionary<int, Theirs>();

    public static void OnRope(NetDataReader r, int from)
    {
        bool on = r.GetBool();
        if (!ropes.TryGetValue(from, out var t)) ropes[from] = t = new Theirs();
        if (!on) { t.Out = false; t.View.Hide(); return; }
        var head = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
        if (!t.Out) t.Shown = head;   // a new throw: no smoothing in from the last one
        t.Head = head;
        t.HeardAt = Time.unscaledTime;
        t.Out = true;
    }

    public static void OnThrow(NetDataReader r, int from)
    {
        bool shout = r.GetBool();
        var avatar = AvatarOf(from);
        HookSounds.Play(avatar ? avatar : null, shout);
    }

    static GameObject AvatarOf(int id)
    {
        if (Plugin == null) return null;
        foreach (var p in Plugin.Players)
            if (p.Id == id && p.Avatar != null && p.Avatar.Visible) return p.Avatar.Root;
        return null;
    }

    /// <summary>Every frame: each teammate's rope, from their stand-in's hand to where their hook is.</summary>
    public static void Tick()
    {
        if (ropes.Count == 0) return;
        float now = Time.unscaledTime;
        foreach (var kv in ropes)
        {
            var t = kv.Value;
            if (!t.Out) continue;
            var who = AvatarOf(kv.Key);
            if (now - t.HeardAt > StaleAfter || !who) { t.Out = false; t.View.Hide(); continue; }
            t.Shown = Vector3.Lerp(t.Shown, t.Head, Mathf.Clamp01(Time.deltaTime * 25f));   // updates come 20x a second: glide between them
            var hand = who.transform.position + Vector3.up * 0.55f; hand.z = 0f;
            t.View.Draw(hand, t.Shown);
        }
    }

    public static void Clear()
    {
        foreach (var t in ropes.Values) t.View.Hide();
        ropes.Clear();
    }
}

/// <summary>A hook on a rope, drawn from a hand to the hook's head (ours or a teammate's).</summary>
internal sealed class RopeView
{
    GameObject ropeGo, headGo;
    LineRenderer rope;
    string layeredFor;
    static Material ropeMat;
    static Sprite headSprite;

    public void Draw(Vector3 from, Vector3 to)
    {
        if (!ropeGo)
        {
            ropeGo = new GameObject("EG_HookRope");
            Object.DontDestroyOnLoad(ropeGo);
            rope = ropeGo.AddComponent<LineRenderer>();
            rope.positionCount = 2;
            rope.useWorldSpace = true;
            rope.startWidth = rope.endWidth = 0.07f;
            if (!ropeMat)
            {
                var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default") ?? Shader.Find("Sprites/Default");
                ropeMat = new Material(shader) { name = "EG_HookRope" };
            }
            rope.sharedMaterial = ropeMat;
            rope.startColor = new Color(0.55f, 0.4f, 0.25f, 1f);
            rope.endColor = new Color(0.4f, 0.28f, 0.18f, 1f);

            headGo = new GameObject("EG_HookHead");
            Object.DontDestroyOnLoad(headGo);
            var sr = headGo.AddComponent<SpriteRenderer>();
            sr.sprite = HeadSprite();
            sr.color = new Color(0.8f, 0.82f, 0.86f, 1f);
        }
        string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (scene != layeredFor)
        {
            // over everything on the map (stairs, bridges and roofs sit on layers above the player's): the highest
            // sorting layer the map's sprites and tiles use, at the top of it
            layeredFor = scene;
            int layer = TopWorldLayer();
            rope.sortingLayerID = layer; rope.sortingOrder = 32000;
            var sr = headGo.GetComponent<SpriteRenderer>();
            sr.sortingLayerID = layer; sr.sortingOrder = 32001;
        }
        ropeGo.SetActive(true);
        headGo.SetActive(true);
        rope.SetPosition(0, from);
        rope.SetPosition(1, to);
        headGo.transform.position = to;
        var d = to - from;
        if (d.sqrMagnitude > 0.0001f) headGo.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
    }

    static int TopWorldLayer()
    {
        int best = 0, bestValue = int.MinValue;
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            if (!r || !r.enabled || !(r is SpriteRenderer || r is UnityEngine.Tilemaps.TilemapRenderer)) continue;
            if (r.gameObject.name.StartsWith("EG_")) continue;
            int value = SortingLayer.GetLayerValueFromID(r.sortingLayerID);
            if (value > bestValue) { bestValue = value; best = r.sortingLayerID; }
        }
        return best;
    }

    public void Hide()
    {
        if (ropeGo) ropeGo.SetActive(false);
        if (headGo) headGo.SetActive(false);
    }

    /// <summary>A small barbed arrowhead, pointing right (turned to the rope's way).</summary>
    static Sprite HeadSprite()
    {
        if (headSprite) return headSprite;
        const int n = 16;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float fy = Mathf.Abs(y - (n - 1) / 2f);
                bool tip = x >= 4 && fy <= (n - 1 - x) * 0.55f;                     // the point
                bool barb = x >= 2 && x <= 5 && fy >= 3f && fy <= 6.5f - (x - 2);   // the barbs, back towards the rope
                bool shaft = x < 6 && fy < 1f;
                px[y * n + x] = tip || barb || shaft ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 0);
            }
        tex.SetPixels32(px); tex.Apply();
        headSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.75f, 0.5f), 32f);
        return headSprite;
    }
}
