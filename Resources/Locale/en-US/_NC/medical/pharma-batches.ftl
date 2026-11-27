# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

ent-NCPharmaAutoinjector = dose autoinjector
    .desc = A single-use pack of medication doses. The label states what the producer claims, not what is inside.
    .suffix = { "" }

nc-pharma-container-examine = Batch [bold]{$code}[/bold]: concentration {$concentration}, purity {$purity}, {$sterile}, {$grade} grade. Doses left: {$doses}.
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
nc-pharma-station-source-present = Canister: {$units} units, stated purity {$purity}, {$type}
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
nc-pharma-reactor-ventilation = Ventilation (reduces a toxic release; needs a working scrubber nearby)
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
nc-pharma-rd-concentration = Concentration
nc-pharma-rd-create = Develop the formula
nc-pharma-rd-approve = Approve selected
nc-pharma-rd-journal = Laboratory journal
nc-pharma-rd-journal-empty = The journal is empty.
nc-pharma-rd-select-hint = Pick a line in the Lines tab to continue it, or build a new formula on the tree.
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
nc-pharma-exp-subjects = patients: {$subjects}
nc-pharma-exp-note-profile = process: {$profile}
nc-pharma-exp-note-units = material: {$units}
nc-pharma-exp-note-input-purity = input purity {$purity}
nc-pharma-exp-note-output = output: {$doses} doses
nc-pharma-exp-note-qc = QC: {$performed} tests, {$failed} failed
nc-pharma-exp-note-outcome-tolerated = patient tolerated it
nc-pharma-exp-note-outcome-strained = patient strained
nc-pharma-exp-note-outcome-lost = severe reaction
nc-pharma-exp-note-peak = peak {$peak}
nc-pharma-exp-note-ratio = {$ratio} of the toxic level
nc-pharma-exp-note-toxin = adulterant toxin {$toxin}
nc-pharma-exp-note-sepsis = sepsis burden {$sepsis}
nc-pharma-exp-note-clinical = clinical: {$patient}, peak {$peak}
nc-pharma-journal-note = note
nc-pharma-rd-note-placeholder = Your note on this version: what you tried, what you concluded
nc-pharma-rd-note-save = Save note
nc-pharma-rd-notice-note-saved = The note is saved in the journal.
nc-pharma-rd-notice-invalid-note = The note is empty or too long.
nc-pharma-formula-trauma-analgesic-desc = A quick autoinjector against severe pain in the field. It numbs without treating the injury, and it is habit-forming.
nc-pharma-formula-field-stimulant-desc = A stimulant for staying awake and working after blood loss. Overdose strains the heart.
nc-pharma-formula-broad-antibiotic-desc = An infusion that works on most wound infections, but is weaker against a resistant strain.
nc-pharma-formula-maintenance-analgesic-desc = A gentler tablet for stepping down from a stronger painkiller without a hard withdrawal.
nc-pharma-formula-euphoric-desc = A clinical euphoric that dulls pain and builds tolerance and dependence quickly.
nc-pharma-formula-social-desc = A social stimulant that loosens the mood. It builds tolerance, and the comedown is heavy.
nc-pharma-formula-gram-positive-antibiotic-desc = A targeted antibiotic: strong against gram-positive strains, weak against gram-negative ones.
nc-pharma-formula-gram-negative-antibiotic-desc = A targeted antibiotic: strong against gram-negative strains, weak against gram-positive ones.
nc-pharma-formula-x17-desc = An unidentified stimulant from a corporate lab. What it really does is not known until it is studied.
nc-pharma-formula-synthetic-blood-desc = A synthetic oxygen carrier made as a drug. It does not depend on a donor.
nc-pharma-formula-immunosuppressant-desc = Therapy that holds back transplant rejection while raising the risk of infection.
nc-pharma-formula-surgical-anesthetic-desc = Controlled anesthesia for deep surgery. Too much slows breathing.
nc-pharma-formula-sedative-desc = Lowers awareness and movement. Stronger and more toxic together with an anesthetic or an analgesic.
nc-pharma-formula-vasopressor-desc = Supports pressure and cardiac output during collapse. Works against a vasodilator.
nc-pharma-formula-vasodilator-desc = Lowers vascular tone. Dangerous in a patient whose perfusion is already poor.
nc-pharma-formula-antiarrhythmic-desc = Steadies an unstable heart rhythm. It cannot restart a heart that has stopped.
nc-pharma-formula-respiratory-stimulant-desc = Helps working lungs move more air. It cannot replace missing lungs.
nc-pharma-formula-antitoxin-desc = Speeds the clearance of circulating toxins through working organs.
nc-pharma-formula-procoagulant-desc = Reduces bleeding through clot support. It does not close a wound, and it works against an anticoagulant.
nc-pharma-formula-anticoagulant-desc = Reduces clotting and increases bleeding. For a clear clinical need only.
nc-pharma-formula-sedation-reversal-desc = An antidote that cuts the effect of sedatives, anesthetic and euphoric.
nc-pharma-formula-stimulant-reversal-desc = An antidote that cuts the effect of stimulants and the social stimulant.
nc-pharma-formula-vasopressor-autoinjector-desc = The vasopressor as a ready autoinjector for collapse in the field.

