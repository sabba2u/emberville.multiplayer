# emberville.multiplayer

Unofficial co-op for **Emberville** (the demo; Unity 6, Mono) as a BepInEx 5 plugin: `spike/EmbervilleGhost`.
Work in progress. Player downloads are on the [Releases](../../releases) page; notes and the roadmap are in
[`todo.md`](todo.md) (top section: "Emberville").

> Everything below "v0.1.3" is built but not tested in a real session yet; it ships as v0.1.3 once it has been.

## What works
- **Steam:** host with F8 > **HOST VIA STEAM**: friends join from their Steam friends list (Join Game) or an invite
  (Insert menu > Invite friends); no IP addresses, no port forwarding (Steam's relay). The mod brings Steamworks.NET
  and `steam_api64.dll` itself (the demo has no Steam code) and runs as the demo (app 5355880).
- **Connecting by IP:** host with F8 (shared XP, instanced loot, PvP, shared buffs), join with F9 by IP. The joiner downloads
  the host's world and brings their own character. If the host leaves, the player who joined first takes over hosting
  where they stand and the others reconnect to them.
- **Other players** as real characters: looks, gear, animations, name, health and Kymia bars, what they're doing
  ("in a chest", "talking"...), status effects, damage numbers and a red flash when they're hit, a speech bubble while
  they type and what they said.
- **A party strip** along the top: every player in join order (the same on every screen) with a live portrait of their
  character's weapon, health and Kymia, status (it moves below a boss's health bar during boss fights). Off-screen teammates get edge arrows with distance; teammates show on the
  minimap and on the big world map (M).
