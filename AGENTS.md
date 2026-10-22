<!-- SPDX-License-Identifier: LicenseRef-OpenSpace-AgentPrompts-Restricted -->

# AGENTS.md

## How To Use Repo Guidance

At the start of a task:

- Read every file under `.agents/rules/`.
- Read every skill under `.agents/skills/` that matches the request.
- If a nearer subtree `AGENTS.md` exists for the files you are touching, read it as scoped guidance in addition to this file.
- Re-read the relevant rules and skills after context compaction, after `.agents/` changes, or after a long pause. Do not repeatedly reload unchanged guidance for every short follow-up in the same task.
- If the task is large research or a long implementation, keep a temporary scratch file for important findings and delete it before finishing.

Rules are always-on and must always be followed. Skills are loaded by topic.

## Skill Routing

Always load these skills:

- `ss14-naming-conventions`
- `ss14-ecs-prototypes`
- `ss14-upstream-maintenance`

If writing or editing C# gameplay code:

- `ss14-ecs-components`
- `ss14-ecs-entities`
- `ss14-ecs-prototypes`
- `ss14-ecs-systems`
- `ss14-events`
- `ss14-prediction`

If the C# change is large or the task needs reviewable notes:

- `ss14-documentation-writing`

If the code touches hot paths, `Update()`, or frequently raised events:

- `ss14-standard-optimizations`

If the task adds or changes player-facing text:

- `ss14-localization-strings`
- `ss14-localization-code` when the change also touches `Loc.GetString(...)`, `LocId`, popup text, or localized component fields

If the task touches network events, `NetEntity`, replicated state routing, or shared/server/client message flow:

- `ss14-netcode`

If the task touches `Appearance`, `GenericVisualizer`, visual state enums, or sprite-layer toggles:

- `ss14-graphics-generic-visualizer-appearance`

If the task touches sprites, RSI metadata, overlays, shaders, or custom client visual effects:

- `ss14-sprite-overlays-shaders`

If the task adds tests or you need to choose the right test layer:

- `ss14-tests-authoring`

If the task is about learning or explaining SS14 architecture, first features, or where code belongs:

- `ss14-prototype-basics`
- `ss14-ecs-basics`
- `ss14-client-server-shared`

If the task is about bug hunting, VV, logs, breakpoints, or runtime inspection:

- `ss14-debugging-workflow`
- `ss14-common-api-patterns`

If the task touches common gameplay helpers such as entity-system methods, spawning, prototypes, audio, popups, or random:

- `ss14-common-api-patterns`
- `ss14-audio` when the work changes audio routing, sound assets, sound collections, or predicted sound feedback

If the task ports code or assets from another repository, or needs license or attribution guidance:

- `ss14-porting-and-licensing`

If the task is about how to use AI tools effectively in this repo:

- `ss14-ai-workflow`

If the task touches atmospherics, gases, fire, pressure, pipes, atmos devices, or atmos UI:

- `ss14-atmos`

If the task touches transforms, coordinates, grids, maps, anchoring, movement, collision, fixtures, or physics:

- `ss14-transform-physics`

If the task touches PVS, visibility, network interest, PVS filters, PVS overrides, or code that must tolerate entities leaving PVS:

- `ss14-pvs`

If the task edits XAML windows, controls, code-behind, or client UI layout:

- `ss14-ui-xaml`

If the task edits BUI flows, UI keys, BUI state/messages, or `BoundUserInterface` classes:

- `ss14-ui-bui`

If the task edits EUI flows, `BaseEui`, `EuiStateBase`, `EuiMessageBase`, or admin/debug UI sessions:

- `ss14-ui-eui`

If the task touches database models, EF Core contexts, migrations, persistence services, or schema compatibility:

- `ss14-databases-migrations`

If the task touches NPCs, HTN, pathfinding, steering, mob AI, AI debug overlays, or NPC prototypes:

- `ss14-npc-ai`
- `ss14-standard-optimizations` for A-Life scheduling, perception, neural wake queues, or other high-frequency city simulation
- `ss14-transform-physics` when physical movement, city navigation, portals, maps, grids, or Z-levels are involved

