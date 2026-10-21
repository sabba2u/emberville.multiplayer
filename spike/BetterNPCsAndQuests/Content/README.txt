BETTER NPCs AND QUESTS
Add your own NPCs, conversations and quests to Emberville with text files. No coding needed.

WHERE THINGS GO (in BepInEx\plugins\BetterNPCsAndQuests)
  npcs\<Name>\npc.txt        who they are and where they stand
  npcs\<Name>\<name>.twee    what they say (a Twine story; start from TEMPLATE.twee)
  npcs\<Name>\portrait.png   optional; without it, a picture of the NPC is made for the dialogue window
  quests\<quest>.txt         quests
  rules\<anything>.txt       world rules: "when this happens, do that"
Ellie (npcs\Ellie, quests\ellie_errands.txt) is a complete example: copy her folder to start.
Files are read when the game starts. Insert > NPCs & Quests (or Insert on its own) has "Read the files again",
your current position to copy, and a list of every problem found in the files.

IN CO-OP (with the Emberville multiplayer mod)
The host's files count: friends who join see the host's NPCs and quests (nothing is written to their folder).
Flags and quest progress belong to the host's world. Anyone can make progress; every player online gets the reward.

------------------------------------------------------------------------------------------------------------------
npc.txt   (one "key = value" per line; lines starting with # are notes)
  name     = Ellie                  shown over their head and in the dialogue window
  scene    = TutorialArea           a hand-made scene (not DungeonScene: dungeons differ in every world)
  spot     = -49.5, 64.8            where they stand. Insert shows your own scene and spot, with a Copy button.
  facing   = down                   down, up, left or right
  hair     = 2                      a hairstyle number (leave it out to keep the default)
  outfit   = light                  light, medium, heavy or none
  holding  = none                   none, a weapon kind (sword, axe, mace, dagger, bow, crossbow, staff, wand) or a weapon's name
  color    = #e8a0c0                their name's colour
  dialogue = ellie.twee             their conversation file, in the same folder
  portrait = portrait.png           optional
  wander   = 3                      how far they walk around their spot (0 = they stand still)
  pause    = 3-8                    seconds they wait between walks
They never walk through walls, water or closed doors, and they stop when someone comes close.

------------------------------------------------------------------------------------------------------------------
CONVERSATIONS (.twee)
They play in the game's own dialogue window. Write them by hand from TEMPLATE.twee, or in the Twine app
(https://twinery.org) and use "Export as Twee". The game reads only part of Twine's language:

  :: Name [NPC:Ellie]              a passage; [NPC:<their folder name>] shows their name and portrait
  [[What you say|Passage]]         a choice (at most 6 per passage); without choices, "Leave" appears
  [[What you say|END]]             a choice that ends the conversation (your own words instead of "Leave")
  (if: $flag)[[Text|Passage]]      a choice that only shows when the condition is true
  (set: $flag to true)             change a flag when the passage shows
  (trigger: "start my_quest")      do something: start / turnin / advance <quest id>, or set $x to ...
  (trigger: "command clucking")    run another mod's chat command for the player talking (pet chicken, clucking, risen...)
  (trigger: "give my_quest")       once the conversation closes: a box (the game's chest screen) to drag what the quest's
                                   step wants into; only matching items can go in. Close it: they take what fits, the rest
                                   comes back, and they talk again with $just_gave (or $give_failed) set: route on it first
                                   and clear it with (set: $just_gave to false)
  {PlayerName}                     the player's name

  A passage with no text: the game goes straight to its first choice whose condition is true. Use it to branch:
      :: DialogStart
      (if: $quest_my_quest_done)[[x|Thanks]]
      [[x|Hello]]
  The passage you start in is called DialogStart.

  Conditions: $a, not $a, $a and $b, and with numbers: $x >= 5, $x is 2, $x != 0, $x < $y  (there is no "or")
  Setting:    (set: $x to true), (set: $x to 3), (set: $x to $x + 1)

  Two traps:
  - The game skips the first two passages of every file (Twine puts its StoryTitle and StoryData there). Keep the
    two from TEMPLATE.twee at the top and write below them. (If they're missing, the mod adds them for you.)
  - Never type two colons in a row in your lines: the game starts a new passage there.

------------------------------------------------------------------------------------------------------------------
FLAGS
Any name you like, e.g. $saved_rory or $ellie_met. Nothing to declare: a flag exists once something sets it, and
before that it reads as false (0). Flags are shared by every NPC, quest and rule, and saved with each world in its
own file (<world>.bnq next to the save, written when the game saves; the game's own save is never touched).
A flag that's checked somewhere but never set anywhere is listed in Insert (usually a typo).

Ready-made flags (worked out on the spot, you can't set them):
  $has_softwood_10       the player has 10 of that item in their bag (by its name; a number at the end = how many)
  $has_any_sword         ...any sword (or axe, mace, dagger, bow, crossbow, staff, wand, weapon, armor, material,
                         consumable, gear); $has_any_material_5 = five of them
  $has_pet               the player has a pet (with Better Pets and Summons; other mods can add flags of their own)
  $quest_<id>_active     the quest is under way
  $quest_<id>_step       which step it's on (1, 2, ...; 0 when not under way)
  $quest_<id>_done       the quest is finished

------------------------------------------------------------------------------------------------------------------
QUESTS (quests\<anything>.txt)
  id       = ellie_errands          used in (trigger: "start ellie_errands") and $quest_ellie_errands_...
  title    = Ellie's Errands        shown in the journal and the quest list
  text     = A description.
  start    = set $x to true         (optional) actions when it starts
  step     = Bring Ellie 10 logs    a step; what follows belongs to it
  collect  = softwood x10 take      items (by name, or "any sword"); "take" = handed over at a turnin
                                    (the poorest ones go first; favourites, Better Chests' heart, never count or go)
  kill     = Mob_Glowbat x5         creatures (names as in the Bestiary, B), or "any"
  flag     = $talked_to_rory        done once this flag is set
  done     = set $x to true         actions when this step is done
  complete = set $y to true         actions when the quest is done
  reward   = gold 200               also: item <name> x5, random good-legendary, xp 50, set $z to true
Item names: the game's own (e.g. softwood finds Item_Log_Softwood); unknown names are listed in Insert.
Qualities for random: none, broken, ordinary, good, superior, epic, legendary.
Quests show in the game's journal and quest list, with its popups.

------------------------------------------------------------------------------------------------------------------
RULES (rules\<anything>.txt)
  when <event> [what] [if <condition>] -> <action>; <action>
Events: kill <creature|any>, boss_kill <floor>, descent_floor <n>, enter <scene>, pickup <item>,
        quest_done <id>, step_done <id> <step>, flag <name>
Actions: set $x to ..., start <quest>, advance <quest> (finish its current step), turnin <quest>,
         command <another mod's chat command>
Example: when boss_kill 5 -> set $saved_rory_d5 to true; start rory_thanks
