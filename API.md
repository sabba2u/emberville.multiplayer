# Emberville mods: commands and API

Everything the mods let you call: chat commands (type them after Enter), lines for NPC dialogue, and C# for other
BepInEx mods. All of it is in the **EmbervilleModPack** download.

## Chat commands

Press **Enter**, type the command with a `/` in front, press Enter. `/help` lists them in the game.

### Travel and players (the multiplayer mod)
| Command | What it does |
|---|---|
| `/tp <name>` | Go to a player (part of the name will do), even onto their dungeon or descent floor. |
| `/summon <name>` | Bring a player to you, now (no asking). |
| `/summon all` | Bring everyone to you. |
| `/online` (`/party`, `/players`) | The Online Players screen (same as **O**). |
| `/trade <name>` | Ask a player to trade. |

### World and story
| Command | What it does |
|---|---|
| `/sleep` | Ready to skip to the morning (everyone has to). |
| `/estate` | Unlock building and repairs on your estate (host). |
| `/repair` | Repair the nearest broken building. |
| `/resources [n]` (`/mats`) | 100 (or n) of every material. |
| `/respec` | Every stat point back to spend again (level, XP and gear kept). |
| `/stash` | Your shared stash (also the U key): 6 tabs of 30 slots, shared by every character on this PC. |
| `/tilt` | The tilted 2.5D view on / off (experimental; Insert > View has its sliders). |

### Dungeons and the descent
| Command | What it does |
|---|---|
| `/dungeon` | Into a generated dungeon, or back out. |
| `/dungeons` | Lists every dungeon type in the game. |
| `/dungeon <name>` | Builds that dungeon type (any part of its name: `negedra`, `library`, `lab`...) with a new seed and takes you in. |
| `/arena` | To this scene's test arena. |
| `/walls` | Show what's solid (debug). |
| `/exitarrow`, `/exitpath` | An arrow / a line to the stairs down (testing). |

### Weapons and pets
| Command | What it does |
|---|---|
| `/hook` | A Hunter's Hook. (`/hookshout` plays its shout.) |
| `/clucking` | A Staff of Clucking. |
| `/risen` | A Staff of the Risen. |
| `/ninja` | Become a Ninja (Better Classes, early alpha): the class on and every Ninja skill learnt. |
| `/pet on`, `/pet off`, `/pet` | Pets on / off; befriend, call or send home your pet. |
| `/pet list`, `/pet <kind>`, `/pet name <name>`, `/pet color <colour>`, `/pet scan` | Kinds, switch, rename, recolour, what's loaded. |

