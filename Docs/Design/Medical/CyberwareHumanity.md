<!-- SPDX-FileCopyrightText: 2026 Astro -->
<!-- SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0 -->
<!-- SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md. -->

# Cyberware Gate 5: humanity and pharmacology

Authority: [Cyberware GDD](https://app.notion.com/p/GDD-4018867306b1807fa573c0d18052296b),
sections 16–18, 24–25 and Gate 5; acceptance T01 and T36–T43.

## Character and configuration

The one actual brain carries `NCHumanityComponent`. A new character starts at 100 H. Existing physical
organ/mind transfers carry this same state into a biopod and between shells. An implant or frame owns
neither its recipient's adaptation history nor a second character identity.

Profiles declare functional packages on implants and assembly parts. Accounting groups matching
`AdaptationKey` values, with an optional anatomical context, taking the highest burden and acute cost in
each family. A wire, controller and casing do not receive separate bills for one function. More radical
versions charge only the previously unadapted acute increment. Used equipment still requires the new
recipient's adaptation.

Installed packages determine `B` and `Hmax = max(0, 100 - B)`. First adaptation reduces current H, bounded
by the new maximum. Removing a package raises the limit without healing; reinstalling an adapted package
does not repeat its acute cost. Power loss, repair and switching a module off retain its installed burden.
Standard medical replacement costs zero. The basic full-conversion package covers the pod and ordinary
frame functions together: B = 20, first acute cost = 25, H = 75. Additional enhancements are separate.

Physical owners settle the old medical interval before installation, removal and anatomical transfers.
Humanity reconciliation runs at committed graph boundaries; deferred anatomical notifications observe
only the completed graph. Restoration suppresses intermediate configuration accounting.
Humans and Borg carriers carry a server-only `NCHumanityMonitorComponent` for body-local event routing.
It has no psychological state. Its separate subscriptions coexist with physiology, persistence and power
service handlers; this engine permits one subscription for each component/event pair.

## Treatment and crisis

Three finite injectable reagents use normal chemistry and `NCApplyMedicalDrug`: neural restoration medium,
neural stabilizer, and combined neural support. `NCPharmacologySystem` owns their measured exposures,
clearance, interactions and overdose. An unadministered syringe has no effect. Therapeutic H/S effects fall
at toxic concentrations and share aggregate content caps: initially 2 H/minute and 2 strain points/second.
No recovery is banked beyond Hmax. Stabilization does not increase Hmax, repair brain damage or supply oxygen.
The biopod uses its actual artificial filtration and existing pharmacology route.

The medical scheduler emits `NCMedicalIntervalCompletedEvent` after pharmacology and physiology.
Humanity applies the resulting aggregate once for that online interval. S has its own 0–100 range;
safe physiological stability reduces strain, never grants free H. Actual hot, loaded enhanced modules and
overburden can add continuous strain. Neural trauma impulses have bounded source IDs and cooldowns.

Initial bands are warning (H < 50, S >= 35), crisis (H < 35, S >= 60), and acute (H < 20, S >= 85).
De-escalation requires hysteresis and 30 seconds of stabilization. Ordinary unaugmented stress is not
classified as a cyber crisis. The state never kills the brain, changes faction, replaces a mind or enables NPC
control. Acute episodes reject positive `TrySetLoad` requests on marked enhancements. Ordinary cyberware
processing clears their active load; the existing body-function aggregate reads a separate crisis efficiency.
Medical/life-support hardware retains its physical function and its own damage and power checks.

Self diagnostics, self examination and the neural-adaptation interaction verb disclose H/Hmax/B/S and
localized guidance. Detailed H data is restricted to the existing self/admin diagnostic capabilities.
Text warnings and mechanical restrictions work without heavy VFX. No new artwork or hallucinations are
required; the existing diagnostic window and build style are reused. EN/RU text is maintained together.

## Persistence and compatibility

Medical version 14 adds current H, actual burden, adaptation history, S, stage, stabilization progress and
remaining impulse cooldowns. Validation compares burden and adapted packages with the saved physical
configuration before graph mutation. Invalid, missing or non-finite state is rejected; clones do not share
mutable histories. Remaining durations do not advance while the server is offline.

Version 13 and older saves adopt their existing packages once without a new acute bill. Loading an old save
over a known live character does not refill H/S. Historical actor Humanity records are read only and imported
through the existing actor health/spawn path before its next medical checkpoint. The anonymous legacy cost
writer rejects new writes; current medicine is the sole authority. Old records remain in storage, and the
shared player/actor medical payload requires no database schema migration. AI and navigation are unchanged.
The migration source remains available across a failed physical spawn, and is retired only after the
committed/reloaded health cache contains version 14, or the actor cache is unloaded. This prevents both a
retry refill and a later replay of old H over treatment already saved by the current medical authority.

## Validation

`NCHumanityMathTest` covers GDD arithmetic, adaptation reuse/upgrades, separate treatment channels,
toxic falloff, hysteresis, finite validation and independent snapshot clones. `NCHumanityTest` prepares
12 integration contracts; Borg contracts also check conversion cost, shell continuity and restorative
exposure through artificial clearance. Integration fixtures are authored for compilation only, per owner
instruction. Their presence does not establish a passed in-game acceptance run.

Verification on 2026-10-23:

- MCP `build-server` passed with the final migration and event-routing changes
  (artifact `validation-6e77a10bd562451987a8178cfead1491`). MCP `build-client` passed
  (artifact `validation-a7e3795366ab4b858513b41f9d282193`); subsequent code changes are server-only.
- `Content.IntegrationTests` compiled with existing diagnostic references, zero errors. This compiles
  the 12 Humanity and 12 Borg contracts without executing them. Log: `TestResults/CyberwareGate5/integration-compile.log`.
- 57 selected tests passed across the arithmetic/support run and the serialization rerun: 28 Humanity
  arithmetic cases, two serialization contracts and 27 existing cyberware/Borg regression cases.
  Logs: `unit-tests.log` and `serialization-tests.log` in the same folder. The serializer rerun used freshly
  restored project dependencies, resolving a stale unrelated `Content.Meta.dll` in the first setup.
- YAML linter compilation passed with one MSBuild worker and existing diagnostic references. The standard
  MCP parallel build stopped progressing and was cancelled. The direct diagnostic runner required a temporary
  directory at the normal output depth for the existing resource mounts. Its final bootstrap did not complete
  and was cancelled; **the resource validation has no passing result**. See `yaml-compile-verified.log` and
  `yaml-validation-verified.log`. The temporary runner was removed.
- The latest isolated MCP server (`f2867c5fd9d944738ab9188837b8fbaa`, operation
  `62806af0bdfb442fb9d6db2ef8774ef1`) reached system initialization and stopped on a different existing
  subscription conflict: `NCMetaCommissioningSystem` and `NCDiagnosticsSystem` both subscribe to
  `NCMedicalBodyComponent` / `GetVerbsEvent<AlternativeVerb>`. The owned environment was stopped.
  That current-tree startup failure blocks a live factory-state and graphical UI acceptance pass.

Integration fixture execution remains explicitly disabled. T01/T36–T43 runtime acceptance is prepared,
not reported as passed; the isolated server must start successfully before that work can be verified.
