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
Then `dotnet build spike/MonsterArt` packs the sheets into MonsterArt.dll (into the game's plugins/MonsterArt).
None of it is in git or in the mod pack: the pack's licence forbids passing its art on, even changed.
"""
import glob
import os
import sys

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
PACK = os.path.join(HERE, "..", "tiny.swords.extract", "Enemy Pack")
OUT = os.path.join(HERE, "sheets")
SHRINK, PPU = 3, 32
# each animation's name in the pack, its speed (frames a second), whether it loops
ANIMS = [("Idle", 10, True), ("Move", 10, True), ("Walk", 10, True), ("Run", 10, True), ("Attack", 16, False),
         ("Attack Fast", 16, False), ("Shoot", 14, False), ("Throw", 14, False), ("Guard_In", 12, False),
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
    "Harpoon Shark": [("Shot", "Enemy Pack/Harpoon Shark/Harpoon.png", 1, True)],
    "Hex Shaman": [("Shot", "Enemy Pack/Hex Shaman/Hex Shaman_Projectile.png", 12, True),
                   ("ShotHit", "Enemy Pack/Hex Shaman/Hex Shaman_Explosion.png", 18, False)],
    "Slingshot Gnome": [("Shot", "Enemy Pack/Slingshot Gnome/Acorn_Projectile.png", 14, True)],
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
    "Bumblebee": dict(base=FLYER, strike=(6, 2), speed=(1.4, 3.4), lunge=9),
    # the bumblebees' queen: bigger, a poison sting, a boss bar
    "Queen Bee": dict(art="Bumblebee", base=FLYER, strike=(6, 2), speed=(1.4, 3.0), lunge=9, poison=(3, 5), size=1.6,
                      reach=(2.0, 1.3), boss=(1.6, 6, 1.4)),
    "Giant Bat": dict(base=FLYER, strike=(4, 2), speed=(1.4, 3.4), lunge=9, bleed=(2, 5)),
    "Turtle": dict(base=MELEE, strike=(5, 2), speed=(0.7, 1.6), lunge=0),
    "Paddle Shark": dict(base=MELEE, strike=(3, 2), speed=(1.2, 2.6), lunge=0, knock=3, stun=2),
    # a boss: its own windup (a second: you see it coming), its swing landing on the attack row's 3rd frame, then
    # its recovery (it stands there tired); drawn 1.25x, reaching further
    "Troll": dict(base=MELEE, strike=(0, 1), speed=(0.9, 1.8), lunge=0, knock=3, stun=2, windup=1.0, hit=2, size=1.25,
                  reach=(1.8, 2.6), boss=(2.0, 10, 1.6)),
    "Spider": dict(base=SPIDER, strike=(4, 2), speed=(1.3, 3.0), lunge=6, trap=8),
    "Bomb Fish": dict(base=RANGED, strike=(4, 1), speed=(1.2, 2.4), lunge=0, ranged=(5, 2.5), bomb=(1.2, 1.3)),
    "Minotaur": dict(base=MELEE, strike=(5, 3), speed=(0.9, 2.0), lunge=0, reach=(1.5, 1.9), block=(1.5, 4)),
    "Panda": dict(base=MELEE, strike=(5, 6), speed=(1.3, 3.0), lunge=0, block=(1.2, 3)),
    "Imp": dict(base=FLYER, strike=(0, 1), speed=(1.4, 3.0), lunge=0, reach=(1.5, 1.8), windup=0.4, breath=(1.0, 0.3),
                burn=(3, 4)),
    # rears up, then a swipe with a short charge
    "Bear": dict(base=MELEE, strike=(6, 2), speed=(1.2, 3.0), lunge=5),
    # the bears' biggest: bigger, its swipe knocks you back, a boss bar
    "Great Bear": dict(art="Bear", base=MELEE, strike=(6, 2), speed=(1.0, 2.6), lunge=4, knock=2.5, size=1.4,
                       reach=(1.9, 1.4), boss=(1.6, 5, 1.3)),
    "Gnoll": dict(base=RANGED, strike=(5, 1), speed=(1.1, 2.6), lunge=0, ranged=(5, 2.5), shot=(8, 0)),
    "Gnome": dict(base=MELEE, strike=(3, 2), speed=(1.6, 3.4), lunge=4),
    "Harpoon Shark": dict(base=RANGED, strike=(4, 1), speed=(1.1, 2.4), lunge=0, ranged=(6, 3), shot=(11, 1, 45)),
    "Hex Shaman": dict(base=RANGED, strike=(5, 1), speed=(1.0, 2.2), lunge=0, ranged=(6, 3), shot=(6, 0), poison=(2, 4)),
    # flares its frill: a short scare (stunned a moment), close all round
    "Lizard": dict(base=MELEE, strike=(2, 5), speed=(1.3, 3.0), lunge=0, reach=(1.2, 1.6), stun=1),
    "Skull": dict(base=MELEE, strike=(2, 3), speed=(1.0, 2.4), lunge=3, block=(1.0, 4)),
    "Slingshot Gnome": dict(base=RANGED, strike=(6, 1), speed=(1.4, 3.0), lunge=0, ranged=(6, 3), shot=(10, 0)),
    "Snake": dict(base=MELEE, strike=(3, 3), speed=(1.2, 3.2), lunge=6, poison=(2, 4)),
    "Spear Goblin": dict(base=MELEE, strike=(2, 2), speed=(1.3, 3.2), lunge=0, reach=(1.4, 1.7)),
    "Thief": dict(base=MELEE, strike=(2, 3), speed=(1.6, 3.8), lunge=8, bleed=(1, 3)),
    "Torch Goblin": dict(base=MELEE, strike=(2, 3), speed=(1.3, 3.0), lunge=0, burn=(2, 3)),
}


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
    rows = []
    wanted = [(anim, fps, loop, find(art, anim)) for anim, fps, loop in ANIMS]
    wanted += [(anim, fps, loop, os.path.join(PACK, "..", path)) for anim, path, fps, loop in EXTRA_ROWS.get(art, [])]
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
    for key in ("burn", "bleed", "poison", "breath", "block", "ranged", "bomb", "shot", "reach"):
        lines.append(pair(fight, key))
    if "knock" in fight or "stun" in fight:
        lines.append(f"knock {fight.get('knock', 0)} {fight.get('stun', 0)}\n")
    for key in ("windup", "hit", "size", "trap"):
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
