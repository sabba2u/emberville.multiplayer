using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using Vector2 = ImGuiNET.Vector2;
using Vector4 = ImGuiNET.Vector4;

namespace EmbervilleGhost;

/// <summary>
/// Trading items between two players (like our BDSP mod's Pokémon trade), from anywhere.
///
/// 1. A invites B (Online Players screen > Trade, or /trade P2), B accepts.
/// 2. Each drags items from their bag (shown in the trade window, real icons, tooltips on hover) into their 8 offer
///    slots; every change sends the whole offer (the game's own save format for items: key, amount, rolled values).
/// 3. Ready = "I accept exactly THIS offer of theirs": a lock naming the fingerprint (hash) of their current offer.
///    Any change on either side changes a fingerprint, so both Ready boxes untick by themselves.
/// 4. When we're locked on their current offer and they're locked on ours, both games swap on their own: ours leave
///    our bag (Inventory.ConsumeItem), theirs are built and go in (Inventory.AddItem; no room: at our feet).
/// While trading, an offered item that leaves our bag (used, dropped, moved to a chest) leaves the offer too, which
/// unticks both. Key items and equipped gear can't be offered. Main thread only.
/// </summary>
internal static class Trade
{
    public static Plugin Plugin;

    public enum Phase { Idle, Inviting, Invited, Trading }
    public static Phase State { get; private set; }
    public static bool Visible => State != Phase.Idle;

    const int Slots = 8;
    const float Cell = 44f;

    class Offered { public InventoryItem Item; public int Amount; }

    static int partner = -1;
    static string partnerName = "";
    static float since;
    static readonly List<Offered> mine = new List<Offered>();
    static string mineHash = "";
    static List<InventoryItem> theirs = new List<InventoryItem>();
    static string theirsHash = "", theirsJson = "";
    static bool theirsReady;      // their offer's items are built (Addressables are async)
    static string myLock, theirLock;
    static int dragFrom = -1;     // index into our bag list while dragging

    [Serializable] class Items { public List<SavedItem> items = new List<SavedItem>(); }

    static bool Connected => Plugin != null && Plugin.Connected && Plugin.MyId >= 0;
    static bool ILocked => myLock != null && myLock == theirsHash;
    static bool TheyLocked => theirLock != null && theirLock == mineHash;

    // ---- invite / answer ----

    public static void Invite(int id)
    {
        if (!Connected || id == Plugin.MyId) return;
        if (State != Phase.Idle) { NetLog.Warn("Finish the trade you're in first", toast: true); return; }
        Begin(id, Phase.Inviting);
        Send(MsgType.TradeInvite, w => { });
        NetLog.Info($"Asked {partnerName} to trade...", toast: true);
    }

    /// <summary>/trade P2 or /trade name.</summary>
    public static void InviteByName(string who)
    {
        who = who.Trim();
        foreach (var p in Plugin.Players)
            if (p.Tag.Equals(who, StringComparison.OrdinalIgnoreCase) || (p.CharacterName ?? "").Equals(who, StringComparison.OrdinalIgnoreCase) || $"P{p.Id + 1}".Equals(who, StringComparison.OrdinalIgnoreCase))
            { Invite(p.Id); return; }
        Chat.System(who.Length == 0 ? "/trade P2 (or their name)" : $"Nobody called \"{who}\" is connected.");
    }

    public static void OnInvite(int from)
    {
        if (State != Phase.Idle || !LocalPlayer.Get())
        {
            Send(MsgType.TradeAnswer, w => { w.Put(false); w.Put("busy"); }, from);
            return;
        }
        Begin(from, Phase.Invited);
        NetLog.Info($"{partnerName} wants to trade with you", toast: true);
        AskInGameBox(from);
    }

    /// <summary>The invite in the game's own yes/no box when there's one to show (else our window asks).</summary>
    static async void AskInGameBox(int from)
    {
        asking = true;
        bool? yes = await GameUi.Ask($"{partnerName} wants to trade with you.", "Trade", "No thanks");
        asking = false;
        if (yes == null || State != Phase.Invited || partner != from) return;   // no game box (our window asks), or it's over
        Answer(yes.Value);
    }

    static bool asking;   // the game's box is up: our own window stays out of the way

