# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

"""Build a continuously rotating one-frame vehicle RSI from transparent top-down art."""

from __future__ import annotations

import argparse
import json
import random
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageEnhance, ImageFilter

FRAME = 128
FORWARD = (0.0, -1.0)
STATIC_STATES = (
    "shadow", "body", "damage-light", "damage-heavy", "headlights", "brakes", "reverse",
    "hood-open", "wheel-damage", "tuning-street",
)


def oriented_box(center: tuple[float, float], forward: tuple[float, float], length: float, width: float):
    fx, fy = forward
    px, py = -fy, fx
    cx, cy = center
    return [
        (cx - fx * length / 2 - px * width / 2, cy - fy * length / 2 - py * width / 2),
        (cx - fx * length / 2 + px * width / 2, cy - fy * length / 2 + py * width / 2),
        (cx + fx * length / 2 + px * width / 2, cy + fy * length / 2 + py * width / 2),
        (cx + fx * length / 2 - px * width / 2, cy + fy * length / 2 - py * width / 2),
    ]


def clip_to_body(overlay: Image.Image, body: Image.Image) -> Image.Image:
    overlay.putalpha(ImageChops.multiply(overlay.getchannel("A"), body.getchannel("A")))
    return overlay


def prepare_frame(source: Image.Image, target_extent: int) -> Image.Image:
    alpha = source.getchannel("A").point(lambda value: 0 if value < 8 else value)
    box = alpha.getbbox()
    if box is None:
        raise ValueError("top-down source has no visible pixels")
    source.putalpha(alpha)
    source = source.crop(box)
    scale = target_extent / max(source.size)
    size = (max(1, round(source.width * scale)), max(1, round(source.height * scale)))
    source = source.resize(size, Image.Resampling.LANCZOS)
    alpha = source.getchannel("A").point(lambda value: 0 if value < 8 else value)
    rgb = source.convert("RGB").filter(ImageFilter.UnsharpMask(radius=0.65, percent=75, threshold=2))
    rgb = ImageEnhance.Contrast(rgb).enhance(1.025)
    source = rgb.convert("RGBA")
    source.putalpha(alpha)
    frame = Image.new("RGBA", (FRAME, FRAME))
    frame.alpha_composite(source, ((FRAME - source.width) // 2, (FRAME - source.height) // 2))
    return frame


def silhouette_extents(body: Image.Image, direction: tuple[float, float]) -> tuple[float, float]:
    fx, fy = direction
    px, py = -fy, fx
    points = [(x - FRAME / 2, y - FRAME / 2)
              for y in range(FRAME) for x in range(FRAME) if body.getpixel((x, y))[3] >= 32]
    return (max(x * fx + y * fy for x, y in points), max(abs(x * px + y * py) for x, y in points))


def damage_layer(body: Image.Image, heavy: bool, seed: int) -> Image.Image:
    output = Image.new("RGBA", body.size)
    draw = ImageDraw.Draw(output)
    rng = random.Random(seed)
    fx, fy = FORWARD
    px, py = -fy, fx
    for _ in range(11 if heavy else 5):
        along, across = rng.uniform(-30, 34), rng.uniform(-27, 27)
        x, y = 64 + fx * along + px * across, 64 + fy * along + py * across
        points = [(x, y)]
        for _ in range(rng.randint(2, 4)):
            x += rng.uniform(-4, 4) + fx * rng.uniform(1, 3)
            y += rng.uniform(-4, 4) + fy * rng.uniform(1, 3)
            points.append((x, y))
        draw.line(points, fill=(17, 13, 15, 225), width=2 if heavy else 1)
        draw.line([(x + 1, y) for x, y in points], fill=(174, 111, 69, 210), width=1)
    if heavy:
        for _ in range(4):
            x, y, radius = 64 + rng.uniform(-27, 27), 64 + rng.uniform(-31, 31), rng.randint(3, 7)
            draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=(25, 18, 18, 150))
            draw.arc((x - radius, y - radius, x + radius, y + radius), 15, 235,
                     fill=(192, 124, 76, 235), width=1)
    return clip_to_body(output, body)


def lamp_layer(body: Image.Image, rear: bool, reverse: bool = False) -> Image.Image:
    output = Image.new("RGBA", (FRAME, FRAME))
    draw = ImageDraw.Draw(output)
    fx, fy = FORWARD
    if rear:
        fx, fy = -fx, -fy
    px, py = -fy, fx
    forward, lateral = silhouette_extents(body, (fx, fy))
    color = (245, 245, 218, 255) if reverse else ((255, 42, 30, 255) if rear else (255, 221, 112, 255))
    glow = (255, 255, 220, 95) if reverse else ((255, 28, 16, 90) if rear else (255, 184, 50, 90))
    for side in (-1, 1):
        x = 64 + fx * max(2, forward - 3) + px * lateral * 0.43 * side
        y = 64 + fy * max(2, forward - 3) + py * lateral * 0.43 * side
        draw.ellipse((x - 4, y - 4, x + 4, y + 4), fill=glow)
        draw.line((x - px * 3, y - py * 3, x + px * 3, y + py * 3), fill=color, width=2)
    return output


def hood_layer(body: Image.Image) -> Image.Image:
    output = Image.new("RGBA", (FRAME, FRAME))
    draw = ImageDraw.Draw(output)
    forward, lateral = silhouette_extents(body, FORWARD)
    center = (64, 64 - forward * 0.58)
    bay = oriented_box(center, FORWARD, forward * 0.42, lateral * 1.05)
    panel = oriented_box((center[0], center[1] + forward * 0.18), FORWARD,
                         max(4, forward * 0.14), lateral * 1.1)
    draw.polygon(bay, fill=(8, 11, 13, 250), outline=(118, 81, 58, 255))
    draw.polygon(panel, fill=(32, 34, 37, 255), outline=(169, 93, 57, 255))
    return output


def wheel_damage_layer(body: Image.Image) -> Image.Image:
    output = Image.new("RGBA", (FRAME, FRAME))
    draw = ImageDraw.Draw(output)
    fx, fy = FORWARD
    px, py = -fy, fx
    forward, lateral = silhouette_extents(body, FORWARD)
    for along in (-forward * 0.52, forward * 0.52):
        for side in (-1, 1):
            x = 64 + fx * along + px * lateral * 0.82 * side
            y = 64 + fy * along + py * lateral * 0.82 * side
            draw.ellipse((x - 5, y - 3, x + 5, y + 3), fill=(12, 12, 14, 245), outline=(163, 52, 34, 255))
            draw.line((x - 4, y + 4, x + 4, y - 4), fill=(245, 112, 34, 255), width=1)
    return output


def tuning_layer(body: Image.Image) -> Image.Image:
    output = Image.new("RGBA", (FRAME, FRAME))
    draw = ImageDraw.Draw(output)
    fx, fy = (0.0, 1.0)
    px, py = -fy, fx
    rear, lateral = silhouette_extents(body, (fx, fy))
    cx, cy = 64, 64 + max(2, rear - 5)
    width = lateral * 0.72
    draw.line((cx - px * width, cy - py * width, cx + px * width, cy + py * width), fill=(17, 19, 22, 255), width=4)
    draw.line((cx - px * width * 0.92, cy - py * width * 0.92,
               cx + px * width * 0.92, cy + py * width * 0.92), fill=(178, 83, 46, 255), width=1)
    return output


def effect_sheet(fire: bool) -> Image.Image:
    output = Image.new("RGBA", (FRAME * 4, FRAME))
    for frame_index in range(4):
        frame = Image.new("RGBA", (FRAME, FRAME))
        draw = ImageDraw.Draw(frame)
        rng = random.Random(700 + frame_index + (100 if fire else 0))
        for index in range(8 if fire else 6):
            x, y = 64 + rng.randint(-9, 9), 55 - index * 4 + rng.randint(-3, 3) - frame_index
            radius = rng.randint(3, 7)
            if fire:
                color = (255, 55 + index * 12, 18, 230) if index < 4 else (255, 176, 45, 220)
            else:
                shade = 55 + index * 5
                color = (shade, shade + 4, shade + 7, max(40, 175 - index * 14))
            draw.rectangle((x - radius, y - radius, x + radius, y + radius), fill=color)
        output.alpha_composite(frame, (frame_index * FRAME, 0))
    return output


def build(source_path: Path, output_path: Path, extent: int, license_id: str, copyright_text: str,
          brightness: float = 1.0) -> None:
    output_path.mkdir(parents=True, exist_ok=True)
    source = Image.open(source_path).convert("RGBA")
    if brightness != 1:
        source = ImageEnhance.Brightness(source).enhance(brightness)
    body = prepare_frame(source, extent)
    body.save(output_path / "body.png")

    shadow_alpha = Image.new("L", (FRAME, FRAME))
    shadow_alpha.paste(body.getchannel("A").filter(ImageFilter.MaxFilter(5)), (3, 4))
    shadow = Image.new("RGBA", (FRAME, FRAME), (5, 7, 9, 0))
    shadow.putalpha(shadow_alpha.point(lambda value: 92 if value else 0))
    layers = {
        "shadow": shadow,
        "damage-light": damage_layer(body, False, 1000),
        "damage-heavy": damage_layer(body, True, 2000),
        "headlights": lamp_layer(body, False),
        "brakes": lamp_layer(body, True),
        "reverse": lamp_layer(body, True, True),
        "hood-open": hood_layer(body),
        "wheel-damage": wheel_damage_layer(body),
        "tuning-street": tuning_layer(body),
    }
    for name, layer in layers.items():
        layer.save(output_path / f"{name}.png")
    effect_sheet(False).save(output_path / "smoke.png")
    effect_sheet(True).save(output_path / "fire.png")

    states = [{"name": name} for name in STATIC_STATES]
    states.extend([
        {"name": "smoke", "delays": [[0.18, 0.18, 0.18, 0.18]]},
        {"name": "fire", "delays": [[0.12, 0.12, 0.12, 0.12]]},
    ])
    metadata = {
        "version": 1,
        "license": license_id,
        "copyright": copyright_text,
        "size": {"x": FRAME, "y": FRAME},
        "states": states,
    }
    (output_path / "meta.json").write_text(json.dumps(metadata, indent=4) + "\n", encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--extent", type=int, default=102)
    parser.add_argument("--license", default="LicenseRef-CDPR-Fan-Content")
    parser.add_argument("--copyright", required=True)
    parser.add_argument("--brightness", type=float, default=1.0)
    args = parser.parse_args()
    build(args.source, args.output, args.extent, args.license, args.copyright, args.brightness)


if __name__ == "__main__":
    main()
