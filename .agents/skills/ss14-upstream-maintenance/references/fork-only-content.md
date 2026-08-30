<!-- SPDX-License-Identifier: LicenseRef-OpenSpace-AgentPrompts-Restricted -->

# Night City Only Content

## Use `_NC` When

- The behavior or data is original to Night City 2045.
- You are adding NC prototypes, locale, assets, or sidecar systems that should remain clearly separated from inherited SS14 content.
- A matching `_NC` feature folder already exists.

## Extend Inherited Content Narrowly

Do not copy an inherited subsystem into `_NC` merely to change one behavior. Prefer its public system API, a shared event, a prototype override, or a narrow marked patch. Keep original NC logic in `_NC` whenever the boundary allows it.

## Current Night City Anchors

- `Content.Shared/_NC/`: shared contracts, components, events, network state, BUI messages, and predicted NC behavior.
- `Content.Server/_NC/`: authoritative simulation, persistence orchestration, Living City, NCPD, medical, economy, housing, mapping, and game rules.
- `Content.Client/_NC/`: NC UI, overlays, visual presentation, lobby, medical, mapping, police, housing, and Living City administration.
- `Resources/Prototypes/_NC/`: NC access, Agent, bank, catalog, city, combat, dungeons, entities, game rules, housing, Living City, maps, medical, missions, roles, shaders, tiles, and test data.
- `Resources/Locale/en-US/_NC/` and `Resources/Locale/ru-RU/_NC/`: NC localization.
- `Resources/Maps/_NC/` and `Resources/Textures/_NC/`: city maps, missions, housing maps, and original NC visual assets. Check whether an `_NC` subtree actually exists before creating one in another resource root.

## License Boundary

Original files in the `_NC` code and prototype roots are governed by the repository's Night City licensing rules. Inherited files retain their original licenses. Read `LICENSE.md` and `LICENSING.md`; never relabel inherited code as original NC work.
