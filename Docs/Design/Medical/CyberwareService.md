# Cyberware world continuity and local service — Gate 8

Implementation date: 2026-10-24. Authority: [cyberware GDD](https://app.notion.com/p/GDD-4018867306b1807fa573c0d18052296b), sections 19, 23, 24 and 31. Gate 8 functional acceptance completed on 2026-10-24: the two-character used-chrome cycle survived an actual process restart. The isolated build boundary is stated below.

## Physical state and persistence

`NCCyberwareComponent.Service` is private physical item data. Both existing persistence paths carry it: medical snapshots for installed modules/anatomical replacements, and generic entity YAML for loose items, held inventory and containers. There is no second character ID, item balance, market database or client authority. The existing round coordinator commits the medical, possessions and world sections together.

Medical version 17 adds firmware and bounded service history. Versions through 16 adopt standard firmware once, retaining already known live service state for the same technical ID. Older schema payloads cannot inject future service fields. Deep cloning and shape validation precede medical graph writes. Ordinary transfer preserves item ID, defects, parts, heat, exterior, battery and cooldown; H/adaptation remains on the actual character brain.

Generic YAML captures a monotonic clock at serialization. Restoring rebases the remaining EMP interval, retains the already captured cooldown remainder, and clears active execution/bindings. The existing battery component owns charge; its recharge deadline already uses the native time-offset serializer. No elapsed calendar time is applied. An event-maintained index covers modules, replaceable parts and physical cells. Medical restore rejects identities already present outside the target's actual medical graph before mutating it. Generic restored conflicts reject the new node; the existing entity is retained. Possessions/map staging checks the rejected tree before replacing existing roots.

Medical loading uses the same selected committed round manifest as world and possessions. Failed capture of a bound or disconnected live medical body fails the common checkpoint instead of silently committing its older cached record. Disconnected bodies remain checkpoint sources while physically present, so post-logout extraction is captured. This is checkpoint durability, not a per-action database transaction or crash-proof write-ahead log. Abrupt failure before a common checkpoint returns to the previous committed world.

## Service workflow

Put a loose cyberware item on `NCCyberwareWorkbench`. All cyberware can be mounted for service; assembly operations remain available only for actual modular assemblies.

Use a held firmware card on the bench. The server checks physical reach, interaction ability, expected module/revision, compatible profile and real bench battery energy. `NCFirmwareStandard` permits normal active modes; `NCFirmwareServiceLock` inhibits active abilities while leaving passive life support unchanged. Neither profile repairs damage, resets cooldowns, adds charge or changes humanity. Cards are reusable physical media. A stale revision fails. Reflashing invalidates the assembly bench test.

Use a held blank `Paper` on the bench to write a report. The server consumes bench energy and writes the current serial, configuration/service revisions, firmware, condition and test status on that same paper entity. Paper remains ordinarily editable: it is a document, not proof of ownership or an authorization token. External claims cannot rewrite the technical instance ID. No wireless personal-cyberware write channel is added. Existing META integration is owned separately and was not modified.

## Audit and administration

The item retains 64 significant service entries, monotonic revisions, original resolution IDs and the existing stable medical owner namespace where available. Older entries are counted rather than silently resetting revision. Install/removal, anatomical transfer, part configuration, repair, bench test and firmware operations are recorded. Technical graph rollback does not invent a successful sale. Fine-grained damage/thermal telemetry and economic transactions remain in their owning systems; this list is not a replacement for their logs.

Admin-only `nccyberwarestatus <net uid>` reads a body or loose module and prints exact physical IDs, firmware, defects and service history, plus the body's H/B/S, stage and adaptation/stimulus counts. Existing `ncmedstatus` supplies the medical graph and drug diagnostics. The new command performs no repair, identity reset or implicit recovery. Normal clients receive no new private hardware component state.

## Acceptance evidence

The user authorized integration execution. The final targeted run passed **102/102**, with no skips (`TestResults/CyberwareClosure/cyberware-acceptance-final.trx`). The current cyberware/Borg/humanity unit run passed **113/113** (`cyberware-units-current.trx`).

`NCCyberwareRestartTest` executed in two different OS processes:

- Save PID **15892**: create two actual character profiles in an on-disk production SQLite database; install, extract, physically service, locally reflash and transfer the same Sandevistan; store seller revision 2 and buyer revision 1; serialize a separate used modular arm and the actual printed paper through native world YAML.
- Restore PID **14688**: open that same database in a fresh server/client test host; compare module GUID, service journal and both owner keys, condition, permanent damage, heat, calibration, firmware, real cell charge, H and cooldown. The loose arm retains all three physical part IDs and a rebased **13-second** EMP remainder. The controller retains **23 seconds** of cooldown and restores inactive.
- Reject the seller's pre-sale medical snapshot while the buyer physically owns the module. Reject a stale SQLite revision without replacing the newer payload. The paper remains editable and contains the real module serial.

Save evidence is the single passed save case inside `regression-candidate3.trx`; restore evidence is the single passed restore case inside the final 102-case TRX. Both phase PID files, the actual SQLite file, medical YAML and possession YAML are under `TestResults/CyberwareClosure/RestartFinal/`. These are two real test-host processes, not two loads in one process. This exercises production persistence adapters and serializers; it does not simulate an abrupt crash between common round checkpoints.

The restart exposed two lifecycle defects that are now fixed. Saved post-map-init assemblies bind their existing part containers during `ComponentInit`, without manufacturing defaults. Saved world clock stamps restore during `ComponentStartup` as well as `MapInit`, because the native loader does not repeat `MapInit` for already initialized saved entities.

The native MCP service scene also passed **45/45** steps, spending exactly **2 J** on actual local flashing and printing. Evidence: [scenario](../../../artifacts/ss14-mcp/scenario-4e7745b4c77744babe889e02c30cbe8c.json), [HTML report](../../../artifacts/ss14-mcp/report-5e24bb7e5e5f405eb25aab8a739fa58e.html).

## Validation boundary

The integration run used current cyberware/medical code with a frozen META source/resource snapshot because another agent was changing META concurrently. Original META source was not edited. Diagnostic-only aliases, compatibility adapters and omission of a duplicate META terminal verb subscription exist only in the retained private cohort. Its test-pair loader uses the prior baseline plus the passive medical collision observer. META test sources were excluded from that cohort.

The exact adaptations are recorded in `TestResults/CyberwareClosure/cohort-adjustments.json`; original and compiled-copy manifests and build inputs are retained under `artifacts/cyberware-acceptance/6dcc030752d84236a9897f1d268d5577/`. META prototypes were copied; other resources use workspace junctions. This acceptance does not certify concurrent META behavior or replace the joint workspace build check recorded in [Gate 9](CyberwareBalance.md).

`Tools/_NC/Cyberware/verify_restart.ps1` runs both phases in separate processes against a selected integration assembly and requires exactly one passed case per phase. Integration remains explicit opt-in. Earlier failed, aborted and compile-only runs remain historical diagnostics and are superseded by the final evidence above.

:cl:
- add: Cyberware retains service history and local firmware through existing saves; benches accept physical firmware cards and can fill an editable paper inspection report.
- fix: Restored cyberware retains its physical assembly parts and remaining disruption timers after a server restart.
