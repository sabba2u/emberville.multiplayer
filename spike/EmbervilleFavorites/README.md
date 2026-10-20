# Emberville Favourite Weapons

A small BepInEx 5 mod for the Emberville Demo: put up to four weapons on F5-F8 and swap between them while playing.
Standalone; works next to the Emberville multiplayer mod (`spike/EmbervilleGhost`) or without it.

## Use
- **Inventory:** hover a weapon and press **F5-F8** to put it on that key (the same key again clears it). Its tooltip
  says "Favourite: F6".
- **Playing:** **F5-F8**, or a click on a slot of the bar above the belt, wields that weapon through the game's own
  equip; the one in your hand goes back into your bag. The weapon in hand is highlighted on the bar.
- Remembered per character, next to the save (`Saves/<character>.egfav`). Weapons have no ids, so a favourite is the
  item plus its rolled stats.
- **Weapon wheel:** hold **Left Alt** (gamepad: hold **LT**) and your four favourites come up in a ring around your
  character (F5 top, F6 right, F7 bottom, F8 left). Point the mouse (or right stick) at one, let go to wield it; let go
  near the middle to cancel. Attacks are off while it's open. Key in the config (`[Wheel] Key`).
- **Hover a slot on the bar:** the game's own item tooltip for that weapon, with how it compares to the one in your
  hand (damage, speed and stat differences in green / red). The walk key (Ctrl) toggles the side panel showing the
  equipped weapon next to it, as in the inventory.

## How it's drawn
The bar is four copies of the HUD's own belt slot (`UISlot`), placed in the same HUD group just above the belt: the
game's frame, icon, highlight and click handling, scaling and hiding with the HUD. No ImGui, so it doesn't clash with
the multiplayer mod's ImGui.

## Build / release
`dotnet build spike/EmbervilleFavorites -c Release` (copies the DLL into the game's `BepInEx/plugins`);
`pwsh spike/EmbervilleFavorites/package.ps1` makes `spike/dist/EmbervilleFavorites-v<version>.zip` (BepInEx included).