If the task touches Living City, physical population, residents, Actors, organizations, gangs, or Neural Cognition:

- `ss14-gameplay-feature`
- `ss14-npc-ai`
- `ss14-standard-optimizations`
- `ss14-databases-migrations` when authoritative persistence or schema compatibility is involved
- Read `.rules/A-life/plan.md` and the linked Physical GPT A-Life v3 specification before changing architecture. The local v2.1 GDD is historical and must not override v3.

If the task touches NCPD behavior, ranks, assignments, patrols, investigations, detention, custody, or the NCPD operations console:

- `ss14-gameplay-feature`
- `ss14-npc-ai` for physical officers
- Read `.rules/NCPD/Night_City_2045_NCPD_Full_Implementation_Plan.md` before changing the NCPD architecture.

If the task touches NC banking, Paydex, scarcity economy, shops, organizations, Fixers, contracts, Night Markets, property, payday, or financial UI:

- `ss14-gameplay-feature`
- `ss14-prototypes-locale`
- `ss14-databases-migrations` when balances, ownership, contracts, or durable transactions are involved
- `ss14-ui-bui` when economy or vending BUI state/messages are involved

If a task spans multiple gameplay/resource areas and you need a broad map first:

- `ss14-gameplay-feature`
- `ss14-prototypes-locale`

If another skill clearly matches the request, load it too.

## Scope

This repository is a large RobustToolbox/SS14-based Night City 2045 overhaul with a clear split between gameplay code, client code, and content data:

- `Content.Shared/`, `Content.Server/`, `Content.Client/`: main shared/server/client content assemblies. Original Night City gameplay belongs under their `_NC/` subtrees.
- `Content.Server.Database/` and `Content.Shared.Database/`: database and persistence projects.
- `Resources/`: prototypes, locale, maps, textures, audio, guidebook/server info, and other content data.
- `Content.Tests/`: NUnit content/unit tests.
- `Content.IntegrationTests/`: NUnit integration tests that boot larger client/server slices.
- `Content.YAMLLinter/`: repository YAML/prototype/content validation tool.

## Working Style

- Make the smallest change that fully solves the task.
- Respect existing folder and system boundaries; do not create new `Misc` buckets.
- Do not mix feature work, bug fixes, refactors, and mapping sweeps unless the task clearly requires it.
- Read nearby systems, prototypes, and tests before introducing a new pattern.
- Prefer expanding an established feature path over inventing a parallel architecture.

## Engine Boundaries

- Do not edit `RobustToolbox/` or other engine-side files unless the task explicitly requires it.
- Prefer fixing gameplay behavior in content code before assuming an engine change is needed.
- For original Night City behavior, use the existing `_NC` folders. When extending inherited upstream behavior, first inspect the actual owner and prefer a narrow hook or patch over copying the subsystem into `_NC`.
- When you must touch an upstream content file, keep the diff narrow and preserve surrounding structure and style.
- When adding or changing NC-specific code in an inherited file outside any `_NC` path, mark it when the surrounding file already uses edit markers or the change would otherwise be hard to distinguish:
  - Single added or changed line: append `// NC` as an inline comment.
  - Multiple lines: wrap with block markers:

```csharp
// NC-Edit-Start
...code here...
// NC-Edit-End
```

- Keep edit-marker ranges as narrow as practical. For files that do not use `//` comments, use the native comment syntax while preserving `NC-Edit-Start`, `NC-Edit-End`, and `NC`.

## Assembly Placement

- Put NC shared data, shared events, networked state, and predicted logic in `Content.Shared/_NC/`.
- Put NC server authority, persistence orchestration, and non-predicted simulation in `Content.Server/_NC/`.
- Put NC visuals, overlays, XAML, and BUI front-ends in `Content.Client/_NC/`.
- Do not make shared projects depend on client-only or server-only projects.

## Night City Architecture

