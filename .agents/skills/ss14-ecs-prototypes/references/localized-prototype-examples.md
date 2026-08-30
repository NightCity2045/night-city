<!-- SPDX-License-Identifier: LicenseRef-OpenSpace-AgentPrompts-Restricted -->

# Localized Prototype Examples

## Access Levels

- `Resources/Prototypes/_NC/Access/levels.yml`
- `Resources/Locale/en-US/_NC/access.ftl`
- `Resources/Locale/ru-RU/_NC/access.ftl`

## Entity Prototypes

- `Resources/Prototypes/_NC/Entities/Structures/Machines/Computers/ncpd_records.yml`
- `Resources/Locale/en-US/_NC/police/police-records.ftl`
- `Resources/Locale/ru-RU/_NC/police/police-records.ftl`

## Why These Are Useful

- the prototype IDs map directly to localized display keys
- the content is clearly project-scoped under `_NC`
- the pair demonstrates prototype-plus-locale edits in the same feature subtree

## Reminder

When a prototype is visible to players, update the matching locale in the same pass instead of leaving a later TODO.
