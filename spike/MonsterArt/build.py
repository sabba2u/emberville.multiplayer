"""Monster art: one sprite sheet per creature, from the Tiny Swords enemy pack (bought, not ours to share).

    python spike/MonsterArt/build.py [Bumblebee "Giant Bat" ...]      (default: every creature in FIGHT)

For each creature, from spike/tiny.swords.extract/Enemy Pack/<Name>/<Name>_<Anim>.png (frames in a row, square: the
sheet's height is the frame size), and its extra animations in "Enemy Pack/Extra/*/<Name>_<Anim>.png" (the pack keeps
some there: the Turtle's guard, the Troll's death):
- its ground shadow left out (the game's creature has one),
- shrunk to a third (Emberville's art: 32 pixels a unit; each 3x3 block one pixel, faint ones gone),
- every animation a row of one sheet: sheets/<Name>.png (frames of different sizes centred in the biggest),
- sheets/<Name>.txt: the game's creature it's built on, the cell size, pixels a unit, the pivot (the middle of the
  creature), where its feet are (from the frame's bottom), how it fights, each row's animation.
A variant (the Queen Bee) is another creature's art ("art") with its own way of fighting.
The Factions troops (Tiny Swords/Factions: the Knights' Warrior, Archer, Pawn; the Goblins' Torch, TNT, Barrel) come
as one sheet each, a row per animation (GRID), in four colours each ("Red Warrior", "Blue TNT Goblin"...), all dying
as the pack's Dead (a puff, then a skull that sinks away).
Then `dotnet build spike/MonsterArt` packs the sheets into MonsterArt.dll (into the game's plugins/MonsterArt).
None of it is in git or in the mod pack: the pack's licence forbids passing its art on, even changed.
"""
import glob
import os
import sys

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
PACK = os.path.join(HERE, "..", "tiny.swords.extract", "Enemy Pack")
FACTIONS = os.path.join(HERE, "..", "tiny.swords.extract", "Tiny Swords", "Factions")
OUT = os.path.join(HERE, "sheets")
SHRINK, PPU = 3, 32
# each animation's name in the pack, its speed (frames a second), whether it loops
ANIMS = [("Idle", 10, True), ("Move", 10, True), ("Walk", 10, True), ("Run", 10, True), ("Attack", 16, False),
         ("Attack Fast", 16, False), ("Attack Strong", 16, False), ("Shoot", 14, False), ("Throw", 14, False), ("Guard_In", 12, False),
         ("Guard_Out", 12, False), ("Guard", 10, True), ("Hit", 12, False), ("Dead", 10, False), ("Windup", 10, False),
         ("Recovery", 10, False),
         ("Attack_Start", 12, False), ("Attack_Loop", 14, True), ("Attack_End", 12, False)]   # (the Imp's attack, in three)
