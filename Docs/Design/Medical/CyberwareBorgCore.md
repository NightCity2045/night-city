<!-- SPDX-FileCopyrightText: 2026 Astro -->
<!-- SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0 -->
<!-- SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md. -->

# Cyberware Gate 4: living brain, biopod and first civilian shell

Authority: GDD sections 12, 24, 25 and Gate 4, acceptance scenarios T30–T35 and T42.
The earlier assembly, power and surgery contracts remain in force. Integration execution remains opt-in.

## Physical ownership

`NCBorgCore` is the permanent medical `Body` and controlled entity after conversion. It starts with an empty
brain socket, a casing and five real artificial support organs. `InitialBody.ExcludedOrgans` deliberately
leaves the expected brain and frame nodes absent at manufacture; no factory brain is spawned and discarded.

The original `BrainComponent` organ moves from the patient into this carrier through the normal organ
container and relation APIs. `BrainSystem` transfers the existing mind on the normal removal/insertion
events. `NCMedicalPersistenceSystem` hands off the existing player binding before those events, preserving
the CharacterId and single current owner. No mind copy, backup or substitute biological brain is created.
Brain integrity, function and provenance remain on the exact organ. Acute brain physiology, systemic burden,
active drugs and pending circulating chemicals follow it. The abandoned organic body retains its other
organs, wounds and blood and reaches ordinary missing-brain death.

`NCBorgStandardShell` supplies one humanoid civilian torso subtree. Mounting moves its actual organs into
the core's existing graph. Unmounting moves that subtree into an empty physical socket. Arms, hands, legs,
sensors, structural damage, permanent cyberware damage and the frame's actual cell holder move together.
The empty socket never owns a brain or character. The core entity, brain and drug state stay the same on
every frame change. A detached core has no movement, hands, vision, hearing or voice from absent equipment;
the existing NC body-function aggregate and Blindable enforce those restrictions on client and server.
The shared attack guard also rejects unarmed attacks without a connected shell and a functional active hand.

`NCOrganDetachmentSystem.TryTransferOrgan` defers its physiological checkpoints until the source and
destination graphs are complete, or the original graph is restored after a failed container operation.
Both final graphs are immediately evaluated. This is a synchronous topology transaction, not a surgical
death exemption. Missing brain in a previously occupied core and actual insufficient support still use
ordinary irreversible-death and brain injury rules. Irreversible patients cannot be converted or revived by
mounting a shell.

## Supplies and physiology

The pod's own real vanilla main/reserve batteries power life support independently of the shell's cells.
They cannot supply frame motors or weapons. The existing body bus powers the shell's physical peripherals.
The first civilian frame uses fixed direct neural couplers; adaptive implants still follow Gate 3 calibration.

The external reservoir contains finite perfusate, oxygen, dialysate and coolant. Default running consumption
is 2 J/s and 0.01 reagent units/s for each medium. Pumps run during preparation as well as occupancy.
Fractional reagent consumption is retained across calls and saves, so repeated short actions cannot round
the debit away. Nutrients use the existing Hunger and chemistry metabolism. Empty manufacture starts with
zero assimilated nutrition and no installed batteries or reservoir contents.

Real provider organs contribute to existing NC circulation, respiration, metabolism and filtration.
Power, perfusion medium, nutrients and thermoregulation constrain perfusion; finite oxygen constrains
respiration; dialysate and local filter condition constrain drug clearance. Failed clearance adds existing
systemic toxin burden. Loss of coolant raises the core's retained temperature and impairs perfusion.
An empty unpowered pod generates no heat. Finite coolant removes generated and retained heat toward the
310.15 K operating setpoint; cooling can recover an overheated pod without manufacturing coolant or charge.
There is no Borg HP pool. The first oxygen model is the GDD's conditional finite reserve, not a new atmos
simulation or immunity to oxygen deprivation.

The normal artificial pump and filter metabolizers process injected and ingested reagents through the same
Human metabolism stages and NC drug profiles. Unpowered or destroyed providers cannot metabolize. Existing
profile baseline clearance remains applicable when artificial organ clearance fails. The mouth/food inlet
is an actual powered shell organ; ingestion is blocked without it. The detached pod instead accepts
nutrient mixtures and medicine through its separate native injection port. Eating never creates charge;
food-to-electricity still requires the existing physical bioconverter.

All timed work uses the body-local NC medical scheduler at a one-second support cadence. No world scan,
offline progression or abstract NPC travel/presence model is introduced.

## Player interaction

1. Supply the pod's cell and external reservoir (`NCBorgSupportBottle`). Inject finite nutrients through the
   separate bloodstream port (`NCBorgNutrientSyringe`) and allow normal metabolism to assimilate them.
2. Prepare an unconscious patient on an actual surgical facility. Open and retract the real cranial casing
   through the existing surgical field until the brain is exposed.
3. Use the held pod on the patient. The 12-second native DoAfter rechecks the exact brain, held carrier,
   patient, table, exposure and every live support supply at completion. A functional probe consumes 1 J
   before severing the old brain supply. Failed preparation never removes the brain.
4. Supply the civilian frame's real thoracic cell holder. Its manufactured service hatch starts open; the
   usual surgery/service rules apply once closed. Use the held occupied pod on the prepared shell socket.
   The four-second connection rechecks the exact torso and live powered frame nodes and consumes a real
   1 J functional test before mounting.
