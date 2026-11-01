# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
ent-NCMetaFabricator = autolathe
    .desc = A native autolathe with a programmable batch controller. Real materials are reserved before production; interrupted batches require reconciliation.
    .suffix = META production controller
ent-NCMetaLaboratoryGrinder = reagent grinder
    .desc = A native grinder with a programmable batch controller. Fill the chamber and attach a beaker before enabling automation.
    .suffix = META batch controller
ent-NCMetaLaboratoryChemMaster = ChemMaster
    .desc = A native chemical packager with a programmable controller. Packaging uses real solution, containers and a nearby commissioned operator.
    .suffix = META packaging controller
ent-NCMetaBodyScanner = body scanner computer
    .desc = A scanner with a commissioning port. It reads only the real patient strapped to its linked operating table.
    .suffix = META commissioning port
ent-NCMetaScannerGateway = META scanner gateway
    .desc = A portable gateway for an owned stationary scanner. Bind its patient, then use the commissioning tool to connect the actual scanner.
    .suffix = META stationary scanner gateway
