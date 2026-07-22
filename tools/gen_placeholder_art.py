#!/usr/bin/env python3
"""
Generates the placeholder pixel art used until real assets land.

Everything here is deliberately simple and reproducible: run the script and
the atlas is rebuilt byte-for-byte. Real art will replace these files without
touching any code, because the tile layout is what the TileSet depends on.

    python3 tools/gen_placeholder_art.py

Atlas layout: one row per TileType (matching the enum order), four variants
per row so ground doesn't read as a flat colour field.
"""

from PIL import Image, ImageDraw
import os

TILE = 32
VARIANTS = 4
OUT = os.path.join(os.path.dirname(__file__), "..", "apps", "client", "art")

# Deliberately limited palette: base, light speckle, dark speckle.
# Keeping every tile inside one palette is what makes mixed terrain read as
# a single world rather than assorted assets.
GROUND = [
    ("deep_water", (24, 52, 87), (32, 66, 105), (18, 42, 72)),
    ("water",      (47, 102, 144), (60, 122, 166), (38, 86, 124)),
    ("sand",       (214, 193, 128), (226, 208, 150), (194, 172, 110)),
    ("grass",      (74, 124, 64), (88, 140, 74), (62, 106, 54)),
    ("forest",     (58, 95, 52), (70, 110, 62), (46, 78, 42)),
    ("rock",       (110, 106, 99), (128, 124, 116), (92, 88, 82)),
]


def rng(seed):
    """Tiny deterministic PRNG so regenerating the art never shifts pixels."""
    state = seed & 0xFFFFFFFF

    def nxt():
        nonlocal state
        state = (state * 1664525 + 1013904223) & 0xFFFFFFFF
        return state >> 16

    return nxt


def speckle(draw, ox, oy, base, light, dark, seed, density=14):
    """Scatters two-tone noise so a tile has texture instead of being flat."""
    draw.rectangle([ox, oy, ox + TILE - 1, oy + TILE - 1], fill=base)
    rand = rng(seed)
    for _ in range(density):
        x = ox + rand() % TILE
        y = oy + rand() % TILE
        draw.point((x, y), fill=light if rand() % 2 else dark)


def build_terrain_atlas():
    img = Image.new("RGBA", (TILE * VARIANTS, TILE * len(GROUND)), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    for row, (name, base, light, dark) in enumerate(GROUND):
        for col in range(VARIANTS):
            ox, oy = col * TILE, row * TILE
            speckle(draw, ox, oy, base, light, dark, seed=row * 977 + col * 131)

            # Water gets horizontal ripples; stone gets angular cracks. Cheap
            # cues that stop every surface reading the same way.
            if name in ("water", "deep_water"):
                rand = rng(row * 31 + col)
                for _ in range(3):
                    y = oy + rand() % TILE
                    x = ox + rand() % (TILE - 12)
                    draw.line([x, y, x + 6 + rand() % 6, y], fill=light)
            if name == "rock":
                rand = rng(row * 57 + col)
                x = ox + 6 + rand() % 12
                y = oy + 6 + rand() % 12
                draw.line([x, y, x + 8, y + 6], fill=dark)

    img.save(os.path.join(OUT, "terrain.png"))
    return img.size


def shadow(draw, cx, bottom, rx, ry):
    """Contact shadow — what stops a sprite looking pasted onto the ground."""
    draw.ellipse([cx - rx, bottom - ry, cx + rx, bottom + ry], fill=(0, 0, 0, 70))


def build_tree(index, height, canopy, light, dark, trunk_x=14):
    """
    32 wide, variable height: one tile of footprint, two to three of height.
    Three variants exist because identical repeated sprites are the loudest
    signal that a world is procedurally filled rather than built.
    """
    img = Image.new("RGBA", (32, height), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    bottom = height - 2
    shadow(draw, 16, bottom + 2, 11, 4)

    trunk_top = height - 22
    draw.rectangle([trunk_x, trunk_top, trunk_x + 4, bottom], fill=(84, 58, 39))
    draw.rectangle([trunk_x, trunk_top, trunk_x + 1, bottom], fill=(62, 42, 28))

    cx, cy = 16, trunk_top - height // 5
    rx, ry = 14, height // 3

    draw.ellipse([cx - rx, cy - ry, cx + rx, cy + ry], fill=dark)
    draw.ellipse([cx - rx + 1, cy - ry, cx + rx - 3, cy + ry - 3], fill=canopy)
    draw.ellipse([cx - rx + 2, cy - ry + 1, cx - 1, cy + 2], fill=light)

    rand = rng(1000 + index * 77)
    for _ in range(60):
        x = cx - rx + rand() % (2 * rx)
        y = cy - ry + rand() % (2 * ry)
        if ((x - cx) / rx) ** 2 + ((y - cy) / ry) ** 2 <= 0.9:
            draw.point((x, y), fill=light if rand() % 3 else dark)

    img.save(os.path.join(OUT, f"tree_{index}.png"))


def build_trees():
    # Three silhouettes and three greens, so a forest has variation in both
    # height and colour rather than one stamp repeated.
    build_tree(0, 64, (44, 88, 48), (66, 120, 66), (26, 54, 32))
    build_tree(1, 56, (52, 96, 52), (74, 128, 72), (30, 60, 34), trunk_x=15)
    build_tree(2, 72, (38, 78, 44), (58, 106, 60), (22, 46, 28), trunk_x=13)


def build_boulder():
    """32x48: a rock outcrop with a visible front face."""
    img = Image.new("RGBA", (32, 48), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    shadow(draw, 16, 45, 12, 4)
    draw.polygon([(4, 44), (7, 22), (16, 14), (26, 24), (28, 44)], fill=(122, 118, 110))
    draw.polygon([(7, 22), (16, 14), (18, 26), (10, 32)], fill=(146, 142, 133))
    draw.polygon([(18, 26), (26, 24), (28, 44), (18, 44)], fill=(96, 92, 86))
    draw.line([16, 20, 13, 34], fill=(84, 80, 75))

    img.save(os.path.join(OUT, "boulder.png"))


def build_player():
    """16x24 placeholder character, anchored at the feet."""
    img = Image.new("RGBA", (16, 24), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    shadow(draw, 8, 22, 6, 2)
    draw.rectangle([5, 11, 10, 20], fill=(178, 88, 62))    # tunic
    draw.rectangle([5, 20, 6, 22], fill=(60, 48, 44))      # boots
    draw.rectangle([9, 20, 10, 22], fill=(60, 48, 44))
    draw.ellipse([4, 3, 11, 12], fill=(226, 190, 156))     # head
    draw.ellipse([4, 2, 11, 8], fill=(84, 60, 42))         # hair
    draw.point((6, 8), fill=(40, 32, 28))
    draw.point((9, 8), fill=(40, 32, 28))

    img.save(os.path.join(OUT, "player.png"))


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    size = build_terrain_atlas()
    build_trees()
    build_boulder()
    build_player()
    print(f"wrote terrain atlas {size}, tree, boulder, player to {os.path.normpath(OUT)}")
