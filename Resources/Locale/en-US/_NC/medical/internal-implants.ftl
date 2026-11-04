# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

nc-medical-quality-improvised = Improvised
nc-medical-quality-standard = Standard
nc-medical-quality-improved = Improved
nc-medical-quality-premium = Premium
nc-medical-slot-controller = Medical controller
nc-medical-slot-sensor = Sensor package
nc-medical-slot-pump = Metering pump
nc-medical-firmware-blank = Blank board
nc-medical-firmware-basic = Basic self-test firmware
nc-medical-firmware-biomonitor = Biomonitor firmware
nc-medical-firmware-injector = Dosing firmware
nc-medical-controller-ready = Controller: compatible firmware installed.
nc-medical-controller-unready = Controller: missing or incompatible firmware; operation blocked.
nc-medical-controller-firmware = Firmware: {$firmware}.
nc-medical-part-quality = Quality: {$quality}.
nc-medical-profile-injector = Implanted autoinjector
nc-medical-bay-biomonitorthorax = Biomonitor bay
nc-medical-bay-medicalabdomen = Abdominal medical implant
nc-medical-stage-fixmedical = Secure medical implant
nc-medical-stage-connectmedical = Connect sensors or delivery line
nc-medical-stage-checkmedical = Check medical connections
nc-medical-implant-injected = Autoinjector delivered {$dose} units.
nc-medical-implant-reading = Biomonitor: oxygen {$oxygen}%, perfusion {$perfusion}%, pain {$pain}, blood {$blood} ml.
nc-medical-implant-read = Biomonitor: read indicators
nc-medical-implant-dose = Autoinjector: inject one dose
nc-medical-implant-blocked = Injection blocked: check power, firmware, cartridge, dose budget and interval.
nc-medical-implant-load = Autoinjector: load held cartridge
nc-medical-implant-unload = Autoinjector: drain into held empty cartridge
nc-medical-implant-auto = Autoinjector: toggle automatic mode
nc-medical-implant-dose-setting = Autoinjector: next dose (1–5 units)
nc-medical-implant-interval-setting = Autoinjector: next interval (30–300 seconds)
nc-medical-implant-limit-setting = Autoinjector: next cartridge budget (5–30 units)
nc-medical-implant-trigger-setting = Autoinjector: next trigger
nc-medical-implant-threshold-setting = Autoinjector: next threshold (10–90)
nc-medical-implant-settings = Autoinjector: show settings
nc-medical-implant-on = enabled
nc-medical-implant-off = disabled
nc-medical-implant-settings-text = Actual dose {$dose} (requested {$requested}); interval {$interval}s; delivered {$delivered}/{$limit}; remaining {$remaining}; cooldown {$cooldown}s; automatic {$automatic}; {$trigger} {$threshold}.
nc-medical-scanner-injector = Cartridge {$remaining}; dose {$dose}; interval {$interval}s; delivered {$delivered}/{$limit}; cooldown {$cooldown}s; automatic {$automatic}.
nc-medical-trigger-0 = oxygen below
nc-medical-trigger-1 = perfusion below
nc-medical-trigger-2 = pain above

ent-NCBiomonitorImplantEmpty = biomonitor frame
    .desc = A medical implant for surgical installation.
ent-NCAutoInjectorImplant = implanted autoinjector
    .desc = A medical implant for surgical installation.
ent-NCAutoInjectorImplantEmpty = autoinjector frame
    .desc = A medical implant for surgical installation.
ent-NCMedicalInjectorCartridge = medical implant cartridge
    .desc = A refillable container for 30 units of reagents.
ent-NCMedicalControllerImprovised = blank medical controller
    .desc = A programmable control board for a medical implant.
    .suffix = Improvised
ent-NCMedicalControllerStandard = blank medical controller
    .desc = A programmable control board for a medical implant.
    .suffix = Standard
ent-NCMedicalControllerImproved = blank medical controller
    .desc = A programmable control board for a medical implant.
    .suffix = Improved
ent-NCMedicalControllerPremium = blank medical controller
    .desc = A programmable control board for a medical implant.
    .suffix = Premium
