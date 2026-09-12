# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

"""Targeted integrity checks for generated Night City vehicle RSIs."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from PIL import Image


EXPECTED_DIRECTIONS = 1
EXPECTED_SIZE = (128, 128)
EXPECTED_STATES = {
    "shadow",
    "body",
    "damage-light",
    "damage-heavy",
    "headlights",
    "brakes",
    "reverse",
    "hood-open",
    "wheel-damage",
    "tuning-street",
    "smoke",
    "fire",
}


def validate(rsi: Path) -> list[str]:
    errors: list[str] = []
    metadata = json.loads((rsi / "meta.json").read_text(encoding="utf-8-sig"))
    size = (metadata["size"]["x"], metadata["size"]["y"])
    if size != EXPECTED_SIZE:
        errors.append(f"frame size is {size}, expected {EXPECTED_SIZE}")

    states = {state["name"]: state for state in metadata["states"]}
    if set(states) != EXPECTED_STATES:
        errors.append(f"state set differs: {sorted(set(states) ^ EXPECTED_STATES)}")

    for name, state in states.items():
        path = rsi / f"{name}.png"
        if not path.exists():
            errors.append(f"missing {path.name}")
            continue
        with Image.open(path) as image:
            if image.mode != "RGBA":
                errors.append(f"{path.name} mode is {image.mode}, expected RGBA")
            directions = state.get("directions", 1)
            delays = state.get("delays")
            frames = sum(len(values) for values in delays) if delays else directions
            capacity = image.width // size[0] * (image.height // size[1])
            if image.width % size[0] or image.height % size[1] or capacity < frames:
                errors.append(f"{path.name} dimensions {image.size} cannot hold {frames} frames")
            if name not in {"smoke", "fire"} and directions != EXPECTED_DIRECTIONS:
                errors.append(f"{name} has {directions} directions, expected {EXPECTED_DIRECTIONS}")
            if name not in {"smoke", "fire"} and image.size != EXPECTED_SIZE:
                errors.append(f"{path.name} is {image.size}, expected one {EXPECTED_SIZE} frame")
            if image.getchannel("A").getbbox() is None:
                errors.append(f"{path.name} is fully transparent")

    png_names = {path.stem for path in rsi.glob("*.png")}
    if png_names != set(states):
        errors.append(f"PNG set differs from metadata: {sorted(png_names ^ set(states))}")
    return errors


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("directories", nargs="+", type=Path)
    args = parser.parse_args()
    failed = False
    for directory in args.directories:
        errors = validate(directory)
        if errors:
            failed = True
            for error in errors:
                print(f"{directory}: {error}")
        else:
            print(f"OK {directory}")
    return int(failed)


if __name__ == "__main__":
    raise SystemExit(main())