- `city-zones.yml` and the City Zone/Connectivity systems are the semantic geography authority for districts, streets, buildings, rooms, apartments, dungeons, entrances, floors, and traversal links.
- CityGraph and Room/Portal Graph augment physical pathfinding; they do not replace local movement or make mappers author per-NPC routes.
- Every living persistent NPC has one physical ECS entity for the entire active-world session. Runtime materialization, dematerialization, player-relevance presence, abstract travel, and physical-actor count caps are forbidden.
- GPT is the primary individual decision source for every Living City NPC. Requests and command batches are per ActorId; crowd or group coalescing must never make one model decision for multiple people.
- GPT receives bounded subjective sensorium, retrieved memory, personality, commitments, and an allowed action catalog. It returns structured intentions and commands, never direct world mutations.
- Physical actions use the same authoritative gameplay systems and constraints available to players. HTN, StateTree, and Utility may execute procedures, schedule work, prioritize queues, or provide safe reflexes, but must not become a second normal NPC brain.
- PVS controls only what clients receive. It never changes whether an NPC physically exists on the server.
- Semantic home, work, organization, and building-type anchors are persistent context and navigation data. They never replace the NPC body or authorize hidden outcomes.
- The A-Life simulation advances only while the round/server world is active. Do not invent offline progression.
- Treat `.rules/A-life/plan.md` and its linked v3 specification as the current authority. Older A-Life documents are historical only.

## ECS Rules

- Keep components data-only. Put gameplay logic in entity systems.
- Prefer entity-system public methods over method events.
- Public entity-system APIs that operate on entities should usually take `Entity<T?>` or `EntityUid` first and call `Resolve(...)` early.
- Prefer `Entity<T?>` over parallel `(EntityUid uid, T component)` parameters when the call site already has the pair.
- Prefer `[Dependency]` fields over ad-hoc `IoCManager.Resolve(...)` inside methods.
- Use `EntityUid?` for optional entities; do not use `EntityUid.Invalid` as a "missing" sentinel.
- Use `sealed`, `abstract`, `static`, or `[Virtual]` on new classes where appropriate.
- Prefer prototypes over enums for in-game content types.
- Use `ProtoId<T>` or `EntProtoId` instead of raw prototype ID strings in data fields and static references.
- Prefer `[DataField]` without string field names on new code unless serializer compatibility or a non-default data name is required.

## Interaction Flow

Use the repo-standard action flow for interactions and state-changing gameplay APIs:

- `OnEvent(...)` is the event entry point.
- `TryDoSomething(...)` is the public action API.
- `CanDoSomething(...)` checks whether the action is allowed.
- `DoSomething(...)` or the execution part of `TryDoSomething(...)` performs the mutation.

More detail lives in `.agents/rules/ss14-interaction-flow.md`.

## Naming

- Event handlers should use `On...` names.
- Public action methods should prefer `Try...`.
- Check methods should prefer `Can...`.
- Dependency fields should use the existing underscore-prefixed style such as `_popup`, `_hands`, `_audio`.
- Use specific `kebab-case` localization IDs.
- Keep new prototype IDs, event names, and component names aligned with nearby conventions rather than inventing new suffixes.

## Prediction And Networking

When a local player action should feel immediate, check whether it should be predicted.

- Predicted systems and their relevant components belong in `Content.Shared/`.
- Shared predicted components should use `NetworkedComponent`, `AutoGenerateComponentState`, and `AutoNetworkedField` where appropriate.
- Dirty networked state every time authoritative data changes. Use `DirtyField(...)` when field deltas make sense.
- Use predicted APIs such as `PopupPredicted`, `PopupClient`, `PlayPredicted`, predicted BUI messages, and predicted spawn/delete helpers instead of server-only equivalents.
- If a shared system needs client/server special cases, keep a shared base plus both server and client concrete systems.
- Never add `NetworkedComponent` to purely server-only or purely client-only components.
- Be careful with predicted randomness and reference-type networked fields.
- Prefer maximum practical prediction support for new player-facing systems instead of adding prediction as after-the-fact cleanup.

## UI

- Prefer XAML over constructing full UIs in C#.
- Keep `.xaml` paired with `.xaml.cs` and the relevant BUI/client system.
- Reuse existing style classes and `FancyWindow` patterns before adding new stylesheet rules.
- Localize all player-visible UI text.
- When the client already has the needed networked component state, prefer reading it instead of duplicating it in separate BUI state objects unless the existing pattern clearly requires both.