ent-NCMedicalSensorImprovised = medical sensor package
    .desc = Replacement sensors for a biomonitor.
    .suffix = Improvised
ent-NCMedicalSensorStandard = medical sensor package
    .desc = Replacement sensors for a biomonitor.
    .suffix = Standard
ent-NCMedicalSensorImproved = medical sensor package
    .desc = Replacement sensors for a biomonitor.
    .suffix = Improved
ent-NCMedicalSensorPremium = medical sensor package
    .desc = Replacement sensors for a biomonitor.
    .suffix = Premium
ent-NCMedicalPumpImprovised = medical metering pump
    .desc = A replacement metering pump for an autoinjector.
    .suffix = Improvised
ent-NCMedicalPumpStandard = medical metering pump
    .desc = A replacement metering pump for an autoinjector.
    .suffix = Standard
ent-NCMedicalPumpImproved = medical metering pump
    .desc = A replacement metering pump for an autoinjector.
    .suffix = Improved
ent-NCMedicalPumpPremium = medical metering pump
    .desc = A replacement metering pump for an autoinjector.
    .suffix = Premium
ent-NCMedicalControllerBasic = medical controller with basic firmware
    .desc = A programmable control board for a medical implant.
ent-NCMedicalControllerBiomonitor = biomonitor controller
    .desc = A programmable control board for a medical implant.
ent-NCMedicalControllerInjector = autoinjector controller
    .desc = A programmable control board for a medical implant.

guide-entry-nc-medical-implants = Medical implants: controllers and dosing
nc-guide-medical-implants-1 = [head=1]Biomonitor and autoinjector[/head]
    The biomonitor reports oxygenation, perfusion, pain and blood volume. Measurements are rounded according to sensor and controller quality. It warns of low oxygenation or perfusion. The injector transfers real reagents from its cartridge into the bloodstream; it does not create medicine.
nc-guide-medical-implants-2 = [head=2]Manufacture and programming[/head]
    Print empty frames and 30-unit cartridges on the medical cyberware lathe. Print controllers and sensors on the electronics lathe, and pumps on the mechanical lathe. Four quality grades are available; improved and premium components require precious metals. A weaker controller limits the sensor or pump. Condition and permanent wear remain separate from quality.
    The electronics lathe supplies blank controllers and controllers with basic self-test firmware. Basic firmware blocks medical operation. Use a META computer and claim the detached board as a MedicalProgrammer device. Bind the medical-board link in Resources/Meta/Medical/Programmer.meta. flash(0) writes the basic firmware, flash(1) the biomonitor firmware and flash(2) the injector firmware. Installed boards cannot be rewritten; remove them on the service bench first.
    Mount the empty frame on the cyberware bench. Fit the programmed controller and matching sensors or pump. Flash the implant with the standard service firmware card, run the powered assembly test and prepare it with sterile wrap. Replacing a part requires another test and preparation.
nc-guide-medical-implants-3 = [head=2]Installation[/head]
    Stabilize and anesthetize the patient. Prepare sterile access. The biomonitor uses the chest bay at depth 0.28; the injector uses the abdominal bay at depth 0.4. Retract the incision, mount the prepared implant with the service probe, then secure it, connect the sensors or delivery line and check the connections. Close the incision and inspect bleeding. Both require a working implant power bus; neither requires a neural link.
nc-guide-medical-implants-4 = [head=2]Operation[/head]
    Use your own character’s alternative verbs to read the biomonitor and configure the injector. Load reagents into a medical cartridge with ordinary chemistry equipment. Hold it and select load; the implanted chamber must be empty. To remove its contents, hold an empty cartridge and select drain. A new load starts a new cartridge budget but never clears the cooldown.
    Configure dose, interval, cartridge budget, trigger and threshold. Settings changes disable automatic mode. Enable it explicitly after checking the cartridge. Automatic injection requires fresh readings from your working biomonitor. Manual and automatic injections share the same budget and cooldown. Loss of power or incompatible firmware blocks delivery. Inspect current settings through the same verbs or the body scanner.
