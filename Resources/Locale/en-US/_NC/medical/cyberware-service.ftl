nc-cyberware-firmware-standard = standard control firmware
nc-cyberware-firmware-service-lock = active mode service lock
nc-cyberware-service-flashed = Firmware written. Repeat the bench test before installation.
nc-cyberware-service-invalid = invalid service state
nc-cyberware-service-mounted = { $item } is connected for local firmware service and inspection. Use physical media or a blank sheet of paper.
nc-cyberware-service-document =
    CYBERWARE SERVICE REPORT
    Technical serial: { $serial }
    Configuration revision: { $revision }; service revision: { $service }
    Firmware: { $firmware }
    Frame integrity: { $integrity }; service ceiling: { $ceiling }
    Heat: { $heat }
    { $tested }
    This report describes the item at inspection. Paper and external markings may be altered; verify the technical serial on the physical bench.
nc-cyberware-service-admin-description = Inspect private physical cyberware identities, firmware and service history.
nc-cyberware-service-admin-help = nccyberwarestatus <network entity uid of a body or loose module>
nc-cyberware-service-admin-empty = No physical cyberware found.
nc-cyberware-service-admin-humanity = H { $current } | B { $burden } | S { $stress } | crisis stage index { $stage } | adaptation packages { $adaptations } | recent stimuli { $impulses }
nc-cyberware-service-admin-item = { $entity } | ID { $serial } | service { $revision } | { $firmware } | I { $integrity } | D { $defect } | heat { $heat } | omitted history { $omitted }
nc-cyberware-service-admin-entry = revision { $revision } | { $operation } | owner { $owner } | ResolutionId { $resolution }
nc-cyberware-service-operation-0 = installed
nc-cyberware-service-operation-1 = removed
nc-cyberware-service-operation-2 = configuration changed
nc-cyberware-service-operation-3 = repaired
nc-cyberware-service-operation-4 = bench tested
nc-cyberware-service-operation-5 = firmware written
ent-NCCyberwareStandardFirmwareCard = cyberware standard firmware card
    .desc = Reusable physical media for restoring standard control firmware on a cyberware service bench.
ent-NCCyberwareServiceLockCard = cyberware service lock card
    .desc = Reusable physical media that inhibits active cyberware abilities. Passive life support remains available.
