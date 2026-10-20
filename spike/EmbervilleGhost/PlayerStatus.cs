using UnityEngine;

namespace EmbervilleGhost;

/// <summary>What a player is busy with, shown under their name so you know why they're standing still
/// (our BDSP mod's "in menu" / "in cutscene" badges). Sent as one byte in PlayerState.</summary>
internal enum PlayerStatus : byte
{
    None, Menu, Inventory, Chest, Store, Journal, Character, Recycling, Talking, Cutscene, Paused, Loading,
    Customizing, Dead, Chatting, ModMenu,
}

internal static class PlayerStatusUtil
{
    static PanelsManager panels;

    /// <summary>Ours, from the game's own state: GameManager.gameState, PanelsManager's chest/store flags and
    /// which panel is on top of UIPanels' stack.</summary>
    public static PlayerStatus Local()
    {
        if (Chat.IsOpen) return PlayerStatus.Chatting;
        if (ImGuiUnity.IsOpen) return PlayerStatus.ModMenu;
        var me = LocalPlayer.Get();
        if (me && me.IsDead) return PlayerStatus.Dead;
        if (Singleton<GameManager>.HasMain)
        {
            switch (Singleton<GameManager>.main.gameState)
            {
                case GameState.Loading: return PlayerStatus.Loading;
                case GameState.Cutscene: return PlayerStatus.Cutscene;
                case GameState.Dialogue: return PlayerStatus.Talking;
                case GameState.Customizing: return PlayerStatus.Customizing;
                case GameState.Paused: return PlayerStatus.Paused;
                case GameState.Gameover: return PlayerStatus.Dead;
            }
        }
        if (!panels) panels = Object.FindAnyObjectByType<PanelsManager>();
        if (panels)
        {
            if (panels.hasChestOpened) return PlayerStatus.Chest;
            if (panels.hasStoreOpened) return PlayerStatus.Store;
            var top = UIPanels.GetTop();
            if (top)
            {
                if (top == panels.panelInventory) return PlayerStatus.Inventory;
                if (top == panels.panelQuests || top == panels.panelLore) return PlayerStatus.Journal;
                if (top == panels.panelStats || top == panels.panelClass) return PlayerStatus.Character;
                if (top == panels.panelRecycling) return PlayerStatus.Recycling;
            }
        }
        return UIPanels.HasOpenedPanel ? PlayerStatus.Menu : PlayerStatus.None;
    }

    /// <summary>The game is covering the world with something (menus, journal, dialogue, cutscene...): our name tags
    /// and labels would just be clutter, so the HUD hides them. Chatting and our own menu don't count.</summary>
    public static bool LocalBusy
    {
        get
        {
            var s = Local();
            return s != PlayerStatus.None && s != PlayerStatus.Chatting && s != PlayerStatus.ModMenu;   // dead: the "YOU DIED" screen covers it
        }
    }

    public static string Label(PlayerStatus s) => s switch
    {
        PlayerStatus.Menu => "in a menu",
        PlayerStatus.Inventory => "in inventory",
        PlayerStatus.Chest => "in a chest",
        PlayerStatus.Store => "shopping",
        PlayerStatus.Journal => "reading the journal",
        PlayerStatus.Character => "in character menu",
        PlayerStatus.Recycling => "recycling",
        PlayerStatus.Talking => "talking",
        PlayerStatus.Cutscene => "in a cutscene",
        PlayerStatus.Paused => "paused",
        PlayerStatus.Loading => "loading",
        PlayerStatus.Customizing => "customizing",
        PlayerStatus.Dead => "down",
        PlayerStatus.Chatting => "typing...",
        PlayerStatus.ModMenu => "in mod menu",
        _ => "",
    };
}
