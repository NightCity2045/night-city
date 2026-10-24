// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Security.Cryptography;
using System.Text;
using Content.Meta.Bytecode;
using Content.Meta.Verification;

namespace Content.Meta.Language;

public sealed record MetaApiField(string Name, string Type);
public sealed record MetaApiType(string Name, MetaTypeKind Kind, MetaApiField[] Fields, string[] Variants)
{
    public string ContractHash => MetaApiCatalog.HashText(Name + "|" + Kind + "|" + string.Join(";", Fields.Select(f => f.Name + ":" + f.Type)) + "|" + string.Join(";", Variants));
}
public sealed record MetaApiMember(string Owner, string Name, string[] Parameters, string ReturnType,
    string Effect, string Scope, string Delivery = "Ordered", int Capacity = 16)
{
    public string QualifiedName => Owner + "." + Name;
    public string ContractHash => MetaApiCatalog.HashText(QualifiedName + "|" + string.Join(",", Parameters) + "|" + ReturnType + "|" + Effect + "|" + Scope + "|" + Delivery + "|" + Capacity);
}
public sealed record MetaIntrinsicSignature(string Module, string Name, ushort Id, string[] Parameters, string ReturnType, MetaEffectFlags Effects);
[Flags]
public enum MetaEffectFlags { None = 0, MaySuspend = 1, CallsHost = 2, ReadClock = 4, UsesMutableGlobal = 8, Allocates = 16, Log = 32, ReadLocal = 64 }

/// <summary>Server-owned whitelist. Source files and worker replies cannot add host functions to it.</summary>
public sealed partial class MetaApiCatalog
{
    public IReadOnlyList<MetaApiType> Types { get; }
    public IReadOnlyList<MetaApiMember> Members { get; }
    public IReadOnlyList<MetaIntrinsicSignature> Intrinsics { get; }
    public IReadOnlyList<string> Modules { get; }
    public string Hash { get; }
    public static MetaApiCatalog Default { get; } = CreateDefault();

