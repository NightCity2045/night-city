<!-- SPDX-License-Identifier: LicenseRef-OpenSpace-AgentPrompts-Restricted -->

# Resources Map

## Purpose

Use this file to place prototype, localization, texture, audio, and map edits in the correct `Resources/` subtree for Night City 2045.

## Main Resource Roots

- `Resources/Prototypes/`: data definitions for entities, reagents, recipes, objectives, roles, maps, loadouts, sound collections, and other game content.
- `Resources/Locale/<culture>/`: Fluent localization files. The repo currently has `en-US` and `ru-RU`; start with `en-US` and update `ru-RU` when the feature already has Russian locale or the wording is known.
- `Resources/Textures/`: sprites, RSIs, decals, and other visual assets.
- `Resources/Audio/`: sound files plus attribution or organization by feature area.
- `Resources/Maps/`: map YAML and related data.

## Prototype Folder Families

The repo already uses a broad set of top-level prototype families. Common ones include:

- `Entities/`: most entity prototype trees, including items, mobs, structures, customization, and machine content.
- `Reagents/`, `Recipes/`, `Nutrition/`: chemistry and crafting data.
- `Objectives/`, `Roles/`, `GameRules/`, `Loadouts/`, `Traits/`: roundflow, jobs, objectives, and character setup.
- `Catalog/`, `Datasets/`, `EntityLists/`: reusable data pools and lookup content.
- `Polymorphs/`, `NPCs/`, `Procedural/`, `Maps/`: runtime content generation and special behaviors.
- `SoundCollections/`, `Shaders/`, `StatusIcon/`: reusable presentation data.
- `_NC/`: original Night City prototypes and local feature data.

Use the most specific existing subtree instead of inventing a new top-level folder.

## Locale Folder Reality

Locale folder names do not always mirror prototype folder names one-to-one.

- Prototype folders often use PascalCase or plural forms such as `AlertLevels`, `SoundCollections`, `Entities`.
- Locale folders usually use lowercase or kebab-case such as `living-city`, `game-ticking`, `mapping`, `medical`, or `police`.
- Some domains are concept-based instead of folder-name-based, for example prototype data in `Entities/` may localize under `items`, `medical`, `markings`, or another nearby feature directory.

Do not assume the FTL path from the prototype path. Search nearby locale folders and follow the local convention.

## Night City Content

Current `Resources/Prototypes/_NC/` content includes `Access`, `Agent`, `Bank`, `Catalog`, `City`, `Combat`, `Dungeons`, `Entities`, `GameRules`, `Guidebook`, `Housing`, `LivingCity`, `Maps`, `Medical`, `Missions`, `Roles`, `Shaders`, `Test`, and `Tiles`.

NC locale exists under both `Resources/Locale/en-US/_NC/` and `Resources/Locale/ru-RU/_NC/`. Search for the actual FTL key before assuming the locale path mirrors the prototype folder.

Use `Resources/Prototypes/_NC/`, `Resources/Locale/*/_NC/`, `Resources/Textures/_NC/`, and `Resources/Maps/_NC/` for original Night City content. For audio or another resource family without an existing `_NC` root, inspect neighboring organization before creating a new subtree. Preserve inherited resources in their current owner.

## Prototype Placement Heuristics

- Spawnable or entity-backed content usually belongs somewhere under `Entities/`.
- Reusable sound bundles belong under `SoundCollections/`.
- Character selection or role data often belongs under `Loadouts/`, `Roles/`, or `Traits/`.
- Chemistry data usually spans `Reagents/`, `Recipes/`, and locale under `chemistry` or a nearby gameplay domain.
- Mapping entities may still live under `Entities/`, while city, housing, mission, dungeon, and test map assets live under `Maps/`.

## Code-To-Resource Pairing

- C# component or system changes often require prototype changes in a same-named or nearby domain folder.
- Player-facing strings in code, prototypes, and UI require FTL under `Resources/Locale/en-US/`.
- Sprite or appearance work usually needs both prototype references and assets under `Resources/Textures/`.
- Audio changes should prefer a sound collection prototype when multiple entities or systems reuse the same sound set.

## Validation Hooks

- Prototype or locale edits: `dotnet run --project Content.YAMLLinter/Content.YAMLLinter.csproj -c DebugOpt`
- RSI edits: `python3 RobustToolbox/Schemas/validate_rsis.py Resources/`
- Map edits: rely on schema validation in CI and keep map-only changes isolated when practical
