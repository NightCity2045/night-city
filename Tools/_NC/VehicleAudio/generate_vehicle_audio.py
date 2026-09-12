# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

"""Deterministically synthesize original vehicle audio without sampled third-party material."""

from __future__ import annotations

import argparse
import wave
from dataclasses import dataclass
from pathlib import Path

import numpy as np


RATE = 22050


@dataclass(frozen=True)
class Voice:
    folder: str
    cylinders: int
    idle: int
    low: int
    mid: int
    high: int
    roughness: float
    bass: float
    turbo: float
    horn: tuple[float, float]


VOICES = (
    Voice("supra", 6, 850, 1900, 3900, 6800, .10, .70, 1.00, (370, 465)),
    Voice("quadra_turbo_r", 8, 700, 1700, 3500, 6100, .24, 1.00, .55, (330, 415)),
    Voice("quadra_type66", 8, 620, 1500, 3200, 5600, .32, 1.15, .05, (300, 375)),
    Voice("thorton_galena", 4, 900, 1900, 3500, 5400, .38, .45, .00, (430, 545)),
    Voice("thorton_colby", 6, 680, 1500, 2900, 4600, .28, 1.10, .12, (285, 360)),
    Voice("archer_quartz", 4, 950, 2200, 4300, 6900, .12, .45, .30, (410, 515)),
    Voice("mizutani_shion", 6, 900, 2200, 4600, 7600, .08, .50, .85, (390, 490)),
    Voice("villefort_alvarado", 8, 600, 1400, 2700, 4800, .08, 1.25, .05, (260, 325)),
    Voice("makigai_maimai", 3, 1050, 2400, 4400, 6500, .18, .25, .00, (510, 645)),
    Voice("rayfield_caliburn", 12, 750, 2100, 4500, 7800, .04, .85, .75, (350, 440)),
    Voice("recovery_truck", 6, 580, 1250, 2200, 3500, .30, 1.35, .35, (245, 305)),
)


def timeline(seconds: float) -> np.ndarray:
    return np.arange(round(RATE * seconds), dtype=np.float64) / RATE


def normalize(data: np.ndarray, peak: float = .88) -> np.ndarray:
    data -= np.mean(data)
    maximum = max(1e-9, float(np.max(np.abs(data))))
    return np.tanh(data / maximum * 1.7) / np.tanh(1.7) * peak


