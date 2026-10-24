"""Pack an imagegen 4x4 limb atlas into the existing SS14 anatomical registration.

SPDX-FileCopyrightText: 2026 Astro
SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

Only crops, nearest-neighbor resampling and RSI packing are performed. No source artwork is painted.
Columns: l_arm, r_arm, l_leg, r_leg. Rows: south, north, east, west.
"""
import argparse
import json
from pathlib import Path

from PIL import Image


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[3])
    parser.add_argument("--parts", nargs=4, default=["l_arm", "r_arm", "l_leg", "r_leg"])
    args = parser.parse_args()
    source = Image.open(args.source).convert("RGBA")
    assert abs(source.width - source.height) <= 8, "Expected a nearly square 4x4 atlas"
    output = args.root / "Resources/Textures/_NC/Mobs/Cyberware/parts.rsi"
    output.mkdir(parents=True, exist_ok=True)
    meta_path = output / "meta.json"
    states = json.loads(meta_path.read_text(encoding="utf-8"))["states"] if meta_path.exists() else []
    states = [state for state in states if state["name"] not in args.parts]
    for column, name in enumerate(args.parts):
        reference = Image.open(args.root / f"Resources/Textures/Mobs/Species/Human/parts.rsi/{name}.png").convert("RGBA")
        packed = Image.new("RGBA", (128, 32))
        for direction in range(4):
            cell = source.crop((round(column * source.width / 4), round(direction * source.height / 4),
                                round((column + 1) * source.width / 4), round((direction + 1) * source.height / 4)))
            # Ignore near-transparent atlas noise when finding the artwork's registration bounds.
            # Cropping/resampling still retains the original generated alpha values.
            bounds = cell.getchannel("A").point(lambda value: 255 if value >= 128 else 0).getbbox()
            assert bounds is not None, (name, direction)
            columns = reference.width // 32
            rx, ry = direction % columns * 32, direction // columns * 32
            target = reference.crop((rx, ry, rx + 32, ry + 32)).getbbox()
            if target is None:  # The far-side arm is intentionally occluded in the native side view.
                continue
            normalized = cell.crop(bounds).resize((target[2] - target[0], target[3] - target[1]), Image.Resampling.NEAREST)
            packed.alpha_composite(normalized, (direction * 32 + target[0], target[1]))
        packed.save(output / f"{name}.png")
        states.append({"name": name, "directions": 4})
    metadata = {
        "version": 1,
        "license": "CC-BY-SA-3.0",
        "copyright": "Generated with OpenAI imagegen for Night City 2045, curated and packed by Astro, 2026. Anatomical registration follows the existing Human parts.rsi.",
        "size": {"x": 32, "y": 32},
        "states": states,
    }
    (output / "meta.json").write_text(json.dumps(metadata, indent=4) + "\n", encoding="utf-8")
    print(f"Packed {len(states)} states, {len(states) * 4} directional frames: {output}")


if __name__ == "__main__":
    main()
