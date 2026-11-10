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
nc-pharma-delivery-inhaled = inhaled
nc-pharma-delivery-sublingual = sublingual
nc-pharma-delivery-patch = transdermal patch

nc-pharma-modifier-fast-release = fast release
nc-pharma-modifier-extended-release = extended release
nc-pharma-modifier-protective-carrier = protective carrier
nc-pharma-modifier-stabilizer = stabilizer
nc-pharma-modifier-buffered = buffered
nc-pharma-modifier-concentrated = concentrated
nc-pharma-modifier-purified = purified
nc-pharma-modifier-cold-solvent = cold solvent
nc-pharma-modifier-absorption-enhancer = absorption enhancer
nc-pharma-modifier-masking-agent = masking agent
nc-pharma-modifier-potentiator = potentiator
nc-pharma-modifier-metabolic-blocker = metabolic blocker

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

nc-pharma-production-manual = manual synthesis
nc-pharma-reaction-stable = stable reaction
nc-pharma-reaction-exothermic = exothermic reaction
nc-pharma-reaction-volatile = volatile reaction
nc-reagent-name-reactor-fumes = reactor fumes
nc-reagent-desc-reactor-fumes = Acrid fumes released by a reaction that got out of control. Damages the lungs.
ent-NCPharmaReactor = thermoreactor
nc-pharma-reactor-title = Thermoreactor
nc-pharma-reactor-section-run = Run
nc-pharma-reactor-section-gauges = Gauges
nc-pharma-reactor-section-controls = Controls
nc-pharma-reactor-start = Start run
nc-pharma-reactor-no-run = No run in progress.
nc-pharma-reactor-windows = Optimal [bold]{$optimalMin}-{$optimalMax}[/bold] °C · safe {$safeMin}-{$safeMax} °C · pressure soft {$soft} / hard {$hard}
nc-pharma-reactor-temperature = Temperature: {$value} °C {$trend}
nc-pharma-reactor-pressure = Pressure: {$value}
nc-pharma-reactor-mixing = Mixing {$value} · catalyst {$catalyst} (charges left: {$left})
nc-pharma-reactor-progress = Reaction progress: {$value}
nc-pharma-reactor-instability = Instability: {$value}
nc-pharma-reactor-heater = Heater
nc-pharma-reactor-cooler = Cooler
nc-pharma-reactor-stirring = Stirrer
nc-pharma-reactor-venting = Pressure relief valve (slows the reaction)
nc-pharma-reactor-ventilation = Ventilation (reduces a toxic release)
nc-pharma-reactor-catalyst = Add catalyst
nc-pharma-reactor-quench = Quench
nc-pharma-reactor-stop = Emergency stop
nc-pharma-reactor-warning-high-temperature = [color=#FFD05E]Temperature is above the optimal window.[/color]
nc-pharma-reactor-warning-near-limit = [color=#FF6170]Temperature is close to the safe limit![/color]
nc-pharma-reactor-warning-high-pressure = [color=#FF6170]Pressure is approaching the limit.[/color]
nc-pharma-reactor-warning-poor-mixing = [color=#FFD05E]Poor mixing creates hot spots.[/color]
nc-pharma-reactor-warning-runaway = [color=#FF6170]The reaction is running away: cool it now.[/color]
nc-pharma-reactor-warning-instability = [color=#FF6170]The mixture is becoming unstable.[/color]
nc-pharma-reactor-notice-not-experimental = Only a formula under development may be made by hand.
nc-pharma-reactor-notice-units-limit = A manual run is limited to a small amount of material.
nc-pharma-reactor-notice-started = The reaction has started.
nc-pharma-reactor-notice-finished = The run is finished.
nc-pharma-reactor-notice-stopped = Emergency stop: the material is lost.
nc-pharma-reactor-notice-quenched = The reaction was quenched and stopped.
nc-pharma-reactor-notice-degraded = The mixture degraded into sludge.
nc-pharma-reactor-notice-released = Toxic fumes were released.
nc-pharma-reactor-notice-ruptured = The vessel ruptured.
nc-pharma-reactor-notice-catalyst = Catalyst added.
nc-pharma-reactor-notice-no-catalyst = No catalyst left.
nc-pharma-reactor-popup-warning = The reactor alarm sounds: the mixture is becoming unstable!
nc-pharma-reactor-popup-degraded = The mixture is breaking down into sludge!
nc-pharma-reactor-popup-release = The reactor vents toxic fumes!
nc-pharma-reactor-popup-rupture = The reactor vessel ruptures and catches fire!
nc-pharma-reactor-popup-severe = The reactor vessel explodes violently!
nc-pharma-reactor-popup-finished = The reactor finishes batch {$code}.

ent-NCPharmaColdStorage = pharmaceutical cold storage
    .desc = A powered cabinet that keeps medicine cold. Unstable batches stop spoiling while they are inside and the cabinet has power.
nc-pharma-container-examine-keep-cold = [color=#7DBDB7]Keep refrigerated: this formula spoils at room temperature.[/color]

# Research & development console and journal.
nc-pharma-rd-title = Formula research console
nc-pharma-rd-formulas = Formulas
nc-pharma-rd-selected = Selected formula
nc-pharma-rd-new-version = New version
nc-pharma-rd-name-placeholder = Name of the new version
nc-pharma-rd-delivery = Delivery form
nc-pharma-rd-concentration = Concentration
nc-pharma-rd-modifier-first = First modifier
nc-pharma-rd-modifier-second = Second modifier
nc-pharma-rd-no-modifier = none
nc-pharma-rd-create = Create experimental version
nc-pharma-rd-approve = Approve selected
nc-pharma-rd-journal = Laboratory journal
nc-pharma-rd-journal-empty = The journal is empty.
nc-pharma-rd-select-hint = Select a formula to see its card and to develop a new version of it.
nc-pharma-rd-candidate = candidate
nc-pharma-rd-formula-title = {$name} v{$version} ({$status})
nc-pharma-rd-card = {$name} · version {$version} · {$form} · {$concentration} per dose · modifiers: {$modifiers} · status: {$status}
nc-pharma-rd-forecast = Forecast: peak {$peak} · toxic margin {$margin} · absorbed over {$seconds} s · toxicity ×{$toxicity} · cost ×{$cost}
nc-pharma-rd-journal-row = [{$kind}] {$formula} · batch {$batch} · {$actor} · {$detail}
nc-pharma-rd-notice-created = A new experimental version was created.
nc-pharma-rd-notice-approved = The formula was approved.
nc-pharma-rd-notice-invalid = The request is not valid for this formula.
nc-pharma-rd-notice-invalid-name = The name is empty, too long or contains forbidden characters.
nc-pharma-rd-notice-too-many = The laboratory cannot hold more derived formulas.
nc-pharma-rd-notice-not-experimental = Only experimental versions can be approved.
nc-pharma-rd-notice-not-certified = This version has not been certified by quality control yet.
nc-pharma-rd-notice-unidentified = The compound has not been identified by chemical analysis yet.
nc-pharma-journal-produced = produced
nc-pharma-journal-qc = quality control
nc-pharma-journal-administered = administered
nc-pharma-journal-approved = approved
nc-pharma-journal-version-created = version created
nc-pharma-station-formula-title = {$name} v{$version}
nc-pharma-status-experimental = experimental
nc-pharma-phase-none = none
nc-pharma-journal-experiment = experiment
nc-pharma-exp-kind-production = production run
nc-pharma-exp-kind-vivarium = vivarium study
nc-pharma-exp-subjects = subjects: {$subjects}
nc-pharma-exp-note-profile = process: {$profile}
nc-pharma-exp-note-units = material: {$units}
nc-pharma-exp-note-input-purity = input purity {$purity}
nc-pharma-exp-note-output = output: {$doses} doses
nc-pharma-exp-note-qc = QC: {$performed} tests, {$failed} failed
nc-pharma-exp-note-outcome-tolerated = subject tolerated it
nc-pharma-exp-note-outcome-strained = subject strained
nc-pharma-exp-note-outcome-lost = subject lost
nc-pharma-exp-note-peak = peak {$peak}
nc-pharma-exp-note-ratio = {$ratio} of the toxic level
nc-pharma-exp-note-toxin = adulterant toxin {$toxin}
nc-pharma-exp-note-sepsis = sepsis burden {$sepsis}
nc-pharma-exp-note-clinical = clinical: {$patient}, peak {$peak}