    static void Answer(bool yes)
    {
        Send(MsgType.TradeAnswer, w => { w.Put(yes); w.Put(""); });
        if (yes) Start(); else Reset();
    }

    public static void OnAnswer(NetDataReader r, int from)
    {
        bool yes = r.GetBool();
        string reason = r.GetString();
        if (State != Phase.Inviting || from != partner) return;
        if (yes) { Start(); return; }
        NetLog.Warn($"{partnerName} {(reason == "busy" ? "is busy" : "said no")}", toast: true);
        Reset();
    }

    public static void Cancel(string why = null)
    {
        if (State == Phase.Idle) return;
        Send(MsgType.TradeCancel, w => w.Put(why ?? ""));
        NetLog.Info("Trade cancelled" + (string.IsNullOrEmpty(why) ? "" : $": {why}"), toast: true);
        Reset();
    }

    public static void OnCancel(NetDataReader r, int from)
    {
        string why = r.GetString();
        if (from != partner || State == Phase.Idle) return;
        NetLog.Info($"{partnerName} cancelled the trade" + (why.Length > 0 ? $" ({why})" : ""), toast: true);
        Reset();
    }

    static void Begin(int id, Phase phase)
    {
        Reset();
        partner = id;
        partnerName = Plugin.NameOf(id);
        State = phase;
        since = Time.unscaledTime;
        GameInput.Block("trade");
    }

    static void Start()
    {
        State = Phase.Trading;
        since = Time.unscaledTime;
        SendOffer();
    }

    static void Reset()
    {
        State = Phase.Idle;
        partner = -1;
        mine.Clear();
        theirs = new List<InventoryItem>();
        mineHash = theirsHash = theirsJson = "";
        theirsReady = false;
        myLock = theirLock = null;
        dragFrom = -1;
        GameInput.Unblock("trade");
    }

    // ---- offers and locks ----

    static void SendOffer()
    {
        var items = new Items();
        foreach (var o in mine)
        {
            var saved = o.Item.ToSavedItem();
            saved.Amount = o.Amount;
            items.items.Add(saved);
        }
        string json = JsonUtility.ToJson(items);
        mineHash = Hash(json);
        myLock = null;   // our offer changed: we're not ready any more (and their lock on the old one is void)
        Send(MsgType.TradeOffer, w => w.Put(json));
        Send(MsgType.TradeLock, w => w.Put(""));
    }

    public static async void OnOffer(NetDataReader r, int from)
    {
        string json = r.GetString();
        if (from != partner || State != Phase.Trading) return;
        theirsJson = json;
        theirsHash = Hash(json);
        theirsReady = false;
        myLock = null;   // their offer changed: we're not ready any more
        var parsed = JsonUtility.FromJson<Items>(json) ?? new Items();
        var built = await parsed.items.ToInventoryItems();
        if (theirsJson != json) return;   // a newer offer came meanwhile
        theirs = built;
        theirsReady = true;
        CheckSwap();
    }

    static void SetReady(bool ready)
    {
        myLock = ready ? theirsHash : null;
        Send(MsgType.TradeLock, w => w.Put(myLock ?? ""));
        CheckSwap();
    }

    public static void OnLock(NetDataReader r, int from)
    {
        string hash = r.GetString();
        if (from != partner || State != Phase.Trading) return;
        theirLock = hash.Length > 0 ? hash : null;
        CheckSwap();
    }

    /// <summary>Both locked on each other's current offer: swap here (they do the same there).</summary>
    static void CheckSwap()
    {
        if (State != Phase.Trading || !ILocked || !TheyLocked || !theirsReady) return;
        var inv = Singleton<Inventory>.main;
        var me = LocalPlayer.Get();
        foreach (var o in mine)
            if (!inv.allItems.Contains(o.Item) || o.Item.itemObject.amount < o.Amount)
            { Cancel("an offered item isn't in the bag any more"); return; }
        foreach (var o in mine) inv.ConsumeItem(o.Item.itemObject, o.Amount);
        int got = 0;
        foreach (var it in theirs)
        {
            int left = inv.AddItem(it.itemObject);
            if (left > 0 && me) it.itemObject.DropToWorld(me.transform.position, left, 1f);
            got += it.itemObject.amount;
        }
        NetLog.Success($"Traded with {partnerName}: gave {mine.Count} stack{(mine.Count == 1 ? "" : "s")}, got {theirs.Count}", toast: true);
        Reset();
    }