# --- Formula tree ---------------------------------------------------------------------------------------------
nc-pharma-rd-tree = Formula tree
nc-pharma-rd-branch-core = Core
nc-pharma-rd-branch-delivery = Delivery
nc-pharma-rd-branch-kinetics = Kinetics
nc-pharma-rd-branch-safety = Safety
nc-pharma-rd-branch-production = Production
nc-pharma-rd-node-header = [bold]{$name}[/bold]: {$state}
nc-pharma-rd-node-state-unknown = untested
nc-pharma-rd-node-state-suspected = suspected
nc-pharma-rd-node-state-confirmed = confirmed
nc-pharma-rd-node-unknown = Effects unknown. Only a batch made with it will show what it does.
nc-pharma-rd-effect-exact = {$what} x{$value}
nc-pharma-rd-effect-delta = {$what} {$value}
nc-pharma-rd-effect-up = {$what} up
nc-pharma-rd-effect-down = {$what} down
nc-pharma-rd-effect-peak = peak
nc-pharma-rd-effect-toxicity = toxicity
nc-pharma-rd-effect-absorption = absorption time
nc-pharma-rd-effect-clearance = clearance
nc-pharma-rd-effect-cost = cost
nc-pharma-rd-effect-stability = stability
nc-pharma-rd-effect-purity = purity
nc-pharma-rd-effect-sterility-risk = sterility risk
nc-pharma-rd-effect-contamination = adulterants
nc-pharma-rd-effect-yield = yield
nc-pharma-rd-effect-sterility = sterility
nc-pharma-rd-effect-homogeneity = homogeneity
nc-pharma-rd-effect-local = acts on the local tissue
nc-pharma-rd-effect-masking = hides part of the impurity from a simple check
nc-pharma-rd-effect-onset = onset {$seconds} s
nc-pharma-rd-effect-absorbed = absorbed over {$seconds} s
nc-pharma-rd-effect-time = run time {$seconds} s
nc-pharma-rd-effect-window = therapeutic {$therapeutic}, toxic {$toxic}
nc-pharma-rd-forecast-unknown = No forecast: the delivery form or production mode is untested.
nc-pharma-rd-forecast-excluded = The forecast leaves out untested nodes: {$nodes}. The real batch will include them.
nc-pharma-rd-notice-same-core = The two cores must be different compounds.
nc-pharma-rd-effect-requires = uses up {$amount} units of {$item} per run

# --- Inventing a compound --------------------------------------------------------------------------------
nc-lab-codename-invented = NX-{$code}
nc-pharma-rd-reagents-slot = reagent beaker
nc-pharma-rd-notice-invented = A new compound is made. It is unidentified: study a batch of it to learn what it does.
nc-pharma-rd-notice-invention-invalid = These ingredients cannot make a compound. Take two or three that lean towards a drug action.
nc-pharma-rd-notice-no-reagents = The beaker lacks the ingredients: put a beaker in the console with at least 5 units of every chosen reagent.

