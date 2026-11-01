# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

nc-cyberware-profile-nctoolhand = integral tool mount
ent-NCToolHandImplant = integral tool mount
    .desc = Deploys an integral screwdriver in the corresponding free hand. Requires a working grip, neural control and body power.
ent-NCToolHandAction = Toggle integral tool mount
    .desc = Deploys an integral screwdriver in the corresponding free hand. Requires a working grip, neural control and body power.
nc-cyberware-profile-ncbiomonitor = biomonitor
ent-NCBiomonitorImplant = biomonitor
    .desc = Reads your own oxygenation, perfusion and pain telemetry. It does not reveal a complete diagnosis.
ent-NCBiomonitorAction = Toggle biomonitor
    .desc = Reads your own oxygenation, perfusion and pain telemetry. It does not reveal a complete diagnosis.
nc-cyberware-profile-ncmantisblade = Mantis blade mount
ent-NCMantisBladeImplant = Mantis blade mount
    .desc = Deploys a powered blade in the corresponding free hand. Uses ordinary melee attacks and occupies that hand until retracted.
ent-NCMantisBladeAction = Toggle Mantis blade mount
    .desc = Deploys a powered blade in the corresponding free hand. Uses ordinary melee attacks and occupies that hand until retracted.
nc-cyberware-profile-ncsandevistan = NC Sandevistan controller
ent-NCSandevistanImplant = NC Sandevistan controller
    .desc = A Night City adaptation: six seconds of motor overdrive, up to 25% movement assistance and 20% shorter permitted manual tool operations. Shared speed limits and a 30-second recovery apply.
ent-NCSandevistanAction = Toggle NC Sandevistan controller
    .desc = A Night City adaptation: six seconds of motor overdrive, up to 25% movement assistance and 20% shorter permitted manual tool operations. Shared speed limits and a 30-second recovery apply.
nc-cyberware-profile-nckerenzikov = NC Kerenzikov controller
ent-NCKerenzikovImplant = NC Kerenzikov controller
    .desc = A Night City adaptation that reduces the existing moving called-shot penalty by 25%. It consumes power continuously and shares a motor channel with Sandevistan.
ent-NCKerenzikovAction = Toggle NC Kerenzikov controller
    .desc = A Night City adaptation that reduces the existing moving called-shot penalty by 25%. It consumes power continuously and shares a motor channel with Sandevistan.
nc-cyberware-ability-unavailable = The hardware, neural control or body is unavailable.
nc-cyberware-slot-motor-burst = Motor burst controller bay
nc-cyberware-slot-motor-stabilizer = Motor stabilization controller bay
nc-cyberware-ability-cooldown = The motor controller is still recovering.
nc-cyberware-ability-motor-busy = Switch off the other motor mode first.
nc-cyberware-ability-hand-busy = The corresponding working hand must be free. Retract or put down its held item first.
nc-cyberware-ability-power = The body bus cannot supply this active mode.
nc-cyberware-ability-burst-visible = { CAPITALIZE(THE($user)) } engages a brief, visibly accelerated motor burst.
nc-cyberware-ability-blade-visible = { CAPITALIZE(THE($user)) } unfolds an integral arm blade.
nc-cyberware-ability-active-visible = { CAPITALIZE(THE($user)) } engages an internal cyberware mechanism.
nc-cyberware-biomonitor-reading = Biomonitor: oxygenation { $oxygen }%, perfusion { $perfusion }%, pain { $pain }. { $condition }
nc-cyberware-biomonitor-alarm = Abnormal support readings: seek medical care.
nc-cyberware-biomonitor-stable = Support readings are within the monitoring range.
ent-NCIntegralScrewdriver = integral screwdriver
    .desc = A screwdriver attached to an implanted tool mount.
ent-NCIntegralMantisBlade = integral Mantis blade
    .desc = A visible arm-mounted blade controlled through its physical implant.