# layers the pack draws apart, put on top of a row (the Troll's club flying off as he falls)
LAYERS = {("Troll", "Dead"): ["ClubPart1", "ClubPart2"]}
# more rows a creature needs from elsewhere (row, file under tiny.swords.extract, speed, loops): what it throws or
# shoots (Shot: flying; ShotHit: where it lands), the Bomb Fish's bomb (flying, its fuse lit) and its blast
EXTRA_ROWS = {
    "Bomb Fish": [
        ("BombSpin", "Enemy Pack/Bomb Fish/Bomb_Spinning.png", 14, True),
        ("BombFuse", "Enemy Pack/Bomb Fish/Bomb_FuseLit.png", 10, True),
        ("Boom", "Tiny Swords (Free Pack)/Tiny Swords (Free Pack)/Particle FX/Explosion_01.png", 14, False),
    ],
    "Gnoll": [("Shot", "Enemy Pack/Gnoll/Gnoll_Bone.png", 14, True)],
    # dust behind a charge (the Pig Rider), a splash on you (the Paddle Shark), the ground pound's dust (the bears)
    "Pig Rider": [("Dust", "Tiny Swords (Free Pack)/Tiny Swords (Free Pack)/Particle FX/Dust_01.png", 16, False),
                  ("Dust2", "Tiny Swords (Free Pack)/Tiny Swords (Free Pack)/Particle FX/Dust_02.png", 16, False)],
    "Paddle Shark": [("Splash", "Tiny Swords (Free Pack)/Tiny Swords (Free Pack)/Particle FX/Water Splash.png", 18, False)],
    "Bear": [("Pound", "Tiny Swords (Free Pack)/Tiny Swords (Free Pack)/Particle FX/Dust_02.png", 16, False)],
    "Harpoon Shark": [("Shot", "Enemy Pack/Harpoon Shark/Harpoon.png", 1, True)],
    "Hex Shaman": [("Shot", "Enemy Pack/Hex Shaman/Hex Shaman_Projectile.png", 12, True),
                   ("ShotHit", "Enemy Pack/Hex Shaman/Hex Shaman_Explosion.png", 18, False)],
    "Slingshot Gnome": [("Shot", "Enemy Pack/Slingshot Gnome/Acorn_Projectile.png", 14, True)],
    # the Pig has no attack: its run, quicker, is its charge
    "Pig": [("Attack", "Enemy Pack/Extra/Pig/Pig_Run.png", 16, False)],
}
FLYER, MELEE, RANGED, SPIDER = "Mob_Glowbat", "Mob_Rodent_Melee", "Mob_Rodent_Ranged", "Mob_Spiderling"
# how each fights:
#   base: the game's creature under it (its health, hits, loot; a Glowbat: it flies)
#   strike: the attack row's wind-up and strike frames (the rest: the recovery); speed: wandering, chasing
#   lunge: the strike's dash speed (0: it strikes where it stands); reach: how close it strikes from, how far it reaches
#   bleed / burn / poison: what its strike leaves (damage a second, seconds); knock: how far its strike pushes you
#   back; stun: how long it leaves you dizzy (the game's stun, seconds); trap: webs you (the game's cocoon), at most
#   every so many seconds
#   ranged: it attacks from that far, backing off when you're closer than the second; bomb: its bomb's fuse and blast;
#   shot: its shot's speed, whether it points the way it flies (a harpoon), and the way its picture points
#   (degrees: the harpoon's is drawn tip up-right, 45)
#   block: hit, it guards that long, then hits back; not again for the second
#   windup / hit / breath: its own windup held that long, the attack row's frame it lands on, its attack row held
#   that long hitting that often (the Imp's flame)
#   size: drawn that much bigger; boss: the creature under it that much bigger, tougher, harder hitting (a boss bar)
#   art: another creature's art (a variant)
FIGHT = {
    # from 3 away: a quick windup, then a charge at you that stings on reaching you
    "Bumblebee": dict(base=FLYER, strike=(6, 2), speed=(1.4, 3.4), lunge=0, reach=(3, 0.9), charge=(0.36, 10, 0.3)),
    # the bumblebees' queen: bigger, a poison sting, a boss bar
    "Queen Bee": dict(art="Bumblebee", base=FLYER, strike=(6, 2), speed=(1.4, 3.0), lunge=9, poison=(3, 5), size=1.6,
                      reach=(2.0, 1.3), boss=(1.6, 6, 1.4), summon=(10, 6, "Bumblebee")),   # (calls 6 Bumblebees, every 10 s)
    "Giant Bat": dict(base=FLYER, strike=(4, 2), speed=(1.4, 3.4), lunge=9, bleed=(2, 5)),
    # its bite slows you to a fifth of your speed for 10 s; hitting it (its shell too) hurts you a little (thorns)
    "Turtle": dict(base=MELEE, strike=(5, 2), speed=(0.7, 1.6), lunge=0, slow=(0.2, 10), thorns=0.15),
    # its paddle: knocked back, and drenched (half speed for 10 s)
    "Paddle Shark": dict(base=MELEE, strike=(3, 2), speed=(1.2, 2.6), lunge=0, knock=3, slow=(0.5, 10)),
    # a boss: its own windup (a second: you see it coming), its swing landing on the attack row's 3rd frame, then
    # its recovery (it stands there tired); drawn 1.25x, reaching further
    "Troll": dict(base=MELEE, strike=(0, 1), speed=(0.9, 1.8), lunge=0, knock=3, stun=2, windup=1.0, hit=2, size=1.25,
                  reach=(1.8, 2.6), boss=(2.0, 10, 1.6)),
    "Spider": dict(base=SPIDER, strike=(4, 2), speed=(1.3, 3.0), lunge=6, trap=8),
    # dying, it drops its last bomb where it falls: it goes off a second later
    "Bomb Fish": dict(base=RANGED, strike=(4, 1), speed=(1.2, 2.4), lunge=0, ranged=(5, 2.5), bomb=(1.2, 1.3), deathbomb=1),
    # hits so hard your controls are flipped for 3 s
    "Minotaur": dict(base=MELEE, strike=(5, 3), speed=(0.9, 2.0), lunge=0, reach=(1.5, 1.9), block=(1.5, 4), backwards=3),
    "Panda": dict(base=MELEE, strike=(5, 6), speed=(1.3, 3.0), lunge=0, block=(1.2, 3)),
    "Imp": dict(base=FLYER, strike=(0, 1), speed=(1.4, 3.0), lunge=0, reach=(1.5, 1.8), windup=0.4, breath=(1.0, 0.3),
                burn=(3, 4)),
    # rears up, then a swipe with a short charge
    "Bear": dict(base=MELEE, strike=(6, 2), speed=(1.2, 3.0), lunge=5),
    # the bears' biggest: bigger, its swipe knocks you back, a boss bar
    "Great Bear": dict(art="Bear", base=MELEE, strike=(6, 2), speed=(1.0, 2.6), lunge=4, knock=2.5, size=1.4,
                       reach=(1.9, 3.0), boss=(1.6, 5, 1.3), pound=1),   # (a ground pound: everyone within 3 knocked back, dust, a shake)
    # 3 bones at once (at you, and 30 degrees each side), bouncing off walls for 6 s
    "Gnoll": dict(base=RANGED, strike=(5, 1), speed=(1.1, 2.6), lunge=0, ranged=(5, 2.5), shot=(8, 0), spread=(3, 30), bounce=6),
    # its bonk knocks your weapon aside: no attacking for 2 s; killed, it splits into 2 smaller ones (3/4 the size,
    # half the health) that don't split again
    "Gnome": dict(base=MELEE, strike=(3, 2), speed=(1.6, 3.4), lunge=4, disarm=2, split=(2, 0.75, 0.5)),
    # its harpoon hooks you and reels you in
    "Harpoon Shark": dict(base=RANGED, strike=(4, 1), speed=(1.1, 2.4), lunge=0, ranged=(6, 3), shot=(11, 1, 45), pull=1),
    # its hex, landing on you: you're a chicken for 5 s (no attacks, no skills), and a poison
    "Hex Shaman": dict(base=RANGED, strike=(5, 1), speed=(1.0, 2.2), lunge=0, ranged=(6, 3), shot=(6, 0), poison=(2, 4), polymorph=5),
    # flares its frill: a scare, close all round: rooted 2 s (no walking or dodging; you can still fight)
    "Lizard": dict(base=MELEE, strike=(2, 5), speed=(1.3, 3.0), lunge=0, reach=(1.2, 1.6), root=2),
    # killed, it breaks into 4 little skull skeletons: half the size, half the health and damage (they don't split)
    "Skull": dict(base=MELEE, strike=(2, 3), speed=(1.0, 2.4), lunge=3, block=(1.0, 4), split=(4, 0.5, 0.5)),
    # its acorn in your eye: blinded 4 s (dark all round, a circle round you clear)
    "Slingshot Gnome": dict(base=RANGED, strike=(6, 1), speed=(1.4, 3.0), lunge=0, ranged=(6, 3), shot=(10, 0), blind=4),
    # its venom builds: the 2nd bite paralyzes you for 10 s
    "Snake": dict(base=MELEE, strike=(3, 3), speed=(1.2, 3.2), lunge=6, poison=(2, 4), venom=(2, 10)),
    # every 3rd attack its strong one: from 4 away, a piercing lunge (11 a second, 0.5 s), twice the damage
    "Spear Goblin": dict(base=MELEE, strike=(2, 2), speed=(1.3, 3.2), lunge=0, reach=(1.4, 1.7), strong=(3, 0.5, 11, 2, 4)),
    # its slash knocks your weapon from your hand: 10 s with your fists (your weapon stays in your bag)
    # unseen but for a shimmer, there only as it strikes
    "Thief": dict(base=MELEE, strike=(2, 3), speed=(1.6, 3.8), lunge=8, fists=10, stealth=1),
    "Torch Goblin": dict(base=MELEE, strike=(2, 3), speed=(1.3, 3.0), lunge=0, burn=(2, 3)),
    # the pack's 23rd (kept in Extra): a Spear Goblin riding a pig; a fast charge, its lance knocking you back
    # no attack of its own: from 3 away a quick shuffle, then it charges straight into you and bowls you over
    "Pig": dict(base=MELEE, strike=(2, 2), speed=(1.5, 3.6), lunge=0, knock=3, reach=(3, 0.9), charge=(0.4, 9, 0.35)),
    # a trampling charge from 5 away: through you (knocked aside), dust behind
    "Pig Rider": dict(base=MELEE, strike=(3, 2), speed=(1.6, 4.0), lunge=0, reach=(5, 1.0), knock=2, charge=(0.6, 12, 0.3), trample=1),
}

