# Multiplayer modding: LiteNetLib / Steamworks (Spacewar 480) via BepInEx

Goal: inject our own multiplayer (LiteNetLib first, Steamworks/Spacewar 480 as an alternative)
into a single-player Unity game with BepInEx + Harmony. Main target: **Emberville**.

---

## Emberville (current focus, started 2026-10-19)

Emberville released 2026-10-19. **Duckov is done**; from now on this repo is about Emberville. What carries over
is only what we learned in Duckov: the techniques, the notes in this file, and the reusable code in
`spike/DuckovGhost` (as a source to copy from, not to keep developing). No more Duckov work.

### Open: descent walkability
- **Test seed 810581054** (type it in the descent setup box): a big open floor ("Endless Tunnel") with open-looking
  floor you can't walk on. Debug with `/walls` (red = tiles we made solid from the rooms' Wall / WallCover / Water
  layers, orange = other colliders near you); fix in `Dungeon.BuildCollision`.

### What we carry over from Duckov
- **Code to copy (game-independent):** `Transport.cs` + `SteamTransport.cs` + `SteamLobby.cs` (LiteNetLib or
  Steam relay, lobbies, invites), `SnapshotBuffer.cs` (100 ms smoothing), `ImGuiUnity.cs` (menu drawn inside the
  game), `NetLog.cs` / `Toasts.cs`, `Perf.cs`, `SaveIsolation.cs` + `WindowFix.cs` (several copies on one PC),
  `Pings.cs` (pings + drawn paths), the `Plugin.cs` routing (player IDs, `Send` / `SendToAllClients` / `Reply` /
  `SendToPlayer`, host relays, level-filtered sends, `MsgType` byte).
- **Techniques:** host authority through a Harmony prefix on one choke-point method per system (client asks, host
  applies + broadcasts); puppets = the game's own character with AI, animation scripts **and physics** switched off;
  IDs from the game's own save keys or host-assigned; harmless "fake" replays of actions for looks; dupe-proof
  item moves (client asks before every exit); per-client level snapshot on arrival; never retry/log per frame.
- **Lessons:** draw ImGui inside Unity (not a separate overlay window); use the game's own Steam app ID if it
  inits Steam; switch off puppet simulation; keep an fps + mod-ms readout from day one.
- Details: "Reusable for Emberville", "how the Duckov spike networks things", "Harmony", "performance lesson" below.

### Recon results (2026-10-19, Emberville Demo)
- **Install:** `K:\__games\steamapps\common\Emberville Demo`. Steam app **5355880** (demo, build 26054950); the full
  game is app 2295170. Developer Cygnus Cross (`app.info`: CygnusCross / Emberville). "Alpha Demo".
