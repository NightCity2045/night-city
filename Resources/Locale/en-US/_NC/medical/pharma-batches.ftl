# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

ent-NCPharmaAutoinjector = dose autoinjector
    .desc = A single-use pack of medication doses. The label states what the producer claims, not what is inside.
    .suffix = { "" }

nc-pharma-container-examine = Batch [bold]{$code}[/bold] — {$concentration} per dose, purity {$purity}, {$sterile}, {$grade} grade. Doses left: {$doses}.
nc-pharma-label-sterile = sterile
nc-pharma-label-not-sterile = not sterile
nc-pharma-grade-street = street
nc-pharma-grade-medical = medical
nc-pharma-grade-corporate = corporate

nc-pharma-administer-started = You begin administering a dose.
nc-pharma-administer-complete = The dose is administered.
nc-pharma-administer-failed = The dose could not be administered.

nc-pharma-delivery-oral = oral
nc-pharma-delivery-autoinjector = autoinjector
nc-pharma-delivery-infusion = infusion

nc-pharma-modifier-fast-release = fast release
nc-pharma-modifier-extended-release = extended release
nc-pharma-modifier-protective-carrier = protective carrier
nc-pharma-modifier-stabilizer = stabilizer

nc-pharma-production-street = quick synthesis
nc-pharma-production-standard = standard synthesis
nc-pharma-production-clean = clean synthesis

nc-pharma-formula-trauma-analgesic = field trauma analgesic
nc-pharma-formula-field-stimulant = field stimulant
nc-pharma-formula-broad-antibiotic = broad-spectrum antibiotic infusion

ent-NCPharmaSourceCanisterStreet = precursor canister
    .desc = Raw material for pharmaceutical synthesis. Street-grade stock of uneven quality.
    .suffix = street grade

ent-NCPharmaSourceCanisterClinical = precursor canister
    .desc = Raw material for pharmaceutical synthesis. Clinical-grade stock with a documented supplier.
    .suffix = clinical grade

ent-NCPharmaSynthesizerBase = pharmaceutical synthesizer
    .desc = Produces a known formula in batches from a raw-material canister.

ent-NCPharmaSynthesizerStreet = pharmaceutical synthesizer
    .desc = A patched-together line. Fast and cheap, but its sterility, mixing and calibration are poor.
    .suffix = street lab

ent-NCPharmaSynthesizerClinical = pharmaceutical synthesizer
    .desc = A clinical production line with sterile handling, even mixing and a calibrated dosing head.
    .suffix = clinical line

nc-pharma-station-title = Pharmaceutical synthesizer
nc-pharma-station-material = Raw material
nc-pharma-station-formula = Formula
nc-pharma-station-profile = Production mode
nc-pharma-station-units = Material to use:
nc-pharma-station-start = Start production
nc-pharma-station-source-slot = raw material
nc-pharma-station-source-present = Canister: {$units} units, stated purity {$purity}
nc-pharma-station-source-missing = No raw-material canister inserted.
nc-pharma-station-no-modifiers = none
nc-pharma-station-formula-card = Version {$version} · {$form} · {$concentration} per dose · modifiers: {$modifiers} · status: {$status}
nc-pharma-station-forecast = Expected: [bold]{$doses}[/bold] doses, [bold]{$seconds}[/bold] s, cost x{$cost}
nc-pharma-station-finished = The synthesizer finishes batch {$code}.
nc-pharma-station-failed = The synthesis run fails and produces nothing.
nc-pharma-station-notice-no-source = Insert a raw-material canister first.
nc-pharma-station-notice-not-enough = The canister does not hold enough material.
nc-pharma-station-notice-invalid = The request was rejected.
nc-pharma-station-notice-busy = A production run is already in progress.
nc-pharma-station-notice-started = Production started.
nc-pharma-station-notice-finished = Production finished.
nc-pharma-station-notice-failed = Production failed.

nc-pharma-status-unknown = unknown
nc-pharma-status-free-sale = free sale
nc-pharma-status-prescription = prescription
nc-pharma-status-controlled = controlled
nc-pharma-status-clinical-only = clinical only
nc-pharma-status-corporate = corporate
nc-pharma-status-banned = banned
nc-pharma-status-recalled = recalled

