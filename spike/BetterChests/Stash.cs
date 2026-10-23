using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.InputSystem;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;

namespace BetterChests;

/// <summary>
/// The shared stash: one big chest for all your characters in every world, open from anywhere (U, /stash, or the Stash
/// button). 6 tabs of 30 (the game's chest window holds 30), each with your own name and colour.
///
/// It's the game's own chest window: a hidden chest (never in the world, never saved with it) whose list of items is
/// one tab, filled from our file when it opens and written back as it changes and when it closes. The items are kept
/// the way the game itself saves them (SavedItem: the item, its rolled stats with our marks, the amount, the slot), in
/// "Saves/BetterChests/stash.json", outside the game's saves; the old file is kept as a backup before each write, and
/// a lock stops two copies of the game on this PC having it open at once. In co-op it's yours alone: nothing about
/// it is sent.
/// </summary>
[HarmonyPatch]
public static class Stash
{
    public const int Tabs = 6, Slots = 30;
    const string Id = "bc.stash";

    [Serializable] public class StashTab { public string Name = ""; public string Hex = ""; public List<SavedItem> Items = new List<SavedItem>(); }
    [Serializable] public class StashFile { public int Version = 1; public List<StashTab> Tabs = new List<StashTab>(); }

    static ConfigEntry<Key> key;
    public static string KeyName => key.Value.ToString();

    static readonly List<InventoryItem>[] pages = new List<InventoryItem>[Tabs];
    static readonly List<SavedItem>[] unknown = new List<SavedItem>[Tabs];   // items whose kind didn't load: kept as they were
    static readonly string[] names = new string[Tabs], hexes = new string[Tabs];
    static int current;
    static bool loaded, loading, openWhenLoaded;
    static Chest chest;
    static float nextAutosave;
    static long lastSig;

    public static bool IsOpen => chest && chest.isOpened;

    public static void Bind(ConfigFile config) =>
        key = config.Bind("Stash", "Key", Key.U, "Opens your shared stash (every character, every world)");

    // ---- open / close ----

    /// <summary>U, /stash, the Stash button: open it, or close it if it's open.</summary>
    public static void Toggle()
    {
        if (IsOpen) { chest.CloseChest(); return; }
        if (!Singleton<GameManager>.HasMain || !Singleton<GameManager>.main.player || Singleton<GameManager>.main.gameState != GameState.Playing)
        { Note("Load a world first"); return; }
        if (ChestFilter.OpenChestId != null) { Note("Close the chest first"); return; }
        if (!loaded) { openWhenLoaded = true; if (!loading) Load().Forget(); return; }
        if (!Lock()) { Note("Your stash is open in another copy of the game"); return; }
        if (!chest)
        {
            var go = new GameObject("BC_Stash");
            go.SetActive(false);   // (never awake: no art, no title, no save)
            UnityEngine.Object.DontDestroyOnLoad(go);
            chest = go.AddComponent<Chest>();
        }
        chest.chestID = Id;
        Singleton<ChestManager>.main.allChests[Id] = new ItemCollection { collectionID = Id, saveCollection = false, shareContent = ChestShare.None, mainItems = pages[current] };
        chest.OpenChest();
    }

    /// <summary>Our hidden chest opens and closes without the art and sounds a real one has (it has none).</summary>
    [HarmonyPatch(typeof(Chest), nameof(Chest.OpenChest)), HarmonyPrefix, HarmonyPriority(Priority.First)]
    static bool OpenOurs(Chest __instance)
    {
        if (!__instance || __instance != chest) return true;
        chest.isOpened = true;
        Bus<ChestEvent>.Invoke(new ChestEvent { chest = chest, EventType = ChestEvent.Event_Type.Open });
        lastSig = Signature(); nextAutosave = Time.unscaledTime + 1f;
        Bar.Show(true);
        return false;
    }

    [HarmonyPatch(typeof(Chest), nameof(Chest.CloseChest)), HarmonyPrefix, HarmonyPriority(Priority.First)]
    static bool CloseOurs(Chest __instance)
    {
        if (!__instance || __instance != chest) return true;
        chest.isOpened = false;
        Bus<ChestEvent>.Invoke(new ChestEvent { chest = chest, EventType = ChestEvent.Event_Type.Close });   // (the window writes the tab back into its list)
        Save();
        Singleton<ChestManager>.main?.allChests.Remove(Id);
        Unlock();
        Bar.Show(false);
        return false;
    }

    // ---- every frame ----

    public static void Tick()
    {
        var kb = Keyboard.current;
        if (kb != null && Application.isFocused && !ChestLooks.Editing && !Bar.Editing && !Typing() && kb[key.Value].wasPressedThisFrame) Toggle();
        if (IsOpen && Time.unscaledTime >= nextAutosave)
        {
            // as it changes: written within a second, so a crash loses nothing that was moved
            nextAutosave = Time.unscaledTime + 1f;
            long sig = Signature();
            if (sig != lastSig) { lastSig = sig; Save(); }
        }
        Bar.Tick();
    }

    static bool Typing()
    {
        var es = UnityEngine.EventSystems.EventSystem.current;
        var go = es ? es.currentSelectedGameObject : null;
        return go && (go.GetComponent<TMPro.TMP_InputField>()?.isFocused == true || go.GetComponent<InputField>()?.isFocused == true) ||
               !MainInput.Actions.Player.enabled && !UIPanels.HasOpenedPanel;   // (chat / a mod's text box has the game's controls off)
    }

    // ---- tabs ----