# ---- the Factions troops: one sheet each (a row per animation), four colours ----
COLOURS = ["Blue", "Purple", "Red", "Yellow"]
DEAD = ("Knights/Troops/Dead/Dead.png", 128)
NO_SKULL = {"Catapult"}   # (troops that don't die as the skull: a machine)   # (everyone's death: 2 rows of 7, played as one)
# each troop: its folder, its cell, its rows (name, row, frames, speed, loops, backwards), and more rows from elsewhere
# (file, cell, row, frames: what it throws or shoots)
GRID = {
    "Warrior": dict(dir="Knights/Troops/Warrior", cell=192,
                    rows=[("Idle", 0, 6, 10, True), ("Run", 1, 6, 10, True), ("Attack", 2, 6, 14, False)]),
    "Archer": dict(dir="Knights/Troops/Archer", cell=192,
                   rows=[("Idle", 0, 6, 10, True), ("Run", 1, 6, 10, True), ("Shoot", 4, 8, 14, False)],
                   more=[("Shot", "Knights/Troops/Archer/Arrow/Arrow.png", 64, 0, 1, 1, True),
                         ("ShotHit", "Knights/Troops/Archer/Arrow/Arrow.png", 64, 1, 1, 1, False)]),
    "Pawn": dict(dir="Knights/Troops/Pawn", cell=192,
                 rows=[("Idle", 0, 6, 10, True), ("Run", 1, 6, 10, True), ("Attack", 2, 6, 14, False)]),
    "Torch Goblin": dict(dir="Goblins/Troops/Torch", cell=192,
                         rows=[("Idle", 0, 7, 10, True), ("Run", 1, 6, 10, True), ("Attack", 2, 6, 14, False)]),
    "TNT Goblin": dict(dir="Goblins/Troops/TNT", cell=192,
                       rows=[("Idle", 0, 6, 10, True), ("Run", 1, 6, 10, True), ("Throw", 2, 7, 14, False)],
                       more=[("BombSpin", "Goblins/Troops/TNT/Dynamite/Dynamite.png", 64, 0, 6, 14, True),
                             ("Boom", "../Effects/Explosion/Explosions.png", 192, 0, 9, 16, False)]),
    # hides in its barrel (closed: row 0), pops up (row 1), stands (row 2), ducks back in (row 3: ends closed), runs
    # on its little legs (row 4), its fuse catching (row 5, played backwards: a spark, then it glows red)
    "Barrel Goblin": dict(dir="Goblins/Troops/Barrel", cell=128,
                          rows=[("Idle", 2, 1, 10, True), ("Run", 4, 3, 10, True), ("Guard_In", 3, 6, 12, False),
                                ("Guard_Out", 1, 6, 12, False), ("Windup", 5, 3, 6, False, True),
                                ("Attack", 5, 3, 6, False, True)],
                          more=[("Boom", "../Effects/Explosion/Explosions.png", 192, 0, 9, 16, False)]),
}
# how each troop fights (every colour alike)
TROOPS = {
    # every 3rd blow a shield bash: stunned 5 s
    "Warrior": dict(base=MELEE, strike=(3, 2), speed=(1.2, 2.8), lunge=3, reach=(1.3, 1.5), bash=(3, 5)),
    "Archer": dict(base=RANGED, strike=(6, 1), speed=(1.2, 2.6), lunge=0, ranged=(6, 3), shot=(12, 1, 0), mark=6),
    "Pawn": dict(base=MELEE, strike=(3, 2), speed=(1.2, 2.8), lunge=0, reach=(1.2, 1.4)),
    "Torch Goblin": dict(base=MELEE, strike=(3, 2), speed=(1.3, 3.0), lunge=2, burn=(2, 3)),
    # 3 sticks at once: at you, and 30 degrees to each side
    "TNT Goblin": dict(base=RANGED, strike=(5, 1), speed=(1.2, 2.6), lunge=0, ranged=(5, 2.5), bomb=(1.0, 1.3), spread=(3, 30)),
    # waits in its barrel (no damage) till you're 3 away, pops out, runs at you, lights up and blows itself up
    "Barrel Goblin": dict(base=MELEE, strike=(0, 1), speed=(1.0, 3.4), lunge=0, reach=(1.0, 1.8), windup=0.8,
                          ambush=3, kamikaze=1, bomb=(0, 1.8)),
}
# ---- the Free Pack's units (Tiny Swords (Free Pack)/Units/<Colour> Units/<Unit>): newer art, an animation a file,
# five colours; named apart from the Factions troops (Knight, Bowman, Peasant) ----
FREE_UNITS = os.path.join(HERE, "..", "tiny.swords.extract", "Tiny Swords (Free Pack)", "Tiny Swords (Free Pack)", "Units")
FREE_COLOURS = ["Black", "Blue", "Purple", "Red", "Yellow"]
# each: its folder, its rows (name, file, speed, loops)
FREE = {
    "Knight": dict(dir="Warrior", rows=[("Idle", "Warrior_Idle", 10, True), ("Run", "Warrior_Run", 10, True),
                                        ("Attack", "Warrior_Attack1", 12, False), ("Guard", "Warrior_Guard", 10, True)]),
    "Bowman": dict(dir="Archer", rows=[("Idle", "Archer_Idle", 10, True), ("Run", "Archer_Run", 10, True),
                                       ("Shoot", "Archer_Shoot", 14, False), ("Shot", "Arrow", 1, True)]),
    "Lancer": dict(dir="Lancer", rows=[("Idle", "Lancer_Idle", 12, True), ("Run", "Lancer_Run", 10, True),
                                       ("Attack", "Lancer_Right_Attack", 10, False), ("Guard", "Lancer_Right_Defence", 10, True)]),
    # a healer: its heal (on itself), and what shows on the one it heals
    "Monk": dict(dir="Monk", rows=[("Idle", "Idle", 10, True), ("Run", "Run", 10, True), ("Attack", "Heal", 14, False),
                                   ("HealFx", "Heal_Effect", 14, False)]),
    "Peasant": dict(dir="Pawn", rows=[("Idle", "Pawn_Idle Knife", 10, True), ("Run", "Pawn_Run Knife", 10, True),
                                      ("Attack", "Pawn_Interact Knife", 12, False)]),
}
FREE_FIGHT = {
    "Knight": dict(base=MELEE, strike=(2, 1), speed=(1.2, 2.8), lunge=2, reach=(1.3, 1.5), block=(1.2, 4), bash=(3, 5)),
    "Bowman": dict(base=RANGED, strike=(6, 1), speed=(1.2, 2.6), lunge=0, ranged=(6, 3), shot=(12, 1, 0), mark=6),
    # a charger: from 5 away, a quick lowering of the lance, then a rush at you (10 a second) that hits once when it
    # reaches you (or gives up after 0.6 s); hit, it blocks (spear down: no damage) and hits back
    "Lancer": dict(base=MELEE, strike=(1, 1), speed=(1.1, 2.6), lunge=0, reach=(5, 1.4), block=(1.0, 4), charge=(0.6, 10, 0.25)),
    # never hits you: keeps its distance, and heals the most hurt creature within 6 by a tenth of its health (a boss: half
    # that), once every 6 seconds
    "Monk": dict(base=RANGED, strike=(7, 1), speed=(1.2, 2.6), lunge=0, ranged=(6, 4), heal=(0.1, 6, 6)),
    "Peasant": dict(base=MELEE, strike=(2, 1), speed=(1.3, 3.0), lunge=3, bleed=(1, 3)),
}
# ---- the Warfare pack (Warfare_001/<Unit>/<Unit> Animations/<Unit>_<Colour>/<Unit>_<Anim>_<Colour>.png): five
# colours; rows (name, the file's animation, speed, loops), and more rows that aren't coloured (file, speed, loops) ----
WARFARE = os.path.join(HERE, "..", "tiny.swords.extract", "Warfare_001")
WAR = {
    "Berserker": dict(rows=[("Idle", "Idle", 10, True), ("Run", "Run", 12, True), ("Attack", "Attack", 16, False)]),
    "Horseman": dict(rows=[("Idle", "Idle", 10, True), ("Run", "Run", 12, True), ("Attack", "Attack", 14, False)],
                     more=[("Dust", "../Tiny Swords (Free Pack)/Tiny Swords (Free Pack)/Particle FX/Dust_01.png", 16, False),
                           ("Dust2", "../Tiny Swords (Free Pack)/Tiny Swords (Free Pack)/Particle FX/Dust_02.png", 16, False)]),
    "King": dict(rows=[("Idle", "Idle", 10, True), ("Run", "Run", 10, True), ("Attack", "Attack", 12, False)]),
    "Swordsman": dict(rows=[("Idle", "Idle", 10, True), ("Run", "Run", 12, True), ("Attack", "Attack", 16, False)]),
    # loaded it stands and rolls with a stone; it loads one (its windup), then flings it (its attack, released on
    # the 3rd frame); the stone spins through the air and shatters where it lands
    "Catapult": dict(rows=[("Idle", "IdleWithStone", 10, True), ("Run", "MoveWithStone", 8, True),
                           ("Windup", "LoadStone", 6, False), ("Attack", "FireStone", 12, False)],
                     more=[("BombSpin", "Catapult/Stone Projectile/Stone_Spinning.png", 12, True),
                           ("BombFuse", "Catapult/Stone Projectile/Stone_Idle.png", 1, True),
                           ("Boom", "Catapult/Stone Projectile/Stone Projectile.png", 14, False)]),
}
WAR_FIGHT = {
    # a whirlwind: after its windup it spins 3 seconds after you (its attack looping), cutting anyone near every half
    # second (a 5 s bleed), and nothing hurts it while it spins; then it rests a moment (your opening)
    "Berserker": dict(base=MELEE, strike=(3, 2), speed=(1.5, 3.6), lunge=0, bleed=(2, 5), breath=(3.0, 0.5), spin=1,
                      reach=(3, 1.4)),   # (starts spinning 3 from you; its spin cuts 1.4 around it)
    # a mounted charge: a long run in, bowled over
    # a cavalry charge from 6 away: through you (knocked aside), dust behind, round again
    "Horseman": dict(base=MELEE, strike=(4, 3), speed=(1.6, 4.0), lunge=0, reach=(6, 1.3), knock=3, charge=(0.7, 13, 0.35), trample=1),
    # a mini-boss of its colour: half again as big, a boss bar; he stands back (4 away) and calls in 2 of his soldiers when he
    # sees you, and again every 20 seconds: always a Monk (his healer) and 1 picked at random (his colour's
    # Swordsman, Lancer, Knight, Berserker, Bowman); hit, he teleports away (5-7 from you; at most every 5 s); cornered (or charging back
    # after a teleport), he swings: a 1.5 s stun, as the Paddle Shark's and the Troll's (no knock back);
    # and every 15 seconds he marks spots around you, and his catapults' stones (from off the screen)
    # crash down on them: 2 the first time, 2 more each time after (at most 30)
    # the Invincible King: his armour lets through a third of your damage; every swing sticks his weapon in the
    # ground 3 s (then his back takes 1.5x); his dash runs straight, and into a wall it dazes him 3 s (open all round);
    # his stunning hit leaves your controls backwards 2 s after the stun
    "King": dict(base=MELEE, strike=(3, 2), speed=(1.1, 2.6), lunge=0, stun=1.5, backwards=2, armour=(0.35, 3, 3), size=1.5, reach=(1.7, 1.9),
                 boss=(1.5, 6, 1.5), keep=4, summon=(20, 1, "Swordsman", "Lancer", "Knight", "Berserker", "Bowman"),
                 summonalso=("Monk",), barrage=(15, 2), blink=5),
    # heavy: a long windup, reaching far, hard: its overhand swing (frames 5-6) hits, and the thrust after it (a moment
    # into its follow-through) hits again
    "Swordsman": dict(base=MELEE, strike=(5, 2), speed=(1.0, 2.4), lunge=2, reach=(1.9, 2.0), stun=1, thrust=0.12),
    # slow, from far: a stone lobbed at where you are, landing hard in a small area (no fuse, no fire)
    "Catapult": dict(base=RANGED, strike=(0, 1), speed=(0.5, 1.0), lunge=0, ranged=(8, 5), bomb=(0, 1.2, 1),
                     windup=0.6, hit=2),
}
TROOP_OF = {f"{c} {t}": (t, c) for t in GRID for c in COLOURS}
TROOP_OF.update({f"{c} {t}": (t, c) for t in WAR for c in FREE_COLOURS})
TROOP_OF.update({f"{c} {t}": (t, c) for t in FREE for c in FREE_COLOURS})
FIGHT.update({name: (TROOPS.get(t) or FREE_FIGHT.get(t) or WAR_FIGHT[t]) for name, (t, c) in TROOP_OF.items()})


