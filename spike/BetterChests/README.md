# Better Chests

A small BepInEx 5 mod for the Emberville Demo. Standalone; works next to the Emberville multiplayer mod
(`spike/EmbervilleGhost`) or without it.

## X at a chest (quick-stack)
Walk up to a chest (its arrow shows) and press **X**: everything in your bag that the chest already holds goes in,
onto its stacks and then into free slots while there's room, each item in the game's list on the right ("Stored
Carrot (12)"). It's the chest screen's own "send common" button (`Inventory.SendCommonToChest`) without the screen:
the chest's items join the inventory as its chest section (as opening does), the button runs, and taking the section
out writes the chest back (as closing does). The key is in the config (`[QuickStack] Key`).

While you stand at a chest, **"X: quick deposit"** shows under its name: a copy of the chest's own name plate (the
game's font and backing). `[QuickStack] ShowHint = false` hides it.

## With the multiplayer mod
`QuickStack.Run(chest)` is the whole action and `QuickStack.StackInto(chest)` the deposit on this copy's contents.
The multiplayer mod (soft dependency, no compile-time reference either way) patches `Run` when Better Chests is
installed: a player whose area someone else runs gets the owner's real contents and lock first, a chest a teammate has
open is refused, and the new contents go to everyone.

## Build / release
`dotnet build spike/BetterChests -c Release` (copies the DLL into the game's `BepInEx/plugins`);
`pwsh spike/BetterChests/package.ps1` makes `spike/dist/BetterChests-v<version>.zip` (BepInEx included).