- **Mono, not IL2CPP:** `Emberville_Data/Managed/Assembly-CSharp.dll` + `MonoBleedingEdge`, no `GameAssembly.dll`.
  -> **BepInEx 5 like Duckov**, Harmony works as before; Ghidra not needed (ILSpy gives readable C#).
- **Unity 6000.1.17f1**, URP (2D renderer), D3D12. **A 2D game:** Tilemap, SpriteShape, 2D Animation + IK,
  `NavMeshPlus` (2D navmesh), `Physics2D`. Other libs: Unity Input System (`GameInputActions`), Unity Behavior
  (enemy AI graphs), Addressables, Localization, Cinemachine, Wwise audio (`AkSoundEngine`), UniTask, R3 +
  ObservableCollections, Newtonsoft.Json (saves), Burst.
- **No Steam in the demo:** no `steam_api64.dll`, no Steamworks.NET. -> Steam transport needs us to bring
  Steamworks.NET + init ourselves (demo app ID 5355880 or Spacewar 480; check again on the full release).
- **No networking anywhere:** no Netcode/Mirror/Photon/Steamworks/sockets in Assembly-CSharp or the other DLLs
  (`UnityEngine.MultiplayerModule` / `Unity.Multiplayer.Center.Common` are stock Unity stubs). Single-player
  assumptions in code, e.g. `PlayerProxy`: `FindObjectsByType<PlayerMaster>()[0]`.
  Misleading names: `ChestShare` (which buildings use your chests), `ISaveRemote`/`ILoadRemote` (save system),
  `TokenValidate` (6-digit TOTP playtest gate), `Ghost` (a spooky NPC).
- **No existing co-op mod found.** Developer: Early Access launches single-player; co-op "planned for the full 1.0
  release" (no date). Official modding (Workshop) also planned for 1.0. So we're first.
- **Decompile:** `reference/emberville/Assembly-CSharp/` (ilspycmd, 1291 files, mostly global namespace).
- **Likely choke points to read next:** `PlayerMaster` (55 KB: movement, states, animator), `GameManager`,
  `SceneLoader`, `TimeManager` / `GameDateTime` / `NewDayEvent`, `SavingManager` / `SaveLoad` / `PlayerData` /
  `WorldData`, `FarmingManager` / `SoilTile` / `Crop`, `PlacementManager` / `PlaceableController`, `Inventory`,
  `Chest` / `ChestManager`, `Enemy` / `EnemiesManager` / `EnemySpawnArea`, `CombatSystem` / `DamageData` /
  `IDamageable`, `GatheringObject`, `Projectile`, `DungeonGeneration` (seeded: `SeededSystemRandomProvider`),
  `WorldMap` / `MinimapControl` / `WorldMapMarker` (for teammate markers), `GameInputActions`.

### Note: saves and copies on one PC
- Saves: `%USERPROFILE%\AppData\LocalLow\CygnusCross\Emberville\Saves\`: `player_slotN.evp` (+ `_playerFriendly.json`),
  `world_slotN.evw/.eve` (+ friendly JSON), `options.eopt`. The slot number is only in the file name; new characters
  take the first free number. **Character select shows at most 3 characters** (`CharacterSelectionPanel`: `i < 3`).
- First try (Duckov-style: whole data folder per copy) hid characters made in one copy from the others. Now characters
  and worlds are **shared**; only `options.eopt` is per copy (`SaveIsolation` patches `SaveLoad.GetPath`). Don't load the
  same character in two copies at once (last save wins).

### Note: no 16:10 option (1920x1200 missing)
- The Video > Resolution list = the monitor's resolutions **filtered by a hardcoded whitelist** (`VideoOptions.defaultOptions`,
  a serialized field inside the game's assets, not a text file). 1920x1200 isn't on it (1280x800 is).
- **Settings files outside the mod exist, but editing them doesn't stick:**
  - `%USERPROFILE%\AppData\LocalLow\CygnusCross\Emberville\Saves\options.eopt`: plain JSON,
    `"Resolution":{"value":{"Width":1280,"Height":800}}`, `"FullScreenMode":2` (0 fullscreen, 1 borderless, 2 windowed).
  - Unity's own startup size in the registry: `HKCU\Software\CygnusCross\Emberville`, `Screenmanager Resolution Width/Height_h...`
    (also `-screen-width 1920 -screen-height 1200` on the command line).
  - Unity starts at that size, but when the options load (main menu / UIManager), `VideoOptions.SetupResolution` looks the
    saved size up in the whitelisted list; not found -> index 0, and the R3 `ReactiveProperty` subscribe fires right away ->
    `Screen.SetResolution(list[0])` and the file is overwritten with it.
- **Also:** `CameraManager` sets the PixelPerfectCamera per resolution (2560x1440/3440x1440 -> 96 PPU, ref 1920x1080;
  1366x768 -> 45; 1280x800 -> 48 PPU ref 1280x800; everything else 64 PPU ref 1920x1080). 1920x1200 would fall into
  "everything else" (16:9 reference) -> likely bars or cropping.
- **Fix = tiny mod patch (to do):** prefix on `VideoOptions.SetupResolution` adding `Res(1920,1200)` (any 16:10) to
  `defaultOptions`, plus a postfix on `CameraManager`'s resolution handler: 1920x1200 -> 72 PPU, ref 1920x1200
  (same ratio as their 1280x800 -> 48 rule). Could ship as its own tiny plugin for regular players too.

### Our own Colyseus projects (reuse for join codes / a room server, roadmap 13c)
Both live in `I:\programming.projects\claude.code\game.reverse.engineering\` (not at the top level).
- **bdsp-multiplayer** (Pokemon BDSP, Unity IL2CPP, BepInEx 6, net6). Colyseus 0.16 server `server\src\WorldRoom.ts`
  (+ `moderation.ts`, `tools\bot.ts`), one room `"world"` (joinOrCreate, optional password/admin), **no Schema**: plain
  messages (`area`, `look`, `pose`, `emote`, `state`, `door`, `npc`, `npc_snap`, `chat`, `ping`/`pong`, admin kick/ban/mute).
  Poses only go to the same area; the longest-present player in an area hosts its NPCs. Port 2568, `npm start`,
  Docker files, packaged as `SabbasUMS-Server.exe` ("Universal Multiplayer Server"; its `todo.md` has the UMS plan).
  Client: hand-written `plugin\Net\ColyseusClient.cs` + `MsgPack.cs` (HTTP matchmake POST, then WebSocket). Uses
  net6-only bits (System.Text.Json, Channels, ClientWebSocket): needs porting for net472/Mono (or use the official
  Colyseus Unity SDK, which targets Mono).
- **scribblenauts-multiplayer** (native engine, C proxy DLL). Same Colyseus 0.16 server shape (`scribble-mp-server`,
  port 2567, room `"world"`, max 4, per-level host in `routing.ts`). `docs\re\colyseus-0.16-wire.md` documents the
  0.16 wire protocol the BDSP C# client was built from.
- **For Emberville:** a Colyseus transport = one more `ITransport` (`Transport.cs`) using the BDSP server as the
  base; our LiteNetLib message bytes can ride inside Colyseus messages unchanged. LAN/LiteNetLib stays for testing.

### Emberville plan
**Step 1 - Recon**
- [x] Find the install, Mono vs IL2CPP (**Mono**), Unity version, Steam (none in demo), existing mods (none).
- [x] Decompile into `reference/emberville/`.
- [ ] Read the choke points above: player class + movement + animator, scene loading, save/load, time of day,
      spawning (NPCs, enemies), damage, interact, item add/remove, tool use, crops/tiles, chests, dungeon seed.

**Step 2 - Mod skeleton**
- [ ] Install BepInEx, hello-world plugin logging (`HideManagerGameObject = true` if the manager gets destroyed).
- [ ] Copy over ImGui (Insert menu), log, toasts, perf readout.
- [ ] Two copies on one PC with separate saves (single-instance flag, `steam_appid.txt`, save redirection), windows side by side.

**Step 3 - Ghost co-op**
- [ ] Copy transport + routing; F8 host / F9 join on LAN.
- [ ] Send player position, facing, animation at 20 Hz (Unreliable); remote player = real character built the game's
      way, simulation off, smoothed with `SnapshotBuffer`.
- [ ] Two copies see each other walk around.

**Step 3 status (2026-10-19):** built: LAN connect, stand-ins (copy of our PlayerMaster, game scripts/physics stripped),
generic `AnimSync` (all animators' states + params), generic `LookSync` (child objects on/off + own material copies +
name), [host]/[client] labels. Seen working in-game; first bug report: host killed a chicken, client still had it
(expected: worlds aren't shared yet).

**Things you can hit (recon):** `Enemy` (EnemyType: mobs, acolytes, soldiers; AI via Unity Behavior graphs, groups,
`EnemiesManager.AllEnemies`), `DamageableObject` (props, `DamageableType.Props`: HP or hit count, `onDestroyEvent`
UnityEvent does the drops/effects, `IResettable`), `SpiderEgg`, `TestDummy`, `DamageableCollider` (forwards to a parent),
`GatheringObject` (trees/rocks, `OnInteract`, saved). No chicken class: it's a prefab on one of these. `HitProbe.cs`
logs every hit (class, scene path, damage, dead) so one test tells us which.

**Probe result:** the chicken is an `Enemy` (type None) placed in the scene: `TutorialArea:002_Cavern/==Fauna/Chicken`;
hits arrive through a child `DamageableCollider` that forwards to `Enemy.ApplyDamage` (the one choke point).

**Built (untested), host-owned creatures (`EnemySync.cs`):** id = hash of scene path (+"#n" for same-named siblings).
Host streams active creatures 10 Hz (pos, flip, dead, hp, animators) to clients in its scene+area. Client "puppet
mode" (host in same scene+area): `RestartGraph` blocked (no AI), navmesh off, rigidbody kinematic, follows the stream;
creatures the host doesn't have are hidden after 3 s. Enemy state machine still runs (hit/die states are timers ->
death anim, corpse fade). Hits: client `ApplyDamage` from our player -> `EnemyHit` request; host applies via
`ApplyDamage`, postfix broadcasts `EnemyDamaged`; clients replay through `ApplyDamage` (applyingHost), killer's own
player as aggressor so they get XP. Not yet: randomly spawned dungeon enemies (ids differ per copy), enemies
targeting client players (host AI only targets `FindWithTag("Player")`), client players taking damage.
Also: `PuppetStateBehaviours.cs` skips every StateMachineBehaviour for stand-in animators (fixed `AttackRateChange` NRE).

**Built (untested), host-owned drops (`DropSync.cs`):** every world drop = `ItemDropHandler.DropToWorld` ->
`MoveToPosition(drop, from, to)`; host ids each drop, sends item address key + amount + `ItemRandomValues` JSON
(what saves store; client rebuilds via `Addressables.LoadAssetAsync<ItemData>`). Client: droppers under an `Enemy` don't
drop locally; our own drops (overflow, from inventory) become `DropRequest`; pickup = `DropTake` request, host grants
first come (Duckov loot rule). Arriving client gets the drops lying in the host's area. Not yet synced, still per copy:
crates (`DamageableObject`), trees/rocks (`GatheringObject`), chests.

**Built (untested), breakables (`PropSync.cs`):** boxes, crates, pots, beds... = `DamageableObject` (HP or hit count;
prefab `onDestroyEvent` breaks it and drops loot). Same request/replay pattern as creatures (`PropHit` -> host
`ApplyDamage` -> `PropDamaged` replay); their droppers only drop on the host; an arriving client gets `PropBroken`
(ids already broken in the scene). Shared helpers: `SceneIds.cs` (path-hash ids for anything placed in a scene),
`RemoteHitter.cs` (an IDamageable "aggressor" standing for another player's stand-in, so knockback and the game's
null checks work), host "client arrived in my area" snapshots in `Plugin.SendArrivalSnapshots`.

**Built (untested), chests (`ChestSync.cs`):** contents live in `ChestManager.allChests[chestID]` (rolled per copy; empty
designer ids become a random GUID per copy -> matched by scene path instead). Opening hands that list to the inventory
UI; closing writes the chest section back into it (`Inventory.AddSectionItems` / `RemoveSectionItems`). So: one player
at a time (lock). Client open -> `ChestOpen` -> host locks + `ChestContents` (host's real items replace the client's roll)
-> client opens for real; close -> `ChestClose` with final contents -> host takes them, unlocks. Host opening a chest a
client has -> "in use"; a client leaving unlocks. Not yet: player-built storage chests (estate placeables differ per
copy), crafting/building that pulls from chests (`ChestShare`), `Chest.DropAllToWorld`.

**Built (untested), area owners instead of "the host owns everything":** whoever has been in an area (scene + GameArea)
the longest runs it: its creatures, breakables, drops, chests (like BDSP's longest-present player hosts its NPCs).
Host only decides who that is (`UpdateOwners`, sends `AreaOwners`) and carries messages: client -> client goes through
the host in envelopes (`ToPlayer` / `ToPlace` / `FromPlayer`). `Plugin.Dispatch`: requests are answered only by the
area's owner, results only taken by puppets, replies always. Handover is safe because puppets mirror the owner as it
happens (creature health, broken list, drop ids, chest contents broadcast after every close as `ChestState`); the new
owner just turns its AI back on and serves requests. Owner sends arrival snapshots (drops, broken things, touched
chests). HUD: a diamond after [host]/[client] for whoever runs that player's area; bottom-left line
"Cohh is the host here". Example that motivated it: client walked into a house alone -> the client should run it,
and tell the host what's broken there when the host comes in.

**Built (untested), chat + status labels (from BDSP):** Enter opens a chat box bottom-left (Enter sends, empty Enter
or Esc closes; game input maps disabled while typing; last 8 lines fade after 12 s). Status byte in PlayerState under
each stand-in: in a menu / inventory / chest / shopping / journal / character menu / recycling / talking / cutscene /
paused / loading / customizing / down / typing / in mod menu (from `GameManager.gameState`, `PanelsManager`
`hasChestOpened`/`hasStoreOpened`, top of `UIPanels`). Our name tags and [host]/[client] labels hide while we're in a
menu, the journal, dialogue or a cutscene.

**Built (untested), creatures hurting other players (`PlayerHits.cs`):** every hit in the game ends in
`CombatSystem.Resolve(damageData, out attackDamage)`, where the TARGET's defence/block/parry/crit/counter are rolled from
its CharacterStats. Stand-ins keep the player's damage collider (`PlayerMaster.iDamageableCollider`, made a trigger) with a
`StandInTarget` on it. On the area owner's copy a creature attack landing on it is stopped in a `Resolve` prefix and sent
unresolved (`PlayerHit`: creature id, amount, crit/effect chance, hit type, knockback, Attack name) to that player; their
copy rebuilds it (attacker = its puppet creature, same Attack asset) and runs the game's own `Resolve` on the real
player (their armour, masteries, block, parry, effects, knockback); a counter-attack becomes a normal hit request.
Only Enemy attacks are forwarded (hazards/traps run on every copy already); our own attacks never hit stand-ins.
**Still missing: creatures CHOOSING the nearest player.** The AI targets one player: `Enemy.Awake` sets the behaviour
graph's "Player" to `FindGameObjectWithTag("Player")`; `EnemyGroup` sight / last-seen-position use it too; many graph
nodes are typed `BlackboardVariable<PlayerMaster>` (IsPlayerDead, ForceEngage...), so pointing it at a stand-in needs a
look at how Unity Behavior casts GameObject -> component variables first. Today creatures chase the area owner, and
their hits land on whoever is in the way.

**Built (untested), shared XP (Insert menu checkbox "Shared XP", config `Gameplay.SharedXp`, default on):** the game gives
kill XP only to the killer (`Enemy.ApplyDamage` -> `expRange` -> `PlayerMaster.GainXP` -> class, weapon category,
armour masteries). With it on, every other player in the area gets the same roll on their own copy (+ the XP pop-up).
**Damage is computed by the attacker's copy** (its weapon, class, masteries, buffs via
`CombatSystem.ProcessAttackDamageData` + the target's side in `Resolve`); the area owner applies the final amount as
sent. Not sent yet: damage effects (bleed, burn, wounds, stun: `DamageData.Effects` / `CreatedEffects`).

**Built (untested), death announcements:** `PlayerMaster.Die` postfix -> `PlayerDied(id, cause)` to everyone: red chat
line + toast "Cohh was killed by Spiderling" (cause = whatever hit them last).

**Built (untested), rocks / ore / trees / herbs (`GatherSync.cs`):** `GatheringObject`: each swing of the gather animation
calls `OnHitCallback` (health - 1, shake, drops; at 0 `FinishObject`: rubble, final drops, life-skill XP; trees fall
first, then become a Residual stump with more hits). Puppet swing -> `GatherHit` request (nothing changes locally until
the owner answers); owner applies `OnHitCallback`, broadcasts `GatherDamaged` (health before, type, who swung); puppets
replay it with drops blocked (drops come from the owner via DropSync). Life-skill XP only to the swinger
(`PlayerData.AddXP` blocked inside `FinishObject` otherwise). Arrival snapshot `GatherState` (health + type, applied quietly).
Bug that triggered it: client mined a rock, the host still had a full rock.

**Built (untested), joining = the host's world (`GuestWorld.cs`): "you bring your character, the host brings the world".**
Saves separate them: `player_slotN.evp` = character; a world = `<prefix>.evw` (WorldData: `MapSeed`, chests, recipes,
story flags, last scene/area/spot), `.eve` (estate: farm, buildings, soil, chopped trees), `.evm` (explored map).
The map is generated from `MapSeed` (`WorldMap.CreateAndRenderDungeon` -> `DungeonSettings.CreateDungeon`) and cached as
`<settings>S<seed>.dmap(.json)`. **Seed is not 100% deterministic by code reading:** `GenerationContext` defaults to
`RandomNeighborSelection` (Unity's global Random), `ConnectChunksStep`, `DungeonEntranceGeneration`,
`ChunkModel.GetRandomAvailableBarrier`, `SelectFromDistanceRange` also use `UnityEngine.Random`, never reseeded
(`SeedTest.cs`, Insert > "Map seed test", generates twice as the game does and twice with Unity Random seeded, compares
with each other and the cached file, writes them to `EmbervilleGhost_SeedTest/`). So we send the generated map file too.
Flow: client's Welcome -> `WorldRequest`; host runs `GameData.CallSaveListeners()` in memory (no disk write), copies
WorldData with `lastActiveScene/lastGameArea/lastSpawnPosition` = where the host stands, sends gzip(.evw, .eve, .evm,
.dmap) as `WorldFiles`; client saves them under prefix `ghostguest_world` (SaveIsolation redirects it into
`EmbervilleGhost_Guest<copy>/`, not in the world list); at the main menu, when a character is picked and the world screen
comes up, `MainMenuManager.LoadWorldSave(guest prefix)` -> spawns next to the host. Character still saves to its own file;
guest world saves stay in the guest folder; only the host's world file is real.
Todo: join while in-game (now: toast "go back to the main menu"), quest/story progress that lives in WorldData is the
host's (by design), dungeons' own random seed (owner must send it).

**Built (untested), creatures spawned while playing (EnemySync):** placed creatures keep path ids; anything with
"(Clone)" in its path (pooled `PoolManager.Get(prefab)`: dungeon spawns, `SpawnEnemyGroupAction`, `SpawnPrefabGroup`;
`EnemyWaveHandler` waves via `Instantiate`) gets an owner id (top bit + owner player id + counter) and `EnemySpawn(id,
prefab name, pos, flip)`; puppets find the prefab among loaded assets (`Resources.FindObjectsOfTypeAll<Enemy>`, not scene
objects) and spawn it through the game's pool; puppets' own spawns are hidden at once; arrival snapshot includes them.
Creatures switched off on a puppet but alive at the owner (a cutscene / event group that only ran there, e.g. the
Acolyte Sorcerer under "== Cutscene HummingSphere") are woken, taken out of their inactive group first.

**Built (untested), doors / gates / levers / switches (`DoorSync.cs`):** a locked door uses the interacting player's own
keys (`GateDoor.TryToUnlock`), so whoever uses a door/lever runs it locally and broadcasts the result (`DoorChange`,
`SwitchChange`); changes that happen by themselves (event-opened gates like the one after killing the sorcerer, auto
close, scripts) only come from the area owner (blocked on puppets); receivers mirror through the same methods
(`Open/Close/ChangeState/ForceState`, `TurnOn/TurnOff/TurnDisabled`), echoes suppressed; arrival `DoorSnapshot` quiet.
Not yet: `DungeonBarrier`, cutscenes (play only for whoever triggers them; `CutsceneCrystal`, HummingSphere...).

**Built (untested), creatures fight whoever is nearest (#4) = per-creature runner:** the AI only ever chases the local
player of the copy it runs on (`FindGameObjectWithTag("Player")`, EnemyGroup sight, PlayerMaster-typed graph nodes), so
instead of teaching the AI about stand-ins, the area owner gives each creature to the nearest player (every 0.5 s,
2-unit margin against flip-flopping; `EnemyRunner` message, also in the arrival snapshot). That copy runs its AI
(which then chases its own player), streams it, and answers hit requests; everyone else freezes and mirrors it. A runner
who leaves -> back to the area owner. Drops still come from the area owner (it replays the death). Area-level things
(breakables, drops, chests, rocks) still use the area owner. Seen first: the sorcerer never attacked the client.

**Built (untested), creature projectiles everywhere (#5, part 1, `FxSync.cs`):** `Projectile.OnEnable` on the shooter's
copy -> `ProjectileFx(prefab, pos, dir, range)`; others spawn the same prefab from the game's pool with
`preventDamage` (flies, stops, hit effect, no damage; flag restored if the game reuses it). Not yet: attack objects
(slashes, ground blasts: `IAttackObject`, `Explosion`).

**Built (untested), "Join host" window (`JoinWindow.cs`, opens on F9):** connection, host's world (downloading /
ready with name, size, seed, whether the generated map came / host not in a world), character picker (current one
preselected in a game), "Connect now" = `GuestWorld.Load` (works from the main menu AND in a game: saves the current
game first, sets prefixes, `LoadData`, `startFromSavedData`, `CallScene(lastActiveScene)`), Cancel = disconnect.

**Built (untested), off-screen teammate arrows (Hud.DrawEdgeArrow):** teammate in our scene + area but off screen ->
arrow on the screen edge in their direction (centre -> their projected position, clamped 46 px inside the edges), in
their tag colour, with "name  34m"; gone when they're on screen; hidden in menus like the other tags. Also: Insert menu
"Teleport to" per teammate (same scene; `GameManager.TeleportPlayerFade`, switches GameArea if needed), main-menu
"MULTIPLAYER: join from here (F9)" banner (`MenuBanner.cs`).

**Built (untested), host window + session rules (`Session.cs`):** F8 opens "Host a game": port, Shared XP (default on),
Instanced loot (default off: Weapon/Armor/Gear categories, not key items, drop once per player, each takes their own
copy once, late arrivals still get theirs; materials/consumables/placeables stay first come first served; records
with who-took-it are kept by everyone via `DropTakenBy` so handovers keep them), PvP (default off), HOST NOW. The
host's rules go to everyone (`SessionSettings` on join + on change in the Insert menu; clients see them greyed out).
**PvP:** with it on, our own attacks that land on a stand-in's hurtbox (in `CombatSystem.Resolve`) go to that player
as `PlayerHit` with attacker id; the victim's copy resolves it (their armour, block, parry; knockback away from the
attacker's stand-in); deaths say "killed by <player>"; status line shows "PvP ON". No counter-attacks between players.
Also fixed: creatures handed to a player stood idle (`StateMachine.ChangeState` ignores the current state, so
`RestartBehavior` did nothing: now `RestartGraph` directly); dying while connected no longer resets the shared world
(`CoopDeath.cs`: blocks `EnemiesManager.ResetAll` + every `ResetToLastState` for that frame).

**Idea (done above): PvP as an option.** Off by default; each player switches it on in the Insert menu, and a hit only counts
between two players who BOTH have it on (show it on the label, e.g. a red sword). Needs: player-vs-player hits (today a
stand-in has no colliders, so our attacks pass through it: give stand-ins a hurtbox / check `Physics2D.OverlapCollider`
against stand-ins in `PlayerMaster.PerformAttackHit`), a `PlayerHit` message to the victim's copy, which applies it with
`PlayerMaster.ApplyDamage` (the victim's own copy decides, like Duckov's PlayerHit), and a "pvp" flag in PlayerState.

**Note: what the host "sees":** Unity keeps the whole loaded scene in memory, not just what the camera shows, so the
host has every object of the scene. But Emberville switches off every GameArea except the current one, so creatures
in other areas exist but don't run. The host can only be authoritative for the area it's in; a client elsewhere falls
back to its own world (todo: per-area host like BDSP's "longest-present player in the area hosts it").

**Step 4 - Host authority for a farming game** (decide what the host owns)
- [ ] Time of day / day / weather / season, scene travel.
- [ ] World tiles: tilling, planting, watering, harvesting, placing/removing objects (IDs = tile coordinates).
- [ ] Chests and item pickups (dupe-proof, like Duckov loot).
- [ ] NPCs, enemies and combat (host-owned, client puppets, damage replay).
- [ ] Sleeping / day end (everyone ready?), saving (whose save is the world?).

**Mouse walking (like our Pokemon BDSP mod):** hold a mouse button and the character walks toward the mouse; a
circle drawn around the character (ImGui) shows the direction, a small inner circle is a dead zone, further out =
faster. Reference: `game.reverse.engineering\bdsp-multiplayer\plugin\MouseInput.cs` ("Walking": the point under the
mouse relative to the player becomes the left stick; the game's own movement does the walking, so collision applies;
pad/keyboard input wins; plus `ClickWalk.cs`: a quick click walks there along the game's route finder).
Emberville: `PlayerMaster.GetInput()` reads `inputActions.Player.Move` into `input` every Update; a postfix that
replaces `input` with (mouse world point - player position) normalized/scaled does it (mouse-to-world:
`mainCam.ScreenToWorldPoint`, as the game's own `GetLookDirection` already does). **Pick the button carefully:** left =
primary attack, right = secondary attack, and the game already aims with the mouse (mouseLook); maybe a held key +
mouse, or middle (but middle is our ping/path in Duckov). Generic enough to reuse in any top-down game.

**BUILT 2026-10-19 (untested; needs 2 Steam accounts on 2 PCs): join through Steam / friends list** (SteamBoot loads our steam_api64.dll + SteamAPI.Init as app 5355880; SteamTransport + SteamLobby from Duckov; F8 HOST VIA STEAM, Insert menu Invite friends, +connect_lobby launches; host migration over Steam IDs). Original plan: Decided 2026-10-19: get the LAN/IP build solid first.
Today: IP only (LAN, or internet by IP with the host forwarding UDP 9060). Plan:
- The demo ships no Steam (no `steam_api64.dll`, even after the 0.20.16.232 update), so the mod brings Steamworks.NET
  (MIT) + `steam_api64.dll` in the plugin folder and calls `SteamAPI.Init` itself (+ `RunCallbacks` every frame).
- Use the demo's own app ID **5355880** (launched from Steam, Steam provides it): overlay, invites and friends list
  "Join Game" show as "Emberville Demo". No Spacewar 480. If Early Access (Oct 27) ships its own Steam, use the game's.
- Port from Duckov: `SteamTransport.cs` (SteamNetworkingSockets P2P relay, no port forwarding) + `SteamLobby.cs`
  (friends-only lobby, rich presence `connect` = `+connect_lobby <id>`, `ActivateGameOverlayInviteDialog`,
  `GameLobbyJoinRequested_t` / `GameRichPresenceJoinRequested_t`, `+connect_lobby` on the command line). Drops in
  next to LAN behind `ITransport`.
- UI: F8 host window gets "Host via Steam" + "Invite friends"; accepting an invite opens the Join window.
- Friends need the demo + the mod, both launched from Steam. Test with the 2 Steam accounts on 2 PCs.

**Step 5 - Play together**
- [ ] Steam lobby + invites (copy `SteamTransport` / `SteamLobby`).
- [ ] Teammate health/status, pings + paths, minimap that tracks everyone, off-screen teammate arrows (details in
      Duckov roadmap #12), chat.
- [ ] Disconnects / rejoin, more than 2 players, join codes, maybe a Colyseus room server.
- [ ] **Built for v0.1.3, all untested (test tonight):** combat gaps 1-6 (status effects ride with hits, player
      spells/skill objects mirrored incl. held beams, creature base effects on PlayerHit, gore matches, hurt numbers +
      red flash on stand-ins, status effect names under tags), heal "+N" popups, Kymia bar, chat bubble, shared chests
      (ChestShare take tracking, live contents, retake after close, saved-id chest ids), host migration (#10: leaving to
      the menu stops; the first-joined client hosts in place, others reconnect, no reload; roster of addresses),
      building/removing placeables + tiles (PlaceSync, to everyone, applied on entering that scene), estate unlock
      (/estate, Insert menu), story flags shared (#5, StorySync), /sleep vote (#7), boss handed to the player she webs
      (#9), team buffs (attack self-buffs + skill stat modifiers like Swift Foot, host option "Share buffs", 8 m),
      /devmode + /devpanel (the game's own dev tools), /help.
      Then: pings + drawing (middle mouse), shared-chest race (later taker pays from bag), Kymia "+N", minimap
      marker tint, 10 review fixes, X at a chest = put in what it already has (the chest screen's "send common").
      Online Players screen (O or /online; Tab and P are the game's): who's connected, place, HP, ping, status;
      mute (this PC, by name); host: kick / ban (2nd click confirms; bans follow a player ID from the config, unban list).
      Party strip (top: portraits via per-player cameras, HP/Kymia; HeadHeight/HeadSize in config), teammates on the
      M world map (when in the wilds), trading (Online Players > Trade or /trade P2: bag grid, 8 offer slots, drag,
      tooltips, Ready = lock on their offer's hash, any change unticks both; key items / equipped gear excluded),
      the game's own bordered boxes (GameUi: GeneralPrompt for "kicked/banned", ConfirmationPrompt for trade invites).
- [ ] **Pet chicken: BUILT, untested** (Pet.cs): /pet befriends (needs any chicken loaded once: prefab or one in the
      scene) / calls / sends home, /pet name X. Stripped Mob_Chicken copy (no AI, no colliders: immune to everyone),
      follows behind, pecks enemies within 3.5 m of us via CombatSystem.Resolve with our player as aggressor (counts as
      our hit, kills give us XP), pet XP (+1 per peck, +10 per kill, 15 x level per level, damage 2 + level), name tag
      with XP bar, others see it follow our stand-in (pet kind / level / name in PlayerState). Check tonight: the
      animator params (Speed, DirFactor, Attack), the flip, whether it pulls mobs too eagerly.
- [ ] **Pet / companion** (asked 2026-10-19): no dog or cat in the demo's art (checked all 478 sprite sheets in
      `<game>\extracted`). Candidates: the chicken (`Mob_Chicken`, full animation set: walk, idle, peck, hurt; the
      best), the Lilshrooms (pink / green / gray mushroom critters), Little Thics, the Magic Wisp, the Glowbat, the crow.
      Plan: befriend = interact (F) with one while holding food (or a /pet command); it becomes a follower: a copy of
      the creature prefab with its AI and hurtbox stripped (like our player stand-ins), walking after us with the
      game's own animator (walk / idle by speed), keeping ~1.5 m behind, teleporting along if far. Saved in our config
      (kind + name). Others see it: it's just "player X's pet of kind K" in our player state; each copy runs its own
      follower behind that player's stand-in (no extra traffic).
- [ ] **Priorities after v0.1.2 (sabba, 2026-10-19):**
  - Built, untested: mid-fight creatures stay with their runner (InFight); one clock (TimeSync: host's day + time
    every second; the demo has no weather, seasons or sleep); crafting stations, all 16 types (cooking pot, campfire,
    anvil, forge, furnace, sawmill, tanning, workbench...: StationSync, the area owner cooks, collect = request);
    farm plots (FarmSync: the farmer sends the plot's save state).
  - Next: building / placing / removing placeables (PlaceableController.PlaceObject / RemoveSelectedObject; apply
    = dataBase.Get(id).Place(pos, dir, hostGuid, placeablesParent) + SaveData; tiles via TilesetDrawerManager).
  - #5 story progress: "unlock for everyone" (chosen over sharing key items): a door / gate / quest flag one player
    opens is open in everyone's world. In-session doors already sync; to persist it, sync GameData.gameBools
    (BoolID...) + door states into the guest's own saves too.
  - #7 everyone goes to bed: the demo has NO sleeping (no bed, the day rolls over at 24:00). Later: a mod "ready to
    sleep" vote that skips to morning on the host's clock, or the full game's own bed.
  - #8 buffs / status effects visible + team buffs (below). #9 the boss's own attacks aim only at its runner's player.
  - #10 host leaving: when the host quits to the menu, the clients stayed connected to the host's menu (Rory "held"
    the room). Wanted: the host leaving ends their part; the next player becomes host and the others connect to them,
    not to the old host. With LiteNetLib + IP that needs the new host reachable (port forward); with Steam relay
    (next version) it's easy. Step 1: host stops hosting on quit-to-menu and tells everyone (HostLeft); with 2 players
    the client just carries on solo in the guest world.
- [ ] **Pings + drawing (like Duckov's `Pings.cs`):** middle-click (or a key) drops a ping on the ground where the
      mouse is: a pulsing ring + the player's colour + name, seen by everyone in the area (and as an edge arrow when
      off screen), fades after a few seconds. Hold to draw: a trail of points following the mouse on the ground,
      sent as a polyline, fading after ~10 s (draw a path, circle a chest, arrow to the boss door). ImGui background
      draw list in world -> screen space, like the name tags. Watch the button: left/right are attacks, the game
      aims with the mouse.
- [x] Chat bubble over a teammate's head: "..." while they type, then their line for 6 s (Hud.DrawBubble).
- [ ] **Auto magnet (hands-free F)** (asked 2026-10-19). What F does: it's the game's `Interaction` action
      (`<Keyboard>/f`, gamepad South). On a dropped item (`ItemDropObject : IInteractable`) it calls
      `OnInteract(player)` -> `OnPickup` (`Inventory.AddItem`; anything that doesn't fit drops back at your feet) + the
      collect VFX/sound. `CanRepeat` = true with `repeatInterval`, so holding F keeps picking up whatever is in reach
      (items join the player's `InteractablesHandler` through their trigger).
      Plan:
      - Every ~0.2 s, find `ItemDropObject`s within the magnet range of the player (circle overlap or scan of active
        drops) whose item passes the filter, and pick up the nearest one through the same `OnInteract(player)` the F
        key uses. That way it goes through DropSync's dupe-proof pickup (a puppet's pickup is a request to the area
        owner; instanced loot stays per player). Optional: pull them toward the player first (lerp over ~0.3 s) so it
        looks like a magnet, then pick up on arrival.
      - Filter UI (ImGui window, a key or /magnet; also a tab in the Insert menu): master on/off; range slider
        (1-8 m); checkboxes per `ItemCategory` (Material, Consumable, Weapon, Armor, Gear, Placeable, Other) plus
        coins and key items; per-item exceptions ("never wood", "always ore") from a searchable list of items seen
        so far (addressKey + name). Saved in the config.
      - Rules: never while a menu / dialogue / cutscene is up, while dead, or while the bag is full (skip what
        wouldn't fit, check `Inventory` space first, so things don't bounce at your feet). Respect instanced loot
        (only your copy). Maybe "don't steal": skip items that dropped from another player's kill for N seconds unless
        loot is shared.
- [ ] **Auto potion** (asked 2026-10-19): if you carry potions or healing food, one gets used automatically before
      you die. How the game uses an item: `InventoryUI.UseItem` -> `item.itemObject.OnUse(player.characterStats)`
      (the item's actions: heals go to `PlayerMaster.RestoreHealth`, some are regen effects), then
      `Inventory.UpdateEquippedList` / `SetInventoryChanged`; items can have a cooldown (`Inventory.SetItemCooldown`,
      `cooldownManager` by addressKey).
      Plan:
      - Two triggers: (1) a prefix on `PlayerMaster.ApplyDamage` for our own player: if this hit would kill us
        (`CurrentHealthPoints - amount <= 0`, after block / parry: check `damageData.Result`) and a heal is ready, use
        the heal first, then let the hit land; (2) every frame, health under a threshold (default 25%) -> use one.
      - Which item: consumables (belt first, then bag) whose use actions heal health; the smallest one that brings us
        back over the threshold (don't waste the big potion on a scratch); skip ones on cooldown; never Kymia-only
        items for health. Optional: the same for Kymia at a separate threshold.
      - Settings (Insert menu tab / /autopotion): on/off, threshold %, "belt items only", per-item allow list (never
        eat the rare cake), a short note in chat when it fires ("Auto potion: Small Health Potion, +30").
      - Never while dead, in cutscenes / dialogue, or when the hit is from our own PvP opt-out. Purely local, nothing to
        sync (the heal shows to others through PlayerFeel's "+N" and the health bar).
- [ ] **FUTURE (full game): loot distributor pad, like Abiotic Factor** (asked 2026-10-20). Abiotic Factor has a
      device you stand on that sends everything in your bag to every storage chest that already holds that item:
      it checks all the chests and the items visibly fly into them. Wait for the full game, when we build our own
      chests (placed storage on the estate). Plan, building on Better Chests' X (`QuickStack.StackInto`, the
      chest screen's own "send common" without the screen):
      - A placeable pad (or a Better Chests block) in the base; standing on it (or F on it) runs the deposit into
        every chest within range / in the base, nearest first, one chest at a time.
      - Each moved stack flies from the player to its chest (a sprite copy on a short arc, staggered), then the
        game's "Stored X (n)" lines, maybe a summary per chest.
      - Rules: only stackables a chest already holds (like X); never equipped gear, the belt, key items; optional
        "fill empty chests by category" later. Respect locks (a chest someone has open is skipped).
      - Multiplayer: one deposit per chest through the multiplayer mod's chest sync (the area owner's real contents,
        everyone sees the new contents), same as X today.
- [ ] **Team buffs** (asked during online play with Rory, 2026-10-19: "was I affected by his Swift Foot buff?" No.)
      The game is single-player at heart: no allies/party anywhere in its code, so buffs like Swift Foot (toggled
      movement buff) only ever touch the caster, and the mod doesn't sync buffs/status effects at all yet (a remote
      player only looks faster because their position is copied). Idea: when a player turns on a buff, tell the area;
      teammates within range get the same buff on their own copy (the game's own effect code, so their speed/stats
      really change), removed when it's toggled off or they walk out of range. Host window option ("Share buffs with
      nearby teammates", range). First: find how a toggle skill applies its buff (EffectsHandler / skill data) and
      which buffs make sense to share (movement, defence auras yes; self-only things like dash i-frames no). Also show
      the buff's visual on the caster's stand-in.

---

## Duckov (done, final state 2026-10-18)

### Where we are
Escape from Duckov is the practice run for Emberville. The DuckovGhost mod (v0.0.4) is a working 2-4 player co-op
prototype over LAN. Steam lobbies and invites are built but have never been tried on two PCs. Nothing is in progress;
the next step is testing, not building.

### What we did (the whole journey so far)
1. **Recon:** studied Stardew (not a target), MegabonkTogether (main reference), BALLxPIT, the existing Duckov co-op
   mod (ideas only, its license forbids code reuse) and Core Keeper (built for multiplayer, see notes below).
2. **BepInEx + Harmony in Duckov:** plugin loads (`HideManagerGameObject = true`), patches game methods.
3. **In-game menu:** Dear ImGui drawn *by Unity inside the game* (`ImGuiUnity.cs`), after the separate-window
   overlay (the BDSP way) failed with DPI offsets and a black screen. Full story in "getting the on-screen menu working".
4. **Two/four copies on one PC:** separate saves, windows tile themselves, free mouse.
5. **LiteNetLib connect** (F8/F9), then the real game stuff, all **host-authoritative** (client asks, host decides,
   host tells everyone):

   | Feature | State |
   |---|---|
   | Real remote duck: face, animation, held gun, health bar, smoothing (100 ms snapshot delay, 20 Hz) | works |
   | Doors (shared IDs from the game's position hash) | works |
   | Shots: harmless replay bullets with the real tracer, muzzle flash, sound and hit effects | works |
   | Host-owned enemies: client hits -> host applies -> damage replayed on the client puppet; enemies attack both players | works |
   | Shared loot from dead enemies, dupe-proof (client must ask the host before taking/merging/splitting) | works |
   | Client lag fixed (puppets frozen, try-once item visuals): 14 fps -> 88 fps | works |
   | Player-vs-player collision = pass through | works |
   | Level-spawned enemies host-only, travel together, enemy shots/melee on the client | built, untested |
   | Placed loot containers (host's roll wins), weather/clock sync, status badges + no pause | built, untested |
   | 3-4 players (IDs, host relays), pings (middle click), drawn paths (middle drag) | built, untested |
   | Swappable transport: LiteNetLib or Steam relay; Steam lobby, overlay invites, friends-list Join Game (F7) | built, needs 2 PCs |
6. **Notes written up below:** Harmony, how the networking works, MegabonkTogether's tricks, Core Keeper,
   LiteNetLib with many players, the performance lesson, what's reusable for Emberville.

### What we need to do
1. **2-PC Steam test** (details below). This is the big unknown.
2. **Full raid test** of everything marked "built, untested".
3. **Roadmap still open:** #7 ground items, #8 breakables/barrels/extraction, #10 armour + attachments on the
   remote duck, #11 player death/spectate, #12 rest (minimap markers, chat, friendly fire), #13b join codes,
   #13c Colyseus room server, #15 disconnects/rejoin.
4. **Smaller backlog:** grenades + enemy skills, cutscenes, vote/ready before travel, other enemy spawn paths
   (`EnemyCreator`, `Egg`, `SpawnControlCharacter`), chunked level snapshots for internet play, menu not blocking
   game input, ImGui colours (linear vs sRGB).
5. **Then Emberville:** recon (Mono or IL2CPP? does it call `SteamAPI.Init`?), and port the patterns (see
   "Reusable for Emberville" and the TODO at the bottom).

### How to pick it back up
**Practice game:** Escape from Duckov, mod = `spike/DuckovGhost` (v0.0.4, BepInEx 5, Mono). Build with
`dotnet build spike/DuckovGhost -c Release` (deploys into the game's `BepInEx/plugins/DuckovGhost`).
Close the games first if the native `cimgui.dll` changed (it's locked while running; unchanged files are skipped).

**Next session, start here:**
1. **2-PC Steam test (roadmap #13).** Copy `spike/dist/DuckovGhost-install.zip` to the second PC, unzip into its
   Duckov folder, launch the game **from Steam** on both. PC 1: F7 (Host via Steam) -> Insert > "Invite friends".
   PC 2: accept the invite (or friends list > Join Game). If it fails, grab the Insert log lines from both PCs
   (lobby created -> joined lobby -> connecting -> connected). Rebuild the zip after mod changes
   (see "Packaging" below).
2. **A full raid with 2 players on LAN** to test all the "built, untested" items: level-spawned enemies, following
   the host into raids, enemy shots on the client, placed loot boxes, weather/clock, status labels, no pause,
   4 players, pings and paths.
3. Then roadmap #7 (ground items) and #11 (player death), or whatever the test turns up.

**Controls:** F7 host via Steam, F8 host LAN, F9 join LAN (Insert menu: Address/Port), F10 disconnect,
Insert = menu (Steam invite button, enemy spawner, log, perf), Delete (host) = spawn a wolf,
middle click = ping, middle hold + drag = draw a path.

**Running copies on one PC:** launch `Duckov.exe` several times (single-instance removed from `boot.config`,
`steam_appid.txt` added - a Steam verify/update undoes both). Copy #2+ get their own saves
(`AppData/LocalLow/TeamSoda/Duckov/DuckovGhost_CopyN`). Windows arrange themselves in a grid (1280x720).
Status line shows `fps, mod ms`; Insert menu shows per-section mod timings.

**Mod files (`spike/DuckovGhost`)**
| File | What |
|---|---|
| `Plugin.cs` | entry point, players/ids, routing (Send / SendToAllClients / Reply / SendToPlayer), relays, `MsgType` list |
| `Transport.cs`, `SteamTransport.cs`, `SteamLobby.cs` | swappable transport: LiteNetLib (LAN) or Steam relay + lobby/invites |
| `RemotePlayer.cs`, `RemoteDuck.cs`, `HeldItemVisual.cs`, `SnapshotBuffer.cs` | other players: state, real-character stand-in, held item, smoothing; damage forwarding |
| `EnemySync.cs`, `NearAnyPlayer.cs` | host-owned enemies, puppets, hits + damage replay, attacks; "active near any player" |
| `ShotSync.cs` | shots -> harmless replay bullets (`FakeBullets`) |
| `LootSync.cs` | shared loot: dead enemies' boxes + placed containers, dupe-proof taking |
| `DoorSync.cs`, `LevelSync.cs`, `WorldSync.cs` | doors, travel together, clock/weather |
| `PlayerStatus.cs`, `Pings.cs`, `Hud.cs`, `DebugPanel.cs`, `Toasts.cs`, `NetLog.cs` | status labels + no-pause, pings/paths, HUD, menu, toasts, log |
| `ImGuiUnity.cs`, `Perf.cs` | Dear ImGui drawn by Unity inside the game; perf timers |
| `SaveIsolation.cs`, `WindowFix.cs` | separate saves per copy; 1280x720 + grid arrangement; free mouse in `Plugin.LateUpdate` |

**Packaging for another PC:** zip = `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `BepInEx/core`,
`BepInEx/config/BepInEx.cfg` (has `HideManagerGameObject = true`), `BepInEx/plugins/DuckovGhost` + a README.
Output: `spike/dist/DuckovGhost-install.zip`. Uninstall = delete `winhttp.dll`.

**Reference material (scratchpad, may be gone next session; re-decompile with `ilspycmd` if needed):**
Duckov decompile (`TeamSoda.Duckov.Core`, `ItemStatsSystem`, Steamworks.NET), the existing Duckov co-op mod
(github.com/Mr-sans-and-InitLoader-s-team/Escape-From-Duckov-Coop-Mod-Preview, license forbids code reuse - ideas
only), MegabonkTogether decompile, Core Keeper notes (below).

---

## Findings so far

### Stardew Valley (not a target)
- Not Unity: C# / .NET 6 / MonoGame. Modded with **SMAPI** (Harmony underneath), not BepInEx.
- Already has built-in co-op (since 1.3; up to 8 players in 1.6), so adding our own is redundant.
- Still useful as a *reference*: decompile with ILSpy to see how a real farming game syncs
  farmers, crops, chests, world ownership.
- Nexus mod 20659 = "Stardew Multiplayer Server Mod" (Moy): a SMAPI bot that automates the host
  so the built-in multiplayer runs as a dedicated server. Not a networking example.

### Megabonk + MegabonkTogether v1.3.0 (main reference)
Install: `K:\__games\steamapps\common\Megabonk`
Decompiled (ilspycmd) to scratchpad:
`C:\Users\sabba2u\AppData\Local\Temp\claude\I--programming-projects-claude-code-multiplayer-litenetlib-spacewar-steamworks\57e80a1f-dceb-4bd8-b8f3-4b9cf890b108\scratchpad\megabonk\`
(scratchpad is temporary. Re-run ilspycmd into this repo if you want to keep it.)

**Stack**
- Game is Unity **IL2CPP** (`GameAssembly.dll`) → **BepInEx 6 BE** (`BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.752`).
- Plugin folder `BepInEx/plugins/MegabonkTogether/`:
  - `MegabonkTogether.dll`: game hooks, services, Unity scripts
  - `MegabonkTogether.Common.dll`: shared message types (also used by their servers)
  - `LiteNetLib.dll`: UDP networking
  - `MemoryPack.Core.dll`: binary serialization of messages
  - `Microsoft.Extensions.*`: dependency injection / hosting / config (nice-to-have, not required)
- Config `BepInEx/config/MegabonkTogether.cfg`: `ServerUrl` (wss matchmaking), `RDVServerPort = 5678`, `PlayerName`.

**1. Bootstrapping (`MegabonkTogether/Plugin.cs`)**
- `[BepInPlugin(...)] class Plugin : BasePlugin` (IL2CPP base class).
- IL2CPP requires custom MonoBehaviours to be registered:
  `ClassInjector.RegisterTypeInIl2Cpp<NetPlayer>()` (~20 types: interpolators, UI, NetworkHandler...).
- `new Harmony("MegabonkTogether").PatchAll()` applies all patches.
- Creates persistent GameObjects: `MainThreadDispatcher`, `NetworkHandler`, `NetPlayersDisplayer`, `CameraSwitcher`.

**2. Connection (`Services/UdpClientService.cs`, `WebsocketClientService.cs`)**
- Lobby/matchmaking over **WebSocket** to their own server.
- Peer-to-peer via **LiteNetLib NAT punch** through a rendezvous server (port 5678):
  `NatPunchModule.SendNatIntroduceRequest(host, port, token)` → `NatIntroductionSuccess` → `netManager.Connect(target, key)`.
- Fallback: **relay** through the same server (`Connect(host, port, "RELAY")`, `RelayEnvelope` messages).
- NetManager setup:
  ```csharp
  netManager = new NetManager(listener) {
      IPv6Enabled = true, UnconnectedMessagesEnabled = true, NatPunchEnabled = true,
      EnableStatistics = true, DisconnectTimeout = 15000, UpdateTime = 5 };
  netManager.Start(port);              // bumps port+1 if busy
  // each tick: netManager.PollEvents(); netManager.NatPunchModule.PollEvents();
  listener.ConnectionRequestEvent += r => r.Accept();
  ```

**3. Messages (`MegabonkTogether.Common.Messages`)**
- ~75 `[MemoryPackable]` classes implementing `IGameNetworkMessage`
  (`PlayerUpdate`, `SpawnedEnemy`, `EnemyDamaged`, `EnemyDied`, `ChestOpened`, `WeaponAdded`, `RunStarted`, `GameOver`...).
- Send: `MemoryPackSerializer.Serialize(msg)` → `peer.Send(bytes, deliveryMethod)`.
  Receive: `MemoryPackSerializer.Deserialize<IGameNetworkMessage>(reader.GetRemainingBytes())`.
- Delivery choice:
  - `Unreliable` → high-frequency state (player positions, projectile updates)
  - `ReliableOrdered` / `ReliableSequenced` → events that must arrive (spawns, deaths, chest opened)
- `PlayerUpdate` = ConnectionId, Position, MovementState, AnimatorState.

**4. The remote player (`Scripts/NetPlayer/NetPlayer.cs`)**
- Instantiates the game's own character prefab: `Object.Instantiate(characterData.prefab)`.
- Adds nameplate, minimap icon, weapons/attacks, hats, item/weapon UI.
- Does **not** run game player logic. It is a puppet driven by incoming `PlayerUpdate`s.
- `PlayerInterpolator` smooths between snapshots (also Enemy/Projectile/BossOrb/TumbleWeed interpolators).

**5. Host authority (`Patches/*`, ~50 files)**
- Host simulates the game. Client only mirrors.
- Pattern, e.g. `EnemyManagerPatches.cs`:
  - `[HarmonyPrefix] SpawnEnemy` → `return false` on client (client never spawns enemies itself).
  - `[HarmonyPostfix] SpawnEnemy` → on host, broadcast `SpawnedEnemy` with the result.
  - `EnemyMovementRb.MyFixedUpdate/FindNextPosition` → skipped on client. Positions come from host snapshots.
- Same pattern for chests, pickups, projectiles, shrines, pylons, bosses, map generation, time, saves,
  Steam achievements/stats (disabled in netplay), etc.
- **Takeaway:** steps 1–4 are a few hundred reusable lines. Most of the work is step 5:
  finding every system that must be host-only and syncing its results.

### BALLxPIT
Install: `I:\__games\steam\steamapps\common\BALLxPIT` (exe `Balls.exe`)
- Unity **IL2CPP** → BepInEx 6 BE. No BepInEx / mod installed yet.
- Nexus mod 24 "BALLxPIT Local Coop": local Player 2 (separate controls/color in config).
  The user linked mod 31; couldn't load it (Nexus returns 403 to fetch).
- Likely approach (unverified): spawn a 2nd player object driven by a 2nd input device, and patch
  single-player assumptions (player lookup, camera, damage, game over, UI). No networking.
- Local co-op is a good stepping stone: once P2 works locally, online = feed P2 from packets instead of a gamepad.

---

## Duckov spike: getting the on-screen menu working (what went wrong, in order)

Practice game: Escape from Duckov (Unity, Mono, BepInEx 5). The spike mod is `spike/DuckovGhost`.
The goal was a Dear ImGui menu + toasts like the Pokemon (BDSP) mod. It took several tries,
because there were **several separate problems stacked on top of each other**.

### Background: two ways to put a menu on a game
- **A. Separate window on top ("overlay").** The mod opens its *own* invisible window, glued on
  top of the game window, and draws the menu there. The game never knows. This is what
  **OverlayDearImGui** and the **Pokemon mod** do. It looks like it's in the game, but it isn't.
- **B. Draw inside the game.** The mod asks the game engine (Unity) to draw the menu as part of
  the game's own picture, like the game's own HUD. No extra window.

Duckov now uses **B**. Pokemon uses **A**.

### Problem 1: nothing showed up at all (F8/F9/Insert dead)
- **Symptom:** the mod said "loaded" in the log, then did nothing. No text, no keys.
- **Cause:** BepInEx runs every mod from one hidden Unity object. Duckov deletes that object
  while loading its scenes, so the mod's `Update()` never ran again.
- **Fix:** `BepInEx/config/BepInEx.cfg`: `HideManagerGameObject = true` (BepInEx's own setting
  for this; it hides the object so the game doesn't delete it).

### Problem 2: the menu was a separate window, not in the game
- **Symptom:** the Dear ImGui panel showed up as its own window ("O" in the taskbar), not on the game.
- **Cause:** that's how the stock **OverlayDearImGui** mod works (approach A, one big window,
  only visible while open).
- **What we tried:** copied the Pokemon mod's improved version of it (`bdsp-multiplayer/plugin/Overlay`),
  which glues the window exactly over each game window and keeps a HUD visible. Still approach A.

### Problem 3: the Pokemon-style overlay was too big and offset
- **Symptom:** overlay bigger than the game window and shifted to the right.
- **Cause:** Windows display scaling. Duckov doesn't tell Windows it handles scaling, so Windows
  "virtualised" the overlay window: wrong size and position.
- **Fix:** `SetThreadDpiAwarenessContext(PER_MONITOR_AWARE_V2)` on the overlay thread. Fixed the size.
  (This code is gone now, see problem 5.)

### Problem 4: the overlay covered the game in black
- **Symptom:** menu and toasts worked, but everything behind them was black instead of see-through.
- **What we proved:** the game was rendering fine underneath (fading the overlay to invisible showed it).
  The overlay window itself was opaque.
- **Tried, didn't help:** re-applying DWM transparency (`DwmExtendFrameIntoClientArea`), and a
  colour-key (make one exact colour transparent).
- **Why Pokemon works and Duckov doesn't: not proven.** It depends on Windows compositing two
  DirectX windows from two different engines/setups. My best guess is something about how
  Duckov presents its frames. We didn't dig further, because...

### Problem 5 (the actual fix): stop using a separate window
- You wanted the menu **in** the game anyway, so we switched to approach **B**.
- `spike/DuckovGhost/ImGuiUnity.cs`:
  1. Dear ImGui builds the menu each frame (it only produces triangles + a font texture).
  2. We hand those triangles to **Unity** as meshes and draw them in `OnGUI` (repaint), which runs
     after the game's cameras, so the menu is drawn on top of the game's picture.
  3. Keyboard/mouse come from Unity's Input System; Insert toggles the menu and shows the cursor.
- No second window = no transparency trick needed, no black, no lag when moving the window,
  works fullscreen.

### Problem 6: the ImGui library wouldn't load
- **Symptom:** `DllNotFoundException: Unable to load library 'cimgui.dll'`.
- **Cause:** the Pokemon mod uses **Hexa.NET.ImGui**. Its DLL loader doesn't work under Unity's
  old **Mono** runtime (Pokemon runs on .NET 6 / IL2CPP, where it does). It failed even when given the full path.
- **Fix:** switched to **ImGui.NET** + its `cimgui.dll` (the same files the stock overlay used, which
  we'd already seen load in Duckov). Kept in `spike/libs/OverlayDearImGui/`.

### Problem 7: warning spam
- **Symptom:** "Mesh vertex buffer attributes were supplied in non-standard order" every frame.
- **Cause:** Unity wants vertex data in the order position, colour, UV. ImGui stores position, UV, colour.
- **Fix:** reorder each vertex while copying it to Unity (`ImVert` → `Vert` in `ImGuiUnity.cs`).

### Other fixes along the way (two copies of the game)
- **Second copy wouldn't start:** removed `single-instance=` from `Duckov_Data/boot.config`
  (backup `boot.config.orig`) and added `steam_appid.txt`. A Steam update/verify undoes this.
- **Window always 3440x1440:** the game re-applies its saved resolution at startup.
  `WindowFix.cs` forces 1280x720 windowed after it (config `[Window]`).
- **Copies corrupting each other's saves** (`Options.ES3` "used by another process"): `SaveIsolation.cs`
  gives copy #2+ its own folder `AppData/LocalLow/TeamSoda/Duckov/DuckovGhost_Copy2`.
- **"Index was out of range ... startIndex" spam:** the game's own bug (`CustomData.GetInt`), harmless.

### Notes: what Harmony is
**Harmony** (HarmonyX, ships with BepInEx as `0Harmony.dll`) lets a mod change what the game's own methods do
**while the game runs**, without editing the game's DLLs. At load, it finds a game method (e.g. `Door.SetClosed`)
and wraps it with our code, so every time the game calls it, ours runs too.

- **Prefix**: runs *before* the game's method. Return `true` = the game's method runs normally;
  return `false` = **the game's method is skipped** (how the client blocks its own decisions).
- **Postfix**: runs *after* it and can see what happened (how the host notices "a door just changed").
- Magic parameter names: `__instance` = the object the method was called on (the door); `__result` = the
  return value (a prefix that skips can set it); other parameters match the game's own names (`_closed`).

```csharp
[HarmonyPatch(typeof(Door), "SetClosed")]          // wrap the game's Door.SetClosed
static class DoorSync
{
    static bool Prefix(Door __instance, bool _closed)
    {
        if (client) { send DoorRequest to host; return false; }   // skip: door doesn't move here
        return true;                                              // host: game opens it normally
    }
    static void Postfix(Door __instance, bool _closed)
    {
        if (host) send DoorState to client;                       // tell everyone what happened
    }
}
```

Our patches so far: `Door.SetClosed` (doors), `Health.Hurt` (client hits -> host), `ItemAgent_Gun.ShootOneBullet`
(send our shots), `Projectile.UpdateMoveAndCheck` + `DamageReceiver.Hurt/AddBuff` (harmless fake bullets),
`ResolutionSetter.UpdateResolutionAndScreenMode` (1280x720), `Application.persistentDataPath` (copy #2 saves).
`harmony.PatchAll()` in `Plugin.Awake` applies every `[HarmonyPatch]` class in the DLL.

Stack: **BepInEx** loads the mod into the game -> **Harmony** changes the game's behaviour ->
**LiteNetLib** carries the messages between copies. MegabonkTogether is the same shape (~50 patches).

### Notes: how the Duckov spike networks things (what we learned)

**Send rate vs frame rate.** Positions go out 20x/sec (`SendInterval = 0.05f`) while the game runs at 60 fps.
The other side still looks smooth because it plays the remote player ~100 ms in the past and blends between
two real samples (`SnapshotBuffer.cs`). Sending 60/sec would triple traffic for little gain. Games commonly
send 10-30/sec; the existing Duckov co-op mod sends ~33-66/sec. Enemies: 10/sec.

**Reliable vs unreliable (LiteNetLib `DeliveryMethod`).**
- *State that's re-sent constantly* (position, health, animation): `Unreliable`. A lost packet is replaced 50 ms later.
- *One-off events* (door opened, enemy hit for 25, enemy died, spawn): `ReliableOrdered`. LiteNetLib resends
  until acknowledged and keeps order, so "hit" can't arrive after "died". Lose one and the worlds drift apart forever.
- *Shots*: `ReliableUnordered`: every shot arrives, but one doesn't wait for another.

**Host authority, who decides what.**
- You move yourself (client-authoritative movement: instant, no lag).
- You report what you did (fired, opened door, hit enemy 17).
- The host decides what happened (damage, deaths, loot, door state) and tells everyone.
- Client-side hit detection ("option B"): the client's game decides "I hit enemy 17 for 25" and sends that to
  the host, which applies it. Feels perfect for the shooter, easy to cheat, fine for co-op with friends.
  (Option A, host re-checks hits, is for competitive games and feels laggy.)

**Matching IDs between two copies.**
- Placed in the level (doors, loot boxes): both copies load the same scene, so derive the ID from the object
  itself. Duckov already does this for saves: `Door.GetKey()` = hash of `"Door_" + rounded position`.
- Spawned at runtime (enemies, drops): the host assigns a number and sends "spawn preset #5, id 17, here".
  Presets are indexes into the game's own list, the same in both copies.
- The client's own random spawning must be switched off, or the two worlds have different enemies.

**Turning off the client's decisions = Harmony prefixes that return false.**
- `Door.SetClosed` on the client: send a request to the host instead.
- `Health.Hurt` on a client puppet enemy: send EnemyHit to the host instead.
- Puppets: AI GameObject disabled, the game's animation scripts disabled, we set position/animator from packets.

**Cosmetic effects: compute them locally, don't send them.**
- The other player's shot sends only muzzle, direction, speed, range and gun ID. No hit position is sent.
- The receiver fires a **real game bullet with damage 0** ("fake bullet", the trick the existing Duckov co-op
  mod uses). The game's own projectile code flies it through the receiver's copy of the level, so it looks
  right and plays the game's own impact sparks/dust wherever it hits. Walls are in the same place on both
  sides, so it lands in the same spot.
- Fake bullets are tracked (`FakeBullets` in `ShotSync.cs`): while one runs its hit check
  (`Projectile.UpdateMoveAndCheck`), patches block `DamageReceiver.Hurt` and `AddBuff`, so even with 0 damage
  it can't cause pain debuffs, aggro or a "hit" report to the host. Cap: 8 fake bullets per frame.
- Moving things (enemies, the other player) can be in slightly different places on each screen (~100 ms late),
  so a cosmetic bullet can occasionally hit or miss differently. Doesn't matter: real damage comes from the
  authority via its own reliable message.

**How MegabonkTogether does shots/hits/damage (compared with ours).**
- Projectiles are real game objects with a network ID (`SpawnedProjectile` with id/owner/weapon, positions in
  `ProjectilesUpdate`, reliable `ProjectileDone`).
- **Hits: host only (option A).** `ProjectileBase.HitEnemy` prefix returns `IsServerMode()`, so client projectiles
  fly but never hit. Fine for Megabonk (auto-fire into swarms); Duckov is aimed, so we chose client-side hits (B).
- **Damage: host decides, client replays through the game's own `Enemy.Damage`.** Host postfix sends `EnemyDamaged`
  (damage, crit, element, knockback, armour, attacker); client prefix blocks `Damage` unless `CAN_DAMAGE_ENEMIES`
  is set, sets it, calls `enemy.Damage(...)`, clears it. So damage numbers, hit flash, knockback and death all
  play on the client. Sent **unreliable** (frequent; death is separate).
- Enemy spawn/target/loot host only: `InitEnemy` postfix picks a target among all players (`TargetSwitcher`),
  client drop chance = 0; chests, orbs, tornadoes, exploders spawn on the host only.

| | MegabonkTogether | Our Duckov spike |
|---|---|---|
| Hit detection | Host (A) | Client reports (B) |
| Remote projectiles | Real, network ID + position updates | Harmless real bullets, fire-and-forget |
| Client sees damage | Game's own `Damage()` with host numbers | Game's own `Health.Hurt` with host numbers (`EnemyDamaged`, replay) |
| Enemy spawn/target/loot | Host only | Host only for button-spawned enemies |

**We copied the damage replay:** host `Health.Hurt` postfix on a synced enemy sends `EnemyDamaged`
(health before minus after: `DamageInfo` is a struct, so the game's `finalDamage` is on its private copy;
we record health in the prefix via Harmony `__state`). The client replays it as `realDamage` with
an `applyingHost` flag that lets it past our own block. Puppets get `dropBoxOnDead = false` so a replayed
killing blow can't drop client-only loot.

**Why the wolf only attacked the host, and the fix.**
- Duckov AI finds targets via `AIMainBrain.AddSearchTask`: it scans for `DamageReceiver`s on an enemy team.
  P2 on the host was only a cloned model (no collider, no DamageReceiver, no team), so it didn't exist to the AI.
- Fix (same as the existing Duckov co-op mod): **the remote player is a real game character**
  (`CharacterCreator.CreateCharacter`, team `player`, hitbox) driven as a puppet (no input, animation scripts off,
  position/aim/Animator from packets). Enemies now see, chase and shoot it.
- Damage to that stand-in (`DamageReceiver.Hurt` prefix, `RemotePlayerDamage` in `RemoteDuck.cs`):
  - Host: block it, send `PlayerHit` to its owner. The owner applies it to its real duck (`mainDamageReceiver.Hurt`)
    with its own armour; new health comes back in `PlayerState`.
  - Client: just block it (client-side enemies aren't authoritative).
  - Also blocks the "Pain" debuff bullets add after a hit.
- General rule: **a remote player must be a real target in the authority's world**, and damage to it is
  forwarded to whoever owns that player.

**Shared loot without dupes (`LootSync.cs`).**
- Host: postfix on `InteractableLootbox.CreateFromItem` (dead characters' boxes) gives the box an ID and sends
  `LootSpawn` with its contents; the client builds a real box with real items (normal loot window works).
- First idea was *optimistic* (client takes, host confirms or we take it back). **That dupes**: stackables
  (ammo) merge into a stack you already own via `Item.Combine`, which never calls `RemoveAt`, so a "too slow"
  can't be undone.
- So the client is **ask first, take after**: items in a shared box are locked on the client at every exit:
  `Item.Detach`, `Slot.Plug`, `Item.Combine`, `Item.Split` (covers double-click, take-all, drag and drop,
  merging). Each becomes a `LootTake` request (one per slot in flight). The host checks the item is still
  there, removes it and answers `LootTakeOk` (client then moves it into the bag with `ItemUtilities.SendToPlayer`)
  or `LootDenied`. The host hands every item out exactly once = no dupes. Cost: one round trip.
- Host changes are mirrored: `LootRemoved`, `LootAdded`, `LootStack` (partial stack merges).
- Lesson: before patching "the" choke point, list **every** path that changes the state (here: remove, plug,
  combine, split). Missing one = a dupe.

**Messages so far** (`MsgType` in `Plugin.cs`, first byte of every packet):
PlayerState 1 (unreliable), FaceData 2, DoorRequest 10, DoorState 11, FireEvent 20,
EnemySpawn 30, EnemyState 31 (unreliable), EnemyDeath 32, EnemyHit 33, EnemyDamaged 34, PlayerHit 40.

### Reusable for Emberville (Duckov is the practice run)
The game-specific parts change (class names, where damage/doors/spawns live); these patterns carry over as-is:
- `SnapshotBuffer` (play remote things ~100 ms in the past), `ImGuiUnity` (in-game menu), `SaveIsolation`
  (two copies), `MsgType` byte + LiteNetLib reliable/unreliable split.
- Puppets: spawn with the game's own factory, switch off AI + the game's animation scripts, drive the Animator.
- IDs: the game's own save keys for placed objects; host-assigned numbers for spawned ones.
- Authority: Harmony prefix on the one choke-point method (e.g. `SetClosed`, `Hurt`) - client sends a request, host applies + broadcasts.
- Fake effects: replay the action locally with the game's own code, made harmless (e.g. fake bullets;
  for Emberville maybe a fake tool swing / crop-hit animation), real outcome comes from the host.
- First recon job in any new game: find those choke points (spawn, damage, interact, item add/remove, scene load).

### Notes: performance lesson (client at 14 fps, our code 56 ms/frame)
- Two bugs: (1) a per-frame retry + log line for held items without a 3D graphic (44k log lines; logging to the
  console every frame is slow); (2) 50 enemy puppets each still running the game's character physics, and us moving
  them through `SetPosition` (which goes through that physics) every frame.
- Fixes: try once and remember; puppets get `Movement`/`CharacterMovement` disabled + kinematic rigidbodies and are
  placed with `transform.position`; switched-off puppets are skipped. Result: 88-89 fps both, mod ~1.2 ms, 40 enemies.
- Rules: **switch off the game's simulation on puppets, not just their AI**; never log or retry per frame;
  keep a perf readout (status line `fps, mod ms` + per-section `Perf` timers in the Insert menu).

### Duckov co-op roadmap (gaps vs the existing Duckov co-op mod + MegabonkTogether)
Done so far: connect (LAN/IP), two copies + separate saves, in-game ImGui, real remote duck (face, animation,
held gun, health bar), smoothing, doors, harmless replay bullets, host-spawned enemies (Delete / menu) with
client hits + damage replay, enemies target both players, shared loot from dead enemies (no dupes).
Since then: items 1-6, 9, 13d, 14 built (most untested), 12 and 13 partly; see the status table at the top.

**Next, in this order**
- [x] 1. Player-vs-player collision: chose **pass through** (Physics.IgnoreCollision between our duck and the
      stand-in, redone on level change). Solid + ~100 ms delay = shoved by where they *were*, blocked doorways.
- [x] 2. Level-spawned enemies host-only (built, needs a raid test): client `CharacterSpawnerRoot.StartSpawn` -> return
      false, and enemies its spawners made before connecting are removed once per level. Host: postfix on
      `CharacterSpawnerRoot.AddCreatedCharacter` registers every level enemy (host-assigned ID; spawn message
      carries the preset **asset name**, so bosses outside the main preset list work). Inactive enemies don't
      stream state. `SetActiveByPlayerDistance.FixedUpdate` replaced by "near ANY player" (`NearAnyPlayer.cs`).
      Not covered yet: other spawn paths (`EnemyCreator`, `Egg`, `SpawnControlCharacter`).
- [x] 3. Enter levels together (built, untested): prefix on the `SceneLoader.LoadScene(SceneReference, ...)` overload
      every level change ends in (raid start after paying, extraction, teleporters, base, death routing). Host sends
      `LevelLoad` (scene ID, evacuation, entry location); client loads the same (host paid the raid cost). Client can
      still travel alone (warning). Everything synced (enemies, loot) is only sent while both are in the same level;
      the level snapshot sends the rest on arrival. Not done: vote/ready, waiting for the host's go-ahead.
- [x] 4. Enemy attacks visible on the client (built, untested): host enemies' held weapon in EnemyState (shown with
      `HeldItemVisual`, the puppet's own random gun hidden); their shots go through the same `ShootOneBullet` postfix
      (`FireEvent` with enemy id) -> harmless bullets from the puppet's muzzle; melee = `attackAction.OnAttack` ->
      `EnemyAttack` -> puppet plays the "Attack" trigger. Not done: grenades / skills.

**Later**
- [x] 5. Pausing / menus (built, untested): `GameManager.Paused` getter -> false while connected (world keeps running,
      pause menu still blocks your own input). Status badge under the teammate's health bar (`PlayerStatus.cs`, byte in
      PlayerState): paused / in inventory / looting / map / in a menu / DuckovGhost menu / loading / dead, derived from
      `PauseMenu.Instance.Shown` and `View.ActiveView`'s type name. Not done: cutscenes.
- [x] 6. Loot containers placed in levels (built, untested): each copy rolls its own (`LootBoxLoader.Setup`), keyed by
      a position hash in `InteractableLootbox.Inventories`. Client opening an unsynced box: postfix on
      `InteractableLootbox.OnInteractStart` sets `Inventory.Loading` (game shows "searching") and sends `LootRequest(key)`;
      host waits for its own roll, registers the box, replies `LootContents`; client replaces its roll, clears Loading.
      Then the dupe-proof take/put rules apply. 3 s timeout falls back to the client's own contents.
- [ ] 7. Items dropped on the ground / picked up: drop + pickup requests with a host-assigned drop ID.
- [ ] 8. Breakables, explosive barrels, extraction points, lottery box, other interactables.
- [x] 9. Weather, time of day, storm (built, untested): weather = f(seed, GameClock.Now), so the host sends day +
      seconds of day + weather seed every 2 s (`WorldClock`, ReliableSequenced); client adopts the seed and corrects
      clock drift > 30 game seconds (`WorldSync.cs`).
- [ ] 10. Remote duck armour, helmet, weapon attachments.
- [ ] 11. Player death: down/spectate, dropping your own bag, buffs (bleeding etc.).
- [~] 12. Teammate map markers, chat, friendly-fire setting.
      - [x] **Ping** (built, untested): middle-click the ground -> RTS-style marker (Age of Empires 4) for everyone in the
        level: 3 expanding rings + bouncing arrow + name in the player's colour (host blue, P2 yellow, P3 green, P4 pink),
        fades over 3 s. Raycast from the camera through the mouse; drawn with ImGui projected from the world
        (`Pings.cs`); `Ping` message relayed by the host.
      - [x] **Drawn paths** (built, untested): hold middle mouse + drag (> 0.6 m) = draw on the ground (points every
        0.4 m, max 256, live while drawing); on release it's sent (`PathDraw`, ReliableOrdered, ~3 KB fragmented by
        LiteNetLib) to everyone in the level; stays 10 s, fades over the last 2, outlined line + arrowhead at the end.
      - [ ] **Minimap that tracks everyone:** every player shown as a dot/arrow in their player colour (host blue,
        P2 yellow, P3 green, P4 pink) with name, facing direction, and an edge arrow when they're off the minimap.
        Data is already there (each `RemotePlayer`'s position, yaw, scene); players in another level shown greyed
        or listed as "in <level>". Two ways: (a) add markers to the game's own minimap / full map (find its marker
        API, like the existing Duckov co-op mod does), or (b) our own ImGui minimap drawn in a corner (works in any
        game, top-down projection around the local player). Pings and drawn paths should show on it too.
        Reusable for Emberville: (b) works without knowing the game's map code.
      - [ ] **Off-screen teammate arrows:** when a teammate isn't visible in the game view, an arrow in their colour
        sits on the screen edge pointing towards them, with name + distance (e.g. "P2 34 m"). Project their position
        with `Camera.WorldToScreenPoint`; if it's off screen (or behind the camera: z < 0, flip it), clamp the
        direction from screen centre to a margin inside the edges and rotate the arrow to match. Drawn with the
        ImGui foreground draw list like pings. Optional: also for pings outside the view; hide for players in
        another level. Generic, reusable for Emberville.
      - [ ] Chat, friendly-fire setting.
- [~] 13. Internet play via Steam (user has 2 Steam accounts + 2 PCs for testing). **Built, needs the 2-PC test:**
      F7 / "Host via Steam" = friends-only lobby (`SteamLobby.cs`) + Steam relay transport (`SteamTransport.cs`:
      `CreateListenSocketP2P` / `ConnectP2P(hostSteamId)`, reliable = `k_nSteamNetworkingSend_Reliable`,
      unreliable = `UnreliableNoNagle`). Rich presence `connect = +connect_lobby <id>` -> friends list "Join Game";
      Insert menu "Invite friends" -> `ActivateGameOverlayInviteDialog`. Joining: `GameLobbyJoinRequested_t`,
      `GameRichPresenceJoinRequested_t`, or `+connect_lobby <id>` on the command line -> `JoinLobby` -> `GetLobbyOwner`
      -> connect. Launch from Steam on both PCs (the overlay only hooks Steam-launched games).
      - **Use the game's own app ID, not Spacewar.** Duckov already calls `SteamAPI.Init` (3167020, bundled
        Steamworks.NET) and our mod runs in that process. Lobbies, invites and P2P work for any app ID; the game
        doesn't need multiplayer of its own. One app ID per process, so 480 isn't even possible inside Duckov.
      - Flow: host `SteamMatchmaking.CreateLobby` -> invite via overlay / friend uses "Join game" -> friend's mod gets
        `GameLobbyJoinRequested_t`, joins the lobby, reads the host's Steam ID -> connect.
      - Transport: LiteNetLib over Steam networking (`ISteamNetworkingSockets` / SDR relay: NAT + firewalls handled,
        free; the existing Duckov co-op mod does this in `Patch/SteamP2P`), or lobby only to exchange IPs (NAT again).
      - Keep direct LiteNetLib (F8/F9 on 127.0.0.1) for side-by-side testing on one PC: one Steam account can't P2P to itself.
      - **Spacewar 480** = fallback for games that aren't on Steam / never init Steamworks: init it ourselves as 480.
        Emberville check: does it ship `steam_api64.dll` and call `SteamAPI.Init`? Yes -> its own app ID. No -> 480.
      - Alternative without Steam: NAT punch + relay server (MegabonkTogether: matchmaking server, punch, relay fallback).
- [ ] 13b. **Join codes** (Core Keeper style): one short string to paste to a friend = lobby/room ID + password.
      Core Keeper: 14 chars from a 54-char alphabet; first 6 encode the Steam lobby AccountId, last 8 are the password;
      client's first reliable message is the password (checked server-side); lobby members also publish an
      HMAC-SHA256(SteamID, password).
- [ ] 13c. **Colyseus room server (future):** a small server that hosts rooms by join code, matchmaking, and
      relays/introduces peers (NAT punch + relay fallback, like MegabonkTogether's matchmaking server). Same message
      types; just another transport behind the same interface.
- [x] 13d. **Swappable transport** (built): `Transport.cs` - `Conn` (Send, Ping), `ITransport` (Poll, Stop),
      `ITransportListener` (Accept, Connected, Disconnected, Receive); `LiteNetTransport` + `SteamTransport`. Original plan: keep all game sync code talking to one small interface
      (send to host / send to all / per-peer events). Backends: LiteNetLib direct (local testing), Steam relay,
      Colyseus. Core Keeper does exactly this: a fake Unity Transport `INetworkInterface` hands raw packets to
      Steam relay, Steam direct or PlayFab Party.
- [x] 14. More than 2 players (built, untested; `MaxPlayers` config, default 4). Join mid-game works through the
      per-client level snapshot (not chunked yet - fine on LAN, may need chunking for big levels over the internet).
      Testing: 4 copies run fine (`SaveIsolation` gives copy #2-#8 their own saves; `WindowFix` arranges them in a
      grid that fits the monitor: 2x2 on 3440x1440).
      How it works:
      - Player IDs: host = 0, each client gets one in a `Welcome` message; per-player state (`RemotePlayer`:
        snapshot buffer, animation, health, status, scene, face, stand-in) instead of one `Remote`.
      - Clients only talk to the host. Host relays player messages (PlayerState, FaceData, FireEvent) to the other
        clients with the sender's player ID; level-bound messages (enemies, doors, loot) go only to clients in the
        host's level; global ones (LevelLoad, WorldClock, faces, PlayerLeft) go to everyone.
      - Replies go to the requester (`LootTakeOk/Denied`), `PlayerHit` goes to the stand-in's owner; a granted
        loot take is also broadcast as `LootRemoved` to the other clients.
      - Per-client level snapshot (doors/enemies/loot) when that client arrives in the host's level.
      - `PlayerLeft(id)` when one disconnects; max players from config.
- [ ] 15. Disconnects: host leaves, timeouts, rejoin.

### Notes: how Core Keeper does it (built for multiplayer from day one)
- Unity 6, Mono, official mod support (mod.io, ships Harmony). **Unity Netcode for Entities** (DOTS/ECS) on
  **Unity Transport**; Facepunch.Steamworks; PlayFab Party for crossplay.
- **Host = server world + client world in one process**; the host's own client talks to the server through an
  in-memory queue, not a socket. Single-player likely runs the same way.
- **Server-authoritative + prediction:** clients only send packed input per tick; the server simulates and
  replicates ~494 "ghost" types as snapshots; own player predicted and corrected. Everything else is RPC structs.
  Big data (map) over a separate reliable Steam lane.
- **20 Hz** simulation and send rate; 8 players default (configurable).
- Transport: fake `INetworkInterface` -> Steam relay (`CreateRelaySocket` / `ConnectRelay(hostSteamId)`, host's
  Steam ID stored in a private lobby), Steam direct IP for dedicated servers (port 27015, `-port/-maxplayers/-password`),
  PlayFab Party for crossplay. Invites via rich presence `connect`.
- **Lesson:** "clients send only input" needs a game written for it (ECS). Retrofits like ours (and Megabonk, the
  Duckov co-op mod) sync state + patch authority instead. Worth stealing: swappable transport, join codes, 20 Hz is enough.

### Notes: LiteNetLib with more than 2 players ("RPCs to all clients")
- The host's `NetManager` has every client as a `NetPeer` (`net.ConnectedPeerList`).
- **Send to all**: `net.SendToAll(writer, DeliveryMethod.ReliableOrdered)`.
- **Send to all except one** (relay a client's message to the others): `net.SendToAll(writer, method, excludePeer)`.
- **Send to one**: `peer.Send(writer, method)`. A client only has one peer: the host.
- **No built-in RPC system.** LiteNetLib is a transport: bytes + delivery guarantees. "RPCs" are what we build
  on top: first byte = message type (our `MsgType`), then a switch/dispatch table. Optional helper:
  `NetPacketProcessor` (register packet classes, `Subscribe<T>(handler)`, auto serialize). Bigger mods do the same
  by hand (the Duckov mod: ~99 opcodes, `[Rpc(op, delivery, direction)]` attribute + registry; Megabonk ~75 types).
- Typical star topology: clients only talk to the host; the host applies the change, then `SendToAll` (or
  `SendToAll(..., excludePeer: sender)`) so every client sees it.
- What changes for us at 3+ players: `Remote` becomes a list of peers; each message carries a player ID
  ("player 2 fired", "door opened by player 3"); one `RemoteDuck` per player; the host relays player states.

### Lesson for Emberville
Prefer **B (draw inside Unity)** from the start. It doesn't depend on Windows window tricks, works
the same in every Unity game, and only needs: a Unity shader (`UI/Default`), `OnGUI`, and ImGui.NET.
On an IL2CPP game the same idea works, but meshes/shaders go through Il2CppInterop.

### Still to check in Duckov
- [x] Insert opens the menu; clicking and typing work.
- [x] Toasts show (the "loaded" one fires before ImGui starts, so you won't see that one).
- [ ] The game still reacts to clicks/keys while the menu is open (not blocked yet).
- [ ] Colours may look slightly washed out (linear vs sRGB); fix if needed.
- [x] F8 host / F9 join → blue capsule ghost of the other duck. **Works (2026-10-16):** two copies
      connect over LiteNetLib and each sees the other's stand-in puppet. (Since replaced by the real duck.)

---

## TODO

The Emberville plan at the top replaces the old TODO list (recon of other mods, LiteNetLib console spike, the
first "ghost co-op" sketch). Duckov taught all of that; those items are dropped.

## Later

- Split the dungeon / Descent / arena / Boss Rush features into their own mod, `BetterDungeonsAndBossRushArena`:
  not started on purpose (2026-10-20). Findings and the two ways to do it: `notes/split-better-dungeons.md`.
- **Better mouse movement**: decided 2026-10-21 to keep it as it is. Mouse walking stays on a side button (mouse 4 by
  default, picked in Insert > Mouse Options); left click stays the attack. The left-click plan (click enemies / NPCs /
  items / ground) was dropped.
- **Build mode anywhere (B)**: switched off 2026-10-21 (`BuildMode.Enabled = false`). The demo has no build
  controller anywhere (not in the tutorial area, not in Town), so the game's build bar never opens; our own placer
  (`FreeBuild.cs`) opened but clicks didn't place, untested why (it now says why on a refused click). Off means: B does
  nothing, nothing built is loaded or saved, no Insert > Construction tab. Spec and plan:
  `docs/superpowers/specs/2026-10-21-build-anywhere-design.md`, `docs/superpowers/plans/2026-10-21-build-anywhere.md`.
- **Lua scripting for BetterNPCsAndQuests** (idea, 2026-10-21; not needed yet: the files cover dialogue, quests and
  rules). If we add it:
  - MoonSharp: a Lua interpreter written in plain C#, so it works inside the game with no extra native files.
  - Optional scripts: in a `scripts/` folder, run only by the host.
  - A small API: read and set flags, start and advance quests, give items, say a line, spawn a creature, and react
    to the same events the rules use.
