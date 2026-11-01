<!-- SPDX-FileCopyrightText: 2026 Astro -->
<!-- SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0 -->
<!-- SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md. -->

# Cyberware presentation and catalog — Gate 7

Implements the first presentation/catalog batch from sections 20–22 and Gate 7 of the
[cyberware GDD](https://app.notion.com/p/GDD-4018867306b1807fa573c0d18052296b).
The current working-tree baseline remains `359e0f3fb8e34f20345fca4a56e7b2e1cff4f219`, with Gates 0–6
already present. META is concurrently maintained by another agent and is outside this change.

## Visible anatomy and catalog

Industrial and clinical arms, clinical legs, and the civilian/clinical full-conversion frames use native
`VisualOrgan` body layers. Clothing draws over those layers, and removing an organ removes its contribution.
The Borg carrier now has the same clothing/inventory layer layout as other humanoids. Its pod icon is shown
only without a shell; its head and real torso/limb/hand/foot/sensor nodes compose the attached shell.
The clinical shell remains an empty manufactured carrier with no new brain. Both shell families keep the
existing standard humanoid equipment envelope and physical support requirements.

The clinical arm and leg profiles provide ordinary rated function through the existing anatomy, power,
calibration and Humanity systems. This stage adds no military force multiplier or new movement mechanics.
Arm replacements also declare their physical utility bays, allowing the already implemented instrumental
and blade mounts to be installed in replacement arms.

Eight new four-direction RSI states cover left/right arms, left/right legs, torso, head and left/right hands.
Native human registration, including the intentionally invisible far-side arm, is preserved. Feet and
clothing reuse existing build assets. No full character sprite is required for each controller configuration.

## Physical coatings

Chrome, white clinical panels and synthetic skin are exterior finishes of the same physical part. A real,
held coating kit can cover one loose anatomical part within normal unobstructed interaction reach.
It cannot coat an installed deep item through a body. The kit is reserved before queued deletion, preventing
two applications in one frame. Applying it changes neither structural integrity, installed parts, calibration,
charge, heat, Humanity nor stable hardware identity.

When actual structural damage crosses 70% integrity, the current finish is marked ruptured. This is a cosmetic
state, not another HP pool. Mechanical service does not restore the covering; replacing the covering does not
repair mechanics. The rupture remains visible until a new coating is applied. Synthetic skin uses the normal
human anatomical sprite while intact; rupture exposes tinted mechanical artwork. Its pigment is saved so a
restart or transplantation cannot silently change it.

The exterior component is deliberately private. The server publishes the resulting ordinary `VisualOrgan.Data`,
which already carries public body appearance. No finish record, implant ID, serial, Humanity, stress or hidden
diagnostic data is added to a public body summary. The native appearance path has a narrow presentation query
and refresh API; an exterior override copies its input before modifying it, protecting another organ's data
when native appearance copying supplies a shared object. A client adapter applies the same sanitized data
to loose-part sprites after detachment.

Medical snapshot version 16 preserves finish, rupture and pigment, validates the component shape and rejects
unknown finishes or invalid/non-opaque pigment before mutation. Clone and runtime rebase preserve this data.
Older snapshots adopt prototype defaults, retaining a known live item's finish when available; old versions
cannot inject future exterior fields. This uses the existing medical record and hardware identity.

## Active equipment and feedback

The Mantis implementation now uses an existing long blade sprite in either hand; its deployed state remains
a real held melee item. Retraction returns it to the hardware container. Mechanical deployment/retraction,
motor activation and biomonitor readings have prototype-configured native PVS audio. No sound is charged
as a second resource action, and unsuccessful activation produces no activation sound.

Moving Sandevistan users leave short native sprite afterimages. The client copies only the public rendered
sprite, preserves its opacity, excludes contained bodies and large position jumps, and maintains an active
index instead of scanning all bodies every frame. Effects last 0.2 seconds, are sampled at 10 Hz and have a
64-effect client budget. They contain no physics, server entity, hidden sensorium or authoritative state.
The existing **Reduced motion** accessibility option clears/suppresses these effects; server speed, cooldown,
power, heat and stress remain unchanged. The action state, visible held equipment and audio still provide
feedback without the trail.

## Artwork and control scene

The built-in imagegen tool produced two new transparent atlases using existing human organ strips and the
build's cyberarm icon as references. Their project copies are:

- `Resources/Textures/_NC/Mobs/Cyberware/limbs-source.png`
- `Resources/Textures/_NC/Mobs/Cyberware/frame-source.png`

`Tools/_NC/Cyberware/pack_limb_atlas.py` performs crop/nearest-neighbor registration and RSI packing; it does
not paint replacement artwork. Alpha above 128 selects registration bounds without changing the generated
alpha values. Metadata records CC-BY-SA-3.0 and attribution; anatomical registration follows the existing
Human parts RSI credited to tgstation and DrSmugleaf. Existing katana, eye, foot and audio assets retain their
original resource paths and attribution.

`Tools/_NC/Cyberware/render_visual_contact_sheet.py` composes 36 asset cases: four directions for industrial,
clinical, synthetic, ruptured, clothed, detached-arm, both Borg families and deployed-blade appearances. Its output is
`TestResults/CyberwareGate7/visual-contact-sheet.png`, with a matching JSON manifest. It is explicitly an
asset composition, **not an engine screenshot or a passed in-game acceptance scene**.

The production prompt briefs were:

1. A new transparent 4×4 SS14 atlas; columns left arm/right arm/left leg/right leg, rows south/north/east/west.
   Sparse silver/graphite plates, pale cyan details, isolated parts without hands/feet, 32px logical scale,
   native human registration and mirrored poses; no labels, shadows, floor or whole characters.
2. A matching transparent 4×4 atlas; columns torso/head/left hand/right hand and the same four direction rows.
   Compact panelled torso, helmet/visor and mechanical palms; about 10×14, 8×8 and 4×4 logical pixels,
   with no organic flesh, text, logos or background. The generated limbs are a style reference, and native
   human organs are registration references. Both calls used the built-in tool with transparent output.

The exact prompts and input lists are preserved in `Tools/_NC/Cyberware/imagegen-prompts.md`.

## Verification

`NCCyberwareExteriorSerializationTest` covers five finish/rupture/pigment round trips, independent mutable
records and the absent exterior on hidden internal hardware. `NCCyberwareExteriorTest` prepares seven
integration contracts for real consumable application, reach/holding, one-kit consumption, persistence,
malformed state and appearance aliasing. Integration fixture execution remains disabled by owner instruction.

Verification on 2026-10-23:

- The final shared project compiled with diagnostic dependencies, zero errors
  (`TestResults/CyberwareGate7/shared-compile.log`).
- 85 selected medical content/unit cases passed after the pigment round-trip change: six exterior
  serialization cases plus 79 ability/Humanity/Borg/cyberware regressions. Result:
  `TestResults/CyberwareGate7/visuals-units.trx`. The unit project was compiled with existing diagnostic
  references and `DefaultExcludesInProjectFolder=**/_NC/Meta/**` because concurrently added META rope
  tests require a newer client assembly. Only unrelated META test sources were excluded; source files
  and the test project were not edited to do this. This is a scoped medical result, not a full suite pass.
- The new eight-state RSI passed the standard validator. The full `Resources/` run reported 17 existing
  issues elsewhere (placeholder/unsupported licenses and missing vehicle/wall metadata). Logs:
  `rsi-targeted.log` and `rsi-validation.log` in the same results directory.
- Ten touched YAML files parsed in a supplemental syntax check; all 19 EN/RU exterior keys matched.
  This does not replace engine schema/prototype validation. Log: `resource-syntax.log`.
- The final server compile failed on one META analyzer error: a duplicate `IServerDbManager` dependency
  in `NCMetaWorkspaceSystem.Media.cs` / `_contactDatabase`. It reported no Gate 7 C# errors.
  Log: `server-verified-compile.log`. The final client attempt reported 11 errors in the changing META UI
  (`SaveStatus`/`NCMetaSaveStatus` and a nullable dereference), with no Gate 7 C# errors after correcting
  the native organ event subscription. Log: `client-verified-compile.log`.
- MCP YAML validation failed during dependency compilation on `MetaFormatException` in
  `Content.Meta/Language/MetaApiCatalog.Compatibility.cs`, artifact
  `validation-8a1f13877f9a4331a609a85a332dcdb5`. A full MCP client attempt encountered the same dependency,
  artifact `validation-0ecdfd8c2a8d4e2e8dbdf449b9080bc6`. These are failed validations.
- Integration compilation could not use the new exterior system because the server build produced no
  updated assembly. The seven contracts remain prepared source; they are not reported as successfully
  compiled or executed. Log: `integration-compile.log`.

The generated contact sheet was inspected after fixing registration bounds. A fresh in-game scene,
clothing/detachment acceptance, client afterimage/accessibility check and full YAML validation remain pending
until consistent server/client dependencies can be built. No stale-binary game launch was used as evidence
of the new implementation. META source was left to its owner. Gate 7 runtime acceptance remains open.
