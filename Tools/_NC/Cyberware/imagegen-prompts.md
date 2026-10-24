<!-- SPDX-FileCopyrightText: 2026 Astro -->
<!-- SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0 -->

# Gate 7 artwork prompts

Both calls used the built-in imagegen tool, `transparent_background: true`. All inputs were references,
not edit targets. Output copies are in `Resources/Textures/_NC/Mobs/Cyberware/`; the packing script creates
the actual `parts.rsi` assets. No external generation API or paid provider CLI was used.

## Limbs

References: native Human `l_arm.png`, `r_arm.png`, `l_leg.png`, `r_leg.png` and the existing NC
`Objects/Cyberware/implants.rsi/cyberarm_left.png`.

```text
Generate a NEW production pixel-art sprite atlas for the Night City 2045 fork of Space Station 14, using the supplied tiny existing images ONLY as style and anatomical registration REFERENCES, not edit targets. Output a perfectly square, transparent 4 by 4 atlas, exactly 16 equal cells, no grid lines, labels, shadows, floor, or other content. Columns from left to right: left upper arm, right upper arm, left lower limb (leg, without foot), right lower limb (leg, without foot). Rows top to bottom: facing SOUTH (front), NORTH (back), EAST (right-facing side view), WEST (left-facing side view). Each cell contains exactly ONE isolated robotic limb. Match SS14 32x32 logical pixel body scale, 1:1 hard pixel edges, tiny 3-5 logical pixels wide, about 8-12 logical pixels long, with sparse metal plate seams and dark articulation. Arms stop at the wrist, legs stop at the ankle; no hands or feet, no whole characters. The logical sprite should sit in the same body-relative position as the reference human organ within an otherwise empty square 32x32 logical frame: arms at the outer left/right of the centerline around upper-middle height; legs below center. Industrial brushed silver and dark graphite, sparse pale cyan highlights like the existing cyberarm item reference. Deliberately neutral metal palette, practical small silhouettes, not detailed large concept art. Anatomically mirrored LEFT and RIGHT, all four directions coherent. The existing reference strips have four 32px-tall cells vertically (south,north,east,west). The output will be downsampled with nearest-neighbor and packed into native RSI states. Preserve genuinely transparent alpha and ample empty area of each cell. No gore, logos, glow clouds, text or decorative background.
```

The native reference actually packs its four frames in a 2×2 grid and intentionally omits the far-side arm.
The packing script uses the real file dimensions/alpha, rather than the prompt's strip-layout assumption.

## Frame

References: the generated limb atlas, then native Human `torso_m.png`, `head_m.png`, `l_hand.png`, `r_hand.png`.

```text
Generate a NEW production 4x4 transparent pixel sprite atlas for Night City 2045 / Space Station14. Reference image 1 (silver robotic limbs) is a STYLE REFERENCE. Reference images 2-5 (native SS14 body strips) are SCALE and registration references only, not edit targets. Match silver metal, graphite articulations and sparse pale cyan highlights of the limb atlas. No skin, organic anatomy, flesh, nipples, gore, text, cell borders, floor or background. Exactly sixteen separate small body-part sprites in a uniform square 4 columns x 4 rows grid. Columns left to right: 1 mechanical humanoid TORSO shell only (neck to hips, no limbs), 2 mechanical humanoid HEAD only (compact rounded helmet with dark visor, no neck or shoulders), 3 LEFT HAND only (small robotic palm), 4 RIGHT HAND only. Rows top to bottom: facing SOUTH front, NORTH back, EAST right profile, WEST left profile. Every square cell has the same 32x32 logical SS14 pixel canvas; sparse chunky pixel art, no antialiasing. Within each otherwise empty cell draw only that part, small and body-relative, matching proportions of the native references, which show four 32x32 frames packed in a 2x2 square. TORSO about 10x14 logical pixels, HEAD about 8x8, each HAND about 4x4. Clear flat chest panels with one cyan status slit, no weapons or brand symbols; neutral practical civilian/industrial shell. Keep all 16 subjects fully separate, coherent front/back and mirrored sides. Transparent alpha with no shadow or peripheral decorations; each cell will be cropped and normalized to native SS14 registration.
```