def save(path: Path, data: np.ndarray) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    pcm = np.asarray(np.clip(data, -1, 1) * 32767, dtype="<i2")
    with wave.open(str(path), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(RATE)
        output.writeframes(pcm.tobytes())


def engine_loop(voice: Voice, rpm: int) -> np.ndarray:
    t = timeline(1)
    firing = max(1, round(rpm / 60 * voice.cylinders / 2))
    phase = 2 * np.pi * firing * t
    body = np.sin(phase) + .48 * np.sin(phase * 2 + .3) + .22 * np.sin(phase * 3 + 1.1)
    sub = np.sin(phase / max(1, voice.cylinders // 2)) * voice.bass
    mechanical = .14 * np.sin(phase * 5 + .7) + .08 * np.sin(phase * 7)
    uneven = 1 + voice.roughness * np.sin(2 * np.pi * max(2, firing // voice.cylinders) * t)
    return normalize((body * uneven + sub + mechanical) * .55)


def sweep_engine(voice: Voice, start: float, end: float, seconds: float) -> np.ndarray:
    t = timeline(seconds)
    rpm = start + (end - start) * np.sin(np.minimum(1, t / seconds) * np.pi / 2) ** 2
    frequency = rpm / 60 * voice.cylinders / 2
    phase = 2 * np.pi * np.cumsum(frequency) / RATE
    envelope = np.sin(np.pi * np.minimum(1, t / seconds)) ** .35
    motor = np.sin(phase) + .42 * np.sin(2 * phase) + voice.bass * .35 * np.sin(phase / 2)
    return normalize(motor * envelope)


def turbo_loop(voice: Voice) -> np.ndarray:
    t = timeline(1)
    base = 1050 + 850 * voice.turbo
    data = .7 * np.sin(2 * np.pi * round(base) * t)
    data += .28 * np.sin(2 * np.pi * round(base * 1.47) * t)
    data *= .75 + .25 * np.sin(2 * np.pi * 7 * t)
    return normalize(data, .55)


def flutter(voice: Voice) -> np.ndarray:
    t = timeline(1.15)
    data = np.zeros_like(t)
    onsets = (.09, .18, .27, .37, .48, .60, .73, .88, 1.04) if voice.folder == "supra" else (.03, .18, .35, .55, .79)
    for index, onset in enumerate(onsets):
        local = t - onset
        mask = local >= 0
        env = np.exp(-local.clip(min=0) * (18 + index * 1.2)) * mask
        freq = 760 - index * 58 + voice.turbo * 440
        # Supra alternates two close compressor resonances to sell the twin-turbo "stu-tu-tu" cadence.
        twin = .62 if voice.folder == "supra" else .35
        data += env * (np.sin(2 * np.pi * freq * local) + twin * np.sin(2 * np.pi * freq * 1.91 * local))
    return normalize(data, .8)


def anti_lag(voice: Voice) -> np.ndarray:
    rng = np.random.default_rng(sum(map(ord, voice.folder)))
    t = timeline(1.25)
    data = np.zeros_like(t)
    if voice.folder == "supra":
        # Tight alternating bursts produce the characteristic sustained "ra-ta-ta-ta" instead of isolated backfires.
        onsets = np.array((.03, .11, .18, .27, .34, .43, .51, .60, .69, .79, .89, 1.00, 1.12))
    else:
        onsets = np.cumsum(rng.uniform(.14, .27, 5))
    for index, onset in enumerate(onsets):
        local = t - onset
        mask = local >= 0
        env = np.exp(-local.clip(min=0) * rng.uniform(24, 38)) * mask
        crack = rng.normal(0, 1, len(t))
        boom = np.sin(2 * np.pi * rng.uniform(62, 94) * local)
        data += env * (boom * .9 + crack * .32) * (1 - index * .035)
    return normalize(data, .94)


def shift_sound(voice: Voice) -> np.ndarray:
    rng = np.random.default_rng(voice.cylinders * 47)
    t = timeline(.32)
    impact = np.exp(-t * 28) * (np.sin(2 * np.pi * (90 + voice.bass * 30) * t) + rng.normal(0, .25, len(t)))
    click = np.exp(-np.maximum(0, t - .11) * 45) * (t >= .11) * np.sin(2 * np.pi * 520 * t)
    return normalize(impact + click * .45, .72)


def horn(voice: Voice) -> np.ndarray:
    t = timeline(.7)
    attack = np.minimum(1, t * 35)
    release = np.minimum(1, (.7 - t) * 10)
    data = np.sin(2 * np.pi * voice.horn[0] * t) + .72 * np.sin(2 * np.pi * voice.horn[1] * t)
    return normalize(data * attack * release, .78)


def skid(voice: Voice) -> np.ndarray:
    rng = np.random.default_rng(900 + voice.cylinders)
    noise = rng.normal(0, 1, RATE)
    # Circular smoothing keeps the one-second noise texture seamless.
    smooth = sum(np.roll(noise, offset) for offset in range(-5, 6)) / 11
    t = timeline(1)
    squeal = np.sin(2 * np.pi * round(980 + voice.bass * 170) * t) * (.25 + voice.roughness)
    return normalize(smooth + squeal, .62)


def build(root: Path, voice: Voice) -> None:
    target = root / voice.folder
    for name, rpm in (("engine_low_loop", voice.low), ("engine_mid_loop", voice.mid), ("engine_high_loop", voice.high)):
        save(target / f"{name}.wav", engine_loop(voice, rpm))
    save(target / "startup.wav", sweep_engine(voice, 90, voice.idle * 1.3, 1.4))
    save(target / "shutdown.wav", sweep_engine(voice, voice.idle, 70, 1.0))
    save(target / "shift.wav", shift_sound(voice))
    save(target / "horn.wav", horn(voice))
    save(target / "tyre_skid_loop.wav", skid(voice))
    if voice.turbo > 0:
        save(target / "turbo_loop.wav", turbo_loop(voice))
        save(target / "turbo_flutter.wav", flutter(voice))
    if voice.folder in {"supra", "quadra_turbo_r", "quadra_type66", "mizutani_shion", "rayfield_caliburn"}:
        save(target / "backfire.wav", anti_lag(voice))
    files = "\n".join(f"  - {path.name}" for path in sorted(target.glob("*.wav")))
    attribution = f"""# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

- files:
{files}
  license: CC-BY-NC-SA-4.0
  copyright: Original procedural vehicle audio by Astro, 2026
  source: Tools/_NC/VehicleAudio/generate_vehicle_audio.py
"""
    (target / "attributions.yml").write_text(attribution, encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    for voice in VOICES:
        build(args.output, voice)


if __name__ == "__main__":
    main()
