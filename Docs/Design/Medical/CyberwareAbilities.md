<!-- SPDX-FileCopyrightText: 2026 Astro -->
<!-- SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0 -->
<!-- SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md. -->

# Cyberware abilities — Gate 6

Implements the staged ability slice of the [cyberware GDD](https://app.notion.com/p/GDD-4018867306b1807fa573c0d18052296b):
an instrumental hand, a biomonitor and one physical weapon, followed by two motor controllers.
The source baseline at the start of this stage was `359e0f3fb8e34f20345fca4a56e7b2e1cff4f219`,
with the previous cyberware gates already present in the working tree. The GDD's static `6125295` audit
is historical; the implementation uses the current anatomy, battery bus, calibration, Borg and Humanity APIs.
META is outside this change and is being developed separately.

## Player behavior

| Physical module | Activation and effect | Main battery cost |
| --- | --- | --- |
| `NCToolHandImplant` | Deploys an ordinary screwdriver into the matching free hand. Uses native tool qualities and delays. | 0 startup; 2 W active |
| `NCBiomonitorImplant` | Reads its owner's oxygenation, perfusion and pain, with a limited threshold alarm. | 1 J per reading |
| `NCMantisBladeImplant` | Deploys a physical melee blade into the matching free hand. Native melee, hit resolution and medical trauma apply. | 0 startup; 5 W active |
| `NCSandevistanImplant` | Six seconds of up to 25% movement assistance and up to 20% shorter permitted manual tool operations. | 10 J startup; 20 W active |
| `NCKerenzikovImplant` | Reduces the existing moving called-shot precision penalty by up to 25%. Stationary precision is unchanged. | 2 J startup; 5 W active |

Each module also draws 0.2 W idle. These are initial NC adaptation balance settings, not claims about
canonical 2045 hardware. Mantis uses the existing knife presentation and melee APIs: Slash 14, Piercing 3,
one attack per second. This stage does not implement monowire, launchers or smartgun targeting.

An attached, connected, calibrated and operational module grants its owner an ordinary action. Conscious
biological brain control, real anatomical ownership, power and Humanity restrictions are checked before
activation and during use. Detachment, neural loss, unconsciousness, power loss, shutdown and active-mode
crisis restrictions retract equipment and clear its bonuses. Integral items cannot attack or operate as tools
after their hardware loses permission. Deploying equipment requires a free hand on the host arm and never
deletes or drops another held item. The extracted implant remains a physical, reusable item.

Sandevistan and Kerenzikov can coexist physically, but occupy one motor control channel. The player must
switch the active controller off before enabling the other. Sandevistan reserves six seconds plus thirty
seconds of recovery on activation; early cancellation does not shorten that reservation. Reinstallation and
ownership changes preserve the physical controller's cooldown and return its active mode to off.

## Runtime ownership and integration

`Content.Server/_NC/Medical/Cyberware/Abilities/NCCyberwareAbilitySystem` owns activation, native actions,
integral item containers and body-local reconciliation. It polls only active modules at 100 ms intervals.
Power integration splits at the active load deadline, so a large observed time interval cannot bill all
of it at the six-second burst's active demand. Startup cost is debited once from the existing main battery;
abilities cannot draw on the critical life-support reserve. Existing heat, wear, repair and calibration rules
remain authoritative. Motor overdrive supplies a small bounded stimulus to the existing Humanity stress system.

Shared components expose sanitized movement/manual-control channels for client prediction; hidden hardware
state and battery authority stay server-side. Movement assistance multiplies current allowed movement,
including injury penalties. A narrow extension to `RefreshMovementSpeedModifiersEvent` clamps final speeds
after all modifiers, independently of subscription order, to policy walk/sprint limits of 4.5/6 m/s.
Unaugmented movement is unchanged.

Only native tool operations requiring exclusively `Screwing`, `Wrenching` or `Prying`, with zero fuel cost,
receive the manual duration factor. There is no global time scale, generic DoAfter multiplier, gun fire-rate
modifier, machine acceleration, bullet simulation shortcut or movement teleport.

Kerenzikov feeds `NCTargetingAccuracySystem`'s existing moving called-shot multiplier. Its reduction is captured
when an actual shot is accepted, carried through projectile context or hitscan impact and used by native hit
location resolution. It restores a fraction of an existing penalty; it creates no general hit bonus or target
information. Physical hit confirmation and server collision remain necessary.

New profiles, actions and item prototypes live under `Resources/Prototypes/_NC/Medical/Cyberware/Abilities/`.
Human and Borg anatomical hosts declare the corresponding physical bays. Existing clinical installation and
removal procedures accept the new profiles, including arm targets. Player-facing text is paired in EN/RU.
Existing build assets are reused; expanded body visuals and dedicated active effects belong to Gate 7.

## Persistence

Medical snapshot version 15 adds an optional remaining ability cooldown to each physical module snapshot.
Validation requires the field exactly when the saved entity prototype declares an ability, and rejects
non-finite, negative or excessive durations before mutating the body. Capture and clone preserve the value.
Restore reconstructs actions and integral items from hardware, with all active modes off, while retaining
cooldown, actual battery charge, heat, wear and Humanity. Remaining recovery time does not advance offline.

Version 14 and older snapshots migrate through the established entry point. Existing live controller cooldown
is retained when migrating the same stable item; a legacy record cannot inject version 15 fields. This stage
does not add the durable loose-item trade/ownership ledger scheduled for Gate 8.

## Verification

`NCCyberwareAbilityMathTest` checks bounded cooldowns, paused offline time, moving penalty reduction and final
movement caps in either modifier order, including injured and unaugmented movement. Serialization contracts
check zero/nonzero cooldown round trips and the absent field on ordinary hardware.

`NCCyberwareAbilityTest` prepares 15 integration contracts for ownership, biomonitor energy, T04/T23–T25,
tool timing, occupied hands, real tool/melee guards, motor exclusivity, power loss, persistence and a large
power interval. These fixtures are compiled only. Their presence is not evidence of passed runtime acceptance.

The owner has explicitly declined integration test execution; no integration fixtures were run for this stage.

Verification on 2026-10-23:

- MCP `build-client` passed, artifact `validation-6418ed6695f646fc9268a84b5b86a377`.
  Subsequent ability C# changes are server-only.
- 79 selected content/unit cases passed: 22 ability math/serialization cases and 57 existing
  Humanity, Borg and cyberware regression cases. Result: `TestResults/CyberwareGate6/abilities-units.trx`.
- The 15 integration contracts compiled against the existing diagnostic references, with zero errors;
  no integration fixtures executed. Log: `TestResults/CyberwareGate6/integration-compile.log`.
- The final serialized server build reported nine errors, all in the concurrently changing
  `Content.Server/_NC/Meta/` sources, including missing `InstallationId`, `RadioBytesPerSecond` and
  a protected `DamageableComponent.TotalDamage` read. No META source was changed here.
  Log: `TestResults/CyberwareGate6/server-final-compile.log`. The last MCP build attempt also failed
  in META, artifact `validation-74f3bc97bed3418a9c6c26edb2e18bb7`; its earlier error set differs because
  that source is changing. A final successful server build remains outstanding.
- An initial native MCP startup exposed a malformed surgical YAML indentation and differing MapInit
  subscription order constraints in the ability system. Both are corrected in source. The current
  server could not be rebuilt successfully afterwards, so runtime acceptance of those fixes is pending.
  The owned environment `9323be4e62f94e70a0e604bf52f9d0b0` was stopped.
- YAML linter compilation passed. A temporary runner at the normal output-directory depth was used
  for its resource mounts; bootstrap stopped progressing and was cancelled after approximately three
  minutes. **Resource validation has no passing result.** Logs: `yaml-compile.log` and
  `yaml-validation.log` in `TestResults/CyberwareGate6/`. The temporary runner was removed.
- The 30 EN/RU localization keys match. MCP inspection confirmed the existing RSI states referenced
  by the new actions and items; no sprite files changed. Whitespace checks passed. A supplementary
  PyYAML syntax check parsed all eight touched ability/anatomy/surgical YAML files successfully
  (`TestResults/CyberwareGate6/yaml-syntax.log`); engine field/schema validation remains pending.

This is implemented source with partial verification, not a closed runtime acceptance gate. Rebuild the
current server and rerun resource validation after the parallel META changes stabilize; in-game ability
and client prediction checks remain pending. Integration execution still requires the owner's explicit opt-in.
