# Emberville Favourite Weapons

A small BepInEx 5 mod for the Emberville Demo: put up to four weapons on F5-F8 and swap between them while playing.
Standalone; works next to the Emberville multiplayer mod (`spike/EmbervilleGhost`) or without it.

## Use
- **Inventory:** hover a weapon and press **F5-F8** to put it on that key (the same key again clears it). Its tooltip
  says "Favourite: F6".
- **In the inventory (and chests, shops):** a weapon on a key shows it in gold in its slot's bottom-right corner (F5..F8).
- **Playing:** **F5-F8**, or a click on a slot of the bar, wields that weapon through the game's own equip; the one in
  your hand goes back into your bag. The bar is small and always there, on top of the row of menu buttons (Journal,
  Inventory...) over the health bar, right-aligned with them; its keys are labelled F5..F8, the weapon in hand in gold.
- Remembered per character, next to the save (`Saves/<character>.egfav`). Weapons have no ids, so a favourite is the
  item plus its rolled stats.
- **Weapon wheel:** hold **Left Alt** (gamepad: hold **LT**) and your four favourites come up in a ring around your
  character (F5 top, F6 right, F7 bottom, F8 left). Point the mouse (or right stick) at one, let go to wield it; let go
  near the middle to cancel. Attacks are off while it's open. Pointing at a weapon shows the game's tooltip for it (its
  stats, and the compare with the one in hand). Key in the config (`[Wheel] Key`).
- **How-to window:** the first time you play with the mod, the game's own tutorial window explains it (title, text, a
  looping video); **Shift+F5** shows it again. The video is `Tutorials/favorites.webm` (or `.mp4`) next to the DLL,
  310x180 like the game's own clips; without it the game's inventory clip plays.
- **Hover a slot on the bar:** the game's own item tooltip for that weapon, with how it compares to the one in your
  hand (damage, speed and stat differences in green / red). The walk key (Ctrl) toggles the side panel showing the
  equipped weapon next to it, as in the inventory.

## How it's drawn
The bar and the wheel are copies of the HUD's own belt slot (`UISlot`, without its key cap): the game's frame, icon,
highlight and click handling, scaling and hiding with the HUD; the labels use the slot's own font. The tooltip is a copy
of the inventory's (`ItemInfoWindow`), and the how-to window is the game's `TutorialManager` with a window made in code. No ImGui, so it doesn't clash with
the multiplayer mod's ImGui.

## Build / release
`dotnet build spike/EmbervilleFavorites -c Release` (copies the DLL into the game's `BepInEx/plugins`);
`pwsh spike/EmbervilleFavorites/package.ps1` makes `spike/dist/EmbervilleFavorites-v<version>.zip` (BepInEx included).
