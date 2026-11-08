#!/usr/bin/env python3
"""Split the ImageGen atlas into native 32px RSI states; retain the immutable source."""
from pathlib import Path
import json
from PIL import Image
ROOT = Path(__file__).resolve().parents[3]
SOURCE = Path(__file__).parent / 'Art/episodes-source.png'
DESTINATION = ROOT / 'Resources/Textures/_NC/Interface/Cyberpsychosis/episodes.rsi'
NAMES = ['stable', 'warning', 'crisis', 'acute', 'recovery', 'phantom']

def main():
    source = Image.open(SOURCE).convert('RGBA')
    assert source.size == (1536, 1024)
    DESTINATION.mkdir(parents=True, exist_ok=True)
    for index, name in enumerate(NAMES):
        x, y = index % 3 * 512, index // 3 * 512
        tile = source.crop((x, y, x + 512, y + 512)).resize((32, 32), Image.Resampling.NEAREST)
        # Binary transparency avoids soft alpha fringes against station floors.
        tile.putalpha(tile.getchannel('A').point(lambda alpha: 255 if alpha >= 200 else 0))
        tile.save(DESTINATION / (name + '.png'))
    (DESTINATION / 'meta.json').write_text(json.dumps({
        'version': 1, 'license': 'CC0-1.0',
        'copyright': 'Generated with OpenAI ImageGen for Night City 2045, 2026; native sprite normalization by project contributors.',
        'size': {'x': 32, 'y': 32}, 'states': [{'name': name} for name in NAMES]
    }, indent=4) + '\n')

if __name__ == '__main__':
    main()
