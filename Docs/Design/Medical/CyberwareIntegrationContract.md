<!-- SPDX-FileCopyrightText: 2026 Astro -->
<!-- SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0 -->
<!-- SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md. -->

# Cyberware implementation: Gate 0 integration contract

This records the original inspected baseline. [Gate 2](CyberwarePower.md) replaces
individual module energy and wires cyberheart output into physiology. Its fixture
now uses real shared supplies and asserts the cardiac coupling, but has not been
executed; the original Gate 0 baseline is still not runtime-accepted. Future
performance reports count body power advances rather than independent energy
advances for each module.

## Scope and evidence

Source baseline: `612529554ec12457ca9c2c34f86b9a8631e23ef0`, inspected locally on
2026-10-22. This is the same revision audited by the
[cyberware GDD](https://app.notion.com/p/GDD-4018867306b1807fa573c0d18052296b).
The companion requirements are in
[the master prompt](../../../.plan/Night_City_2045_Cyberware_Master_Prompt.md).

Gate 0 records existing integration boundaries and supplies a repeatable runtime
scene. It does not introduce modular assembly, a power bus, Humanity gameplay or
Full Borg. A profile, event declaration or passing diagnostic probe does not prove
that the corresponding player feature works.

Owner decisions take priority over implementation and balance proposals. Preserve
physical items and the existing organ graph; use vanilla battery charge and real
nutrition; initialize new characters with 100 Humanity; keep ordinary player
control during crises; allow physically possible surgery without consent gates;
exclude remote implant control and AI dependencies. Other GDD numbers remain
proposed balance values.

## Existing ownership and APIs

Paths below are relative to the repository root.

| State | Existing owner / path | Integration boundary |
| --- | --- | --- |
| Live organ graph | `Content.Shared/Body/`, `Content.Shared/_NC/Medical/Anatomy/` | `BodyComponent.Organs`, parent/child organ relationships; no second anatomy graph |
| Physical detachment | `Content.Server/_NC/Medical/Anatomy/Detachment/NCOrganDetachmentSystem.cs` | `CanDetachOrgan`, `TryDetachOrgan`, `CanAttachOrgan`, `TryAttachOrgan`; exact subtrees and rollback |
| Implant placement and condition | `Content.Server/_NC/Medical/Cyberware/NCCyberwareSystem.cs` | `TryInstall(in NCCyberwareInstallRequest)`, `TryRemove(in NCCyberwareRemoveRequest)`, `TrySetNeuralInterface`, `TryApplyService`, `TryApplyMechanicalTrauma` |
| Surgical access and player intent | `Content.Server/_NC/Medical/Surgery/NCFreeformSurgerySystem.cs`, `NCFreeformSurgeryEffectSystem.cs` | Session, sequence, revision, tool, geometry and access checks; effects call the physical mutation APIs |
| Timed body boundary | `Content.Server/_NC/Medical/Physiology/NCPhysiologySchedulerSystem.cs` | `Schedule`, `ProcessNow`; medical dirty events and requested next deadlines |
| Life and consciousness | `Content.Server/_NC/Medical/Physiology/NCPhysiologySystem.cs` | `EvaluateBody`; organ functions, perfusion, oxygenation and irreversible state |
| Movement, grip and senses | `Content.Server/_NC/Medical/Function/NCBodyFunctionSystem.cs` | `RefreshBody`, sanitized `NCBodyFunctionComponent`; health is normalized to 0–1 |
| Drug exposure | `Content.Server/_NC/Medical/Pharmacology/NCPharmacologySystem.cs` | `ApplyDose`, `AdvanceBody`, `GetModifiers`; concentrations, interactions and organ-dependent clearance |
| Actual electrical charge | `Content.Shared/Power/EntitySystems/SharedBatterySystem.API.cs` | `GetCharge`, `ChangeCharge`, `TryUseCharge`, `SetMaxCharge`; no copied body charge |
| Physical battery cell | `Content.Shared/PowerCell/PowerCellSystem.API.cs` | `TryGetBatteryFromSlot`; service access must guard ordinary insertion/ejection too |
| Actual nutrition | `Content.Shared/Nutrition/EntitySystems/HungerSystem.cs` | `GetHunger(HungerComponent)`, `ModifyHunger`, `SetHunger`; converter must measure the amount really spent |
| Medical persistence | `Content.Server/_NC/Medical/Persistence/`, `Content.Server/_NC/Medical/Cyberware/NCCyberwareSystem.Persistence.cs` | Semantic capture/restore, stable organ/module identity, versioned migrations and rebased time |

`NCCyberwareComponent` is deliberately not networked. Detailed diagnostics remain
capability-filtered. `NCBodyFunctionComponent` publishes sanitized gameplay
aggregates rather than implant inventories or hidden medical details.

## Event and update flow verified in source

1. Organ/container lifecycle events register or remove individual cyberware
   scheduling entries. `NCCyberwareSystem.Update` advances due modules through its
   own queue; it is not currently driven by the body's physiology scheduler.
2. Implant mutations emit `NCCyberwareInstalledEvent`, `NCCyberwareRemovedEvent`
   and/or `NCCyberwareStateChangedEvent`. At the baseline there are no production
   subscribers to these three events. Existing body-function refreshes instead
   arrive through `NCMedicalBodyChangedEvent` and organ lifecycle events.
3. Cyberware marks anatomy/function/consciousness/diagnostics/persistence dirty.
   Motor and sensory aggregates can refresh immediately; body physiology is
   scheduled. The implant mutation itself does not synchronously evaluate life.
4. `NCPhysiologySchedulerSystem.ProcessNow` raises
   `NCMedicalScheduledUpdateEvent`, advances bleeding and systemic state, then
   evaluates physiology. Pharmacology subscribes to the scheduled event.
   `NCPhysiologyEvaluatedEvent` drives the final body-function refresh.
5. Detachment is a distinct boundary: `MarkBodyChanged` also calls
   `ProcessNow(body, TimeSpan.Zero)` synchronously. Zero elapsed time prevents
   invented bleeding but does not suppress missing-vital-organ consequences.

Later stages must explicitly order power, thermal/structural state, drug exposure,
life-critical functions, physiology and H/S. Advance the old configuration before
changing it; do not rely on incidental event subscription order or add a second
expense for the same battery consumers.

## Capability consumers and unresolved contracts

| Area | Source-confirmed baseline | Required change / gate |
| --- | --- | --- |
| `Movement`, `Grip`, `Vision` | Body functions read anatomy and `NCCyberwareComponent.FunctionalEfficiency`; they do not dispatch on strings in `Capabilities` | Keep functional health separate from rated strength and ability modifiers; Gates 1/6 |
| `CirculatoryAssist`, `NeuralInterface`, `SmartDeviceBus`, `ServiceHatch` | Profiles declare these labels; labels have no matching gameplay dispatcher | Add actual consumers only through the owning systems; declaration is not feature acceptance |
| Cyberheart | `GetVitalFunctions` reads `CurrentCapacity / BaseCapacity` without cyberware efficiency | An unpowered heart must stop contributing circulation; Gate 2, prerequisite for Gate 4 |
| Reserve organs | Vital functions and pharmacological organ clearance use the minimum matching normalized capacity | Explicit serial/parallel/standby topology; an inactive reserve must not disable a working primary |
| Electrical power | `AdvanceModule` spends each module's `CurrentPower`; repair profiles can also restore it | Replace with one aggregate vanilla-battery expense and a one-time migration; Gate 2 |
| Heat over long intervals | Power availability is sampled at interval start; overheat damage uses final heat for the interval | Split at exhaustion/thermal thresholds; do not generate work after shutdown; Gate 2 |
| Permanent defects | Integrity repair can reach profile maximum; no permanent damage ceiling or large-part assembly exists | Real replaceable parts and nominal-integrity denominator; Gate 1 |
| Brain transfer | Missing Brain sets irreversible state during ordinary synchronous detachment evaluation | Supported atomic core transfer with real life support; ordinary unsupported removal stays dangerous; Gate 4 |
| Borg drugs | Clearance expects metabolism/filtration organ functions; profile has no H/S effects | Artificial providers and real delivery, then Humanity/crisis modifiers; Gates 4/5 |

The existing `LivingCity/CityServices/NCHumanityRegistrySystem` already owns a
persisted ActorId aggregate (`living-city.humanity`) and emits committed crisis
transitions to `NCCyberpsychosisIncidentBridgeSystem`. The medical installation
path does not call its `ApplyCyberwareCostAsync` API. Its permanent cumulative
loss and H-only stage model do not implement GDD burden/adaptation/Hmax or S.
Gate 5 must decide migration and authoritative ownership before adding another
Humanity store. The city bridge may observe committed transitions; medical
cyberware must function independently of city cognition.

The medical snapshot is version 10 (`SpatialSurgery`) at this baseline. Preserve
its existing stable identities, captured-time rebasing and exclusion of runtime
replay/session state. Reconnection or shell changes must not rerun initialization
or migration, refill charge, clear drugs or recreate a second physical item.

## Repeatable scene and measurement

Fixture:
`Content.IntegrationTests/Tests/_NC/Medical/NCCyberwareIntegrationContractTest.cs`.
It loads a healthy `MobHuman` medical snapshot with the existing cyberarm, optics
and cyberheart, then uses public mutation APIs. Snapshot restore is setup only;
this does not establish that the same operation is reachable through player UI.
There is one measured subject and one unmeasured source/template body.

| Scenario | What it proves or records |
| --- | --- |
| `CyberarmPowerLossAndServiceReachOnlyTheAffectedGrip` | Module drain disables its grip, other grip/movement remain intact, service restores the function |
| `CyberopticsPowerLossAndServiceReachVision` | Module drain and service reach the vision aggregate without a manual function refresh |
| `ReportCyberheartPowerLossIntegration` | Reports actual versus target cardiac output after power loss and explicit physiology evaluation |
| `ReportOrdinaryBrainDetachmentBoundary` | Reports irreversible state across detachment/reattachment of the same physical brain; no biopod is present |
| `CaptureExplicitMedicalBoundaryBaseline` | Five warmups, then 100 explicit one-second module/body boundaries for three replacements; reports elapsed time, thread allocations and function-refresh deltas |

JSON records use the prefix `NC_CYBERWARE_GATE0` in NUnit output. The heart and
brain scenarios are diagnostic probes, not acceptance tests for life support.
In particular, a passing probe with `MeetsTarget: false` records a missing
integration; it must never be presented as a working cyberheart. Turn the heart
target into an acceptance assertion when Gate 2 implements its coupling.

Measurement excludes spawning, restore, serialization, database IO, networking,
real server ticks and the timing of queue-budget deferrals. Simulated time is
advanced through explicit APIs, not the game clock. No absolute timing threshold
is asserted. These numbers are a local comparison baseline, not server p95/p99
or a player-capacity claim. Measure actual server ticks and client traffic in
Gate 9 on the target hardware.

## Validation and completion

Prepare/build without starting a runtime:

```powershell
dotnet restore Content.IntegrationTests/Content.IntegrationTests.csproj
dotnet build Content.IntegrationTests/Content.IntegrationTests.csproj -c DebugOpt --no-restore /m
```

After the repository's required explicit integration-test authorization, run:

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj -c DebugOpt --no-build --filter "FullyQualifiedName~NCCyberwareIntegrationContractTest" --results-directory TestResults/CyberwareGate0 --logger "trx;LogFileName=cyberware-gate0.trx" -- NUnit.ConsoleOut=0
```

Gate 0 is complete only when the fixture builds, the selected runtime scenarios
run, the JSON results are inspected and the baseline measurements are recorded.
Unimplemented GDD contracts stay explicitly assigned to their later gates. No
manual in-game surgery or client presentation is validated by this server fixture.

Validation recorded on 2026-10-22 with SDK 10.0.103:

- Restore succeeded. The first build of the integration project and its
  dependencies succeeded before the final fixture setup adjustment.
- The final fixture code compiled successfully with
  `dotnet build Content.IntegrationTests/Content.IntegrationTests.csproj -c DebugOpt --no-restore /p:BuildProjectReferences=false /m:1 /nr:false`,
  using the dependencies produced by that successful build.
- A full repeat build encountered concurrent work outside Gate 0:
  `Content.Shared/_NC/Diagnostics/NCDiagnosticProjection.cs:96`, CS0136, duplicate
  local name `reference`. That file and the concurrent diagnostic/project changes
  were left untouched. The isolated fixture build does not validate those changes.
- Source review checked replacement snapshot component shapes, including the
  additional nerve section required by cyberoptics. No runtime result is inferred
  from that review or compilation.

The user explicitly requested preparation without integration-test execution.
Runtime scenarios and numerical baseline measurements remain pending; Gate 0 is
prepared but not yet accepted. No in-game/client pass has been run.
