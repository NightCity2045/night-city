# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

nc-corp-militech = Militech (field office)
nc-corp-biotechnica = Biotechnica (field office)

nc-corp-sales-title = Sales contracts
nc-corp-sales-refresh = Check pallet
nc-corp-sales-hint = Put the goods on the sales pallet, loose or in crates, then fulfil the contract. Only full deliveries are accepted.
nc-corp-sales-no-pallet = No sales pallet next to the console.
nc-corp-sales-not-employee = Only employees of this corporation can fulfil contracts.
nc-corp-sales-no-contracts = No open contracts.
nc-corp-sales-incomplete = The pallet does not hold everything the contract asks for.
nc-corp-sales-fulfilled = Contract "{$contract}" fulfilled: {$reward} €$.
nc-corp-sales-fulfill = Fulfil
nc-corp-sales-requirement = {$what}: {$have}/{$need}
nc-corp-sales-reward = Reward {$reward} €$ ({$department} to the department, {$corporation} to the corporation)
nc-corp-sales-drug = {$formula} doses, grade {$grade} or better
nc-corp-sales-drug-certified = {$formula} doses, grade {$grade} or better, QC certified
nc-corp-sales-grade-street = street
nc-corp-sales-grade-medical = medical
nc-corp-sales-grade-corporate = corporate

nc-corp-contract-militech-saratoga = Saratoga SMGs for a security contractor
nc-corp-contract-militech-saratoga-desc = A private security firm re-arms its patrol teams.
nc-corp-contract-militech-copperhead = Copperhead rifles
nc-corp-contract-militech-sidearms = Federal X-9 sidearms
nc-corp-contract-militech-snipers = Nomad sniper rifles
nc-corp-contract-militech-breachers = Crusher breaching shotguns
nc-corp-contract-militech-assault-kit = Assault team kit
nc-corp-contract-militech-assault-kit-desc = Rifles with spare 5.56 magazines.
nc-corp-contract-militech-ammo = SMG ammunition supply
nc-corp-contract-militech-armor = Body armor and helmets
nc-corp-contract-militech-optics = Cybereye components
nc-corp-contract-militech-optics-desc = Optic controllers and matrices for the implant line.
nc-corp-contract-militech-neural = Neural implant components

nc-corp-contract-biotechnica-burgers = Cheeseburgers for a corporate canteen
nc-corp-contract-biotechnica-pizza = Pizza for a night shift
nc-corp-contract-biotechnica-bread = Fresh bread
nc-corp-contract-biotechnica-soup = Tomato soup
nc-corp-contract-biotechnica-tomatoes = Tomato harvest
nc-corp-contract-biotechnica-wheat = Wheat harvest
nc-corp-contract-biotechnica-vegetables = Potatoes and cabbage
nc-corp-contract-biotechnica-orchard = Apples and corn
nc-corp-contract-biotechnica-antibiotic = Broad-spectrum antibiotic
nc-corp-contract-biotechnica-analgesic = Trauma analgesic
nc-corp-contract-biotechnica-synthetic-blood = Synthetic blood
nc-corp-contract-biotechnica-autoinjectors = Corporate vasopressor autoinjectors
nc-corp-contract-biotechnica-drug-desc = Packaged batches of this formula, medical grade or better, certified by QC.
nc-corp-contract-biotechnica-corporate-desc = Packaged batches of corporate grade, certified by QC.

ent-NCCorpSalesConsoleBase = sales contract console
    .desc = Shows the corporation's open contracts and accepts the goods on the sales pallet next to it.
ent-NCCorpSalesPallet = sales pallet
    .desc = Goods for corporate contracts go here.