5. With the cyberware service probe held, use the core's **Disconnect and evacuate biopod** interaction verb.
   The four-second operation leaves the real frame on its socket. The live brain continues on the pod's
   finite reserves. A damaged shell does not have to pass its mounting test to be evacuated.

The server owns the pending operation token and expected organ reference. Client input cannot nominate an
arbitrary brain or install an unseen shell. Movement, damage, lost physical access or changed anatomy
invalidate the operation. Standard speech, movement, hand and ingestion checks use replicated existing
body functions; only the public inlet/shell flags and appearance are new replicated state. EN/RU text is
provided. Existing build RSIs are reused; final Borg art/catalogue remains a later gate.

## Persistence

Medical snapshot version 13 adds `BorgCore`: CoreId, occupancy, temperature, remaining EMP duration,
real cell identities/charge/recharge delays, finite support solution, pending circulating/metabolized/stomach
solutions and fractional debit remainders. Existing organ, drug, nutrition and physiological records remain
authoritative. Version 12 migrates without creating a pod, brain, food or charge. Remaining durations are
rebased without applying offline time. Pending UI/DoAfter sessions do not survive restore.

Live CoreIds are indexed and cannot be restored into a second carrier. Disconnected player cores retain
their stable binding and round checkpoints. A pre-spawn handler returns the session to the existing physical
core and mind, preserving roles and CharacterId without manufacturing a new body or resetting online disease.
After a world reload, reconnect materializes the saved pod and graph rather than forcing its anatomy into a fresh human.
The current player binding and mind move only after successful restoration. Global durable ownership of
loose shells/items, cross-owner transactions and anti-duplication storage are Gate 8 work. Persistent Living
City actor conversion is rejected until its authoritative ActorId ownership coordinator supports the handoff.
Humanity and character-level ability cooldown systems belong to their later gates; frame changes already
keep the same medical/core actor so those systems have one stable carrier to attach to.

## Validation

`NCBorgCoreTest` prepares eleven integration contracts covering factory emptiness, exact brain/mind transfer,
real support failure, damaged-frame evacuation and drug continuity, dead-patient rejection, artificial drug
clearance, finite-resource snapshot restore, rejection of a second live CoreId, conservation of pending injected
medicine, isolation of shell and pod cells, and loss of clearance after physical filter removal.
They are not executed without owner opt-in.
All eleven fixture methods compile against the built diagnostic dependencies with
`BuildProjectReferences=false`: zero errors (`TestResults/CyberwareGate4/integration-compile-final.log`).
`NCBorgSupportTest` covers finite resource intervals and invalid numeric inputs; `NCBorgThermalTest` covers
empty-pod heat generation, finite cooling and recovery toward the setpoint. These and the existing cyberware
power/condition suites passed: 27 content/unit cases, zero failures (`TestResults/CyberwareGate4/unit-final.log`).

Full T30–T35/T42 acceptance
requires the opted-in integration or equivalent complete in-game conversion/evacuation pass.

MCP factory inspection in owned fixture `0e85bc7cc12f4e5aa701d56b20718e63` observed all six actual pod nodes
and sixteen actual frame nodes: none had a `Brain` component. Pod nutrition was zero, the frame had no
Hunger component, and standalone pod movement, grip, vision, hearing and speech channels were zero.
The empty pod was not irreversibly dead. The retained factory snapshot is
`snapshot-36f3d4b1f19c423399df0be24afbca9f`; it predates the thermal correction above.
No complete conversion/evacuation or graphical client acceptance pass has been performed.

The corrected thermal model was then observed through MCP in owned zero-client environment
`680c963f5bd14d85b36b56fea428737e`, fixture `deb4c0b8b505442fb6dd9eaae63750c5`, pod `29103`.
Snapshots `snapshot-06d519c588ed44f7b15714e8d7d60844` and
`snapshot-b60bc4128f364e5cb9e9396b83edf1e3` show 310.15 K at support times 17.900 s and 40.633 s.
Both real battery slots remained empty, occupancy false and irreversible death false; standalone function
channels remained zero. This confirms the empty unpowered-pod thermal regression only.

The earlier complete diagnostic build and MCP server build succeeded. The final complete diagnostic build
(`TestResults/CyberwareGate4/build-final.log`) stopped on three concurrent Meta workspace errors
(`NCMetaWorkspaceSystem`: ref-event capture and then-missing RuntimeRequestAsync/PopulateReleaseAsync).
That run reported no Borg compilation errors, but did not produce a final server assembly for the last
physical-provider check. The Meta work is outside this change and was preserved.
MCP YAML operation `e66df12ac72c40909ace4afe55f90c14` likewise stopped before resource validation on
eleven Meta compilation errors (workspace ref captures, persistence mutation APIs and TryEvaluateDebug).
Its retained output is `validation-6dbe27a0da9e49c09d8adb3cc97c0430`. It is not a successful YAML pass.
An isolated linter-only build against ready dependencies succeeded with zero errors. The fallback resource
run used the ready server dependency manifest to resolve Meta and was stopped after eight minutes without
a validation result (`TestResults/CyberwareGate4/linter-resources-final.log`). Resource validation therefore
remains unconfirmed; repeat the standard linter once the concurrent Meta tree builds successfully.

All owned MCP environments and fixtures were stopped/removed after observation. No NUnit integration
fixtures were executed.