def grid_rows(name):
    """A Factions troop's rows: from its colour's sheet, the extra rows, then the Dead."""
    troop, colour = TROOP_OF[name]
    dead = Image.open(os.path.join(FACTIONS, DEAD[0])).convert("RGBA")
    if troop in FREE or troop in WAR:   # (an animation a file: frames in a row, square)
        rows = []
        if troop in WAR:
            files = [(anim, os.path.join(WARFARE, troop, f"{troop} Animations", f"{troop}_{colour}", f"{troop}_{file}_{colour}.png"), fps, loop)
                     for anim, file, fps, loop in WAR[troop]["rows"]]
            files += [(anim, os.path.join(WARFARE, path), fps, loop) for anim, path, fps, loop in WAR[troop].get("more", [])]
        else:
            files = [(anim, os.path.join(FREE_UNITS, f"{colour} Units", FREE[troop]["dir"], f"{file}.png"), fps, loop)
                     for anim, file, fps, loop in FREE[troop]["rows"]]
        for anim, path, fps, loop in files:
            if not os.path.exists(path):
                # (the pack's own misspellings: the red Berserker's "Berserker_Attac_Red.png") a near name, if just one
                head, tail = os.path.basename(path).rsplit("_", 1) if troop in WAR else (os.path.splitext(os.path.basename(path))[0], "")
                near = glob.glob(os.path.join(os.path.dirname(path), head[:-2] + "*" + (("_" + tail) if tail else ".png")))
                if len(near) == 1:
                    print(f"{name}: {anim} from {os.path.basename(near[0])} (the pack's spelling)")
                    path = near[0]
                else:
                    print(f"{name}: WARNING no {anim} ({os.path.basename(path)})")
                    continue
            img = Image.open(path).convert("RGBA")
            h = img.height
            rows.append((anim, fps, loop, [shrink(img.crop((i * h, 0, (i + 1) * h, h))) for i in range(img.width // h)]))
        if troop not in NO_SKULL:   # (a machine doesn't leave a skull: it's flattened and fades instead)
            rows.append(("Dead", 12, False, [shrink(dead.crop((i * DEAD[1], r * DEAD[1], (i + 1) * DEAD[1], (r + 1) * DEAD[1])))
                                             for r in range(2) for i in range(7)]))
        return rows
    g = GRID[troop]
    sheets = [f for f in glob.glob(os.path.join(FACTIONS, g["dir"], colour, "*.png")) if "NoArms" not in f]
    if not sheets:
        return []
    sheet = Image.open(sheets[0]).convert("RGBA")
    c = g["cell"]

    def cut(img, cell, row, n):
        return [shrink(img.crop((i * cell, row * cell, (i + 1) * cell, (row + 1) * cell))) for i in range(n)]

    rows = []
    for r in g["rows"]:
        anim, row, n, fps, loop = r[:5]
        frames = cut(sheet, c, row, n)
        if len(r) > 5 and r[5]:
            frames.reverse()
        rows.append((anim, fps, loop, frames))
    for anim, path, cell, row, n, fps, loop in g.get("more", []):
        rows.append((anim, fps, loop, cut(Image.open(os.path.join(FACTIONS, path)).convert("RGBA"), cell, row, n)))
    rows.append(("Dead", 12, False, cut(dead, DEAD[1], 0, 7) + cut(dead, DEAD[1], 1, 7)))
    return rows


def shrink(frame):
    """A third of the size; the faint dark ground shadow left out."""
    w, h = frame.size
    src = frame.load()
    out = Image.new("RGBA", (w // SHRINK, h // SHRINK))
    dst = out.load()
    for oy in range(h // SHRINK):
        for ox in range(w // SHRINK):
            r = g = b = a = 0.0
            for dy in range(SHRINK):
                for dx in range(SHRINK):
                    pr, pg, pb, pa = src[ox * SHRINK + dx, oy * SHRINK + dy]
                    if pa < 100 and pr < 60 and pg < 60 and pb < 80:
                        continue  # (the shadow)
                    k = pa / 255.0
                    r += pr * k; g += pg * k; b += pb * k; a += k
            alpha = a / (SHRINK * SHRINK)
            if a <= 0 or alpha < 0.35:
                continue
            dst[ox, oy] = (int(r / a), int(g / a), int(b / a), 255 if alpha > 0.8 else int(alpha * 255))
    return out


def find(name, anim):
    """Its sheet for that animation: in its own folder, or in Extra."""
    own = os.path.join(PACK, name, f"{name}_{anim}.png")
    if os.path.exists(own):
        return own
    extra = glob.glob(os.path.join(PACK, "Extra", "*", f"{name}_{anim}.png"))
    return extra[0] if extra else None


def pair(fight, key, default=None):
    v = fight.get(key, default)
    return None if v is None else f"{key} " + " ".join(str(x) for x in v) + "\n"


def build(name):
    fight = FIGHT.get(name, {})
    art = fight.get("art", name)
    rows = grid_rows(name) if name in TROOP_OF else []
    wanted = [] if rows else [(anim, fps, loop, find(art, anim)) for anim, fps, loop in ANIMS]
    wanted += [(anim, fps, loop, os.path.join(PACK, "..", path)) for anim, path, fps, loop in EXTRA_ROWS.get(art, [])] if not rows else []
    for anim, fps, loop, path in wanted:
        if not path or not os.path.exists(path):
            continue
        sheet = Image.open(path).convert("RGBA")
        size = sheet.height
        for layer in LAYERS.get((art, anim), []):
            extra = find(art, layer)
            if extra:
                sheet.alpha_composite(Image.open(extra).convert("RGBA").crop((0, 0, sheet.width, sheet.height)))
        frames = [shrink(sheet.crop((i * size, 0, (i + 1) * size, size))) for i in range(sheet.width // size)]
        rows.append((anim, fps, loop, frames))
    if not rows:
        print(f"{name}: no sheets in {os.path.join(PACK, art)}")
        return False
    cell = max(f.width for r in rows for f in r[3])
    out = Image.new("RGBA", (cell * max(len(r[3]) for r in rows), cell * len(rows)))
    for y, (_, _, _, frames) in enumerate(rows):
        for x, f in enumerate(frames):
            out.paste(f, (x * cell + (cell - f.width) // 2, y * cell + (cell - f.height) // 2))
    # the pivot: the middle of the creature in its first idle frame (x from the left, y from the bottom, 0..1)
    first = out.crop((0, 0, cell, cell))
    box = first.getbbox() or (0, 0, cell, cell)
    px, py = (box[0] + box[2]) / 2 / cell, 1 - (box[1] + box[3]) / 2 / cell
    # one with a shell (guard frames): across, the middle of its shell (its last guard frame: all shell), so the head
    # sticks out in front of where it stands rather than the shell sitting behind it
    guard = next((y for y, r in enumerate(rows) if r[0] == "Guard_In"), None)
    if guard is not None:
        n = len(rows[guard][3])
        shell = out.crop(((n - 1) * cell, guard * cell, n * cell, (guard + 1) * cell)).getbbox()
        if shell:
            px = (shell[0] + shell[2]) / 2 / cell
    os.makedirs(OUT, exist_ok=True)
    out.save(os.path.join(OUT, f"{name}.png"))
    attack = next((r for r in rows if r[0] in ("Attack", "Attack Fast", "Shoot", "Throw")), rows[-1])
    lines = [f"base {fight.get('base', FLYER)}\n", f"cell {cell}\nppu {PPU}\npivot {px:.3f} {py:.3f}\nfeet {1 - box[3] / cell:.3f}\n",
             pair(fight, "strike", (max(1, len(attack[3]) // 2), 1)), pair(fight, "speed", (1.4, 3.4)),
             f"lunge {fight.get('lunge', 9)}\n"]
    for key in ("burn", "bleed", "poison", "breath", "block", "ranged", "bomb", "shot", "reach", "heal", "summon", "summonalso", "barrage", "armour", "charge", "slow", "split", "spread", "venom", "strong", "bash"):
        lines.append(pair(fight, key))
    if "knock" in fight or "stun" in fight:
        lines.append(f"knock {fight.get('knock', 0)} {fight.get('stun', 0)}\n")
    for key in ("windup", "hit", "size", "trap", "ambush", "kamikaze", "keep", "spin", "blink", "backwards", "thrust", "disarm", "silence", "root", "blind", "fists", "deathbomb", "thorns", "mark", "polymorph", "stealth", "bounce", "pull", "trample", "pound"):
        if key in fight:
            lines.append(f"{key} {fight[key]}\n")
    if "boss" in fight:
        lines.append(f"boss {fight['boss'][0]} {fight['boss'][1]} {fight['boss'][2]}\n")
    for y, (anim, fps, loop, frames) in enumerate(rows):
        lines.append(f"anim {anim.replace(' ', '_')} {y} {len(frames)} {fps} {'loop' if loop else 'once'}\n")
    with open(os.path.join(OUT, f"{name}.txt"), "w", encoding="utf-8") as t:
        t.write("".join(l for l in lines if l))
    print(f"{name}: {out.width}x{out.height} (cell {cell}), " + ", ".join(f"{r[0]} {len(r[3])}" for r in rows) + f", pivot {px:.2f} {py:.2f}")
    return True


if __name__ == "__main__":
    names = sys.argv[1:] or list(FIGHT)
    ok = [build(n) for n in names]
    sys.exit(0 if all(ok) else 1)
