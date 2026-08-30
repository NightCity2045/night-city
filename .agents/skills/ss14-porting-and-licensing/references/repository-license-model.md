<!-- SPDX-License-Identifier: LicenseRef-OpenSpace-AgentPrompts-Restricted -->

# Repository License Model

This file summarizes the current Night City 2045 repository license model for agent routing and review. It is not a legal opinion.

## Current Local Anchors

Use these root files and local metadata when checking licensing:

- `README.md`
- `LICENSE.md`
- `LICENSING.md`
- `LICENSE-MIT.TXT`
- `PolyForm-Noncommercial-1.0.0.txt`
- `COMMUNITY-FUNDING-PERMISSION.md`
- per-file `SPDX-*` headers
- neighboring `.license` files where present
- RSI `meta.json` files for sprites
- asset-specific metadata beside audio, textures, maps, or other resources

## Practical Summary

Night City 2045 is multi-licensed:

- Code inherited from Space Station 14, RobustToolbox, or another upstream remains under its original license.
- Original Night City files identified by `LICENSING.md` or the SPDX identifier `PolyForm-Noncommercial-1.0.0` use the PolyForm Noncommercial License 1.0.0.
- The Community Funding Additional Permission expands permitted noncommercial community funding for covered original NC code; it does not change upstream or asset licenses.
- Assets remain under the licenses in their metadata. An asset is not PolyForm merely because it is stored under `_NC`.
- Most assets are CC-BY-SA 3.0 unless their metadata says otherwise; some use non-commercial licenses and must be reviewed individually.

Do not assume a file is MIT or PolyForm based only on its path. Check its header, `LICENSING.md`, and neighboring metadata. Never add the Astro/PolyForm header to unchanged upstream files or third-party material.

## Rule

If a port touches external code or assets, read the local license files and the source license before proceeding.