nc-pharma-qc-untested = untested
nc-pharma-qc-partial = partially tested
nc-pharma-qc-certified = certified
nc-pharma-qc-rejected = rejected
nc-pharma-qc-recalled = recalled

ent-NCPharmaQcAnalyzer = pharmaceutical QC analyzer
    .desc = Measures what a batch really contains. Each run uses up one dose of the inserted pack as the sample.

nc-pharma-container-examine-qc = Laboratory status: {$state}.
nc-pharma-qc-title = Pharmaceutical QC analyzer
nc-pharma-qc-sample = Sample
nc-pharma-qc-sample-slot = sample pack
nc-pharma-qc-sample-info = Batch [bold]{$code}[/bold] · doses left: {$doses} · status: {$state}
nc-pharma-qc-sample-missing = Insert a dose pack to analyze.
nc-pharma-qc-tests = Tests to run
nc-pharma-qc-test-concentration = concentration
nc-pharma-qc-test-purity = purity
nc-pharma-qc-test-sterility = sterility
nc-pharma-qc-test-homogeneity = homogeneity
nc-pharma-qc-test-stability = stability
nc-pharma-qc-run = Run analysis (uses one dose)
nc-pharma-qc-result = Result
nc-pharma-qc-no-result = No analysis of this pack yet.
nc-pharma-qc-row-passed = [color=#67E89A]{$name}[/color]: label {$label} · measured {$value}
nc-pharma-qc-row-failed = [color=#FF6170]{$name}[/color]: label {$label} · measured {$value} — [bold]does not match[/bold]
nc-pharma-qc-row-not-tested = [color=#7898A0]{$name}[/color]: not tested
nc-pharma-qc-finished = The analyzer finishes the check of batch {$code}.
nc-pharma-qc-notice-no-sample = Insert a dose pack first.
nc-pharma-qc-notice-no-dose = The pack has no dose left to use as a sample.
nc-pharma-qc-notice-unknown-batch = The pack does not belong to any known batch.
nc-pharma-qc-notice-no-tests = Select at least one test.
nc-pharma-qc-notice-busy = An analysis is already in progress.
nc-pharma-qc-notice-started = Analysis started.
nc-pharma-qc-notice-finished = Analysis finished.
nc-pharma-formula-maintenance-analgesic = maintenance analgesic

nc-pharma-formula-euphoric = clinical euphoric
nc-pharma-formula-social = social stimulant
ent-NCPharmaSynthesizerCorporate = pharmaceutical synthesizer
    .desc = A corporate production line: sterile throughout, precise dosing and a large run capacity.
    .suffix = corporate lab

nc-pharma-phase-onset = { $effect ->
   *[stimulation] Your pulse quickens and your thoughts sharpen.
    [sedation] A heavy calm begins to settle over you.
    [euphoria] A warm wave of well-being starts to spread through you.
    [perception] The edges of the world begin to shift.
    [sociability] You feel looser and more talkative.
    [dissociation] You start to feel distant from your own body.
}
nc-pharma-phase-peak = { $effect ->
   *[stimulation] You are wired, quick and wide awake.
    [sedation] You are deeply, heavily calm.
    [euphoria] Everything feels warm, easy and good.
    [perception] Colors and sounds are strange and vivid.
    [sociability] You feel at ease with everyone around you.
    [dissociation] You watch yourself from far away.
}
nc-pharma-phase-decline = { $effect ->
   *[stimulation] The edge is wearing off.
    [sedation] The calm is thinning out.
    [euphoria] The warmth is slowly fading.
    [perception] The strangeness is settling down.
    [sociability] The easy mood is slipping away.
    [dissociation] You feel yourself coming back.
}
nc-pharma-phase-comedown = { $effect ->
   *[stimulation] The rush is gone and you feel drained.
    [sedation] You feel sluggish and slow.
    [euphoria] The warmth is gone and you feel flat and tired.
    [perception] The world is dull and you feel worn out.
    [sociability] The mood drops and you feel tired and irritable.
    [dissociation] You are back, but heavy and numb.
}
