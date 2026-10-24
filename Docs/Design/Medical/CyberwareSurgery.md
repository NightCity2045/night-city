<!-- SPDX-FileCopyrightText: 2026 Astro -->
<!-- SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0 -->
<!-- SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md. -->

# Cyberware Gate 3: surgery and physical service

Implements sections 10–11 and Gate 3 of the [GDD](https://app.notion.com/p/GDD-4018867306b1807fa573c0d18052296b).
Starting source revision: `359e0f3fb8e`. Gates 0–2 are present in this revision;
their recorded runtime acceptance limitations still apply.

## Player path

Use the existing interactive surgical field and its real incisions, anatomical
structures and bone barriers. Stage the held implant against a compatible body
zone, then use a multitool or the new `NCCyberwareServiceTool` to mount it at the
exposed bay. Player staging checks live bay compatibility; the older procedure
catalogue's two-profile allowlist no longer excludes power holders and converters.
Anatomical replacements still use physical graft attachment in the same field.

Simple hardware receives its functional check at mounting completion. Industrial
arms and neural links have `requiresSeparateCalibration: true`: mounting succeeds
with an explicit warning and a separate service-probe action completes calibration.
The surgeon can close the actual incision while the device remains uncalibrated.
There is no rehabilitation timer, consent check or antagonist/clinic permission
requirement in this player path. Dirt and wounds remain medical state.

The test checks actual required parts, frame/part output, neural signal, EMP,
temperature and the body's ability to allocate full-load draw alongside its
other consumers. Successful powered tests debit `calibrationEnergy` (default 1 J)
from the real main battery or an eligible life-support reserve. Output threshold
defaults to 50% and is prototype-configurable. Failure names the specific cause;
it does not remove mounted hardware or refill food, batteries or blood.

The probe also physically disconnects an exposed neural interface. The finite
`NCCyberwareServiceKit` repairs one local frame/interface or damaged assembly part
per application, using the existing profile and actual integrity. It cannot exceed
any node's permanent-damage ceiling. Parts are replaced on the existing bench;
ordinary organ/nerve repair does not bypass mechanical service. Missing parts,
incompatible mountings and exhausted supplies fail without spending another use.
These items reuse existing build sprites.

## Authority and transactions

`NCFreeformSurgerySystem` retains the existing cancellable tool DoAfter and
server-issued structure handles. Its server-created event also captures the exact
target, staged graft and assembly configuration. Completion rechecks those against
the current field, capability, hand, range and access. Staged hardware must still
be held by the operator or physically reachable outside a container. A different
item or newly swapped configuration cannot inherit an in-flight action.

Each completed contact derives a ResolutionId from the private session and action
sequence. A bounded, unsaved body receipt queue prevents a repeated physical
contact from consuming another kit use. The existing cyberware mutation receipts
also protect installation, extraction and calibration energy. This is active-round
replay protection, not Gate 8's durable loose-item transfer ledger.

Committed extraction and graft attachment invalidate neural calibration, including modules carried
inside a removed anatomical subtree. The same physical entity, stable identity,
parts, damage and batteries survive. Failed container transfer rolls back before
this invalidation. Reattaching to another patient therefore requires a new test.
Old loose-item calibration flags cannot authorize a new graft attachment.
Assembly configuration changes also invalidate calibration. Medical snapshots
already save the resulting calibration flag, so no payload/table migration is
needed. The trusted medical-restore graph path explicitly preserves calibration
during its technical detach/rollback; ordinary surgery uses the invalidating default.
Uncalibrated hardware temporarily marked `Disrupted` by EMP is also a valid saved
state; accepting that state never grants calibration during restore.

Successful contacts log the true surgeon, patient, tool, item, action identity,
stable item identity, configuration revision, consumed tool uses, calibration
energy and result through the admin log.
Existing earlier cuts, bleeding and extracted items remain when the UI closes;
no UI cancellation rewinds completed medical work.

Detailed state remains server-owned and capability-filtered. The existing field
predicts presentation only; hidden anatomy, real containers and service charge
cannot be mutated by a client prediction. No second UI framework or remote
implant-control route is introduced.

## Validation

`NCCyberwareSurgeryTest` authors contracts for simple and complex mounting,
calibration receipts and six concrete failure causes, physical disconnection and
donor transfer, permanent-ceiling service, repeated use, competing extractions,
unreachable staged hardware, technical restore rollback and uncalibrated EMP-state
restore (14 authored cases).
The older surgery fixture now expects complex grafts to await their functional test.
Integration execution remains disabled at the owner's direction.

Validation on 2026-10-22 uses the SS14 MCP connector:

- The diagnostic server build passed (385,599 ms) and the diagnostic client build
  passed (109,571 ms), with no source exclusions. Final follow-up checks are recorded below.
- An owned isolated `fixture-admin` server started. Live prototype inspection
  confirmed the probe's three capabilities and separate-calibration flag on the
  neural link. Real fixture spawns produced the probe, a service kit with ten uses,
  and a detached arm with three real contained parts, full frame integrity, no
  calibration, no supply grant and zero functional output. No field setter was
  used to manufacture a successful operation.
- Retained item snapshots are `snapshot-c42da56a77e7404c97bb99abc8267042`
  and `snapshot-16f37ecf54fb4eec972f1cc832998100`. An HTML view of the arm
  observation is `artifacts/ss14-mcp/report-9eecc2a8bac04c9b8ab541b2152f4b9e.html`.
  The fixture was destroyed and its owned environment stopped.
- A static locale check found matching EN/RU sets of 15 keys. No RSI metadata changed.
- The completed MCP YAML profile compiled the normal server/client/test slice
  and then reported one resource error after 560,553 ms:
  `Resources/Prototypes/_NC/City/city-zones.yml: Expected a non-empty GUID for NCZoneId`.
  It reported no cyberware resource errors. The repository resource check remains
  failing on that pre-existing issue. Its retained output is
  `validation-8f92a6f800e9438b9fe1acc12e07bab6`.
- After the final C#-only EMP-restore and API-compatibility adjustments,
  `dotnet build Content.IntegrationTests/Content.IntegrationTests.csproj -c DebugOpt --no-restore /m:1 /nr:false /p:EnableNCDiagnostics=true`
  compiled the full affected shared/server/client/test slice: zero errors,
  358 existing warnings, 2:45.26. This compile-only fallback is necessary because
  MCP has no separate integration-project build profile; its integration profile
  executes tests and was not used. The log is
  `TestResults/CyberwareGate3/build-final.log`.
- All 14 new integration cases are compiled only. No NUnit integration fixture
  was executed. `git diff --check` is clean for these changes.

Interactive DoAfter cancellation and complete player operations still require a
runtime acceptance pass; these bounded observations and authored fixtures alone
are not gate acceptance. No graphical client pass or NUnit integration execution
was performed.

Suggested changelog:

```text
:cl:
- add: Exposed cyberware supports functional calibration, physical disconnection and consumable local service.
- fix: Complex cyberware requires its own functional test after mounting or transplantation.
- fix: Changing a staged item or assembly cannot redirect an in-flight surgical action.
```