    public MetaApiCatalog(IEnumerable<MetaApiType> types, IEnumerable<MetaApiMember> members, IEnumerable<MetaIntrinsicSignature> intrinsics)
    {
        Types = types.OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
        Members = members.OrderBy(m => m.QualifiedName, StringComparer.Ordinal).ToArray();
        Intrinsics = intrinsics.OrderBy(i => i.Id).ToArray();
        Modules = Types.Select(t => t.Name[..t.Name.LastIndexOf('.')]).Concat(Intrinsics.Select(i => i.Module)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Hash = HashText("meta-api-1.0\n" + string.Join('\n', Types.Select(t => t.ContractHash)) + "\n" +
            string.Join('\n', Members.Select(m => m.ContractHash)) + "\n" + string.Join('\n', Intrinsics.Select(i => i.Id + ":" + i.Module + "." + i.Name + ":" + string.Join(',', i.Parameters) + ":" + i.ReturnType + ":" + i.Effects)));
    }

    public string ModuleHash(string module) => HashText(module + "\n" + string.Join('\n', Types.Where(t => t.Name.StartsWith(module + ".", StringComparison.Ordinal)).Select(t => t.ContractHash)) + "\n" +
        string.Join('\n', Members.Where(m => m.Owner.StartsWith(module + ".", StringComparison.Ordinal)).Select(m => m.ContractHash)));

    public MetaVerificationCatalog CreateVerificationCatalog(MetaModule module)
    {
        if (module.Manifest.ApiCatalogHash != Hash)
        {
            if (!ReferenceEquals(this, Default)) throw new MetaFormatException("API catalog hash mismatch.");
            return ResolveTrustedCatalog(module.Manifest.ApiCatalogHash).CreateVerificationCatalog(module);
        }
        var moduleTypes = module.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        int Resolve(string name)
        {
            if (!moduleTypes.TryGetValue(name, out var type)) throw new MetaFormatException("Catalog type missing from module.");
            return type.Id;
        }
        return new MetaVerificationCatalog
        {
            Hash = Hash,
            NativeTypes = Types.Select(t => new MetaTypeDef
            {
                Id = Resolve(t.Name), Kind = t.Kind, Name = t.Name, ContractHash = t.ContractHash,
                Fields = t.Fields.Select(f => new MetaFieldDef(f.Name, Resolve(f.Type), true)).ToArray(),
                Variants = t.Variants.Select((v, i) => new MetaVariantDef(v, i, null)).ToArray()
            }).ToArray(),
            Imports = module.Imports.Select(import =>
            {
                if (import.Kind == "BuiltinModule")
                {
                    if (!Modules.Contains(import.Name, StringComparer.Ordinal)) throw new MetaFormatException("Unknown builtin module.");
                    return new MetaImportDef { Kind = "BuiltinModule", Name = import.Name, ContractHash = ModuleHash(import.Name) };
                }
                if (import.Kind == "Library") return import;
                var member = Members.SingleOrDefault(m => m.QualifiedName == import.Name) ?? throw new MetaFormatException("Unknown host member.");
                return new MetaImportDef
                {
                    Kind = member.Effect == "Event" ? "HostEvent" : "HostMethod", Name = member.QualifiedName, ContractHash = member.ContractHash,
                    CapabilityTypeId = Resolve(member.Owner), ParameterTypes = member.Parameters.Select(Resolve).ToArray(), ReturnType = Resolve(member.ReturnType), Effect = member.Effect
                };
            }).ToArray(),
            ConstructibleEnums = Types.Where(t => t.Kind == MetaTypeKind.Enum).Select(t => t.Name).ToArray(),
            Events = Members.Where(m => m.Effect == "Event").Select(m => new MetaEventContract(m.QualifiedName, m.Delivery, m.Capacity)).ToArray()
        };
    }

    internal static string HashText(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static MetaApiCatalog CreateDefault()
    {
        const string S = "meta.system.";
        var types = new List<MetaApiType>();
        var members = new List<MetaApiMember>();
        var intrinsics = new List<MetaIntrinsicSignature>();
        void Dto(string module, string name, params string[] fields) => types.Add(new(module + "." + name, MetaTypeKind.NativeDto,
            fields.Select(f => { var p = f.IndexOf(':'); return new MetaApiField(f[..p], f[(p + 1)..]); }).ToArray(), []));
        void Enum(string module, string name, params string[] variants) => types.Add(new(module + "." + name, MetaTypeKind.Enum, [], variants));
        void Capability(string module, string name) => types.Add(new(module + "." + name, MetaTypeKind.Capability, [], []));
        void Opaque(string module, string name) => types.Add(new(module + "." + name, MetaTypeKind.Opaque, [], []));
        void Method(string owner, string name, string result, string scope, bool async, params string[] args) => members.Add(new(owner, name, args, result, async ? "Await" : "Read", scope));
        void Event(string owner, string name, string argument, string scope, string delivery = "Latest", int capacity = 1) => members.Add(new(owner, name, [argument], "void", "Event", scope, delivery, capacity));
        void Intrinsic(string module, string name, ushort id, string result, MetaEffectFlags effect, params string[] args) => intrinsics.Add(new("meta." + module, name, id, args, result, effect));
        Dto("meta.system", "SampleStamp", "epoch:int", "sequence:int", "tick:int");
        Enum("meta.system", "ErrorCode", "InvalidArgument", "OutOfRange", "NotFound", "Conflict", "AccessDenied", "PermissionRevoked", "Disconnected", "PowerOff", "DeviceDestroyed", "Incompatible", "Busy", "RateLimited", "QuotaExceeded", "Timeout", "Cancelled", "Unsupported", "IoFailure", "StaleState", "Interlock", "PathUnavailable", "ActionRejected", "OutcomeUnknown", "ControlLost");
        Dto("meta.system", "Error", "code:" + S + "ErrorCode", "message:string", "retryAfterMs:int");
        Enum("meta.system", "OperationStatus", "Prepared", "Committing", "Running", "Applied", "Rejected", "OutcomeUnknown");
        Dto("meta.system", "JobReceipt", "operationId:string", "status:" + S + "OperationStatus", "requested:int", "completed:int",
            "errorCode:option<" + S + "ErrorCode>", "stamp:" + S + "SampleStamp");
        Intrinsic("system", "log", 1, "void", MetaEffectFlags.Log, "string");
        Intrinsic("system", "isFresh", 2, "bool", MetaEffectFlags.ReadClock, S + "SampleStamp", "int");
        Intrinsic("system", "nowMs", 3, "int", MetaEffectFlags.ReadClock);
        Intrinsic("system", "error", 4, S + "Error", MetaEffectFlags.None, S + "ErrorCode", "string");
        Intrinsic("text", "fromInt", 0x10, "string", MetaEffectFlags.None, "int");
        Intrinsic("text", "fromFloat", 0x11, "string", MetaEffectFlags.None, "float");
        Intrinsic("text", "parseInt", 0x12, "result<int>", MetaEffectFlags.None, "string");
        Intrinsic("text", "parseFloat", 0x13, "result<float>", MetaEffectFlags.None, "string");
        Intrinsic("text", "slice", 0x14, "result<string>", MetaEffectFlags.Allocates, "string", "int", "int");
        Intrinsic("text", "byteLength", 0x15, "int", MetaEffectFlags.None, "string");
        Intrinsic("numeric", "toFloat", 0x20, "float", MetaEffectFlags.None, "int");
        Intrinsic("numeric", "toInt", 0x21, "result<int>", MetaEffectFlags.None, "float");
        Intrinsic("numeric", "toByte", 0x22, "result<byte>", MetaEffectFlags.None, "int");
        Intrinsic("numeric", "absInt", 0x23, "int", MetaEffectFlags.None, "int");
        Intrinsic("numeric", "absFloat", 0x24, "float", MetaEffectFlags.None, "float");
        Intrinsic("numeric", "byteToInt", 0x25, "int", MetaEffectFlags.None, "byte");
        Capability("meta.files", "FileStore");
        Opaque("meta.app", "UserRef"); Opaque("meta.app", "SessionRef");
        Dto("meta.app", "AppContext", "user:meta.app.UserRef", "session:meta.app.SessionRef", "view:string", "presentation:string", "locale:string");
        Intrinsic("app", "sameSession", 0x0030, "bool", MetaEffectFlags.ReadLocal, "meta.app.SessionRef", "meta.app.SessionRef");
        Method("meta.files.FileStore", "readText", "result<string>", "files.read", true, "string");
        Method("meta.files.FileStore", "writeTextAtomic", "result<void>", "files.write", true, "string", "string");
        Capability("meta.net", "MessagePort");
        Dto("meta.net", "Message", "replyTo:string", "topic:string", "payload:string", "stamp:" + S + "SampleStamp");
        Dto("meta.net", "DeliveryReceipt", "messageId:string", "accepted:bool");
        Method("meta.net.MessagePort", "send", "result<meta.net.DeliveryReceipt>", "net.send", true, "string", "string", "string");
        Event("meta.net.MessagePort", "messageReceived", "meta.net.Message", "net.receive", "Ordered", 16);
        Capability("meta.doors", "Door"); Capability("meta.doors", "DoorPanel");
        Enum("meta.doors", "DoorPhase", "Closed", "Opening", "Open", "Closing", "Blocked");
        Dto("meta.doors", "DoorState", "desiredOpen:bool", "phase:meta.doors.DoorPhase", "stamp:" + S + "SampleStamp");
        Dto("meta.doors", "DoorRequest", "open:bool", "requestId:string", "stamp:" + S + "SampleStamp");
        Method("meta.doors.Door", "snapshot", "result<meta.doors.DoorState>", "door.read", false);
        Method("meta.doors.Door", "setOpen", "result<meta.doors.DoorState>", "door.control", true, "bool");
        Event("meta.doors.Door", "stateChanged", "meta.doors.DoorState", "door.observe");
        Event("meta.doors.DoorPanel", "requested", "meta.doors.DoorRequest", "doorpanel.observe", "Ordered", 16);
        Capability("meta.security", "Camera"); Capability("meta.security", "Alarm");
        Dto("meta.security", "Detection", "confidence:float", "stamp:" + S + "SampleStamp");
        Dto("meta.security", "AlarmState", "requestedActive:bool", "stamp:" + S + "SampleStamp");
        Event("meta.security.Camera", "motionDetected", "meta.security.Detection", "camera.motion.observe", "Ordered", 16);
        Method("meta.security.Alarm", "setActive", "result<meta.security.AlarmState>", "alarm.control", true, "bool");
        Capability("meta.sensors", "TemperatureSensor");
        Dto("meta.sensors", "TemperatureSample", "celsius:float", "stamp:" + S + "SampleStamp");
        Event("meta.sensors.TemperatureSensor", "temperatureChanged", "meta.sensors.TemperatureSample", "sensor.temperature.observe");
        Capability("meta.vehicle", "EngineController");
        Enum("meta.vehicle", "EngineMode", "Normal", "Economy", "Safe");
        Dto("meta.vehicle", "EngineState", "temperatureC:float", "rpm:int", "oilPressureKPa:float", "coolantFraction:float", "faults:array<string>", "mode:meta.vehicle.EngineMode", "stamp:" + S + "SampleStamp");
        Method("meta.vehicle.EngineController", "snapshot", "result<meta.vehicle.EngineState>", "vehicle.engine.read", false);
        Method("meta.vehicle.EngineController", "setMode", "result<meta.vehicle.EngineState>", "vehicle.engine.mode", true, "meta.vehicle.EngineMode");
        Event("meta.vehicle.EngineController", "telemetryChanged", "meta.vehicle.EngineState", "vehicle.engine.observe");
        Capability("meta.vehicle", "TransmissionController");
        Enum("meta.vehicle", "TransmissionStrategy", "Normal", "Economy", "Low");
        Dto("meta.vehicle", "TransmissionState", "gear:int", "automatic:bool", "strategy:meta.vehicle.TransmissionStrategy", "shiftPending:bool", "stamp:" + S + "SampleStamp");
        Method("meta.vehicle.TransmissionController", "snapshot", "result<meta.vehicle.TransmissionState>", "vehicle.transmission.read", false);
        Method("meta.vehicle.TransmissionController", "setStrategy", "result<meta.vehicle.TransmissionState>", "vehicle.transmission.strategy", true, "meta.vehicle.TransmissionStrategy");
        Method("meta.vehicle.TransmissionController", "requestGear", "result<meta.vehicle.TransmissionState>", "vehicle.transmission.gear", true, "int");
        Event("meta.vehicle.TransmissionController", "stateChanged", "meta.vehicle.TransmissionState", "vehicle.transmission.observe");
        Capability("meta.vehicle", "VehicleDashboard");
        Dto("meta.vehicle", "DashboardSample", "speedMps:float", "fuelLitres:float", "batteryFraction:float", "gear:int", "automatic:bool", "engineRunning:bool", "stamp:" + S + "SampleStamp");
        Method("meta.vehicle.VehicleDashboard", "snapshot", "result<meta.vehicle.DashboardSample>", "vehicle.telemetry.read", false);
        Event("meta.vehicle.VehicleDashboard", "telemetryChanged", "meta.vehicle.DashboardSample", "vehicle.telemetry.observe");
        Capability("meta.vehicle", "DriverAssistance");
        Enum("meta.vehicle", "AssistanceSystem", "Abs", "Stability", "Traction");
        Dto("meta.vehicle", "AssistanceState", "absSupported:bool", "absEnabled:bool", "stabilitySupported:bool", "stabilityEnabled:bool",
            "tractionSupported:bool", "tractionEnabled:bool", "longitudinalSlip:float", "lateralSlip:float", "wheelAngularSpeedsRadS:array<float>", "stamp:" + S + "SampleStamp");
        Method("meta.vehicle.DriverAssistance", "snapshot", "result<meta.vehicle.AssistanceState>", "vehicle.assistance.read", false);
        Method("meta.vehicle.DriverAssistance", "setEnabled", "result<meta.vehicle.AssistanceState>", "vehicle.assistance.control", true, "meta.vehicle.AssistanceSystem", "bool");
        Event("meta.vehicle.DriverAssistance", "stateChanged", "meta.vehicle.AssistanceState", "vehicle.assistance.observe");
        Capability("meta.vehicle", "VehicleSecurity");
        Dto("meta.vehicle", "SecurityState", "armed:bool", "locked:bool", "alarmActive:bool", "pending:bool", "stamp:" + S + "SampleStamp");
        Method("meta.vehicle.VehicleSecurity", "snapshot", "result<meta.vehicle.SecurityState>", "vehicle.security.read", false);
        Method("meta.vehicle.VehicleSecurity", "setArmed", "result<meta.vehicle.SecurityState>", "vehicle.security.control", true, "bool");
        Event("meta.vehicle.VehicleSecurity", "stateChanged", "meta.vehicle.SecurityState", "vehicle.security.observe");
        Capability("meta.drone", "DroneController"); Opaque("meta.drone", "WaypointRef");
        Enum("meta.drone", "NavigationStatus", "Arrived", "Blocked", "Cancelled");
        Dto("meta.drone", "NavigationReceipt", "requestId:int", "stamp:" + S + "SampleStamp");
        Dto("meta.drone", "NavigationEvent", "requestId:int", "status:meta.drone.NavigationStatus", "stamp:" + S + "SampleStamp");
        Method("meta.drone.DroneController", "resolveWaypoint", "result<meta.drone.WaypointRef>", "drone.route.read", false, "string");
        Method("meta.drone.DroneController", "setDestination", "result<meta.drone.NavigationReceipt>", "drone.navigate", true, "meta.drone.WaypointRef");
        Event("meta.drone.DroneController", "navigationFinished", "meta.drone.NavigationEvent", "drone.navigation.observe", "Ordered", 8);
        Capability("meta.drone", "NavigationSupervisor");
        Enum("meta.drone", "DroneMode", "Manual", "Assistant", "Autonomous", "Maintenance", "Emergency");
        Enum("meta.drone", "NavigationOutcome", "None", "Moving", "Arrived", "Blocked", "Cancelled", "NoPath");
        Dto("meta.drone", "DroneState", "mode:meta.drone.DroneMode", "requestId:int", "outcome:meta.drone.NavigationOutcome", "stamp:" + S + "SampleStamp");
        Dto("meta.drone", "NavigationOutcomeEvent", "requestId:int", "outcome:meta.drone.NavigationOutcome", "stamp:" + S + "SampleStamp");
        Method("meta.drone.NavigationSupervisor", "snapshot", "result<meta.drone.DroneState>", "drone.state.read", false);
        Method("meta.drone.NavigationSupervisor", "requestMode", "result<meta.drone.DroneState>", "drone.mode.control", true, "meta.drone.DroneMode");
        Method("meta.drone.NavigationSupervisor", "cancel", "result<meta.drone.DroneState>", "drone.navigation.cancel", true, "int");
        Event("meta.drone.NavigationSupervisor", "stateChanged", "meta.drone.DroneState", "drone.state.observe");
        Opaque("meta.drone", "ContactRef");
        Enum("meta.drone", "ContactKind", "Item", "Storage", "Mobile");
        Dto("meta.drone", "Contact", "reference:meta.drone.ContactRef", "kind:meta.drone.ContactKind", "bearingDegrees:float", "rangeMeters:float", "confidence:float", "stamp:" + S + "SampleStamp");
        Dto("meta.drone", "WeaponState", "equipped:bool", "ready:bool", "loaded:int", "stamp:" + S + "SampleStamp");
        Capability("meta.drone", "Perception"); Capability("meta.drone", "Manipulator"); Capability("meta.drone", "WeaponController");
        Method("meta.drone.Perception", "observe", "result<array<meta.drone.Contact>>", "drone.observe", false);
        Method("meta.drone.Manipulator", "holding", "result<bool>", "drone.cargo.read", false);
        Method("meta.drone.Manipulator", "pickUp", "result<meta.system.JobReceipt>", "drone.cargo.pickup", true, "meta.drone.ContactRef", "string");
        Method("meta.drone.Manipulator", "dropAt", "result<meta.system.JobReceipt>", "drone.cargo.drop", true, "meta.drone.WaypointRef", "string");
        Method("meta.drone.Manipulator", "storeIn", "result<meta.system.JobReceipt>", "drone.cargo.store", true, "meta.drone.ContactRef", "string");
        Method("meta.drone.WeaponController", "snapshot", "result<meta.drone.WeaponState>", "drone.weapon.read", false);
        Method("meta.drone.WeaponController", "fire", "result<meta.system.JobReceipt>", "drone.weapon.fire", true, "meta.drone.ContactRef", "string");
        foreach (var owner in new[] { "meta.drone.Manipulator", "meta.drone.WeaponController" })
        {
            Method(owner, "operationStatus", "result<meta.system.JobReceipt>", "drone.operation.read", true, "string");
            Method(owner, "cancel", "result<meta.system.JobReceipt>", "drone.operation.cancel", true, "string");
            Event(owner, "operationChanged", "meta.system.JobReceipt", "drone.operation.observe", "Ordered", 16);
        }
        Event("meta.drone.NavigationSupervisor", "navigationOutcome", "meta.drone.NavigationOutcomeEvent", "drone.navigation.detail", "Ordered", 8);
        Capability("meta.industrial", "LoadSensor"); Capability("meta.industrial", "Conveyor");
        Dto("meta.industrial", "LoadSample", "kilograms:float", "stamp:" + S + "SampleStamp");
        Dto("meta.industrial", "ConveyorState", "requestedEnabled:bool", "stamp:" + S + "SampleStamp");
        Event("meta.industrial.LoadSensor", "loadSampled", "meta.industrial.LoadSample", "sensor.load.observe");
        Method("meta.industrial.Conveyor", "setEnabled", "result<meta.industrial.ConveyorState>", "conveyor.control", true, "bool");
        Capability("meta.industrial", "Fabricator");
        Method("meta.industrial.Fabricator", "recipes", "result<array<string>>", "machine.recipes.read", false);
        Method("meta.industrial.Fabricator", "start", "result<meta.system.JobReceipt>", "machine.produce", true, "string", "int", "string");
        Method("meta.industrial.Fabricator", "operationStatus", "result<meta.system.JobReceipt>", "machine.operation.read", true, "string");
        Method("meta.industrial.Fabricator", "cancel", "result<meta.system.JobReceipt>", "machine.cancel", true, "string");
        Event("meta.industrial.Fabricator", "operationChanged", "meta.system.JobReceipt", "machine.operation.observe", "Ordered", 16);
        Capability("meta.lab", "Grinder"); Capability("meta.lab", "ChemMaster");
        Enum("meta.lab", "GrinderProgram", "Grind", "Juice");
        Method("meta.lab.Grinder", "start", "result<meta.system.JobReceipt>", "lab.process", true, "meta.lab.GrinderProgram", "string");
        Method("meta.lab.Grinder", "operationStatus", "result<meta.system.JobReceipt>", "lab.operation.read", true, "string");
        Method("meta.lab.Grinder", "cancel", "result<meta.system.JobReceipt>", "lab.cancel", true, "string");
        Event("meta.lab.Grinder", "operationChanged", "meta.system.JobReceipt", "lab.operation.observe", "Ordered", 16);
        Method("meta.lab.ChemMaster", "bottle", "result<meta.system.JobReceipt>", "lab.package", true, "int", "string", "string");
        Method("meta.lab.ChemMaster", "pills", "result<meta.system.JobReceipt>", "lab.package", true, "int", "int", "string", "string");
        Method("meta.lab.ChemMaster", "operationStatus", "result<meta.system.JobReceipt>", "lab.operation.read", true, "string");
        Event("meta.lab.ChemMaster", "operationChanged", "meta.system.JobReceipt", "lab.operation.observe", "Ordered", 16);
        Capability("meta.implants", "Optics");
        Capability("meta.implants", "FunctionalController");
        Enum("meta.implants", "FunctionKind", "Tool", "Blade", "Biomonitor", "MotorBurst", "Stabilization");
        Dto("meta.implants", "FunctionState", "kind:meta.implants.FunctionKind", "active:bool", "operational:bool",
            "cooldownMs:int", "activeRemainingMs:int", "stamp:" + S + "SampleStamp");
        Method("meta.implants.FunctionalController", "snapshot", "result<meta.implants.FunctionState>", "implant.function.read", false);
        Method("meta.implants.FunctionalController", "setEnabled", "result<meta.implants.FunctionState>", "implant.function.control", true, "bool");
        Method("meta.implants.FunctionalController", "pulse", "result<meta.implants.FunctionState>", "implant.function.pulse", true);
        Event("meta.implants.FunctionalController", "stateChanged", "meta.implants.FunctionState", "implant.function.observe");
        Enum("meta.implants", "OpticFilter", "Clear", "LowLight");
        Dto("meta.implants", "LightSample", "normalizedLight:float", "stamp:" + S + "SampleStamp");
        Dto("meta.implants", "OpticState", "requestedFilter:meta.implants.OpticFilter", "stamp:" + S + "SampleStamp");
        Event("meta.implants.Optics", "lightSampled", "meta.implants.LightSample", "implant.optics.observe");
        Method("meta.implants.Optics", "setFilter", "result<meta.implants.OpticState>", "implant.optics.filter", true, "meta.implants.OpticFilter");
        Capability("meta.netrun", "NetSession"); Opaque("meta.netrun", "NetNodeRef");
        Enum("meta.netrun", "NetNodeKind", "Data", "Control", "Defense");
        Dto("meta.netrun", "NetNode", "reference:meta.netrun.NetNodeRef", "label:string", "kind:meta.netrun.NetNodeKind");
        Dto("meta.netrun", "ProbeOutcome", "success:bool", "traceLevel:int", "detail:string");
        Method("meta.netrun.NetSession", "listVisibleNodes", "result<array<meta.netrun.NetNode>>", "netrun.observe", true);
        Method("meta.netrun.NetSession", "probe", "result<meta.netrun.ProbeOutcome>", "netrun.probe", true, "meta.netrun.NetNodeRef");
        Opaque("meta.medical", "PatientRef");
        Capability("meta.medical", "MedicalMonitor"); Capability("meta.medical", "DosingController"); Capability("meta.medical", "ProcedureController");
        Enum("meta.medical", "DiagnosticBand", "None", "Low", "Moderate", "High", "Critical");
        Enum("meta.medical", "Consciousness", "Alert", "Dazed", "Confused", "Stuporous", "Unconscious", "Comatose", "BrainDead");
        Enum("meta.medical", "Condition", "Stable", "Compensating", "Critical", "ClinicalArrest", "Dead");
        Enum("meta.medical", "OperationStatus", "Prepared", "Committing", "Running", "Applied", "Rejected", "OutcomeUnknown");
        Dto("meta.medical", "NumericRange", "minimum:int", "maximum:int");
        Dto("meta.medical", "MedicalReport", "pain:meta.medical.DiagnosticBand", "shock:meta.medical.DiagnosticBand",
            "consciousness:meta.medical.Consciousness", "condition:meta.medical.Condition", "oxygenationPermille:option<meta.medical.NumericRange>",
            "externalBleeding:meta.medical.DiagnosticBand", "internalBleeding:meta.medical.DiagnosticBand", "stamp:" + S + "SampleStamp");
        Dto("meta.medical", "OperationReceipt", "operationId:string", "status:meta.medical.OperationStatus", "errorCode:option<" + S + "ErrorCode>", "stamp:" + S + "SampleStamp");
        Method("meta.medical.MedicalMonitor", "patient", "result<meta.medical.PatientRef>", "medical.patient.read", false);
        Method("meta.medical.MedicalMonitor", "snapshot", "result<meta.medical.MedicalReport>", "medical.diagnostics.read", false, "meta.medical.PatientRef");
        Method("meta.medical.DosingController", "dose", "result<meta.medical.OperationReceipt>", "medical.dose", true, "meta.medical.PatientRef", "string");
        Method("meta.medical.DosingController", "operationStatus", "result<meta.medical.OperationReceipt>", "medical.operation.read", true, "string");
        Method("meta.medical.ProcedureController", "startSelectedStep", "result<meta.medical.OperationReceipt>", "medical.procedure", true, "meta.medical.PatientRef", "string");
        Method("meta.medical.ProcedureController", "operationStatus", "result<meta.medical.OperationReceipt>", "medical.operation.read", true, "string");
        Event("meta.medical.ProcedureController", "operationChanged", "meta.medical.OperationReceipt", "medical.operation.observe", "Ordered", 8);
        Capability("meta.services", "ServiceOrders");
        Enum("meta.services", "OrderStatus", "Submitted", "Assigned", "InProgress", "AwaitingPayment", "Paid", "Ready", "Completed", "Cancelled");
        Enum("meta.services", "OrderAction", "Begin", "Ready", "Complete", "Cancel");
        Dto("meta.services", "Order", "id:string", "subject:string", "customer:string", "executor:string", "status:meta.services.OrderStatus",
            "revision:int", "invoiceId:string", "invoiceHash:string", "amount:int", "paid:bool");
        Dto("meta.services", "PaymentRequest", "confirmationId:string", "pending:bool");
        Dto("meta.services", "OrderChange", "id:string", "revision:int");
        Method("meta.services.ServiceOrders", "create", "result<meta.services.Order>", "services.create", true, "meta.app.UserRef", "meta.app.SessionRef", "string", "string");
        Method("meta.services.ServiceOrders", "listOrders", "result<array<meta.services.Order>>", "services.read", true, "meta.app.UserRef", "meta.app.SessionRef");
        Method("meta.services.ServiceOrders", "cachedList", "result<array<meta.services.Order>>", "services.read", false, "meta.app.UserRef", "meta.app.SessionRef");
        Method("meta.services.ServiceOrders", "get", "result<meta.services.Order>", "services.read", true, "meta.app.UserRef", "meta.app.SessionRef", "string");
        Method("meta.services.ServiceOrders", "assign", "result<meta.services.Order>", "services.assign", true, "meta.app.UserRef", "meta.app.SessionRef", "string", "string", "int", "string");
        Method("meta.services.ServiceOrders", "transition", "result<meta.services.Order>", "services.control", true, "meta.app.UserRef", "meta.app.SessionRef", "string", "meta.services.OrderAction", "int", "string");
        Method("meta.services.ServiceOrders", "invoice", "result<meta.services.Order>", "services.invoice", true, "meta.app.UserRef", "meta.app.SessionRef", "string", "int", "string", "int", "string");
        Method("meta.services.ServiceOrders", "requestPayment", "result<meta.services.PaymentRequest>", "services.pay", true, "meta.app.UserRef", "meta.app.SessionRef", "string");
        Event("meta.services.ServiceOrders", "orderChanged", "meta.services.OrderChange", "services.observe", "Ordered", 16);
        return new(types, members, intrinsics);
    }
}