    /// <summary>Every frame: offered items that left our bag leave the offer; a partner who left ends it.</summary>
    public static void Tick()
    {
        if (State == Phase.Idle) return;
        if (!Connected || !Plugin.Players.Any(p => p.Id == partner)) { NetLog.Info("Trade ended: they're gone", toast: true); Reset(); return; }
        if ((State == Phase.Inviting || State == Phase.Invited) && Time.unscaledTime - since > 30f) { Cancel("no answer"); return; }
        if (State != Phase.Trading || !Singleton<Inventory>.HasMain) return;
        var inv = Singleton<Inventory>.main;
        bool changed = false;
        for (int i = mine.Count - 1; i >= 0; i--)
        {
            var o = mine[i];
            if (!inv.allItems.Contains(o.Item) || !Tradable(o.Item)) { mine.RemoveAt(i); changed = true; }
            else if (o.Item.itemObject.amount < o.Amount) { o.Amount = o.Item.itemObject.amount; changed = true; }
        }
        if (changed) SendOffer();
    }

    static bool Tradable(InventoryItem it) =>
        it?.itemObject?.data != null && (it.section == InventorySection.Main || it.section == InventorySection.Belt) &&
        it.itemObject.data.itemBehaviour != ItemBehaviour.KeyItem;

    static void Add(InventoryItem it)
    {
        if (mine.Count >= Slots || mine.Any(o => o.Item == it) || !Tradable(it)) return;
        mine.Add(new Offered { Item = it, Amount = it.itemObject.amount });
        SendOffer();
    }

    static void RemoveAt(int i)
    {
        if (i < 0 || i >= mine.Count) return;
        mine.RemoveAt(i);
        SendOffer();
    }

    // ---- window ----

    public static void Draw()
    {
        if (State == Phase.Idle || Plugin == null) return;
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(new Vector2(vp.WorkPos.x + vp.WorkSize.x * 0.5f, vp.WorkPos.y + vp.WorkSize.y * 0.45f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize;

        if (State == Phase.Invited)
        {
            if (asking) return;
            if (ImGui.Begin("Trade?##tradeask", flags))
            {
                ImGui.Text($"{partnerName} wants to trade with you.");
                if (ImGui.Button("Accept", new Vector2(120, 0))) Answer(true);
                ImGui.SameLine();
                if (ImGui.Button("Decline", new Vector2(120, 0))) Answer(false);
            }
            ImGui.End();
            return;
        }
        if (State == Phase.Inviting)
        {
            if (ImGui.Begin("Trade##tradewait", flags))
            {
                ImGui.Text($"Waiting for {partnerName} to answer...");
                if (ImGui.Button("Cancel", new Vector2(120, 0))) Cancel();
            }
            ImGui.End();
            return;
        }

        if (!ImGui.Begin($"Trade with {partnerName}##trade", flags)) { ImGui.End(); return; }
        var inv = Singleton<Inventory>.HasMain ? Singleton<Inventory>.main : null;
        var bag = inv == null ? new List<InventoryItem>() : inv.allItems.Where(Tradable).OrderBy(i => i.section).ThenBy(i => i.slotIndex).ToList();

        // Our bag: drag (or click) into the offer.
        ImGui.Text("Your bag (drag into your offer, or click)");
        for (int i = 0; i < bag.Count; i++)
        {
            if (i % 8 != 0) ImGui.SameLine();
            bool offered = mine.Any(o => o.Item == bag[i]);
            if (ItemCell($"bag{i}", bag[i], bag[i].itemObject.amount, offered ? 0.35f : 1f))
                if (!offered) Add(bag[i]);
            if (!offered && ImGui.BeginDragDropSource())
            {
                dragFrom = i;
                ImGui.SetDragDropPayload("EG_TRADE", IntPtr.Zero, 0);
                ImGui.Text(Name(bag[i]));
                ImGui.EndDragDropSource();
            }
        }
        if (bag.Count == 0) ImGui.TextDisabled("(nothing tradable)");

        ImGui.Separator();
        ImGui.Text("Your offer (click to take back)");
        for (int s = 0; s < Slots; s++)
        {
            if (s > 0) ImGui.SameLine();
            var o = s < mine.Count ? mine[s] : null;
            if (ItemCell($"mine{s}", o?.Item, o?.Amount ?? 0, 1f) && o != null) RemoveAt(s);
            if (ImGui.BeginDragDropTarget())
            {
                unsafe
                {
                    var payload = ImGui.AcceptDragDropPayload("EG_TRADE");
                    if (payload.NativePtr != null && dragFrom >= 0 && dragFrom < bag.Count) { Add(bag[dragFrom]); dragFrom = -1; }
                }
                ImGui.EndDragDropTarget();
            }
        }

        ImGui.Text($"{partnerName}'s offer");
        for (int s = 0; s < Slots; s++)
        {
            if (s > 0) ImGui.SameLine();
            var it = theirsReady && s < theirs.Count ? theirs[s] : null;
            ItemCell($"theirs{s}", it, it?.itemObject.amount ?? 0, 1f);
        }
        if (!theirsReady && theirsJson.Length > 0) ImGui.TextDisabled("(loading their items...)");

        ImGui.Separator();
        bool ready = ILocked;
        if (ImGui.Checkbox("Ready to trade", ref ready)) SetReady(ready);
        ImGui.SameLine(200);
        if (TheyLocked) ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.5f, 1f), $"{partnerName} is ready");
        else ImGui.TextDisabled($"{partnerName} isn't ready yet");
        ImGui.TextDisabled("Any change on either side unticks both.");
        if (ImGui.Button("Cancel trade", new Vector2(140, 0))) Cancel();
        ImGui.End();
    }