    public static int Current => current;
    public static string NameOf(int tab) => string.IsNullOrEmpty(names[tab]) ? (tab + 1).ToString() : names[tab];
    public static string HexOf(int tab) => hexes[tab] ?? "";

    /// <summary>Another tab into the same window: this one written back, the next one in.</summary>
    public static void Switch(int tab)
    {
        if (tab == current || tab < 0 || tab >= Tabs || !IsOpen) return;
        var inv = Singleton<Inventory>.main;
        inv.RemoveSectionItems(InventorySection.Chest);   // (the open tab's items back into its list)
        current = tab;
        Singleton<ChestManager>.main.allChests[Id].mainItems = pages[current];
        inv.AddSectionItems(pages[current]);
        var ui = UnityEngine.Object.FindAnyObjectByType<InventoryUI>();
        if (ui) ui.UpdateChest();
        Save();
        lastSig = Signature();
    }

    public static void Rename(int tab, string name, string hex)
    {
        names[tab] = (name ?? "").Trim();
        hexes[tab] = hex ?? "";
        Save();
    }

    // ---- the file ----

    static string FilePath
    {
        get
        {
            var path = SaveLoad.GetPath(Path.Combine("BetterChests", "stash.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            return path;
        }
    }

    static async UniTaskVoid Load()
    {
        loading = true;
        for (int i = 0; i < Tabs; i++) { pages[i] = new List<InventoryItem>(); unknown[i] = new List<SavedItem>(); }
        try
        {
            StashFile file = null;
            if (File.Exists(FilePath)) file = JsonUtility.FromJson<StashFile>(File.ReadAllText(FilePath));
            if (file != null)
                for (int t = 0; t < Tabs && t < file.Tabs.Count; t++)
                {
                    var tab = file.Tabs[t];
                    names[t] = tab.Name; hexes[t] = tab.Hex;
                    foreach (var s in tab.Items ?? new List<SavedItem>())
                    {
                        ItemData data = null;
                        try
                        {
                            var h = Addressables.LoadAssetAsync<ItemData>(s.Key);
                            await h.Task;
                            if (h.Status == AsyncOperationStatus.Succeeded) data = h.Result;
                        }
                        catch (Exception e) { Plugin.Log.LogWarning($"Stash: {s.Key}: {e.Message}"); }
                        if (!data) { unknown[t].Add(s); continue; }
                        s.Values.SetData(data);
                        pages[t].Add(new InventoryItem(new ItemObject(data, s.Amount) { randomValues = s.Values }, s.SlotIndex, InventorySection.Chest));
                    }
                }
            loaded = true;
            Plugin.Log.LogInfo($"Stash: {pages.Sum(p => p.Count)} items in {Tabs} tabs ({FilePath})");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"Stash: couldn't read it: {e.Message}"); Note("Couldn't read your stash (see the log); it wasn't changed"); }
        loading = false;
        if (loaded && openWhenLoaded) { openWhenLoaded = false; Toggle(); }
    }

    /// <summary>Every tab to the file (the open one from the window), the old file kept as stash.json.bak.</summary>
    static void Save()
    {
        if (!loaded) return;
        try
        {
            var file = new StashFile();
            var openNow = IsOpen ? Singleton<Inventory>.main.allItems.Where(i => i.section == InventorySection.Chest).ToList() : null;
            for (int t = 0; t < Tabs; t++)
            {
                var tab = new StashTab { Name = names[t] ?? "", Hex = hexes[t] ?? "" };
                foreach (var it in t == current && openNow != null ? openNow : pages[t])
                    if (it?.itemObject?.data)
                        tab.Items.Add(new SavedItem { Key = it.itemObject.data.addressKey, Values = it.itemObject.randomValues, Amount = it.itemObject.amount, SlotIndex = it.slotIndex, Section = InventorySection.Chest });
                tab.Items.AddRange(unknown[t]);
                file.Tabs.Add(tab);
            }
            var path = FilePath;
            File.WriteAllText(path + ".tmp", JsonUtility.ToJson(file, true));
            if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
            File.Copy(path + ".tmp", path, overwrite: true);
            File.Delete(path + ".tmp");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"Stash: couldn't write it: {e.Message}"); }
    }

    static long Signature()
    {
        long sig = 17;
        foreach (var it in Singleton<Inventory>.main.allItems)
            if (it.section == InventorySection.Chest && it.itemObject?.data)
                sig = sig * 31 + (it.itemObject.data.addressKey?.GetHashCode() ?? 0) * 7 + it.itemObject.amount * 131 + it.slotIndex;
        return sig;
    }

    // ---- one copy of the game at a time ----

    static string LockPath => FilePath + ".lock";

    static bool Lock()
    {
        try
        {
            int me = System.Diagnostics.Process.GetCurrentProcess().Id;
            if (File.Exists(LockPath) && int.TryParse(File.ReadAllText(LockPath).Trim(), out int pid) && pid != me)
            {
                try { if (!System.Diagnostics.Process.GetProcessById(pid).HasExited) return false; }
                catch (ArgumentException) { }   // (that copy is gone: a stale lock)
            }
            File.WriteAllText(LockPath, me.ToString());
            return true;
        }
        catch (Exception e) { Plugin.Log.LogWarning($"Stash lock: {e.Message}"); return true; }
    }

    static void Unlock()
    {
        try { if (File.Exists(LockPath)) File.Delete(LockPath); } catch { }
    }

    static void Note(string text)
    {
        var n = UnityEngine.Object.FindAnyObjectByType<NotificationsManager>();
        if (n) n.AddNotification(typeof(Stash), null, text, 0f, 1);
        Plugin.Log.LogInfo("Stash: " + text);
    }
}
