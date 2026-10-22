<!-- SPDX-FileCopyrightText: 2026 Astro -->
<!-- SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0 -->
<!-- SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md. -->

# Cyberware Gate 1: physical arm assembly

Gate 1's record below describes the initial assembly slice. [Gate 2](CyberwarePower.md)
subsequently replaces individual module charge, adds a real bench test battery,
and advances medical snapshots from 11 to 12.

## Playable slice

The existing industrial cyberarm is now a frame with three physical child items:
actuator, controller and sensor interface. Both left and right versions use the
same compatible part family. The frame retains its own identity and integrity;
internal parts are container contents, not extra nodes in the human organ graph.

Spawn `NCCyberwareWorkbench`, a detached `NCOrganHumanCyberArmLeft` or
`NCOrganHumanCyberArmRight`, and the three optional spare items
`NCCyberarmActuator`, `NCCyberarmController`, `NCCyberarmSensor`.
For assembly from loose parts, use `NCOrganHumanCyberArmLeftFrame`, which starts
with three empty required slots instead of factory parts.

1. Hold the detached arm and use it on the bench.
2. Examine the bench at close range for each node's current, serviceable maximum
   and nominal integrity, missing slots, configuration revision and check status.
3. Use the context menu to remove a node. The same item is picked up or left beside
   the bench. Use a compatible held part on the bench to fill its empty slot.
4. Use steel sheets on the bench to repair one damaged node per sheet. The frame
   is repaired first, then parts in stable slot order. No sheet is spent when
   nothing can be repaired. This service does not add charge or reset defects.
5. Run the context-menu bench check, then remove the arm. Changing a part or
   damaging/servicing the assembly invalidates the previous check.

Missing or destroyed required parts set assembly efficiency to zero. The existing
cyberware function calculation reads that cached result, and ordinary body dirty
events propagate it to grip. A higher-rated strength or an active weapon is not
implemented by this stage. Assembly maintenance currently requires a detached
item; installed service access is a later surgery/service concern.

## State and boundaries

- `Content.Shared/_NC/Medical/Cyberware/NCCyberwareAssembly.cs` contains data-only
  components, prototype definitions, bounded snapshots and the pure condition
  calculation. Detailed part condition is not broadcast in a networked component.
- `NCCyberwareSystem.Assembly.cs` owns creation, cache invalidation, transmitted
  damage, identity validation and transactional part restore. It extends the
  established physical owner rather than introducing a second cyberware system.
- `NCCyberwareSystem.Workbench.cs` handles physical held-item interactions and
  server-generated context verbs. Every action rechecks interaction range,
  action blockers, held items and the actual workpiece. Remove/test verbs also
  check the captured workpiece and revision. There is no new client protocol or
  hidden item mutation from UI.
- Required-slot prototypes define a small compatibility family. A controller
  cannot occupy an actuator slot, and an occupied slot rejects insertion without
  moving either item. New fabricated arms are preassembled; a saved incomplete
  arm stays incomplete.

`PermanentDamage` is local to the frame or individual physical part. Repair stops
at `nominal * (1 - PermanentDamage)`, while efficiency still divides by nominal.
Replacing one node leaves all other nodes and their permanent damage intact.
Large impacts transmit damage beyond the frame's content-configured threshold;
damaging heat accumulates permanent loss continuously. Normal safe operation does
not randomly destroy a node. Explicit administrative rejuvenation can clear
defects on existing nodes; it does not fabricate missing parts.

The bench check verifies completeness, usable frame/parts, charge, temperature and
EMP state on the bench's own service interface. It is not the patient's neural
calibration and does not reconnect the implant to a person. The old individual
`CurrentPower` mechanism remains until Gate 2. Its known long-interval thermal and
power limitations are tracked in the Gate 0 contract.

## Saving and migration

Medical snapshot version 11 (`ModularCyberware`) includes frame permanent damage,
configuration/check revision and each contained part's prototype, stable ID,
integrity and defect. Collection sizes, slot compatibility, repair ceilings and
duplicate identities are checked before restoration. Cloning and timestamp
rebasing preserve the nested state; replay/session tokens remain runtime-only.

Versions 1–10 migrate the former monolithic factory arm to its original three
parts. Their IDs derive deterministically from the existing item's ID and slot.
Only old-format migration creates this default configuration. Current snapshots
with missing parts keep those slots empty, and invalid current snapshots are
rejected instead of silently refilling them. The existing medical payload table
already stores a versioned document, so no relational schema/EF migration changes
are required.

Contained-part state is covered by the existing medical/organ snapshot boundary.
Detached loose parts retain data on their ECS components. This stage does not
claim a completed city-wide durable transfer ledger for all loose world items;
that belongs to Gate 8. Full Borg, Humanity, the battery migration and expanded
assembly UI remain later gates.

## Validation

`NCCyberwareConditionTest` covers permanent repair ceilings, nominal-denominator
efficiency, thresholded irreversible damage and invalid numeric inputs.
`NCCyberwareAssemblyTest` covers physical removal/refitting, occupied-slot
rejection, material consumption and charge preservation, incomplete snapshots,
duplicate IDs and repeatable one-time migration. Existing cyberware fixtures now
include an assembly when constructing a current-format arm snapshot.

Validation recorded on 2026-10-22 with SDK 10.0.103:

- Shared/server code and the DebugOpt validation slice of the client, content
  tests, integration-test project and YAML linter compiled with zero errors.
  Concurrent `_NC/Diagnostics` work prevented a full client build (RA0002 in
  `NCDiagnosticsClientSystem.Replication.cs`). A temporary MSBuild target excluded
  only that separate diagnostic subtree for the validation slice; project files
  and diagnostic sources were not changed by this feature. The builds used
  `--no-restore /p:BuildProjectReferences=false /m:1 /nr:false` after dependencies
  had been built. Logs and the exclusion target are under
  `TestResults/CyberwareGate1/`.
- The targeted `NCCyberwareConditionTest` run passed all five cases. The TRX result
  is `TestResults/CyberwareGate1/condition.trx`. VSTest required execution outside
  the filesystem sandbox because the sandbox denied its local test-host socket.
- The standard YAML linter was run twice. Its initial report exposed missing
  `woundProfile` values in the existing cyber-organ payloads, also inherited by
  the new empty frame; these now name `NCHumanWoundSystem`. The final report has
  no cyberware errors and one unrelated error in
  `Resources/Prototypes/_NC/City/city-zones.yml`: `Expected a non-empty GUID for
  NCZoneId`. The full repository resource check therefore remains failing.
  The final output is `TestResults/CyberwareGate1/yaml.log`.
- The six new integration scenarios compiled but were not executed, following
  the user's instruction. No in-game client pass was run. Runtime acceptance of
  the assembly flow remains pending; compilation does not establish all GDD
  T02–T12 outcomes. Weapon packages and patient calibration retain their later
  gate scope. No Gate 0 runtime result is implied by proceeding with Gate 1.

Suggested changelog:

```text
:cl:
- add: Industrial cyberarms now contain replaceable drives, controllers and sensor interfaces.
- add: A cyberware service bench supports physical part replacement, diagnosis and testing.
- tweak: Severe damage can permanently reduce the repairable capacity of individual cyberware nodes.
```
