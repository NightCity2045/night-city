"""Compose native RSI frames for Gate 7 art QA. This is not an engine screenshot.

SPDX-FileCopyrightText: 2026 Astro
SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
"""
from pathlib import Path
import json
from PIL import Image, ImageChops, ImageDraw

ROOT = Path(__file__).resolve().parents[3]
TEXTURES = ROOT / "Resources/Textures"
OUTPUT = ROOT / "TestResults/CyberwareGate7/visual-contact-sheet.png"


def frame(path, direction):
    image = Image.open(TEXTURES / path).convert("RGBA")
    x, y = direction % (image.width // 32) * 32, direction // (image.width // 32) * 32
    return image.crop((x, y, x + 32, y + 32))


def tint(image, color):
    return ImageChops.multiply(image, Image.new("RGBA", image.size, color))


def main():
    rows = ["Industrial chrome", "Clinical panels", "Synthetic skin", "Torn skin",
            "Clothing overlap", "Left arm detached", "Civilian Borg", "Clinical Borg", "Mantis deployed"]
    directions = ["South", "North", "East", "West"]
    sheet = Image.new("RGB", (1120, 70 + len(rows) * 190), "#17212B")
    draw = ImageDraw.Draw(sheet)
    draw.text((18, 12), "GATE 7 / RSI ASSET COMPOSITION / NOT IN-GAME", fill="#EDF5FA")
    for i, direction in enumerate(directions):
        draw.text((225 + i * 220, 45), direction, fill="#9EDCE8")
    skin, metal = "#C0967F", "#A8BAC9"
    for row, label in enumerate(rows):
        draw.text((12, 110 + row * 190), label, fill="#D2E0EB")
        for direction in range(4):
            body = Image.new("RGBA", (32, 32))
            for name in ["torso_m", "head_m", "r_arm", "l_arm", "r_leg", "l_leg"]:
                if row == 5 and name == "l_arm":
                    continue
                mechanical = row in (6, 7) or name in {"l_arm", "r_arm", "l_leg", "r_leg"} and row != 2
                path = f'_NC/Mobs/Cyberware/parts.rsi/{name}.png' if mechanical else f'Mobs/Species/Human/parts.rsi/{name}.png'
                color = "#EFF5FF" if mechanical and (row == 1 or row == 7 and name != 'head_m') else "#B87872" if mechanical and row == 3 else metal if mechanical else skin
                body.alpha_composite(tint(frame(path, direction), color))
            if row in (4, 8):
                body.alpha_composite(frame('Clothing/Uniforms/Jumpsuit/detective.rsi/equipped-INNERCLOTHING.png', direction))
            for name in ["l_foot", "r_foot", "l_hand", "r_hand"]:
                if row == 5 and name == "l_hand":
                    continue
                path = f'_NC/Mobs/Cyberware/parts.rsi/{name}.png' if row in (6, 7) and name.endswith('hand') else f'Mobs/Species/Human/parts.rsi/{name}.png'
                body.alpha_composite(tint(frame(path, direction), metal if row in (6, 7) else skin))
            if row == 8:
                body.alpha_composite(frame('Objects/Weapons/Melee/katana.rsi/inhand-right.png', direction))
            scaled = body.resize((160, 160), Image.Resampling.NEAREST)
            sheet.paste(scaled, (200 + direction * 220, 70 + row * 190), scaled)
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(OUTPUT)
    OUTPUT.with_suffix('.json').write_text(json.dumps({"kind": "asset-composition-not-engine", "rows": rows,
        "directions": directions, "cases": len(rows) * 4}, indent=2) + '\n', encoding='utf-8')
    print(OUTPUT)


if __name__ == '__main__':
    main()
