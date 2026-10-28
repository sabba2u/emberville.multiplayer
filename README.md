# emberville.multiplayer

> ### ⌨️ Press **Insert** on your keyboard to open the mods menu
> Every mod's switches and sliders live there (or click **MODS** on the title screen / Esc menu, just above Options).
> Close it with Insert again.

Unofficial online co-op and a set of gameplay mods for **Emberville** (the demo; Unity 6, Mono), as BepInEx 5 plugins.
Not made or supported by Cygnus Cross.

- **Download:** the [Releases](../../releases) page: **EmbervilleModPack** has everything in one zip. New builds go up
  often; everyone playing together needs the same version.
- **Install:** unzip it into the Emberville Demo folder (Steam > right-click Emberville Demo > Manage > Browse local
  files), so `winhttp.dll` and the `BepInEx` folder sit next to `Emberville.exe`. To leave a mod out, delete its folder
  in `BepInEx\plugins`.
- **Commands and API:** [API.md](API.md) (every chat command, NPC dialogue lines, and C# for other mods).
- Notes and the roadmap: [`todo.md`](todo.md).

### 🎥 New: Twitch integration ([Better Streaming](#better-streaming-twitch-plays-along))
Hook the game up to your Twitch chat: bits, cheers and votes set off heals, loot, monster raids, lightning storms or a
random weapon swap every few seconds, right in your game. Chat can spin a wheel, roll dice, pull slots, drop a Plinko
ball, cheer on a monster race, form a raid party where every raider shows up as a monster wearing their name... and
bet on whether you'll survive. MODS (Insert) > **Streaming** > **Connect Twitch**.

### What's new in 0.6
- **Twitch integration** (above): the wheel, dice, the casino, the monster race, raid parties, chatters as enemies,
  votes, polls, predictions and a leaderboard.
- **Combat that answers you** (Better Combat): spell keys that never eat a press, a spell that cuts a swing's
  follow-through, dodge and attack presses remembered, **no stun lock**, a slowdown slider for moving while you
  attack, and a **Before / after** button. [Details](#your-character).
- **No level caps:** your character levels past the demo's 15, and `/unlockclasses` opens the class tree to level 40.
  **Shift + `+`** on the character screen gives you a level. [Details](#levels-no-cap-and-test-keys).

## Contents
[Better Streaming: Twitch](#better-streaming-twitch-plays-along) · [Playing together](#playing-together) · [Travel: homes, portals, /tp](#travel-homes-portals-tp) ·
[The descent and the boss arena](#the-descent-and-the-boss-arena) · [Hidden dungeons](#dungeons-hidden-in-the-demo) · [Weapons and summons](#weapons-and-summons) ·
[Your character](#your-character) · [Levels: no cap](#levels-no-cap-and-test-keys) · [Chests and loot](#chests-and-loot) · [Weather](#weather) ·
[NPCs and quests](#npcs-and-quests) · [Fishing](#fishing) · [Pets](#pets) · [Companion: an AI adventurer](#companion-an-ai-adventurer) ·
[The Ninja (early alpha)](#the-ninja-a-new-class-early-alpha) ·
[The mod menu](#the-mod-menu) · [Quality of life](#quality-of-life) ·
[Keys](#keys) · [Commands](#commands) · [API](#api) · [Secrets](#secrets-the-dev-panel-test-arenas-and-the-estate) ·
[Build](#build)

## Better Streaming: Twitch plays along
For streamers: your Twitch chat sets off effects in your game, with the viewer's name on screen ("Twitch raid incoming
by xxJoexx", their message under it).
- **Connect:** MODS > **Streaming** > **Connect Twitch**. The game shows a short code; enter it on twitch.tv (the link
  is right there). No password is ever typed in the game; **Sign out** ends it.
- **Cheers:** a word in the cheer picks the effect (`cheer500 raid`), else the exact amount; bigger cheers make raids
  bigger. Effects so far: **Raid** (creatures that fit the area, around you), **Chicken raid** (waves of hostile
  chickens), **Heal**, **Poison** (down to 1 health, never further), **Instant death**, **Chain lightning** (every enemy on screen zapped for 30 s), **Freeze** (frozen solid
  for 5 s) and the **Mystery chest** (a chest beside you: full of loot, or a trap that springs a raid when opened).
  `!effects` in chat lists them with prices. Bits need a Twitch Affiliate or Partner channel.
- **Fair play:** the **chaos dial** (Chill to Mayhem) sets how often a mean effect may land; the rest wait in a queue
  shown on screen ("3 curses incoming..."). Nice ones skip the queue, and nothing lands in a cutscene, a menu or while
  dead. **Chat can kill you for real** (then a breather from mean effects); switch on **Twitch deaths are free** in
  the tab and you get up where you fell instead, bag intact.
- **Try it first:** every effect has a free **Test** button, and **Simulate a cheer** runs a pretend cheer through the
  same rules. Prices, caps, times and which effects are on are all in the tab.
- **Messing with you** (30 s each, longer when bought again): backwards walking, can't stop walking, swapped buttons
  (attack and dodge), random dodges, invisible; upside down, mirror world, sideways, dizzy, seasick, earthquake, zoomed
  way in or out; drunk, colour blind, tunnel vision, lights out, retro; fists only (your own weapon comes back after); weapon
  roulette (every 5 s a weapon of a different type from your bag, never the same type twice in a row); random x2 / x3 (that many naughty effects at once); and a **lightning storm** (Better Weather's rain, strikes all around you;
  your weather comes back after). They work the same on a controller.
- **Spin the Wheel** (`cheer525 wheel`): a game-show wheel pops up and spins. A peg sits on every line between
  slices and a rubber flapper clicks over each one, slowing it more and more; a wheel too slow to clear a peg gets
  thrown back into the slice it was leaving. Green slices are nice, red naughty, gold spins again, and instant death
  is a thin black sliver. Where it stops happens. The streamer sets the slices and their widths.
- **Roll the Dice** (`cheer475 dice`): three dice tumble across the screen; the total picks the outcome (low bad,
  high good). Three 1s: instant death. Three 6s: the jackpot (a heal, chain lightning and a loot drop at once).
- **The casino:** **Pull the Slots** (`cheer495 slots`: three reels; three of a kind is that effect doubled, three
  7s the jackpot, three skulls instant death, two of a kind that effect), **Plinko** (`cheer510 plinko`: a ball
  bounces down the pegs into a bin; the middle is mild, the edges are the jackpot and death), **Coin flip**
  (`cheer125 coin`: heads nice, tails naughty, `heads`/`tails` in the cheer calls it and doubles it; once in a
  while it lands on its edge) and **Roulette** (`cheer540 roulette red`: red naughty, black nice, the green 0 the
  jackpot; bet a colour in the cheer and win it doubled). One gamble on screen at a time; the rest wait their turn.
- **Chat plays together:** a **monster race** (`cheer575 race`, a button, or every few minutes): four of the game's
  creatures race across the top of the screen, each carrying an effect, and chat cheers them on with `!1` to `!4`;
  the winner's effect happens. A **raid party**: `!party` opens one, chatters `!join` for 45 seconds, and a raid comes
  with a monster for every raider, each wearing their name. **Chatters as enemies**: the monsters wear the names of
  people chatting, "RIP name" when they fall, and the one nearest when you go down gets the kill on the leaderboard.
- **Free for chat:** **votes** (`!raid` from 10 different chatters within a minute sets it off; the counts show on
  stream), **polls** ("What happens next?", from a button or every few minutes; the winner happens), and a
  **"Will they survive?" prediction** that starts by itself when a big raid lands and settles itself when you fall or
  win. A **leaderboard** of top raiders, top healers and most deaths caused shows on stream (`!top` in chat).
  Polls and predictions need an Affiliate or Partner channel; if you connected Twitch before this version, sign out
  and connect again to allow predictions.
- Coming next: channel-point rewards.

## Playing together
- **Steam (easiest):** host with **F9 > Host via Steam**; friends join from their Steam friends list (right-click >
  Join Game) or an invite (Insert menu > Invite friends). No IP address, no port forwarding (Steam's relay). The mod
  brings Steamworks itself (the demo has none).
- **By IP:** host with **F9**, friends press **F10** and type the host's IP. Over the internet the host has to forward
  **UDP port 9060** on their router; on the same home network nothing's needed. If the host leaves, the player who
  joined first takes over and the others reconnect to them.
- **Other players** are real characters: looks, gear and dyes, animations, name, health and Kymia bars, what they're
  doing, status effects, damage numbers, a speech bubble while they type.
- **One shared world:** creatures (run by whoever is nearest), bosses and their phases, attacks and spells, gory deaths,
  drops (dupe-proof), chests, breakables, ore and trees, doors, levers, key altars, floor buttons, town repairs, the
  clock, story progress and cutscenes, every crafting station, farm plots, building on the estate, NPCs (everyone sees
  the host's NPCs looking the same), the weather.
- **Party strip** (top): every player with health and Kymia; live character portraits (MODS > Multiplayer) or
  their weapon. Right-click a card: teleport to them (any scene), **bring them to you**, trade, mute. Off-screen teammates get edge arrows; teammates show on the
  minimap and the world map (M).
- **Online Players (O):** who's online and where; teleport to them, trade, mute; the host can kick and ban.
- **Trading:** Online Players > Trade, or `/trade <name>`: drag items into your offer, both tick Ready, the swap happens
  once both agree on exactly the same two offers.
- **Chat** (Enter), pings and drawing on the ground (middle mouse), death announcements.
- **Dungeon invites:** when a teammate goes into a dungeon or down a floor of the descent, a card offers Join.

## Travel: homes, portals, /tp
- **Homes:** `/sethome` where you stand makes it your home; `/sethome mansion` (any name) saves another place.
  `/home` or `/tp mansion` takes you back from anywhere, `/homes` lists them, `/delhome mansion` forgets one. Each home
  leaves a green portal on the ground with its name over it, there every time you come back (even after restarting).
  Every character has its own homes. Not on dungeon floors or in the boss arena (they're built anew each time).
- **Town Portal / Home Portal** (the portal picture on the right edge, half way down): hold still 3 seconds (moving or
  being hit stops it). A swirling portal opens where you stand, its twin opens in town (by the starting door), or
  beside your home once you've set one (the button then says **Home Portal** and turns green), and you're taken there.
  Step into either end, you or your friends, to go to the other; on a dungeon or descent floor it builds that floor
  again and puts you at the portal. Each player has one pair, with their name over it, until they open a new one.
- **Right-click the portal button:** a **Go to** list of your homes and Town; click one to go there now (or Cancel).
- **`/tp <name>`:** go to one of your homes, or to a player, even onto their dungeon floor. **`/summon <name>`** or
  **`/summon all`:** bring them to you, now.

## The descent and the boss arena
- **The descent:** the full game's dungeon generator ships in the demo; the mod builds the levels. Up the path from the
  starting door, the **water well** is the way down (and the **lamp** the boss arena, with **Rory** the archer between).
  Set a seed, how many floors, how much stronger each floor gets, how many creatures, and the layout (Sprawl). Each
  floor has chests (better loot deeper), the stairs at the far end, a mini-boss guardian every 5th floor (now and then
  it drops a staff), a minimap, and a blue pad or the Leave button back to where you came in. Everyone gets the same
  floors.
- **Boss arena** (the lamp): a walled floor with pillars, spike traps and arrow shooters, and a panel to make mini-bosses
  from the game's creatures (name, colour, size, health, damage). **Boss rush:** 30 bosses, tougher each wave.
- **There is no chicken level:** a button in the arena's panel. Hostile chickens come at you from every side every
  5 seconds, a little tougher each wave; big chickens every minute, **The Colonel** every 3 minutes; the spike traps
  wake after a minute, the arrows after three. Survive as long as you can: your time and your best time are kept.
  Your companion fights the chickens with you.

## Dungeons hidden in the demo
The demo ships about 20 of the full game's dungeons that you never get to visit. Type **`/dungeons`** in chat (Enter)
to list them, and **`/dungeon <name>`** to build one and go straight in: any part of the name works, and each time is a
new layout. `/dungeon` on its own goes back out.

| Type `/dungeon ...` | What it is |
|---|---|
| `fullmap` | the big cave: the world map's own dungeon |
| `cave_small` | a small cave |
| `df_small`, `negedra`, `tolcan` | the Dark Forest (Negedra's is where you find the carpenter; Tolcan's is someone we haven't met) |
| `mining_01`, `mining_02`, `morcha` | the mines, and Morcha's mine |
| `nature_01`, `nature_02` | overgrown caves |
| `lab` | Mordaxius' lab |
| `morchahideout` | Morcha's hideout |
| `library` | the library |
| `kl` | a dungeon only called "KL" (Kaldhor's? his altar and throne are in the files) |
| `basement_first`, `basement_second`, `stonebasement_first`, `stonebasement_second` | wooden and stone basements |

Everyone in a session goes in together and gets the same layout.


## Weapons and summons
Type the command in chat to get one, or MODS > Better Combat > **New Weapons**.
- **Staff of Clucking (`/clucking`):** right-click summons a battle chicken (a tenth of your Kymia); left-click is a
  normal attack.
  
  ![chickens](chickens2_sm.gif)
  
- **Staff of the Risen (`/risen`):** creatures that die near you glow green; right-click near one raises it to fight
  for you (a fifth of your Kymia).
  
  ![zombie](zombie_sm.gif)
  
- With either staff, **middle-click** sends your chickens or risen there (they path around walls and water). Both
  staffs level up as you use them; summons go out in their creature's gore burst.
- **Hunter's Hook (`/hook`):** right-click throws it (a sixth of your Kymia): it drags a creature to your feet, stunned
  (big guardians pull you to them instead), with a spray of blood. While it's in your hand your whole outfit turns
  Scorpion yellow.
  
  ![hook](hook2_sm.gif)


## Your character
- **Respec:** `/respec`, or ask Ellie: every stat point back to spend again (level, XP and gear kept).
- **Respec tab** (MODS > Respec): unlock every screen, class and mastery; give yourself a level's worth of XP; reset
  attributes or class points; and a table with **−** / **+** / Reset for your level, each class, each weapon and armour
  mastery, and gathering and crafting (mining, woodcutting, farming, alchemy...). Lowering a mastery takes its perks
  away again.
- **Dye your gear:** right-click an equipped item in your inventory's gear slots, pick one of 12 colours, **Dye** (or
  Cancel). The dye stays with the item and your friends see it.
- **Favourite weapons:** hover a weapon in your inventory and press **F5-F8** to put it on that key; F5-F8 wield it
  (the boxes sit between the two skulls on the HUD), or hold **Left Alt** (gamepad LT) for a weapon wheel.
- **Bestiary (N):** every creature you've killed, with its stats and your kill count.
- **Lock-on (G):** point at an enemy and press **G**: you face it whatever the mouse does (circle and strafe round it),
  and swings, spells and arrows go at it; a ring under it shows the lock. G on it again lets go; G pointing at nothing
  locks the nearest enemy, again the next one. When it dies the lock moves on to the next one close by. Enemies only
  (creatures, spider eggs): never doors, barrels or furniture. The Hunter's Hook throws at the locked enemy too.
  **Controller (partial support):** hold the **right trigger** to lock on to the nearest enemy (hold again: let go), tap
  it for the next one; locked, you face it whatever the right stick does, so arrows and spells go at it.
- **Better Combat:** move while attacking (`/moveattack`), bows that aim in any direction and lock on (`/bowlock`).
- **Spell keys that answer every press** (Better Combat, on to start): the game ignores a spell key for 1 s after each
  press and drops presses made mid-swing; now every press counts, and one made while you're busy (or just before the
  cooldown ends) is remembered for 0.4 s and cast the moment it can. `/spellkeys` turns it off, `/spellkeys 0.6` sets the time.
  A spell also cuts the end of a weapon swing short once the swing's hit has landed, and a dodge press you couldn't do
  yet (cooling down, being hit, mid-spell) is remembered the same way, and so is an attack clicked during a roll (the
  game drops it): it goes the moment you land. **No stun lock:** after a hit staggers you, you can't be staggered again for 0.5 s
  (you still take the damage), so a pack of chickens can't keep you from casting; how long a stagger lasts is a
  slider too (the game's 0.3 s, down to never). Each has its own switch in MODS > Better Combat, and **Before / after**
  (or `/gamecombat`) turns them all off and back on, to show the difference.
- **Centred camera** (Better Combat, off to start): `/centercam` keeps the camera on you, so a dash doesn't leave you at
  the edge of the screen; `/centercam 30` lets it trail a little (30% of the game's lag). Also in MODS > Better Combat.
  
  ![moveandattack](moveandattack_sm.gif)


## Levels: no cap, and test keys
- **Your level has no cap** (Better Companions): the demo stops at 15; you keep levelling, each level past the game's
  table costing what its last one did. Your companion too.
- **Classes past 15:** `/unlockclasses` learns every class and opens the class tree to **level 40** (the game's own XP
  table goes that far), its "coming soon" skills too (they may be unfinished in the demo). XP your class earned while
  capped was kept, so those levels come back at once. You get 2 skill points every 5 class levels. `/lockclasses`
  puts the cap back.
- **Test keys on the character screen** (open it, hold **Shift**):

  | Keys | What it does |
  |---|---|
  | **Shift + `+`** (or `=`, numpad +) | a level up: the XP for it, given like a kill (your class and weapon mastery get it too) |
  | **Shift + `/`** | a level down (attribute points back if you'd spent more than the lower level gives) |
  | **Shift + `-`** | your attribute points back to spend again |
  | **Shift + `*`** (Shift+8) | back to level 1 (no XP, points back) |

  The same keys **without Shift** do it to your companion while its attributes are showing.
- More in **MODS > Respec**: − / + / Reset for your level, each class, each weapon and armour mastery and every job.

## Chests and loot
- **F** on an item marks it a favourite (a gold star on its slot): favourites never get swept into chests.
- **J** on an item marks it junk (a red X on its slot); **Shift+J** drops all your junk at your feet (on the ground a red X marks junk, a gold star a favourite).
- At a chest: **X** puts in everything the chest already holds; **V** customizes it (name, colour, and what it takes:
  items it refuses grey out in your bag).
- **Search boxes** above your bag and an open chest.
- **Ctrl+click** an item while a chest is open: it goes straight across (bag to chest, chest to bag).
- **Shared stash (U, or `/stash`):** a big chest you can open anywhere: 6 tabs of 30 slots, shared by every character
  on your PC. Right-click a tab to rename it and give it a colour. Kept in its own file next to your saves, with a
  backup. (There's a chest button under the portal button too.)
- A renamed chest shows its own name straight away (no more "Chest" for a moment first).
- **Better Looting:** the magnet picks up items near you (Insert menu: range and kinds), and a green arrow marks better
  gear.

## Weather
`/rain`, `/snow`, `/clear`. One weather for everyone; outside only.
- **Rain:** pixel rain across the screen, slanted by a shifting, gusting wind; splashes on the ground (under trees and
  props), rings on open water; lightning strikes now and then with a flash; the sky goes dark.
- **Snow:** drifting flakes; the ground whitens over a few minutes (32 stages, the first see-through, thinning toward
  water and cliffs, in several shades of white), trees and other things get snow caps, you leave footprints; it slowly
  melts when it stops.
- **Sliders:** rain drops, snowflakes, how fast snow settles, how dark the rain / snow sky gets, lightning. Other mods
  and NPC scripts can set the weather with settings of its own ([API.md](API.md)).

## NPCs and quests
- **Ellie** (in the starting area): a two-part quest (wood, then a sword) with a reward; she'll give you a chicken or
  either staff, and can respec you. Her portrait in the dialogue window is her, live.
- **The camp** (next to Ellie): characters the demo has the art for but never lets you meet.
  - **Atwor the blacksmith**, at his anvil: talk to him with a weapon in your hands and he sharpens it (+1, more damage).
  - **Gumu**: his shop, with good-to-legendary gear by your level, fresh every day in the game.
  - **Nagedra the carpenter**: her quests from the full game (a wooden chest, then her tools) for blueprints; she'll
    send you into the dungeons she knows; and **"Could you help me change my look?"** opens the game's character
    creator (hair, beard, face, skin, colours, body, name). **Hide your hat first** (the show / hide toggle by the head slot
    in your inventory), or you won't see your new hair.
- **Make your own:** NPCs, dialogue (Twine `.twee`) and quests from text files: see `README-BetterNPCsAndQuests.txt` in
  the download. Dialogue can run any chat command (`(trigger: "command snow on")`).
- In co-op everyone shares the host's NPCs and story.


## Fishing
Every catch brings something up (food, materials, sometimes rare gear), and you can fish wherever there are fish
shadows in the water, the starting area too. Cast, wait for the bite, left-click quickly.

![fishing](fishing2_sm.gif)


## Pets
`/pet on`, then `/pet` befriends a pet that follows you, fights with you and levels up. 13 kinds (Chicken, Glowbat,
Lilshrooms, Lil Ticks, Lil Bombers, Spiderling, Bug Fuzz): `/pet list`, `/pet glowbat`, `/pet name ...`,
`/pet color red`. Everyone sees your pet.

![clucky](clucky_sm.gif)

## Companion: an AI adventurer
A second adventurer who plays beside you, run by an AI (Better Companions). It isn't a pet or a puppet: it's a real
second player character, and the AI plays it the way you would, pushing its stick and pressing its buttons, so the
game itself moves it, swings its weapon, fires its arrows and spells, spends its Kymia and dodges.
- **Get one** (MODS > **Companions**, or chat): **clone** one of your other characters (pick the save slot by name,
  "Slot 2: Cohh (level 12)": its looks, gear, bag and level), **start a new one**, or take a ready-made one:
  **Sabba** the blond knight (sword, heavy armour), **Ellie** the mage (staff), **Rory** the archer (bow) or
  **GraveBear** the dark-haired wanderer (two-handed sword, medium armour and a cloak), each dressed and armed for their
  class. In chat: `/companion 2`, `/companion new`, `/companion dismiss`, `/companion call`, and `/companion` for its
  status.
- **How it fights:** it follows you, and fights the way its weapon does: up close with a sword, axe or fists, from
  range with a bow or staff. It carries a melee and a ranged favourite and swaps between them by itself, dashes in to
  finish an enemy or out of a crowd, and uses **its own skills** (from its own skill tree: attack skills on an enemy in
  reach, a dash to close in or catch up, buffs when a fight starts). It walks the game's own paths around cliffs and
  water, and if it gets cut off from you it comes straight to you.
- **Tell it where:** **middle-click** sends it there to fight (your lock-on target first); **Shift+middle-click** makes
  it hold that spot and guard it.
- **Enemies fight it back.** At zero health it's down: **hold F** next to it for 2 seconds to get it up (a teammate can
  too).
- **Its card** (top, with the party): its portrait, health and Kymia, and its skills with their cooldowns. Right-click
  the card: heal it with one of your potions, or trade.
- **Gear and growth:** **F** next to it (or Trade) opens your bag with its gear beside it: drag gear across to dress
  it. It earns XP from the kills it helps with, levels up past the game's cap, and has its own **Stats** and class
  screen to spend its points (its skill tree is its own copy, not yours).
- **MODS > Companions:** its status, **rename** it, send it home / call it / follow / trade / heal, and **your other
  companions**: every companion you've had is kept; **Swap in** brings one back (or Delete). A **Respec** section for
  the companion too (its level, classes and masteries, up and down).
- Companions are kept in their own files, outside the game's saves: a clone of your character doesn't write its XP
  back to that character.
- Everyone sees everyone's companion in co-op.

## The Ninja: a new class (early alpha)
A new class (Better Classes), all about speed, things thrown and the Hunter's Hook. **Early alpha:** it works, but
it's rough. The art is borrowed for now (arrows until there are throwing stars), and the numbers will change.
- **Get it:** on the class screen, like any class (it takes the game's empty Archer slot), or `/ninja` in chat. For
  testing, `/ninja` learns every Ninja skill at once, and as a Ninja you get **20 extra skill points** to place.
- **Fast:** +15% move speed.
- **Poison throws:** your bare hands throw instead of punching (the game's punch moves, the white swipe hidden): each
  throw is a green poisoned arrow, **half the creature's health over 5 seconds** (a boss: a tenth). Kicks stay kicks.
  Any weapon you hold works as usual.
- **Smoke step:** your dodge vanishes in a puff of smoke and you reappear **1.5 times as far**, straight **through
  walls, doors and fences** as long as there's floor on the other side.
- **Skills** (put them on your skill bar from the class screen):
  - **Hunter's Hook:** a foe it catches is dragged to you and stunned.
  - **Fire Palm:** hold it for a cone of fire from your hands, burning until you let go or run out of Kymia. Burning
    creatures panic and run, and the fire spreads from one to the next.
  - **Chain Lightning:** jumps to the nearest creature, then on from each to the next, until everything on screen is
    hit; each one is stunned for 5 seconds.
  - **Spike Trap:** set at your feet (up to three at once, armed for 30 s): the first creature to step on one is hit
    by sparkly steel spikes and held fast.
- Without the mod, a Ninja save just has no class on; nothing else in your save changes.
- Coming: Shadow Step (unseen for a while, to sneak past), throwing stars, more skills.

## The mod menu
**Insert**, or the **MODS** button on the title screen and the Esc menu (just above Options). Its tabs:
**Multiplayer** (hosting, the party strip and who's connected), **Font Options**, **Mouse Options**, **Better Combat**,
**Better Looting**, **Companions**, **Respec**, **Pets**, **Streaming** and **Log**. Close it with Insert again or the [x].

## Quality of life
- **Faster loading:** worlds load much faster (in one test, about 100 s down to under 20 s): the game's loading runs at
  high priority while the loading screen is up. (`FastLoad` in the multiplayer mod's config turns it off.)
- **Mouse walking:** hold the mouse side button to walk toward the mouse; a click walks there.
- **Sleep vote:** everyone types `/sleep` and the clock skips to the morning.
- **Team buffs:** a buff you cast on yourself also lands on teammates near you (host option).
- **Keep playing after the demo:** its "Thank you for playing" stop is skipped.
- **Your characters are safe:** before joining, the character files are backed up (`Saves/EmbervilleGhost_Backups`).
- **Typing anywhere** (chat, a chest's name, search boxes, the stash's tab names) keeps the mods' hotkeys out of it.
- **Font Options** (MODS menu): the story text's size (what NPCs say, speech bubbles, the quest tracker: the boxes
  grow with it), the size of the words in the world ("Open", "Examine", names over heads), and the font for all the
  game's text: the game's own Emberfont, Noto Serif, Liberation Sans or Perfect DOS VGA.
- **Tilted view (experimental, `/tilt`):** the world seen from behind and above at an angle, the
  floor receding and everything that stands (you, creatures, props, trees) stood up facing you, like a pop-up book.
  Only the picture changes: the game plays exactly as before, and your aim follows what you see. Tilt and field of view
  are sliders (each with a Reset). Houses stand with their vines and windows on them; stairs, rugs and paths stay lying
  (`TiltFlatNames` in the config lists the words that keep ground art flat).
- **Chase and first-person views (experimental, `/chase` or `/fps`):** the camera low behind you, or
  through your own eyes. The mouse turns the camera (hold **Alt** for the cursor); W walks the way you face, A/D
  strafe, attacks go straight ahead. The walls are raised as blocks around you. Sliders for distance, angle, eye
  height, field of view and mouse speed.
- **Tilt-shift (`/tiltshift`):** the miniature look: a sharp band in the middle, the top and bottom
  blurred, richer colours. Works with any view.
- **Death never gets stuck:** a hit landing during your death animation no longer leaves you standing at zero health
  with no death screen.
- **Staff tooltips** sit beside the item card, and off the second card when the game shows two side by side.
- **Title screen:** Better & Online under the Emberville logo; the Next Fest popup is skipped.


## Keys
| Key | What |
|---|---|
| F9 / F10 | host (IP or Steam) / join (again: disconnect) |
| Insert (or the MODS button) | the mods' menu (every switch and slider) |
| Shift + `+` / `/` / `-` / `*` (character screen) | a level up / a level down / attribute points back / back to level 1 ([more](#levels-no-cap-and-test-keys)) |
| Enter | chat (and `/commands`) |
| O | Online Players |
| N | Bestiary |
| F5-F8, hold Left Alt | wield a favourite weapon, weapon wheel |
| G | lock on to the enemy under the mouse (again: let go); at nothing: the nearest / the next one |
| Right trigger (controller) | hold: lock on to the nearest (hold again: let go); tap: the next one |
| F (on an item) | favourite it (gold star) |
| J (on an item) | junk it (red X) |
| Shift+J | drop all your junk at your feet |
| X / V (at a chest) | put matching items in / customize the chest |
| Ctrl+click (chest open) | move the item across |
| U | the shared stash |
| Right-click (gear slot) | dye the item |
| Right-click (staff / hook) | summon, raise, throw |
| Middle mouse | ping, draw; with a staff: send your summons; send your companion (Shift: guard there) |
| Mouse side button | walk toward the mouse |
| Portal button (right edge) | a portal home; right-click: go to one of your homes |
| Delete | the status line (top left) |

## Commands
The full list is in [API.md](API.md#chat-commands). The main ones: `/tp`, `/summon`, `/sethome`, `/home`, `/homes`, `/delhome`, `/trade`, `/online`, `/sleep`,
`/respec`, `/rain`, `/snow`, `/clear`, `/hook`, `/clucking`, `/risen`, `/pet`, `/companion`, `/dungeon`, `/dungeons`, `/dungeon negedra`, `/estate`, `/repair`,
`/resources`, `/stash`, `/tilt`, `/chase`, `/fps`, `/tiltshift`, `/bowlock`, `/moveattack`, `/spellkeys`, `/gamecombat`; testing: `/nodeath`, `/onehitkill`, `/noclip`, `/devmode`, `/devpanel`,
`/unlockclasses` (every class, and the class tree open past the demo's level 15, to 40).

## API
Other BepInEx mods (and NPC dialogue) can drive these mods; the full reference with examples is in [API.md](API.md).
- **Multiplayer** (`EmbervilleGhost.Api`): `Command`, `Online`, `Players`, `TeleportTo`, `Summon`, `SummonAll`, `Teleport`.
- **Weather** (`BetterWeather.Plugin`): `SetWeather(kind)`, `SetWeather(kind, new WeatherOptions { Amount, Darkness,
  Settle, LightningEvery, Wind })`, `StrikeAt(position)`, `Current`.
- **Pets and summons** (`BetterPetsAndSummons`): `Plugin.Command`, `Plugin.Rally`, `Plugin.HoldingStaff`, staffs'
  `Make` / `Give`.
- **NPCs and quests** (`BetterNPCsAndQuests`): `Events.Raise`, `Plugin.Bodies`; any mod's `Command(string)` /
  `NpcFlag(string)` is picked up by dialogue.
- **Chests** (`BetterChests.ChestLooks`): `Set`, `All`, `TakesOf`, `Changed`.
- **Favourite weapons** (`EmbervilleFavorites.Favorites`): `Wield`, `Find`, `HasKey`, `KeyOf`.
- **Combat** (`BetterCombat`): `BowAssist.SetAngle`, `BowAssist.Toggle`, `MoveAttack.Command`.

## Secrets: the dev panel, test arenas and the estate
- **The dev panel:** the demo still has the developers' tools, switched off. `/devmode` switches them on:
  **Ctrl+Shift+Numpad7** opens the developer panel (go to Town, unlock skills, heal, advance time...), `/devpanel` opens
  it without a numpad.
- **Test arenas:** `/arena` teleports you to the scene's test arena; Numpad1 spawns monster groups there.
- **The estate (your house and farm in Town):** `/estate` (host) unlocks building and repairs. **Repairing the town:**
  `/resources`, walk onto a ruin and press F (or `/repair`); everyone sees it repaired.

## Build
Requirements: .NET SDK, Emberville installed with BepInEx 5.4.23 (`winhttp.dll`, `doorstop_config.ini`,
`.doorstop_version` and `BepInEx/core` in the game folder).
```
dotnet build spike/EmbervilleGhost -c Release -p:GamePath="<path to Emberville Demo>"
```
Each mod is its own project in `spike/` (EmbervilleGhost = multiplayer, EmbervilleFavorites, BetterChests, BetterCombat,
BetterLooting, BetterPetsAndSummons, BetterFishing, BetterNPCsAndQuests, BetterWeather, BetterCompanions, BetterStreaming); a build copies the DLL into the
game's `BepInEx/plugins`. The game's own DLLs are referenced from `<GamePath>/Emberville_Data/Managed` (not included).

**Release zip:** `pwsh spike/package-all.ps1` makes `spike/dist/EmbervilleModPack-v<version>.zip` (BepInEx and every
mod, each with its own README).

**Testing on one PC:** launch `Emberville.exe` twice (don't load the same character in both).

## Not included
No game code: the decompiled reference used for research stays local (`reference/`, git-ignored). The only game art
is two small button icons cut from the game's own sprites (`spike/EmbervilleGhost/Assets/Images`: the chest and the
portal swirl).
`spike/libs/OverlayDearImGui`: ImGui.NET + cimgui (MIT). `spike/libs/Steamworks`: Steamworks.NET (MIT) + Valve's
redistributable `steam_api64.dll`.
