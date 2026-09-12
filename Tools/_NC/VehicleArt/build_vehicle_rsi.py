# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

"""Build consistent eight-direction vehicle RSIs from approved source sheets."""

from __future__ import annotations

import argparse
import json
import math
import random
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter


FRAME = 128
# ImageGen source order -> RobustToolbox RSI order: S, N, E, W, SE, SW, NE, NW.
SOURCE_ORDER = (0, 4, 2, 6, 1, 7, 3, 5)
DIRECTIONS = (
    (0.0, 1.0),
    (0.0, -1.0),
    (1.0, 0.0),
    (-1.0, 0.0),
    (0.707, 0.707),
    (-0.707, 0.707),
    (0.707, -0.707),
    (-0.707, -0.707),
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


def sheet(frames: list[Image.Image]) -> Image.Image:
    output = Image.new("RGBA", (FRAME * 4, FRAME * 2))
    for index, frame in enumerate(frames):
        output.alpha_composite(frame, ((index % 4) * FRAME, (index // 4) * FRAME))
    return output


def clip_to_body(overlay: Image.Image, body: Image.Image) -> Image.Image:
    alpha = ImageChops.multiply(overlay.getchannel("A"), body.getchannel("A"))
    overlay.putalpha(alpha)
    return overlay


def keep_largest_component(cell: Image.Image) -> Image.Image:
    """Discard neighboring-cell fragments while retaining the centered vehicle silhouette."""
    alpha = cell.getchannel("A")
    width, height = alpha.size
    pixels = alpha.load()
    visited = bytearray(width * height)
    largest: list[int] = []
    for y in range(height):
        for x in range(width):
            start = y * width + x
            if visited[start] or pixels[x, y] < 32:
                continue
            visited[start] = 1
            stack = [start]
            component: list[int] = []
            while stack:
                current = stack.pop()
                component.append(current)
                cx, cy = current % width, current // width
                for nx, ny in ((cx - 1, cy), (cx + 1, cy), (cx, cy - 1), (cx, cy + 1)):
                    if nx < 0 or ny < 0 or nx >= width or ny >= height:
                        continue
                    offset = ny * width + nx
                    if visited[offset] or pixels[nx, ny] < 32:
                        continue
                    visited[offset] = 1
                    stack.append(offset)
            if len(component) > len(largest):
                largest = component
    mask = Image.new("L", cell.size)
    mask_pixels = mask.load()
    for offset in largest:
        mask_pixels[offset % width, offset // width] = 255
    cell.putalpha(ImageChops.multiply(alpha, mask))
    return cell


def prepare_frames(source: Image.Image, target_extent: int) -> list[Image.Image]:
    raw: list[Image.Image] = []
    width, height = source.size
    for index in range(8):
        column, row = index % 4, index // 4
        bounds = (
            round(column * width / 4),
            round(row * height / 2),
            round((column + 1) * width / 4),
            round((row + 1) * height / 2),
        )
        cell = keep_largest_component(source.crop(bounds))
        box = cell.getchannel("A").getbbox()
        if box is None:
            raise ValueError(f"source cell {index} is empty")
        cell = cell.crop(box)
        scale = target_extent / max(cell.size)
        size = (max(1, round(cell.width * scale)), max(1, round(cell.height * scale)))
        # Downsampling followed by palette reduction produces stable hard pixel clusters.
        cell = cell.resize(size, Image.Resampling.LANCZOS)
        alpha = cell.getchannel("A").point(lambda value: 255 if value >= 72 else 0)
        rgb = cell.convert("RGB").quantize(colors=28, method=Image.Quantize.MEDIANCUT,
                                            dither=Image.Dither.NONE).convert("RGB")
        cell = rgb.convert("RGBA")
        cell.putalpha(alpha)
        frame = Image.new("RGBA", (FRAME, FRAME))
        frame.alpha_composite(cell, ((FRAME - size[0]) // 2, (FRAME - size[1]) // 2))
        raw.append(frame)
    return [raw[index] for index in SOURCE_ORDER]


def damage_layer(body: Image.Image, direction: tuple[float, float], heavy: bool, seed: int) -> Image.Image:
    output = Image.new("RGBA", body.size)
    draw = ImageDraw.Draw(output)
    rng = random.Random(seed)
    fx, fy = direction
    px, py = -fy, fx
    count = 11 if heavy else 5
    for _ in range(count):
        along = rng.uniform(-30, 34)
        across = rng.uniform(-27, 27)
        x = 64 + fx * along + px * across
        y = 64 + fy * along + py * across
        points = [(x, y)]
        for step in range(rng.randint(2, 4)):
            x += rng.uniform(-4, 4) + fx * rng.uniform(1, 3)
            y += rng.uniform(-4, 4) + fy * rng.uniform(1, 3)
            points.append((x, y))
        draw.line(points, fill=(17, 13, 15, 225), width=2 if heavy else 1)
        draw.line([(p[0] + 1, p[1]) for p in points], fill=(174, 111, 69, 210), width=1)
    if heavy:
        for _ in range(4):
            x = 64 + rng.uniform(-27, 27)
            y = 64 + rng.uniform(-31, 31)
            radius = rng.randint(3, 7)
            draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=(25, 18, 18, 150))
            draw.arc((x - radius, y - radius, x + radius, y + radius), 15, 235,
                     fill=(192, 124, 76, 235), width=1)
    return clip_to_body(output, body)


def lamp_layer(direction: tuple[float, float], rear: bool, reverse: bool = False) -> Image.Image:
    output = Image.new("RGBA", (FRAME, FRAME))
    draw = ImageDraw.Draw(output)
    fx, fy = direction
    if rear:
        fx, fy = -fx, -fy
    px, py = -fy, fx
    color = (245, 245, 218, 255) if reverse else ((255, 42, 30, 255) if rear else (255, 221, 112, 255))
    glow = (255, 255, 220, 95) if reverse else ((255, 28, 16, 90) if rear else (255, 184, 50, 90))
    for side in (-1, 1):
        x = 64 + fx * 42 + px * 18 * side
        y = 64 + fy * 42 + py * 18 * side
        draw.ellipse((x - 4, y - 4, x + 4, y + 4), fill=glow)
        draw.line((x - px * 3, y - py * 3, x + px * 3, y + py * 3), fill=color, width=2)
    return output


def hood_layer(direction: tuple[float, float]) -> Image.Image:
    output = Image.new("RGBA", (FRAME, FRAME))
    draw = ImageDraw.Draw(output)
    fx, fy = direction
    center = (64 + fx * 27, 64 + fy * 27)
    bay = oriented_box(center, direction, 21, 29)
    panel_center = (center[0] - fx * 8, center[1] - fy * 8)
    panel = oriented_box(panel_center, direction, 7, 31)
    draw.polygon(bay, fill=(8, 11, 13, 250), outline=(118, 81, 58, 255))
    draw.line(oriented_box(center, direction, 15, 20) + [oriented_box(center, direction, 15, 20)[0]],
              fill=(49, 58, 60, 255), width=1)
    draw.polygon(panel, fill=(32, 34, 37, 255), outline=(169, 93, 57, 255))
    return output


def wheel_damage_layer(direction: tuple[float, float]) -> Image.Image:
    output = Image.new("RGBA", (FRAME, FRAME))
    draw = ImageDraw.Draw(output)
    fx, fy = direction
    px, py = -fy, fx
    for along in (-24, 24):
        for side in (-1, 1):
            x = 64 + fx * along + px * 29 * side
            y = 64 + fy * along + py * 29 * side
            draw.ellipse((x - 5, y - 3, x + 5, y + 3), fill=(12, 12, 14, 245), outline=(163, 52, 34, 255))
            draw.line((x - 4, y + 4, x + 4, y - 4), fill=(245, 112, 34, 255), width=1)
    return output


def tuning_layer(direction: tuple[float, float]) -> Image.Image:
    output = Image.new("RGBA", (FRAME, FRAME))
    draw = ImageDraw.Draw(output)
    fx, fy = -direction[0], -direction[1]
    px, py = -fy, fx
    cx, cy = 64 + fx * 37, 64 + fy * 37
    draw.line((cx - px * 23, cy - py * 23, cx + px * 23, cy + py * 23), fill=(17, 19, 22, 255), width=4)
    draw.line((cx - px * 21, cy - py * 21, cx + px * 21, cy + py * 21), fill=(178, 83, 46, 255), width=1)
    return output


def effect_sheet(fire: bool) -> Image.Image:
    output = Image.new("RGBA", (FRAME * 4, FRAME))
    for frame in range(4):
        image = Image.new("RGBA", (FRAME, FRAME))
        draw = ImageDraw.Draw(image)
        rng = random.Random(700 + frame + (100 if fire else 0))
        for index in range(8 if fire else 6):
            x = 64 + rng.randint(-9, 9)
            y = 83 - index * 4 + rng.randint(-3, 3) - frame
            radius = rng.randint(3, 7)
            if fire:
                color = (255, 55 + index * 12, 18, 230) if index < 4 else (255, 176, 45, 220)
            else:
                shade = 55 + index * 5
                color = (shade, shade + 4, shade + 7, max(40, 175 - index * 14))
            draw.rectangle((x - radius, y - radius, x + radius, y + radius), fill=color)
        output.alpha_composite(image, (frame * FRAME, 0))
    return output


def build(source_path: Path, output_path: Path, extent: int, license_id: str, copyright_text: str) -> None:
    output_path.mkdir(parents=True, exist_ok=True)
    frames = prepare_frames(Image.open(source_path).convert("RGBA"), extent)
    sheet(frames).save(output_path / "body.png")

    shadows = []
    light_damage = []
    heavy_damage = []
    headlights = []
    brakes = []
    reverse = []
    hoods = []
    wheels = []
    tuning = []
    for index, (body, direction) in enumerate(zip(frames, DIRECTIONS, strict=True)):
        alpha = body.getchannel("A").filter(ImageFilter.MaxFilter(5))
        shadow = Image.new("RGBA", (FRAME, FRAME), (5, 7, 9, 0))
        shadow.putalpha(alpha.point(lambda value: 92 if value else 0))
        shadows.append(ImageChops.offset(shadow, 3, 4))
        light_damage.append(damage_layer(body, direction, False, 1000 + index))
        heavy_damage.append(damage_layer(body, direction, True, 2000 + index))
        headlights.append(lamp_layer(direction, False))
        brakes.append(lamp_layer(direction, True))
        reverse.append(lamp_layer(direction, True, True))
        hoods.append(hood_layer(direction))
        wheels.append(wheel_damage_layer(direction))
        tuning.append(tuning_layer(direction))

    for name, values in (
        ("shadow", shadows),
        ("damage-light", light_damage),
        ("damage-heavy", heavy_damage),
        ("headlights", headlights),
        ("brakes", brakes),
        ("reverse", reverse),
        ("hood-open", hoods),
        ("wheel-damage", wheels),
        ("tuning-street", tuning),
    ):
        sheet(values).save(output_path / f"{name}.png")
    effect_sheet(False).save(output_path / "smoke.png")
    effect_sheet(True).save(output_path / "fire.png")

    states = [{"name": name, "directions": 8} for name in (
        "shadow", "body", "damage-light", "damage-heavy", "headlights", "brakes", "reverse",
        "hood-open", "wheel-damage", "tuning-street")]
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
    args = parser.parse_args()
    build(args.source, args.output, args.extent, args.license, args.copyright)


if __name__ == "__main__":
    main()