- **One shared world:** creatures (controlled by whoever is nearest, kept with whoever is fighting them), bosses (phases,
  immunity, the spider queen's eggs and hatchlings, webbed players), attacks and spells everyone can see, status effects
  on hits (burning, poison...), matching gory deaths, drops (dupe-proof), chests, breakables, rocks / ore / trees,
  doors and levers, key altars / item locks (whoever has the item opens it for everyone), floor buttons (down while
  anyone stands on one), town repairs, the clock (day and time), story progress (a door one player opens is open for everyone), story cutscenes (whoever sets one off,
  everyone in the area watches it; someone arriving later skips to how it left the world), every
  crafting station (cooking pot, campfire, anvil, forge, furnace, sawmill...: the food cooks once, collecting is
  dupe-proof), chests that stations and farming pull from, farm plots, building and removing on the estate.
- **Chat** (Enter), death announcements, pings and drawing on the ground (middle mouse).

## Quality of life
- **Mouse walking:** hold the mouse side (thumb) button and your character walks toward the mouse (closer = slower,
  rings show it); a quick click walks to that spot. Walls still block, WASD / pad always win. Insert menu: on / off,
  which button (Back, Forward, or Middle).
- **Magnet:** Insert menu > Magnet picks up items near you by themselves (range 1-8, per category: materials, food,
  coins, weapons, armour, gear, placeables, key items, other), only what fits in your bag. Off to start with.
- **X at a chest:** walk up to a chest (its arrow shows) and press X: everything in your bag that the chest already
  holds goes in (onto its stacks, then free slots while there's room), and each item shows in the game's list on the
  right ("Stored Carrot (12)"). It's the chest screen's own "send common" button without opening the screen, and works
  in multiplayer like a normal open + close.
- **Online Players screen (O):** who's connected, where, health, ping, what they're doing; mute anyone (just for you);
  trade; the host can kick and ban (a second click confirms; bans follow a player ID, not the name; unban list).
- **Sleep vote:** the demo has no beds, so everyone types `/sleep` and the clock skips to 6:00 the next morning.
- **Team buffs:** a buff you cast on yourself (Swift Foot...) also lands on teammates within 8 m (host option).
- **Keep playing after the demo:** the demo's "Thank you for playing" screen and freeze (after Gumu's cutscene in Town)
  are skipped, so you can keep walking around Town, the estate and the mansion (Insert menu to turn it back on).
- **Characters are safe:** before joining from inside a game, the character files involved are backed up to
  `Saves/EmbervilleGhost_Backups` (last 10 kept); a character open in another copy of the game can't be picked twice.
- **"Cohh is the authority of this map"** (top right): whose game runs the enemies, chests and drops of the area you're
  in (the `[Host]` / `[Client]` tags under the feet say who hosts the session). The status line (top left: version,
  connection, fps) starts hidden: **Delete** shows it.
- **Heal / Kymia popups** for teammates, a **Kymia bar** under their health bar, the game's own bordered boxes for
  messages ("you were kicked") and trade invites.

## Trading with friends
Online Players screen > **Trade**, or `/trade P2` (or their name). The other player accepts (in the game's own yes/no
box when it can show one). The trade window shows your bag with the real item icons; drag (or click) up to 8 items into
your offer, click one to take it back; hover anything for its tooltip. Tick **Ready to trade** when you're happy with
**their** offer: any change on either side (or an offered item leaving your bag) unticks both, and the swap happens only
when both are ticked on the offers as they are. Key items and equipped gear can't be traded. Nothing is lost or
duplicated: each game swaps its own side once both agree on exactly the same two offers.

## Pets (early)
Pets start **off**: `/pet on` (or the Insert menu checkbox) turns them on. Then `/pet` befriends a pet that follows you around, fights with you and levels up:
- **13 kinds**, the demo's small creatures: Chicken, **Glowbat** (flies, hovers with a bob), Pink / Green / Gray
  Lilshroom, Blue / Green / Purple Lil Tick, Red / Green / Salmon Lil Bomber, Spiderling, Bug Fuzz. (No dog or cat in
  the demo's art.) `/pet list`, `/pet glowbat` to switch: name, level and colour stay.
- **Immune** to everything (nothing can hit it). It attacks enemies close to you through the game's own damage, so its
  hits count as yours (kills give you the XP, works in multiplayer).
- **XP and levels:** +1 per hit, +10 per kill; more damage each level; a name tag with an XP bar.
- **Names:** each kind has its own default (Clucky the chicken, Fang the Glowbat, ...); type your own in the Insert
  menu's Name box or `/pet name ...` (a name you picked stays when you switch kinds).
- **Colours:** `/pet color red` (or any `#RRGGBB`) or the colour picker in the Insert menu; everyone sees your pet in
  your colour, following your character on their screen.
- A kind can be made once the game has that creature loaded (most are; `/pet scan` shows what's in memory here).
  Early: it walks through walls and the attack range / animations still need tuning.

## Commands and keys

| Chat command | What it does |
|---|---|
| `/help` | this list in chat |
| `/online` (`/players`, `/party`) | the Online Players screen |
| `/trade P2` (or a name) | ask someone to trade |
| `/sleep` | ready to skip to the morning (everyone has to) |
| `/pet on`, `/pet off` | pets on / off (they start off) |
| `/pet` | befriend your pet / call it / send it home |
| `/pet list`, `/pet <kind>` | the pet kinds; switch to one |
| `/pet name Clucky`, `/pet color red` | rename, recolour |
| `/pet scan` | which creatures the game has loaded right now |
| `/estate` | unlock building and repairs on your estate (host); lists the broken buildings here |
| `/resources` (`/resources 250`) | 100 (or more) of every material, and enough for every repair in the area |
| `/repair` | repair the nearest broken building (uses the materials, like walking onto it and pressing F) |
| `/devmode` | the game's hidden developer tools on / off |
| `/devpanel` | open the developer panel (no numpad needed) |
| `/arena` | teleport to this scene's test arena |

| Key | Mod | | Key | Game (left alone) |
|---|---|---|---|---|
| F8 / F9 / F10 | host (IP or Steam) / join / disconnect | | Tab | inventory + character |
| Insert | mod menu | | I / C / P / K / J | inventory, character, classes, masteries, quests |
| Delete | status line (top left) on / off | | | |
| Mouse side button | hold: walk toward the mouse; click: walk there | | | |
| Right-click a party card | teleport / trade / mute | | | |
| Enter | chat | | M | world map |
| O | Online Players | | F | interact / pick up |
| X | put matching items into the chest in front of you | | Q E R T | quick-bar items |
| Middle mouse | ping (click), draw (hold and drag) | | Space / Shift / Ctrl | dodge / show names / walk |

## Secrets: the dev panel, test arenas and the estate
- **The secret dev panel:** the demo still contains the developers' own tools, switched off. `/devmode` (or the Insert
  menu checkbox) switches them back on in every scene: **Ctrl+Shift+Numpad7** opens the developer panel (go to Town,
  unlock skills and masteries, heal, advance time, player speed...), with Ctrl+Shift held **Numpad+** gives XP and
  **Numpad-** hurts you, **Numpad1** opens the arena and spawn panel. `/devpanel` opens it without a numpad.
- **Test arenas:** the developers' fighting arenas (a spawn point and a zone where test monsters appear). The panel's own
  "Test Arena" button loads a scene the demo doesn't ship, so the Insert menu lists the arenas in the scene you're in and
  `/arena` teleports you to one; Numpad1 then spawns monster groups there.
- **The estate (your house / mansion and farm in Town):** building, placing and repairing are behind two story flags
  the demo never sets. `/estate` or the Insert menu checkbox turns them on (the host decides, everyone gets it), with a
  "Go to Town" button. Everything built is synced. **Repairing the town:** 11 ruined buildings (Blacksmith, Bar,
  Mayor, Carpenter...): `/resources`, walk onto a ruin and press F (or `/repair`). The repairer pays, everyone sees
  it repaired (also the broken portal), and each building saves on its own (the demo gave them all the same save id).

## Build
Requirements: .NET SDK, Emberville installed with BepInEx 5.4.23 (copy `winhttp.dll`, `doorstop_config.ini`,
`.doorstop_version` and `BepInEx/core` into the game folder).

```
dotnet build spike/EmbervilleGhost -c Release -p:GamePath="<path to Emberville Demo>"
```
The build references the game's own DLLs from `<GamePath>/Emberville_Data/Managed` (not included here) and copies the
plugin into `<GamePath>/BepInEx/plugins/EmbervilleGhost`. Default `GamePath` is in `EmbervilleGhost.csproj`.

## Release zip (for players)
```
pwsh spike/EmbervilleGhost/package.ps1 [-GamePath "<path to Emberville Demo>"]
```
Builds Release and writes `spike/dist/EmbervilleMultiplayer-v<version>.zip` (version = `Plugin.Version`): BepInEx 5
(taken from the game folder), the mod in `BepInEx/plugins/EmbervilleMultiplayer`, a player README (install, host,
join, uninstall, credits). Console window off, log file on. Players unzip it into the game folder.

## Testing on one PC
Launch `Emberville.exe` twice. Copy #2 keeps its own options file (window size); characters are shared, so don't load
the same character in both. Windows arrange themselves side by side at 1280x720.

## Not included
No game code or assets: the decompiled reference used for research stays local (`reference/`, git-ignored).
`spike/libs/OverlayDearImGui`: ImGui.NET + cimgui (MIT). `spike/libs/Steamworks`: Steamworks.NET (MIT) + Valve's
redistributable `steam_api64.dll`.
