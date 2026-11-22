# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

nc-guide-pharma-overview-h = [head=1]Pharmacology: overview[/head]
nc-guide-pharma-overview-formula = [head=2]Formula and batch[/head]
    A [color=#58e6d9]formula[/color] is a recipe: which compound, in which delivery form, at which target concentration. A [color=#58e6d9]batch[/color] is one real production run of a formula. The label on a pack says what the producer claims; what is really inside is only known to the world and is revealed by quality control.
nc-guide-pharma-overview-chain = [head=2]The production chain[/head]
    1. [color=#58e6d9]Raw material[/color] of the right kind — bought, found, seized, or brewed by players.
    2. [color=#58e6d9]Extraction[/color] turns raw material into a source canister of the same kind, with its own purity.
    3. [color=#58e6d9]Purifier[/color] (optional) removes impurities at a cost.
    4. [color=#58e6d9]Synthesizer[/color] makes a batch of a known formula from a canister that suits it.
    5. [color=#58e6d9]Sterile cabinet[/color] and [color=#58e6d9]formulation station[/color] (optional) improve sterility and mixing.
    6. [color=#58e6d9]Filling station[/color] splits packs into single doses.
    7. [color=#58e6d9]QC analyzer[/color] tests what the batch really contains.
    8. The drug is administered to a patient.
nc-guide-pharma-overview-quality = [head=2]What makes a batch good or bad[/head]
    Every batch has hidden [color=#58e6d9]purity[/color], [color=#58e6d9]sterility[/color], [color=#58e6d9]homogeneity[/color], [color=#58e6d9]stability[/color] and real concentration. Low purity brings toxic adulterants. Low sterility risks sepsis. Poor mixing makes doses uneven. Low stability makes a drug spoil over time; keep unstable batches in [color=#58e6d9]cold storage[/color].
nc-guide-pharma-overview-dose = [head=2]Dosing the patient[/head]
    A dose is scaled to the patient's body mass. The same dose acts weaker on a heavy patient and harder on a light one. Check the patient's active drugs and toxic burden before administering anything, and repeat the scan afterwards. See also the Clinical Pharmacology page for the effects of each drug class.
nc-guide-pharma-overview-kinds = [head=2]Kinds of raw material[/head]
    Raw material comes in five kinds, and a canister keeps its kind:
    • [color=#58e6d9]Neuro-organic[/color] — painkillers, anesthetic, sedatives, stimulants, the respiratory stimulant and the recreational compounds.
    • [color=#58e6d9]Cardio-enzyme[/color] — vasopressor, vasodilator and antiarrhythmic.
    • [color=#58e6d9]Hemo-factor[/color] — procoagulant, anticoagulant, synthetic blood, plasma and red cells.
    • [color=#58e6d9]Microbial culture[/color] — every antibiotic.
    • [color=#58e6d9]Immuno-modulator[/color] — immunosuppressant, antitoxin and the antidotes.
    A formula is made from its main kind at full yield. Many can also be made from another kind, but only a share of the doses comes out; the formula card lists them. The synthesizer refuses a canister that cannot make the formula you picked.
guide-entry-nc-pharma-overview = Pharmacology: overview
nc-guide-pharma-brewing-h = [head=1]Brewing raw material[/head]
nc-guide-pharma-brewing-intro = Raw material does not have to be bought. Players can brew it in an ordinary beaker or the chem master. Raw material comes in [color=#58e6d9]kinds[/color]: neuro-organic, cardio-enzyme, hemo-factor, microbial and immuno-modulator. Every kind has its own substrate, and the solvent is plain [color=#58e6d9]ethanol[/color] from the chemical dispenser. The substrates do nothing inside a body: they only become useful once brewed and extracted.
nc-guide-pharma-brewing-recipe = [head=2]Recipes[/head]
    {""}[color=#58e6d9]Broth[/color]: 2 parts of a [color=#58e6d9]substrate[/color] + 1 part of [color=#58e6d9]ethanol[/color], kept warm between 330 K and 380 K. 3 parts of the broth of the substrate's kind result.
    {""}[color=#58e6d9]Brew sludge[/color]: a broth heated above 400 K spoils into sludge. Heat gently and take it off the heat in time.
    A cold mixture does not react at all.
nc-guide-pharma-brewing-source = [head=2]Where to get the ingredients[/head]
    Each substrate comes in its own jug of 150 units. Ethanol comes from the chemical dispenser, so a lab always has the solvent but has to find the substrates.
nc-guide-pharma-brewing-extract = [head=2]From the beaker to a source canister[/head]
    Put the beaker with the brew into the [color=#58e6d9]extraction station[/color] and start it. The extractor reads the beaker itself:
    • the [color=#58e6d9]kind[/color] of the extract is the broth there is most of;
    • the share of that broth in the whole solution is the [color=#58e6d9]purity[/color] (never above 80% for a home brew), so a mixture of kinds or leftover ingredients dilute it;
    • the share of sludge becomes hidden [color=#58e6d9]contamination[/color] in the extract, on top of a small base amount;
    • every unit of broth gives about 0.6 units of raw material before extraction losses.
    The beaker stays in the machine and is emptied. A solution without enough broth is refused.
    Home brew always yields street-grade source material.
nc-guide-pharma-brewing-tips = [head=2]Brewing tips[/head]
    • Use exact ratios: leftover ingredients dilute the broth and lower purity.
    • Do not mix kinds: the lesser broth only dilutes the greater one.
    • Do not overheat: sludge goes straight into the contamination of the extract.
    • To raise purity further, pass the canister through the [color=#58e6d9]purifier[/color].
    • Never trust your own brew without QC: contamination is hidden from the label.
guide-entry-nc-pharma-brewing = Brewing raw material
nc-guide-pharma-stations-h = [head=1]Stations and equipment[/head]
nc-guide-pharma-stations-all = Every station needs power, takes time, produces waste and asks how well the operator handled the procedure; skill changes the quality of the result.
    
    {""}[color=#58e6d9]Extraction station[/color] — extracts usable source material from raw material. An unlabelled sample is isolated instead and may reveal an unknown compound.
    {""}[color=#58e6d9]Purifier[/color] — removes impurities from raw material. Each pass costs material, filter capacity and time; the last percents of purity are the dearest.
    {""}[color=#58e6d9]Pharmaceutical synthesizer[/color] — produces a known formula in batches from a canister. It comes in street, clinical and corporate grades: a patched-together line is fast and cheap but poor in sterility, mixing and calibration; a clinical line is sterile with even mixing; a corporate line is precise with a large run capacity.
    {""}[color=#58e6d9]Sterile cabinet[/color] — sterilizes a batch; the heat costs some stability.
    {""}[color=#58e6d9]Formulation station[/color] — mixes a batch more evenly; the handling costs some stability.
    {""}[color=#58e6d9]Filling station[/color] — splits a pack into single-dose packs of the formula's own kind.
    {""}[color=#58e6d9]Pharmaceutical QC analyzer[/color] — measures what a batch really contains. Each run uses up one dose of the inserted pack as the sample.
    {""}[color=#58e6d9]Thermoreactor[/color] — runs the reaction of a synthesis. Keep it in its window; an exothermic or volatile reaction that gets away releases fumes that damage the lungs.
    {""}[color=#58e6d9]Cold storage[/color] — unstable batches stop spoiling while they are inside and the cabinet has power.
    {""}[color=#58e6d9]Research console[/color] — develops new versions of formulas, approves certified ones and keeps the laboratory journal.
    {""}[color=#58e6d9]Waste incinerator[/color] and [color=#58e6d9]waste canisters[/color] — every station makes hazardous waste; full canisters must be burned.
nc-guide-pharma-stations-kits = [head=2]Starter kits[/head]
    {""}[color=#58e6d9]Street lab starter crate[/color] — raw material of four kinds, filters, a catalyst vial and a stabilizer vial, waste canisters and vials.
    {""}[color=#58e6d9]Clinical lab starter crate[/color] — corporate raw stock of five kinds with filters, two catalyst and two stabilizer vials, waste canisters and vials.
    {""}[color=#58e6d9]Sampling kit[/color] — blood samplers, wound swab kits and empty vials.
    {""}[color=#58e6d9]Blood bank kit[/color] — empty bags for collecting blood from donors.
    {""}[color=#58e6d9]Implant preparation kit[/color] — seed cultures for coating, antibacterial material, tissue interface, neural gel and regenerative matrix.
    {""}[color=#58e6d9]Unlabelled sample box[/color] — raw samples with no label; nobody knows what they hold.
nc-guide-pharma-stations-consumables = [head=2]Process consumables[/head]
    {""}[color=#58e6d9]Catalyst vial[/color] — an ordinary chemistry vial with reaction catalyst in it. Put it in the thermoreactor's catalyst slot. The run draws two units out of it for every charge, at most two charges, and the vial stays: top it up, pour it out or move it like any vial.
    {""}[color=#58e6d9]Stabilizer vial[/color] — a vial of stabilizer for the synthesizer's additive slot. A formula with the stabilizer modifier needs five units in it for every run and uses them up; the formula card says so.
    Ordinary stations need only power and a waste canister with room in it; a standard batch by hand takes about ten minutes from raw material to a tested pack.
guide-entry-nc-pharma-stations = Stations and equipment
nc-guide-pharma-formulary-h = [head=1]Formulary and production options[/head]
nc-guide-pharma-formulary-formulas = [head=2]Known formulas[/head]
    {""}[color=#58e6d9]Field[/color]: field trauma analgesic, field stimulant, maintenance analgesic.
    {""}[color=#58e6d9]Anesthesia and sedation[/color]: surgical anesthetic, sedative, sedation reversal agent.
    {""}[color=#58e6d9]Circulation[/color]: vasopressor (also as an autoinjector), vasodilator, antiarrhythmic, procoagulant, anticoagulant, synthetic blood.
    {""}[color=#58e6d9]Respiration and detox[/color]: respiratory stimulant, antitoxin, stimulant reversal agent.
    {""}[color=#58e6d9]Infection and transplants[/color]: broad-spectrum antibiotic infusion, gram-positive antibiotic, gram-negative antibiotic, immunosuppressive therapy.
    {""}[color=#58e6d9]Recreational[/color]: clinical euphoric, social stimulant.
    Other compounds can be found by isolating unlabelled samples. They are experimental until a formula is developed for them.
nc-guide-pharma-formulary-forms = [head=2]Delivery forms[/head]
    {""}[color=#58e6d9]Oral[/color] — pills; weak but low risk. [color=#58e6d9]Sublingual[/color] — strips; moderate and safe. [color=#58e6d9]Inhaled[/color] — inhaler; quick but with more adulterant exposure. [color=#58e6d9]Autoinjector[/color] — fastest and full strength. [color=#58e6d9]Infusion[/color] — an IV bag, full strength, but a higher sterility risk. [color=#58e6d9]Transdermal patch[/color] — very slow and weak, but steady. [color=#58e6d9]Surgical gel[/color] — acts mostly on the local tissue it is applied to. [color=#58e6d9]Transfusion[/color] — a blood bag, delivered in volume.
nc-guide-pharma-formulary-modifiers = [head=2]Modifiers[/head]
    Modifiers are added to a formula for a price.
    • [color=#58e6d9]Fast release[/color] — stronger and faster, shorter-lived, a little more toxic.
    • [color=#58e6d9]Extended release[/color] — weaker and much slower, more stable.
    • [color=#58e6d9]Protective carrier[/color] — much less toxic, a little weaker.
    • [color=#58e6d9]Stabilizer[/color] and [color=#58e6d9]Cold solvent[/color] — raise stability. The stabilizer also uses up five units from a stabilizer vial on every production run.
    • [color=#58e6d9]Buffered[/color] — less toxic, absorbed slightly slower.
    • [color=#58e6d9]Concentrated[/color] and [color=#58e6d9]Potentiator[/color] — stronger but more toxic and less stable.
    • [color=#58e6d9]Purified[/color] — higher purity, expensive.
    • [color=#58e6d9]Absorption enhancer[/color] — absorbed faster, more toxic.
    • [color=#58e6d9]Masking agent[/color] — hides the compound from scans, costs purity and adds toxicity.
    • [color=#58e6d9]Metabolic blocker[/color] — slows the body's destruction of the compound, so the effect lasts longer and repeated doses build up.
    • [color=#58e6d9]Tissue targeting[/color] — part of the dose acts on local tissue.
nc-guide-pharma-formulary-profiles = [head=2]Production profiles[/head]
    {""}[color=#58e6d9]Quick synthesis[/color] — high yield and cheap, fast, but poor purity, sterility and mixing. Its label does not claim sterility.
    {""}[color=#58e6d9]Standard synthesis[/color] — the medical grade baseline.
    {""}[color=#58e6d9]Clean synthesis[/color] — slow and expensive with a low yield, but the best purity, sterility and stability: the corporate grade.
    {""}[color=#58e6d9]Manual synthesis[/color] — a one-off hand-made dose.
    A formula also has a [color=#58e6d9]reaction type[/color]: stable reactions have a wide safe window; exothermic ones run hot; volatile ones have a narrow window, a pressure limit and are hazardous when they get away.
nc-guide-pharma-formulary-dependence = [head=2]Tolerance and dependence[/head]
    Repeated doses build [color=#58e6d9]tolerance[/color]: the same dose acts weaker. Some compounds, such as stimulants, analgesics, sedatives and euphorics, also build [color=#58e6d9]dependence[/color]. When the supply stops, the patient suffers [color=#58e6d9]withdrawal[/color] with symptoms such as fatigue and tremor. A [color=#58e6d9]maintenance analgesic[/color] replaces the street drug and lets the patient come down safely; reversal agents cancel the effect of sedation or stimulants.
guide-entry-nc-pharma-formulary = Formulary and production options
nc-guide-pharma-laboratory-h = [head=1]Laboratory and diagnostics[/head]
nc-guide-pharma-laboratory-samples = [head=2]Samples[/head]
    Use the [color=#58e6d9]blood sampler[/color], [color=#58e6d9]wound swab kit[/color] and [color=#58e6d9]sample vials[/color] to take a sample from a patient. A sample records what the patient has at the moment it is taken.
    {""}[color=#58e6d9]Metabolic analyzer[/color] — reads the blood chemistry of a sample: blood group, compounds and how the body is responding to them.
    {""}[color=#58e6d9]Culture station[/color] — grows cultures from samples to find infections and the antibiotics that work on them, and tests compounds on living tissue. Strains differ: the gram-positive antibiotic works best on one, the gram-negative on another, the broad antibiotic on both, and a resistant strain responds only weakly to everything.
nc-guide-pharma-laboratory-blood = [head=2]Blood bank[/head]
    Collect blood from a donor into an empty [color=#58e6d9]blood bag[/color]. The [color=#58e6d9]blood centrifuge[/color] splits whole blood into red cells and plasma. A donated bag is a transfusion product with its own group and compatibility; a wrong group causes a reaction. [color=#58e6d9]Synthetic blood[/color] is made as a pharmacology formula and does not depend on a donor.
nc-guide-pharma-laboratory-tissue = [head=2]Tissue products[/head]
    The [color=#58e6d9]tissue incubator[/color] grows tissue products from seed cultures: biocompatible coating, antibacterial material, tissue interface, neuroconductive gel and regenerative matrix. It needs power and a person to load it and collect the result. The implant preparation kit holds the seeds. Synthetic skin is grown the same way and used as a wound dressing.
nc-guide-pharma-laboratory-vivarium = [head=2]Vivarium and research[/head]
    The [color=#58e6d9]vivarium post[/color] is an observation post for experimental pharmacology and toxicology. A dose given to a patient near a working post is written down as a study: what the patient received and how the body took it. The [color=#58e6d9]research console[/color] turns those studies into new versions of a formula and keeps the laboratory journal.
guide-entry-nc-pharma-laboratory = Laboratory and diagnostics
nc-guide-pharma-quality-h = [head=1]Quality control and storage[/head]
nc-guide-pharma-quality-qc = [head=2]Quality control[/head]
    The [color=#58e6d9]pharmaceutical QC analyzer[/color] measures what a batch really contains. Insert a dose pack, tick the tests you want and run the analysis. Each run uses up one dose of the pack as the sample, so test a pack you can spare.
    Tests: [color=#58e6d9]concentration[/color], [color=#58e6d9]purity[/color], [color=#58e6d9]sterility[/color], [color=#58e6d9]homogeneity[/color] and [color=#58e6d9]stability[/color]. Only the tests you ticked are revealed; the rest stay unknown.
    The result lists every tested line with the label value next to the measured one. A line that does not match is marked as failed. A pack that was never tested shows as untested.
nc-guide-pharma-quality-status = [head=2]Laboratory status[/head]
    A batch is [color=#58e6d9]untested[/color], [color=#58e6d9]partially tested[/color], [color=#58e6d9]certified[/color], [color=#58e6d9]rejected[/color] or [color=#58e6d9]recalled[/color]. Only a certified batch has passed every test within tolerance: concentration within 10% of the label, purity within 5%, and sterility, homogeneity and stability above their minimums.
    A formula's [color=#58e6d9]regulatory status[/color] is free sale, prescription, controlled, clinical only, corporate, banned, recalled, unknown or experimental. It describes how the formula may be handled, not how good a particular batch is.
nc-guide-pharma-quality-grades = [head=2]Grades and labels[/head]
    The pack label shows the [color=#58e6d9]grade[/color] (street, medical or corporate) and whether the producer claims it sterile. A medical grade batch needs purity, sterility and homogeneity of at least 90%, 90% and 85%; a corporate grade batch needs 97%, 97% and 95%. A quick synthesis never claims sterility.
    A [color=#58e6d9]masking agent[/color] hides the real composition: the QC analyzer warns when it finds one, because the batch is worse than a simple purity check shows.
nc-guide-pharma-quality-impurities = [head=2]Impurities[/head]
    Raw material carries hidden [color=#58e6d9]contaminants[/color]. Seized samples carry the most, bioreactor biomass and corporate stock carry a little, and a home brew carries what its sludge brings. Extraction passes them into the source canister, synthesis passes them into the batch, and the patient receives them as adulterant toxin. Low sterility adds a sepsis burden on top.
    The label never shows contaminants; only QC and the patient's condition do.
nc-guide-pharma-quality-cold = [head=2]Cold chain[/head]
    Some formulas are unstable and spoil at room temperature; examining a pack of such a formula says it must be kept refrigerated. [color=#58e6d9]Cold storage[/color] stops the spoilage while the batch is inside and the cabinet has power. A stabilizer, a cold solvent and clean synthesis all make a batch more stable. Keep unstable batches cold from the moment they are made.
nc-guide-pharma-quality-waste = [head=2]Waste[/head]
    Every station makes hazardous waste and pours it into the [color=#58e6d9]waste canister[/color] standing in its waste slot. Without a canister with room in it the station will not run. The canister is an ordinary jug: carry it, pour it, spill it, but the waste is poisonous. Take it to the [color=#58e6d9]waste incinerator[/color] (it needs power): the incinerator burns the waste and leaves you the empty canister. A canister with waste in it that is left lying around leaks toxic fumes.
guide-entry-nc-pharma-quality = Quality control and storage
nc-guide-pharma-reactor-h = [head=1]Thermoreactor and experimental formulas[/head]
nc-guide-pharma-reactor-rd = [head=2]The research console[/head]
    The [color=#58e6d9]research console[/color] lists the laboratory's formulas and is where new ones are built; see Building a formula. The [color=#58e6d9]laboratory journal[/color] records what was produced, tested, administered, approved and created, with who did it and details such as input purity, number of QC tests, and how a patient took a dose. You can attach your own [color=#58e6d9]note[/color] to a version.
    An experimental version can be approved only after quality control has certified a batch of it. A compound that has not been identified by chemical analysis cannot be approved.
nc-guide-pharma-reactor-unknown = [head=2]Unknown compounds[/head]
    An unlabelled raw sample hides an unidentified compound. Run it through the extraction station to [color=#58e6d9]isolate[/color] it. The result is an experimental formula with a code name. Its card starts mostly unknown; each study fills lines in as suspected, then confirmed. Use the vivarium and the metabolic analyzer to learn what it does before anyone takes it for real.
nc-guide-pharma-reactor-reactor = [head=2]The thermoreactor[/head]
    Only a formula under development may be made by hand in the reactor, and a manual run is limited to a small amount of material. The gauges show temperature, pressure, mixing, catalyst charges, reaction progress and instability. The windows depend on the reaction type, in °C:
    • [color=#58e6d9]stable[/color]: optimal 60-75, safe 45-95.
    • [color=#58e6d9]exothermic[/color]: optimal 55-70, safe 40-85; it heats itself.
    • [color=#58e6d9]volatile[/color]: optimal 50-62, safe 38-75, pressure soft limit 170 and hard limit 240; hazardous.
    Controls: [color=#58e6d9]heater[/color], [color=#58e6d9]cooler[/color], [color=#58e6d9]stirrer[/color], [color=#58e6d9]pressure relief valve[/color] (slows the reaction), [color=#58e6d9]ventilation[/color] (reduces a toxic release), [color=#58e6d9]catalyst[/color], [color=#58e6d9]quench[/color] and [color=#58e6d9]emergency stop[/color] (the material is lost).
nc-guide-pharma-reactor-accident = [head=2]When it goes wrong[/head]
    Watch the warnings: temperature above the optimal window, close to the safe limit, rising pressure, poor mixing that creates hot spots, a runaway reaction, an unstable mixture. If you ignore them the mixture [color=#58e6d9]degrades into sludge[/color], the reactor [color=#58e6d9]vents toxic fumes[/color] that damage the lungs, the vessel [color=#58e6d9]ruptures and catches fire[/color], or at worst [color=#58e6d9]explodes violently[/color]. Cool it, vent it, quench it or stop it in time, and keep the ventilation on.
guide-entry-nc-pharma-reactor = Thermoreactor and experimental formulas
nc-guide-pharma-pharmacopoeia-h = [head=1]Pharmacopoeia and interactions[/head]
nc-guide-pharma-pharmacopoeia-drugs = [head=2]Drugs[/head]
    {""}[color=#58e6d9]Trauma analgesic[/color] — suppresses pain without repairing the injury.
    {""}[color=#58e6d9]Surgical anesthetic[/color] — controlled anesthesia; too much depresses breathing.
    {""}[color=#58e6d9]Clinical sedative[/color] — reduces awareness and movement.
    {""}[color=#58e6d9]Vasopressor[/color] — supports cardiac output and vascular tone.
    {""}[color=#58e6d9]Vasodilator[/color] — reduces vascular tone; unsafe dosing can collapse perfusion.
    {""}[color=#58e6d9]Antiarrhythmic[/color] — stabilizes an unstable rhythm but cannot restart an arrested heart.
    {""}[color=#58e6d9]Respiratory stimulant[/color] — improves working lungs; it cannot replace absent ones.
    {""}[color=#58e6d9]Clinical stimulant[/color] — raises arousal and cardiopulmonary drive; overdose destabilizes physiology.
    {""}[color=#58e6d9]Procoagulant[/color] — reduces active bleeding through systemic clot support; it does not close a wound.
    {""}[color=#58e6d9]Anticoagulant[/color] — reduces clotting and increases bleeding.
    {""}[color=#58e6d9]Broad-spectrum antibiotic[/color], [color=#58e6d9]gram-positive antibiotic[/color], [color=#58e6d9]gram-negative antibiotic[/color] — suppress susceptible wound infection while the concentration is maintained.
    {""}[color=#58e6d9]Systemic antitoxin[/color] — speeds the clearance of circulating toxic burden through working organs.
    {""}[color=#58e6d9]Immunosuppressant[/color] — suppresses transplant rejection while raising vulnerability to infection.
    {""}[color=#58e6d9]Synthetic oxygen carrier[/color] — adds oxygen capacity without replacing lost blood volume.
    {""}[color=#58e6d9]Cyberware coolant agent[/color] — removes measured excess heat from compatible implants.
    {""}[color=#58e6d9]Maintenance analgesic[/color] — a weaker analgesic for coming off a stronger one.
    {""}[color=#58e6d9]Sedation reversal agent[/color] and [color=#58e6d9]stimulant reversal agent[/color] — see below.
    {""}[color=#58e6d9]Clinical euphoric[/color] and [color=#58e6d9]social stimulant[/color] — recreational compounds.
nc-guide-pharma-pharmacopoeia-inter = [head=2]Interactions[/head]
    Two drugs together change each other's effect and toxicity:
    • [color=#58e6d9]Sedative + surgical anesthetic[/color]: stronger effect, much more toxic.
    • [color=#58e6d9]Sedative + trauma analgesic[/color]: more toxic.
    • [color=#58e6d9]Sedative + stimulant[/color] or [color=#58e6d9]respiratory stimulant[/color]: weaker effect, more toxic.
    • [color=#58e6d9]Vasopressor + vasodilator[/color]: each cancels half of the other, a little more toxic.
    • [color=#58e6d9]Vasopressor + antiarrhythmic[/color]: a little less toxic.
    • [color=#58e6d9]Procoagulant + anticoagulant[/color]: each cancels half of the other, more toxic.
    • [color=#58e6d9]Euphoric + sedative[/color] or [color=#58e6d9]trauma analgesic[/color]: more toxic.
    • [color=#58e6d9]Social stimulant + clinical stimulant[/color]: more toxic.
    The metabolic analyzer reports a combination as compatible, caution, dangerous or unknown. A compound whose interactions are not charted may be reported as unknown: do not combine it with anything you cannot afford to lose.
nc-guide-pharma-pharmacopoeia-reversal = [head=2]Antidotes[/head]
    {""}[color=#58e6d9]Sedation reversal agent[/color] cuts the effect of sedatives, the surgical anesthetic, euphoric and maintenance analgesic to under half and lowers their toxicity. [color=#58e6d9]Stimulant reversal agent[/color] does the same for the clinical stimulant, the social stimulant and NC-X17. Use them to wake a patient or to take the edge off a bad reaction.
nc-guide-pharma-pharmacopoeia-dep = [head=2]Tolerance and withdrawal[/head]
    Stimulants, analgesics, sedatives, euphorics and the social stimulant build [color=#58e6d9]tolerance[/color] with every dose, and it fades slowly. When tolerance is high and the drug is gone, [color=#58e6d9]withdrawal[/color] sets in: after stimulants it is [color=#58e6d9]fatigue[/color] that begins after about two minutes and deepens over several, and the social stimulant also leaves fatigue; after analgesics, sedatives and euphorics it is [color=#58e6d9]tremor[/color]. The [color=#58e6d9]maintenance analgesic[/color] gives a weaker, safer level of pain relief to step down from a stronger drug.
    Recreational compounds go through phases the user feels: onset, peak, decline and comedown. Stimulation, sedation, euphoria, perception, sociability and dissociation each have their own messages. The first dose is always at full strength: the tolerance a dose earns applies from the next dose on, and a single dose leaves none behind.
guide-entry-nc-pharma-pharmacopoeia = Pharmacopoeia and interactions
nc-guide-pharma-fielduse-h = [head=1]Field use: local effect and kits[/head]
nc-guide-pharma-fielduse-local = [head=2]Local effect on wounds[/head]
    Part of a dose works directly on the wounds, not only through the blood:
    • an [color=#58e6d9]analgesic[/color] numbs the wound that hurts most;
    • a [color=#58e6d9]procoagulant[/color] slows the bleeding of the open wound that bleeds hardest;
    • an [color=#58e6d9]antibiotic[/color] reduces infection in every wound, scaled by how susceptible the strain is.
    The effect lasts for a limited time and depends on blood supply: poor perfusion carries less of the drug to the wound. The [color=#58e6d9]tissue targeting[/color] modifier and the [color=#58e6d9]surgical gel[/color] form send more of the dose to the local tissue. A dose also still acts on the whole body.
nc-guide-pharma-fielduse-dress = [head=2]Dressings[/head]
    A dressing soaks through and can carry contamination: check its saturation and replace a saturated one. [color=#58e6d9]Synthetic skin dressing[/color] is grown in the tissue incubator and is used like a sterile bandage. See the field treatment page for tourniquets and packing.
nc-guide-pharma-fielduse-bag = [head=2]Trauma Team combat medic bag[/head]
    A corporate bag for stabilising yourself and the person next to you. It holds 4 trauma analgesic pens, 3 procoagulant pens, 2 respiratory stimulant pens, 2 antitoxin pens, 2 vasopressor pens, 2 gauze and 2 tourniquets. The pens are corporate-grade autoinjectors from clean production: high purity, sterility and stability. The bag is only a container of ready items; it is a quick way to deal with bleeding, pain, poisoning and collapse, not a replacement for a doctor.
nc-guide-pharma-fielduse-injectors = [head=2]Ready-made injectors[/head]
    Stock medicine comes as an [color=#58e6d9]autoinjector[/color], an [color=#58e6d9]IV bag[/color] or a [color=#58e6d9]blood bag[/color], depending on the formula's delivery form. Each is only a sprite and a dose from a real batch: it cannot be drawn from, poured out or mixed. Use it on the patient and the dose goes in with the delivery time of its form; an autoinjector is quick, an IV bag and a blood bag take several seconds.
guide-entry-nc-pharma-fielduse = Field use: local effect and kits
nc-guide-pharma-implants-h = [head=1]Implants and transplants[/head]
nc-guide-pharma-implants-prep = [head=2]Preparing a module for living tissue[/head]
    A module that has not been prepared still works, but the patient has a rough first minutes. Prepare it on the bench, before it is installed; once it is in the body it is too late. Grow the products in the tissue incubator from the implant preparation kit:
    • [color=#58e6d9]Biocompatible coating[/color] — the body accepts the surface.
    • [color=#58e6d9]Antibacterial material[/color] — keeps infection out of the surgical site.
    • [color=#58e6d9]Tissue interface[/color] — tissue grows into the implant, so it is accepted sooner.
    • [color=#58e6d9]Regenerative matrix[/color] — the tissue around the implant heals faster.
    • [color=#58e6d9]Neuroconductive gel[/color] — restores the neural contact of an implant.
    Examining a module shows its biocompatibility: how well it fits and how clean it is.
nc-guide-pharma-implants-trans = [head=2]Transplants[/head]
    After a transplant the body may reject the graft. The [color=#58e6d9]immunosuppressant[/color] suppresses rejection but raises the patient's vulnerability to infection, so keep the wounds clean and watch for infection while it is active. Donated blood must be matched to the recipient and kept cold.
guide-entry-nc-pharma-implants = Implants and transplants
nc-guide-pharma-walkthrough-h = [head=1]Your first drug, step by step[/head]
nc-guide-pharma-walkthrough-steps = 1. [color=#58e6d9]Get raw material[/color] of the kind your formula needs: a starter crate, a corporate or street purchase, a seized sample, or brew your own (see Brewing raw material).
    2. [color=#58e6d9]Extract[/color]: put the raw material into the extraction station and activate it. You get a source canister of the same kind, with a purity of its own. Empty the waste tank when it fills.
    3. [color=#58e6d9]Purify[/color] (optional): pass the canister through the purifier for higher purity at a cost in material, filters and time.
    4. [color=#58e6d9]Synthesize[/color]: put the canister into the synthesizer, pick a formula, a production mode and how much material to use. The formula card shows which kinds it can be made from; a canister of another kind gives fewer doses or is refused. If the formula uses a stabilizer, put a stabilizer vial in the additive slot. The forecast shows doses, time and cost. Start production. You get a batch with its own code.
    5. [color=#58e6d9]Treat[/color] (optional): put the pack into the sterile cabinet to raise sterility, or the formulation station to even out the mixing; both cost a little stability.
    6. [color=#58e6d9]Fill[/color] (optional): the filling station splits a pack into single-dose packs.
    7. [color=#58e6d9]Test[/color]: insert a spare pack into the QC analyzer and tick the tests you care about. Do not use a batch that fails.
    8. [color=#58e6d9]Store[/color]: keep unstable batches in cold storage.
    9. [color=#58e6d9]Administer[/color]: scan the patient, check active drugs and toxic burden, give the dose, then scan again and watch the response.
    Doing it by hand, a standard batch takes about ten minutes. Your own formula is built in the research console (see Building a formula) and is never faster than its tests.
guide-entry-nc-pharma-walkthrough = Your first drug, step by step
nc-guide-pharma-build-h = [head=1]Building a formula[/head]
nc-guide-pharma-build-tree = [head=2]The tree[/head]
    The research console builds a formula on a tree of five branches. Pick nodes with the buttons:
    • [color=#58e6d9]Core[/color] — the compound, one or two.
    • [color=#58e6d9]Delivery[/color] — the delivery form, one.
    • [color=#58e6d9]Kinetics[/color] — the concentration, and modifiers for peak, speed and duration.
    • [color=#58e6d9]Safety[/color] — modifiers that make the drug gentler.
    • [color=#58e6d9]Production[/color] — the production mode, and modifiers for stability, purity and masking.
    At most two modifiers in all. Nothing limits how much you change at once, but changing one thing at a time is the only way to tell what did what.
nc-guide-pharma-build-cores = [head=2]One core or two[/head]
    A core is a compound the laboratory already knows: those of its formulas, once identified. With two cores each is delivered at 60% of the concentration, the pair is 25% more toxic than a single one, and the raw material must suit both; the yield is the worse of the two. Two cores are a choice with a price, not an upgrade.
nc-guide-pharma-build-names = [head=2]Names and versions[/head]
    You do not name a drug. A formula built on the same cores as the one you selected is its [color=#58e6d9]next version[/color]. Any other combination starts a new [color=#58e6d9]line[/color], named after its first core, with the second core added after a plus. A laboratory can develop 30 lines; versions inside a line are not counted.
nc-guide-pharma-build-knowledge = [head=2]What the laboratory knows[/head]
    Every node is [color=#58e6d9]untested[/color] (grey), [color=#58e6d9]suspected[/color] (amber) or [color=#58e6d9]confirmed[/color]. Everything the stock formulas use is confirmed. A node used only by a version you developed is suspected until a batch of such a version is certified by quality control. Hover over a node to read what is known: exact numbers for a confirmed one, only the direction for a suspected one, nothing for an untested one.
    The [color=#58e6d9]forecast[/color] counts only confirmed nodes and says which it left out; the real batch will include them. If the delivery form or production mode is untested there is no forecast at all. The only way to learn a node is to make a batch with it, test it, and write down what you saw.
nc-guide-pharma-build-approve = [head=2]From idea to regular production[/head]
    A new version is [color=#58e6d9]experimental[/color]. Make a small batch, run QC and, where it matters, a vivarium study. Once a batch of the version is certified you can approve it, and it joins regular production. Notes in the laboratory journal are yours: say what you tried and what you concluded.
guide-entry-nc-pharma-build = Building a formula