    /// <summary>One square: the item's icon and amount, its tooltip on hover. True = clicked.</summary>
    static bool ItemCell(string id, InventoryItem item, int amount, float alpha)
    {
        var start = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton(id, new Vector2(Cell, Cell));
        var dl = ImGui.GetWindowDrawList();
        var end = new Vector2(start.x + Cell, start.y + Cell);
        dl.AddRectFilled(start, end, ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.12f, 0.15f, 1f)), 4f);
        dl.AddRect(start, end, ImGui.ColorConvertFloat4ToU32(new Vector4(0.35f, 0.35f, 0.4f, 1f)), 4f);
        if (item?.itemObject?.data == null) return false;

        var sprite = item.itemObject.data.itemIconUI;
        if (sprite && sprite.texture)
        {
            Rect r;
            try { r = sprite.textureRect; } catch { r = sprite.rect; }
            float w = sprite.texture.width, h = sprite.texture.height;
            uint tint = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, alpha));
            dl.AddImage(ImGuiUnity.TextureId(sprite.texture), new Vector2(start.x + 4, start.y + 4), new Vector2(end.x - 4, end.y - 4),
                new Vector2(r.xMin / w, r.yMax / h), new Vector2(r.xMax / w, r.yMin / h), tint);   // Unity's v is bottom-up
        }
        if (amount > 1)
        {
            string n = amount.ToString();
            var ts = ImGui.CalcTextSize(n);
            dl.AddText(new Vector2(end.x - ts.x - 3, end.y - ts.y - 1), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f)), n);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text(Name(item) + (amount > 1 ? $"  x{amount}" : ""));
            var data = item.itemObject.data;
            ImGui.TextDisabled($"{data.itemCategory}  {data.itemTier}");
            string desc = Text(data.Description);
            if (desc.Length > 0) { ImGui.PushTextWrapPos(300); ImGui.TextWrapped(desc); ImGui.PopTextWrapPos(); }
            ImGui.EndTooltip();
        }
        return clicked;
    }

    static string Name(InventoryItem it)
    {
        string n = Text(it.itemObject.data.Name);
        return n.Length > 0 ? n : it.itemObject.data.name;
    }

    static string Text(UnityEngine.Localization.LocalizedString s)
    {
        try { return s == null || s.IsEmpty ? "" : s.GetLocalizedString() ?? ""; }
        catch { return ""; }
    }

    // ---- wire ----

    static void Send(MsgType type, Action<NetDataWriter> body, int to = -2)
    {
        var w = new NetDataWriter();
        w.Put((byte)type);
        body(w);
        Plugin.SendTo(to == -2 ? partner : to, w);
    }

    static string Hash(string s)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s))).Replace("-", "");
    }
}
