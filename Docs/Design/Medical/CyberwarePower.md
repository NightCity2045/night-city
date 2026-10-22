<!-- SPDX-FileCopyrightText: 2026 Astro -->
<!-- SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0 -->
<!-- SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md. -->

# Cyberware Gate 2: physical body power

## Player flow

`NCImplantPowerBusMk1` is a physical implant in the thoracic power bay. It holds
one main vanilla power cell and an optional isolated life-support reserve. The
holder and `NCCyberwarePowerCell` are fabricated empty. The latter has a vanilla
`BatteryComponent` with 64,800 J capacity; ordinary SS14 cells also fit and retain
their actual, usually smaller capacity. Use existing equipment to recharge cells.

Fit a held cell to a detached holder, then implant the holder through the existing
surgical field. Context actions distinguish main and reserve sockets. Installed
service appears on the patient's context menu only with real operative access:
matching chest incision, sufficient depth/retraction, opened bone casing, and no
closure sutures. Legacy sites require an opened cavity/casing and retracted tissue.
Every action rechecks access, range, blockers, held item and captured socket.
Stock item-slot actions stay locked, including when detached; only the checked
service operation temporarily unlocks its own socket. There is no consent check.

Warnings distinguish low charge (initially 20%, configurable), reserve operation
and unavailable power. Ordinary diagnostics show the actual supply fraction.
Exact admin diagnostics add `SupplyCharge`, `SupplyFraction` and `PowerGranted`;
their old `CurrentPower` field remains zero for compatibility. The assembly bench
has a separate vanilla battery slot and consumes its configured test energy
(initially 1 J) on a successful check. The check does not charge the arm.

## Ownership and simulation

All operating charge lives on physical vanilla batteries. Module and snapshot
`CurrentPower` fields are legacy compatibility fields, always zero in current
state. Old profile capacities remain solely to validate pre-12 saves. Repair
profiles no longer provide electricity. Explicit administrative rejuvenation can
refill existing real cells; ordinary service, reinstall and restore cannot.

The existing cyberware system schedules one update per body and queries only its
bounded physical organ/module containers. No world scan, per-implant charge pool
or `PowerCellDraw` duplicates the aggregate debit. The bus allows initially 40 W,
scaled by holder integrity, independently of cell capacity. It assigns whole
consumer demands in stable priority/instance-ID order: life support, control and
senses, professional functions, combat. Reserve charge can serve only life support.
A missing or empty reserve never creates an emergency timer.

Intervals split at battery exhaustion, thermal shutdown/restart, EMP expiry and
attachment start. Loaded heat/work requires powered electronics and required
neural control. Shutdown remains latched until the derating threshold. Heat damage
integrates only actual time and excess above its threshold, including cooling from
an EMP spike. Unpowered remaining time cools. Source removal and external charge
changes refresh grants; checkpoints advance the aggregate only once per body time.

Cardiac aggregation now takes the minimum of medical organ capacity and physical
cyberheart output without writing the result back into `CurrentCapacity`. A
stopped cyberheart cannot provide cardiac output, and an organic heart does not
read the cyberware bus. Parallel organ topology and biopod support remain Gate 4.

## Food and EMP

`NCImplantBioconverterMk1` is an optional physical implant in its own thoracic bay.
One working converter per body can charge the main cell: initially 3 W, 120 J per
nutrition point, protected nutrition 100. Integrity scales throughput. A missing,
destroyed, EMP-disabled or shut-down converter does not convert food; a missing or
full cell consumes no extra nutrition. This route does not recharge the reserve.

Conversion reads assimilated nutrition through `HungerSystem`, subtracts through
its authoritative API, measures the actual decrease and credits only corresponding
joules through `SharedBatterySystem`. Unaccepted energy returns its nutrition.
A one-second conversion boundary bounds long-interval charge/work feedback. There
is no eat-click reward, parallel converter multiplier or offline conversion.
Normal biological decay remains in the existing nutrition system.

The narrow NC-marked extension to content `SharedEmpSystem` gives an area pulse
one `PulseId` across targets. Body handling uses the same ID to expose contained
cells to vanilla battery EMP effects. Each admitted cell retains a bounded runtime
pulse cache, preventing area/body processing from debiting it twice in either
order. Electronics receive their own profile-defined surge/heat/neural effects.
Runtime replay caches are not saved. No engine or remote-control path was added.

## Snapshot version 12

`BodyPowerBus` saves holder sockets, cell prototype/stable ID, actual charge and
remaining self-charge cooldown, plus current nutrition and thermal latch state.
Cloning is deep. Restore resets the power clock to current active-world time;
neither cooldown rebasing nor downtime produces food conversion.

Pre-12 migration validates and clears old module charge on a clone, then adds one
holder/main cell for powered cyberware in the thoracic power bay. It transfers the
sum capped at the real 64,800 J capacity; excess is discarded. IDs derive
deterministically from the old stable identities. Current snapshots never issue
another cell or refill a missing socket. Invalid shadow charge, duplicate IDs,
abstract/incompatible cells and out-of-range charge are rejected before restore.
The versioned medical document needs no EF/table migration.

The global durable transfer ledger and arbitrary extra payload on loose items
remain Gate 8. Ability activation reservations will be connected to actual ability
executors in Gate 6. This stage does not implement biopod feeding or full Borg.

## Verification

`NCCyberwarePowerMathTest` covers limits, priorities, stable ordering, reserve
isolation, conversion bounds and heat integration. The new integration fixture
authors ten cases for physical access/reinsertion, aggregate draw, finite reserve,
long-interval depletion, actual food use, both EMP orders, cardiac output,
one-time migration and current charge/nutrition/identity restore. Existing Gate 0
and cyberware fixtures now use real test supplies.

Validation recorded on 2026-10-22 with SDK 10.0.103:

- Dependency restore succeeded. The final full
  `dotnet build Content.IntegrationTests/Content.IntegrationTests.csproj -c DebugOpt --no-restore /m:1 /nr:false`
  compiled shared, server, client and test projects with zero errors. No diagnostic
  source subtree was excluded. The YAML-linter project also built with zero errors
  using these dependencies and `/p:BuildProjectReferences=false`.
- The targeted `Content.Tests` filter for `NCCyberwarePowerMathTest` and
  `NCCyberwareConditionTest` passed all 15 cases (10 power cases, 5 condition cases).
  VSTest used `RunConfiguration.DotNetHostPath` and required execution outside the
  sandbox for its local test-host socket. Pure simulation math was unchanged by
  the subsequent presentation and malformed-snapshot guards.
- A static locale check found matching EN/RU sets of 16 power keys and all eight
  literal power-localization references present. No RSI assets were changed.
- The first standard YAML-linter pass reported no cyberware errors and one
  unrelated failure in `Resources/Prototypes/_NC/City/city-zones.yml`:
  `Expected a non-empty GUID for NCZoneId`. The repeat pass after localized profile
  names produced the same single unrelated error (150,164 ms). The repository-wide
  resource check therefore remains failing; both passes found no cyberware errors.

Build logs, `unit.trx` and YAML output are in `TestResults/CyberwareGate2/`.
Integration scenarios are compiled only and are not executed at the user's
direction. No in-game client pass has been run; compilation does not establish
runtime gate acceptance. Gate 0 baseline measurements remain uncollected.

Suggested changelog:

```text
:cl:
- add: Cyberware can share a physical body battery and an isolated life-support reserve.
- add: An optional bioconverter turns real assimilated nutrition into main-cell charge.
- fix: Cyberhearts stop providing cardiac output when power is unavailable.
- fix: Repairing or reinstalling cyberware no longer creates charge.
```
