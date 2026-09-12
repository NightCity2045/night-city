# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

"""Build cropped, transparent vehicle HUD textures from the approved source atlas."""

import json
from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[3]
SOURCE_DIRECTORY = ROOT / "Docs/Design/SourceArt/VehicleHud"
SOURCE = SOURCE_DIRECTORY / "vehicle-driver-hud-atlas-source.png"
SHIFTER_SOURCE = SOURCE_DIRECTORY / "vehicle-shifter-source.png"
WHEEL_SOURCE = SOURCE_DIRECTORY / "vehicle-steering-wheel-source.png"
OUTPUT = ROOT / "Resources/Textures/_NC/Vehicle/Hud"

ASSETS = {
    "instrument-cluster.png": (382, 110, 1265, 455),
    "warning-lights.png": (520, 455, 1130, 585),
    "pedals.png": (545, 575, 1175, 865),
}

MAX_SIZES = {
    "instrument-cluster.png": (768, 300),
    "warning-lights.png": (420, 90),
    "pedals.png": (420, 194),
    "shifter-console.png": (300, 300),
}


def save_asset(image: Image.Image, name: str) -> None:
    image.thumbnail(MAX_SIZES[name], Image.Resampling.LANCZOS)
    image.save(OUTPUT / name, optimize=True)


def main() -> None:
    source = Image.open(SOURCE).convert("RGBA")
    OUTPUT.mkdir(parents=True, exist_ok=True)

    for name, box in ASSETS.items():
        image = source.crop(box)
        alpha_bounds = image.getchannel("A").getbbox()
        if alpha_bounds is not None:
            image = image.crop(alpha_bounds)
        save_asset(image, name)

    shifter = Image.open(SHIFTER_SOURCE).convert("RGBA")
    alpha_bounds = shifter.getchannel("A").getbbox()
    if alpha_bounds is not None:
        shifter = shifter.crop(alpha_bounds)
    save_asset(shifter, "shifter-console.png")

    wheel = Image.open(WHEEL_SOURCE).convert("RGBA")
    alpha_bounds = wheel.getchannel("A").getbbox()
    if alpha_bounds is not None:
        wheel = wheel.crop(alpha_bounds)
    wheel.thumbnail((360, 360), Image.Resampling.LANCZOS)
    frame_count = 17
    frame_size = 360
    strip = Image.new("RGBA", (frame_size * frame_count, frame_size))
    centered = Image.new("RGBA", (frame_size, frame_size))
    centered.alpha_composite(wheel, ((frame_size - wheel.width) // 2, (frame_size - wheel.height) // 2))
    for frame in range(frame_count):
        angle = 360 - frame * 720 / (frame_count - 1)
        strip.alpha_composite(centered.rotate(angle, Image.Resampling.BICUBIC), (frame * frame_size, 0))

    rsi = OUTPUT / "steering-wheel.rsi"
    rsi.mkdir(parents=True, exist_ok=True)
    strip.save(rsi / "wheel.png", optimize=True)
    metadata = {
        "version": 1,
        "license": "CC-BY-NC-SA-4.0",
        "copyright": "Original AI-assisted HUD artwork commissioned by Astro, 2026.",
        "size": {"x": frame_size, "y": frame_size},
        "states": [{"name": "wheel", "delays": [[1] * frame_count]}],
    }
    (rsi / "meta.json").write_text(json.dumps(metadata, indent=4) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
