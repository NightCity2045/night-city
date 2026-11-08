# Cyberarm art sources

`arm-modules-source.png` is an original OpenAI ImageGen reference generated for
this project. It supplies the nine inventory silhouettes, not the on-body frames.
The prompt is preserved in `prompt.txt`. No external sprite pixels were copied.

`../generate_cyberarm_sprites.py` normalizes the inventory icons to a small indexed
palette and contains the separately authored on-body pixel patterns. These use
32×32 cells, anatomical anchors, independent front/side projections, and separate
deployment, recoil, reload and retraction frames. Do not replace these with a
rotation of the inventory icon.

Generated source and original pixel patterns: CC0-1.0.