## Resources

- When additional sprites or UI images are needed, generated artwork may be used. Match the existing build's visual style and use nearby project assets as references.
- Put prototypes under the most specific existing subtree in `Resources/Prototypes/`.
- If you introduce a new prototype parent tree, put parent prototypes in `base.yml` and variants in sibling files.
- Keep entity prototype field order as `type`, `abstract`, `parent`, `id`, `categories`, `name`, `suffix`, `description`, `components`.
- Do not insert blank lines between `- type:` entries inside a `components:` list.
- Separate prototype blocks with one blank line.
- Prefer `suffix` for spawn-menu distinctions instead of changing prototype `name`.
- Use sound collections/specifiers and sprite specifiers instead of ad-hoc raw asset strings in code when reusable content is intended.
- Keep RSI `meta.json` ordered as `version`, `license`, `copyright`, `size`, `states` with 4-space indentation.

## Localization

- Every player-facing string must be localized.
- Add or update FTL entries under `Resources/Locale/`, starting with `en-US`; add/update matching `ru-RU` entries when the same feature already maintains Russian locale or the change is NC-facing and you can do so without guessing translations.
- Use specific `kebab-case` localization IDs.
- Do not compare localized strings or expose raw enum `ToString()` output to players.
- Treat localization as mandatory work, not optional polish.

## Rider MCP

If Rider MCP is available in the environment, prefer it over shell equivalents for search, navigation, edits, and file diagnostics.

Preferred order:

1. Search and navigation: symbol/text/file/tree tools.
2. Reading and diagnostics: file reads, symbol info, file problems.
3. Solution structure: modules, dependencies, run configurations.
4. Edits and refactors: replace text, rename refactorings, reformat.
5. Verification: build the affected project, then run targeted configurations when available.

If Rider MCP is not available, use the normal shell/file tools.

## Testing And Validation

Pick the smallest verification that meaningfully covers the change.

- SDK: `global.json` pins .NET SDK `10.0.100` with `latestFeature` roll-forward.
- Submodules: run `git submodule update --init --recursive` before restore/build/test when `RobustToolbox/` is not initialized; CI also pulls engine updates before building.
- Baseline build: `dotnet restore` then `dotnet build --configuration DebugOpt --no-restore /m`.
- Content tests after build: `dotnet test --no-build --configuration DebugOpt Content.Tests/Content.Tests.csproj -- NUnit.ConsoleOut=0`.
- Integration tests are opt-in for this project: run them only when the user explicitly requests them or approves them after a concrete need is identified.
- Standalone content test run without a prior build: `dotnet test --configuration DebugOpt Content.Tests/Content.Tests.csproj -- NUnit.ConsoleOut=0`.
- YAML/resource edits: `dotnet run --project Content.YAMLLinter/Content.YAMLLinter.csproj -c DebugOpt`. CI builds Release first and then runs `dotnet run --project Content.YAMLLinter/Content.YAMLLinter.csproj --no-build`.
- RSI edits: `python3 RobustToolbox/Schemas/validate_rsis.py Resources/` after initializing the `RobustToolbox` submodule and installing the script dependencies (`pillow` and `jsonschema`).
- Gameplay/UI fixes should ideally be verified in-game; if you cannot do that locally, say so explicitly.
- If code touches prototypes or FTL, run the YAML linter.
- If code touches C#, build the affected project or the repo slice that covers it.
- If code touches the client, run or otherwise verify the client path when possible and call out when runtime verification was not possible.

More detail lives in `.agents/rules/ss14-testing-and-validation.md`.

## PR Expectations

- Keep feature work, bug fixes, refactors, and mapping changes separate when practical.
- Do not force-push or rewrite history unless explicitly asked.
- For player-visible changes, prepare PR-ready changelog text in `:cl:` format when useful, but do not hand-edit generated changelog artifacts unless the task explicitly asks for it.
- If a change is breaking for APIs, namespaces, or prototype IDs, call it out clearly.
