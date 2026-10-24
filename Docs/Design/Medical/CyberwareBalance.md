# Cyberware balance and measurement — Gate 9

Acceptance date: 2026-10-24. Authority: [cyberware GDD](https://app.notion.com/p/GDD-4018867306b1807fa573c0d18052296b), Gate 9. Functional acceptance and initial bounded measurements are complete on the retained isolated cohort. The current joint workspace build has a separate META blocker, described below.

## Accepted checks

| Area | Observed result |
| --- | --- |
| Cyberware/Borg/humanity units | 113/113 passed, no skips |
| Targeted integration and content cases | 102/102 passed, no skips |
| Ability prototypes after resource validation fixes | 15/15 integration cases passed, no skips |
| Held restorative syringe, ordinary human | H 50 → 50.812504 after 15 simulated seconds; maximum 100 |
| Held restorative syringe, living biopod | H 50 → 50.734596; maximum 80, same physical brain |
| Actual medicine delivery | Native interaction, injector DoAfter and metabolism; 5 units remained in each syringe; perfusion 1 |
| Cardiac power loss/recovery | Organic heart unaffected; actual cyberheart stops at zero charge and resumes on the same hardware ID after recharge |
| Biopod support loss/recovery | Actual perfusion falls to zero, insufficiency accumulates, recharge restores perfusion without replacing or resurrecting the brain |
| Service through native MCP | 45/45 steps passed; held items, real containment, local firmware, editable paper, exactly 2 J used |
| Used chrome through two owners and restart | Passed save PID 15892 → restore PID 14688; see [Gate 8](CyberwareService.md) |

Final test evidence is retained in `TestResults/CyberwareClosure/cyberware-units-current.trx`, `cyberware-acceptance-final.trx`, `regression-candidate3.trx`, the two `restorative-biopod-*.json` files and `RestartFinal/`. The save case passed once in the candidate run; its restore case passed once in the final full run. Earlier failed or aborted runs are diagnostic history, not passed acceptance.

## Physical combat scenario

`NCBorgCoreTest.Acceptance.cs` converts a real living brain, docks the standard physical shell, equips a heavy suit and basic helmet, installs/calibrates Sandevistan and picks up/racks a real Lecter. This is a defined experimental NC combat fit, not a newly claimed canonical military shell or an expansion of the catalog.

The opposing team consists of three armored humans with Lecters; one has an active calibrated Kerenzikov. Both runs use the same scripted six-second sequence with native gun attempts, real ammunition, physics and native projectile-hit events. The second run triggers a real EMP grenade at the Borg position. A passive fixture system counts collisions with the four combatants; it cannot change damage or projectile behavior. Armor-stopped hits and mechanical Borg damage do not have to create organic wounds.

Final observed runs:

| Measurement | No EMP | Team uses EMP |
| --- | ---: | ---: |
| Accepted Borg shots | 10 | 0 |
| Accepted team shots | 33 | 33 |
| Physical hits on combatants | 7 | 9 |
| Hits on Borg | 4 | 9 |
| Organic wound records | 3 | 0 |
| Remaining frame cell charge, J | 1824.094 | 0 |
| Borg condition | Stable | ClinicalArrest |
| Team conditions | Stable / Stable / Compensating | Stable / Stable / Stable |

The first run shows real injuries to the team while the Borg remains stable. The second demonstrates loss of fire and life support after a native EMP pulse. It does not test throwing tactics, cover, movement decisions, evacuation or a player-operated fight. The offstage surgical fixture remains physically present. Results depend on this loadout and random shot sequence; they do not establish universal competitive balance.

Raw results: `TestResults/CyberwareClosure/combat-emp-False.json` and `combat-emp-True.json`.

## Measurement limits

Each combat run measures twelve **30-tick batches**. The mean wall time divided by ticks was **25.02 ms** without EMP and **30.19 ms** with EMP. P95 across those twelve normalized batch averages was **34.26 ms** and **41.95 ms**. These are not individual-frame or server-tick percentiles.

The measured process CPU windows were 12.09 s and 18.86 s; working sets were approximately 1.30 GB and 1.32 GB. They include the headless client/server harness, fixture support and runtime overhead, on a host also used for development. No production FPS, supported player count or city population capacity follows from them. A deployment-scale load test remains separate from this initial feature baseline.

The explicit medical-boundary baseline also passed 100 measured iterations after five warm-up iterations, with all three replacements and the real fourth power-bus module. Its structured `NC_CYBERWARE_GATE0` output is retained in the final integration TRX.

`audit_balance.py` exported all 17 profiles with source hashes to `TestResults/CyberwareClosure/Balance/`. Sandevistan's intact nominal bounds remain 131.2 J per six-second burst, 137.2 J over the burst/recovery cycle and 3.811 W cycle average for that module. These are prototype calculations, not observed combat endurance. No coefficients, catalog types or population settings were expanded on their strength.

## Runtime fixes and graphical verification

The acceptance work fixed component resolution in public Borg conversion/docking/undocking and finish-application APIs; a UID-only call now resolves its own `Entity<T?>` copy. Detachment treats valid slot-contained implants as contents of their anatomical host, so an arm with a deployed integral blade can detach and lose attack permission while retaining its real module.

World-loaded assemblies now rebind existing containers during `ComponentInit`, and saved disruption clocks restore during `ComponentStartup` as well as `MapInit`. Fresh empty biopods accept a legitimately absent pharmacology snapshot, and optional battery lookups no longer emit errors for ordinary humans.

Native graphical MCP verification confirmed that the surgical window's title/close region fits 1280×720 at bounds (69,15,1141,688). Retained buttons accepted native Incision and Debridement clicks through the same snapshot handles across periodic updates; Close also worked. Evidence: `TestResults/CyberwareClosure/native-observations.json`, [Incision frame](../../../artifacts/ss14-mcp/capture-822f1822d7b44c36836de74b8c1cc227.png), [Debridement frame](../../../artifacts/ss14-mcp/capture-c6ca860252b24783a009acca10db2e50.png).

Legacy test fixtures were corrected to omit fields newer than their declared schema, retire a source before restoring its hardware IDs elsewhere, represent complete incision geometry and separate single-organ power checks from a shared artificial heart. Production validation and duplicate protection were retained.

Resource validation also exposed obsolete `Action.icon` definitions and an undefined `Wrenching` quality. All five cyberware actions now use the native `BaseAction`/`Sprite` pattern, and manual wrench operations reference the actual `Anchoring` quality. The existing 15 ability integration cases passed again after these prototype changes (`abilities-resource-fix.trx`).

## Build and resource boundary

The server, client, shared, database, unit, integration and linter projects built for the private cohort. Current medical code ran against a frozen META snapshot; META test fixtures were excluded. Diagnostic-only compatibility changes and the prior test-pair baseline are recorded in `TestResults/CyberwareClosure/cohort-adjustments.json`. Original META source was not modified. The common test loader only adds the passive medical impact observer alongside the other agent's existing hooks. `medical-source-hashes.json` records the final medical source/resource inputs; `final-cohort-hashes.json` identifies the retained binaries.

The separate **current workspace** MCP server build was repeated at 16:54 UTC after the other agent fixed the earlier META `uint`/`int` mismatch. It still failed with ten compile errors in `Content.Meta/Language/MetaCompiler.Semantics.cs`: the `Session` constructor has no `semantic` argument, `_semantic` is inaccessible, and two fields trigger warnings treated as errors. Evidence: `TestResults/CyberwareClosure/native-current-build-latest.json` and [MCP artifact](../../../artifacts/ss14-mcp/validation-591fd9b55db9494db3d95bf91c137cb9.json). The private feature results do not certify that evolving META tree or a clean joint build.

The complete client/server YAML linter ran again after the prototype fixes: **zero medical/cyberware errors**, with two remaining errors elsewhere after 225,893 ms. The retained META snapshot has obsolete `Damageable.damageContainer` in `_NC/Meta/network.yml:129`; `_NC/City/city-zones.yml` contains an invalid empty `NCZoneId`. Full resource validation therefore remains failed, rather than being reported as a global pass. Evidence: `TestResults/CyberwareClosure/yaml-verified.log` and `yaml-verified-exit.txt` (exit -1). Those unrelated sources were not edited by this acceptance work.

All owned native MCP fixtures/environments were cleaned up. The engine was not edited for this acceptance work.
