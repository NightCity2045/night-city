---
name: ss14-upstream-maintenance
description: Safely maintain the Night City 2045 SS14 fork with minimal upstream churn. Use when deciding whether to edit inherited files, place original behavior in `_NC`, preserve path similarity, or avoid unnecessary changes to RobustToolbox and upstream content.
---

<!-- SPDX-License-Identifier: LicenseRef-OpenSpace-AgentPrompts-Restricted -->

# SS14 Upstream Maintenance

Use this skill whenever a change touches inherited upstream code or may introduce avoidable fork drift.

## Workflow

1. Open `references/edit-strategy.md`.
2. Open `references/engine-boundaries.md` before considering engine or broad upstream edits.
3. Open `references/edit-types.md` for the expected fork-edit patterns.
4. Open `references/path-similarity.md` when adding fork-side files.
5. Open `references/fork-only-content.md` when `_NC` or an inherited owner may be the right home.
6. Prefer the smallest diff that solves the task.
7. Treat `RobustToolbox/` as off-limits unless the task explicitly requires engine work and no content-side path will solve it.
8. Mirror existing folder paths when adding fork-side extensions.
9. Prefer reusable extensions and public APIs over one-off branches, special cases, or hardcoded fork behavior.
10. Mark NC-specific blocks in inherited files outside `_NC` when the local style uses edit markers or the change would otherwise be hard to identify.

## Reference Map

- `references/edit-strategy.md`
- `references/engine-boundaries.md`
- `references/edit-types.md`
- `references/path-similarity.md`
- `references/fork-only-content.md`
- `../../../AGENTS.md`