# --- The research console: a map of the formula, inventing a compound, materials -------------------------------
nc-pharma-rd-tab-tree = Formula tree
nc-pharma-rd-tab-invent = Invent a compound
nc-pharma-rd-tab-lines = Lines and journal
nc-pharma-rd-hub = New formula
nc-pharma-rd-new = Start from nothing
nc-pharma-rd-tree-hint = Click nodes to build a formula from nothing: a core, a delivery form, kinetics and a production mode. Drag to move, wheel to zoom. A [color=#FFC95C]question mark[/color] marks a node nobody has tested.
nc-pharma-rd-composition = The formula so far
nc-pharma-rd-composition-line = [bold]{$branch}:[/bold] {$value}
nc-pharma-rd-composition-missing = [bold]{$branch}:[/bold] [color=#FF8A8A]not chosen yet[/color]
nc-pharma-rd-continues = Continues the line [bold]{$name}[/bold]: the result becomes its next version when the cores match.
nc-pharma-rd-starts-new = A new line of its own.
nc-pharma-rd-node = Node
nc-pharma-rd-node-hint = Point at a node to see what the laboratory knows about it.
nc-pharma-rd-materials = Materials
nc-pharma-rd-materials-hint = Choose a core and the console shows what it is made from and how to get it.
nc-pharma-rd-materials-core = [bold]{$name}[/bold]
nc-pharma-rd-materials-none = Not made from any raw material yet.
nc-pharma-rd-materials-kind = • raw material: [color=#58e6d9]{$kind}[/color], {$yield}% of the doses
nc-pharma-rd-materials-substrate = Substrate [bold]{$name}[/bold]: {$recipe}
nc-pharma-rd-materials-broth = Broth [bold]{$name}[/bold]: {$recipe}
nc-pharma-rd-materials-supplied = only from a supplier
nc-pharma-rd-materials-extract = Then the extraction station turns the broth into a source canister.
nc-pharma-rd-materials-above = , warm above {$min} K
nc-pharma-rd-materials-between = , warm, {$min} to {$max} K
nc-pharma-rd-unidentified = {$name} (not studied)
nc-pharma-rd-lines-hint = Pick a line to see its card and to continue it: its nodes go onto the map, and a new version becomes the next one of the line.
nc-pharma-rd-continue = Continue this line on the map
nc-pharma-rd-ingredients = Ingredients
nc-pharma-rd-ingredients-hint = Take two or three. The reagents come from the beaker in the console, {$units} units of each are used up. Point at an ingredient to see what it does.
nc-pharma-rd-beaker = In the beaker
nc-pharma-rd-beaker-row = {$name}: {$amount}
nc-pharma-rd-beaker-empty = The beaker is empty.
nc-pharma-rd-beaker-missing = No beaker in the console. Put a beaker with the reagents into its slot.
nc-pharma-rd-expected = Expected result
nc-pharma-rd-expected-pick = Take two or three ingredients to see what they would make.
nc-pharma-rd-expected-nothing = These ingredients lean towards no drug action: nothing would come of them.
nc-pharma-rd-expected-action = Action: [bold]{$action}[/bold]
nc-pharma-rd-expected-strength = Strength: {$strength}
nc-pharma-rd-expected-risk = Risk: {$risk}
nc-pharma-rd-expected-side = Brings a side effect.
nc-pharma-rd-expected-dependence = Can lead to dependence.
nc-pharma-rd-expected-note = This is what the ingredients promise. What the compound really does shows only after it is studied.
nc-pharma-rd-invent = Make the compound
nc-pharma-rd-band-strength-low = weak
nc-pharma-rd-band-strength-medium = moderate
nc-pharma-rd-band-strength-high = strong
nc-pharma-rd-band-risk-low = low
nc-pharma-rd-band-risk-medium = moderate
nc-pharma-rd-band-risk-high = high
nc-pharma-rd-band-harm-low = low
nc-pharma-rd-band-harm-medium = moderate
nc-pharma-rd-band-harm-high = high
nc-pharma-rd-band-harm-negative = takes harm away
nc-pharma-rd-leaning-analgesic = pain relief
nc-pharma-rd-leaning-sedative = sedation
nc-pharma-rd-leaning-stimulant = stimulation
nc-pharma-rd-leaning-antibiotic = fighting bacteria
nc-pharma-rd-leaning-immunosuppressant = immune suppression
nc-pharma-rd-leaning-antitoxin = clearing toxins
nc-pharma-rd-leaning-neutral = neutral: only takes harm away
nc-pharma-rd-ingredient-row = {$name} · {$leaning} · in beaker: {$held}
nc-pharma-rd-ingredient-leans = Leans towards {$action}, {$power}.
nc-pharma-rd-ingredient-harm = Harm: {$harm}.

# --- The research chip -------------------------------------------------------------------------------------
nc-pharma-rd-chip-slot = research chip
nc-pharma-research-chip-examine = Studied nodes of the formula tree: [bold]{$count}[/bold].
nc-pharma-rd-notice-no-chip = There is no research chip in the console: nothing is studied. Put a chip into its slot.
nc-pharma-rd-notice-not-researched = The chip has not studied a core of this formula yet. Study it first: make a batch and run quality control.
nc-pharma-rd-no-chip = [color=#FF8A8A]No research chip.[/color] Everything is unstudied until a chip is put into the console.
nc-pharma-rd-chip-blank = The chip has studied nothing yet. Crossed nodes are cores waiting to be studied: point at one to see how. Make batches of the stock formulas and run quality control on them: what you learn is written to the chip.
ent-NCPharmaResearchChip = research chip
    .desc = A data chip that holds what a laboratory has studied. A blank one has studied nothing: put it into a research console and the research is written to it.
ent-NCPharmaResearchChipStock = research chip (stock formulas)
    .desc = A research chip with the stock formulas already studied. Put it into a research console to work with them at once.
nc-pharma-rd-core-locked = [bold]Not studied yet.[/bold] This core cannot be built on until the research chip has studied it. Make a batch of [bold]{$formula}[/bold] at a synthesizer and run quality control on it: what the laboratory learns is written to the chip, and the node opens.