### Combat (Better Combat)
| Command | What it does |
|---|---|
| `/bowlock`, `/bowlock 15` | Arrows curve onto a creature near your aim; the cone in degrees. |
| `/moveattack`, `/moveattack 70` | Move while attacking; bow aiming speed in %. |
| `/centercam`, `/centercam 30` | Camera centred on you, dashes too (off to start); its lag in % of the game's (0 = locked). |
| `/spellkeys`, `/spellkeys 0.6` | Spell keys answer every press (on to start); how long a press is remembered, in seconds. |
| `/gamecombat` | Before / after: every Better Combat switch off (the game's own combat), and back. |

### Weather (Better Weather)
| Command | What it does |
|---|---|
| `/rain`, `/snow` | That weather on / off (for everyone). |
| `/rain on`, `/snow on`, `/rain off`, `/snow off`, `/clear` | Set it. |

### Testing
`/nodeath` (no damage), `/onehitkill` (500 a hit), `/noclip` (walk through walls), `/devmode` (the game's developer
tools), `/devpanel` (its panel), `/uidump` (the on-screen UI to `BepInEx/UIDumps`), `/unlockclasses` (every class
learned, and the class tree open past the demo's level 15, to 40, its "coming soon" skills too), `/lockclasses` (back).

## NPC dialogue (Better NPCs and Quests)

In an NPC's `.twee` file:
- `(trigger: "command <anything>")` runs **any chat command above**: `command tp sabba`, `command snow on`,
  `command respec`, `command summon all`...
- `(trigger: "start <quest>")`, `turnin <quest>`, `advance <quest>`, `(set: $flag to 1)`.
- Conditions can read `$flags`, `$has_<item>`, `$quest_<id>...`, and `$has_pet` (Better Pets and Summons).
- Quest rules react to events: `boss_kill <floor>`, `descent_floor <n>`, `pickup <item>`, kills.

See `README-BetterNPCsAndQuests.txt` in the download for the files.

## C# API (for other mods)

Add a **soft** dependency on the mod (`[BepInDependency("<guid>", BepInDependency.DependencyFlags.SoftDependency)]`)
and call it from a method that only runs when it's installed (`Chainloader.PluginInfos.ContainsKey("<guid>")`).

### Multiplayer: `EmbervilleGhost.Api` (guid `spike.embervilleghost`)
```csharp
EmbervilleGhost.Api.Command("summon all");   // any chat command (true if it was one)
bool online = EmbervilleGhost.Api.Online;     // playing together right now
List<string> names = EmbervilleGhost.Api.Players();
EmbervilleGhost.Api.TeleportTo("sabba");      // go to a player (dungeon floors too)
EmbervilleGhost.Api.Summon("Cohh");           // bring them here, now
EmbervilleGhost.Api.SummonAll();
EmbervilleGhost.Api.Teleport(new Vector3(-52f, 66f, 0f));   // move yourself within your current area
```

### Weather: `BetterWeather.Plugin` (guid `spike.emberville.betterweather`)
```csharp
using BetterWeather;
Plugin.SetWeather(Kind.Rain);                 // with each player's own sliders
Plugin.SetWeather(Kind.Clear);
Plugin.SetWeather(Kind.Snow, new WeatherOptions { Amount = 2f, Darkness = 0.5f, Settle = 3f });
Plugin.SetWeather(Kind.Rain, new WeatherOptions { Amount = 0.5f, Darkness = 0.9f, LightningEvery = 4f, Wind = 2f });
Plugin.StrikeAt(position);                    // lightning there, for everyone nearby (harmless)
Kind now = Plugin.Current;                    // Clear, Rain, Snow
WeatherOptions o = Plugin.CurrentOptions;
Plugin.Command("rain on");
```
`WeatherOptions` (any left out: that player's slider; for this spell of weather only, never saved):

| Option | Meaning |
|---|---|
| `Amount` | Drops or flakes (x; 1 = medium). |
| `Darkness` | How dark the sky gets (0 - 0.9). |
| `Settle` | How fast snow settles (x; 1 = about 7-8 minutes to white). |
| `LightningEvery` | Seconds between strikes, about (0 = none; rain). |
| `Wind` | Wind strength (x; 1 = usual, 0 = still). |

`SetWeather` and `StrikeAt` are shared with everyone in co-op.

### Pets and summons: `BetterPetsAndSummons` (guid `spike.emberville.betterpetsandsummons`)
```csharp
Plugin.Command("pet on");                     // any of its chat commands
Plugin.Rally(point);                          // your chickens and risen run there
bool staff = Plugin.HoldingStaff;
Plugin.MaybeDrop(at, floor);                  // a chance of a staff dropping (guardians use it)
CluckStaff.Give(); RisenStaff.Give();         // into your bag
ItemObject s = CluckStaff.Make(quality: 3);   // make one
```

### NPCs and quests: `BetterNPCsAndQuests` (guid `spike.emberville.betternpcsandquests`)
```csharp
Events.Raise("boss_kill", "5");               // feed an event to the quest rules
foreach (var (id, root) in Plugin.Bodies()) { } // the NPCs standing in this scene
```
Any mod with `public static bool Command(string)` gets dialogue's `command ...` lines; any mod with
`public static int? NpcFlag(string)` can answer `$flags` in conditions.

### Chests: `BetterChests.ChestLooks` (guid `spike.emberville.betterchests`)
```csharp
ChestLooks.Set(chestId, "Ore", "FF8A80", takes: (int)ItemCategory.Material);   // name, colour (RRGGBB), what it takes
var all = ChestLooks.All();                   // (id, name, hex, takes) for every customized chest
int takes = ChestLooks.TakesOf(chestId);
ChestLooks.Changed += (id, name, hex, takes) => { };   // on every local edit
```

### Favourite weapons: `EmbervilleFavorites.Favorites` (guid `spike.emberville.favorites`)
`Favorites.Wield(slot)`, `Favorites.Find(slot)`, `Favorites.HasKey(slot)`, `Favorites.KeyOf(item)` (slots 0-3 = F5-F8).

### Combat: `BetterCombat` (guid `spike.emberville.bettercombat`)
`BowAssist.SetAngle(degrees)`, `BowAssist.Toggle()`, `MoveAttack.Command("70")`, `Plugin.Command("bowlock")`.

### Streaming: `BetterStreaming` (guid `spike.emberville.betterstreaming`)
`Hooks` (set by the multiplayer mod; on its own Better Streaming connects and runs heal, poison, instant death and
chain lightning, and raids say they need the pack):
- `Hooks.DrawsUi`: true when another mod draws `Plugin.DrawMenu()` (the Streaming tab) and `Plugin.DrawHud()` (banners,
  the effects panel).
- `Hooks.SpawnRaid(around, count, toughness, viewer) → bool`, `Hooks.ChickenRaid(around, waves, toughness, viewer) → bool`:
  the raids' creatures.
- `Hooks.IsTwitchCreature(object) → bool`: a creature Twitch sent (a death to it costs nothing).
- `Hooks.Say(text)`: a message for the player.
