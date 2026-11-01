// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Bytecode;

namespace Content.Meta.Language;

public sealed partial class MetaApiCatalog
{
    // Immutable authored catalog used by the first native release. New native APIs must not edit this snapshot.
    public const string BaselineCatalogHash = "1851be948b8724c359735ced19e9aa74e8a3206bd255118655cdbccf960f765f";
    private static readonly Lazy<MetaApiCatalog> Baseline = new(() =>
    {
        var data = JsonSerializer.Deserialize<CatalogSnapshot>(BaselineCatalogJson, new JsonSerializerOptions { MaxDepth = 16 })
            ?? throw new InvalidOperationException("Missing trusted META catalog snapshot.");
        var catalog = new MetaApiCatalog(data.Types, data.Members, data.Intrinsics);
        if (catalog.Hash != BaselineCatalogHash) throw new InvalidOperationException("Trusted META catalog snapshot hash mismatch.");
        return catalog;
    });
    private sealed record CatalogSnapshot(MetaApiType[] Types, MetaApiMember[] Members, MetaIntrinsicSignature[] Intrinsics);

    public static MetaApiCatalog ResolveTrustedCatalog(string hash)
    {
        if (hash == Default.Hash) return Default;
        if (hash == BaselineCatalogHash) return Baseline.Value;
        if (hash == NativeControlsCatalogHash) return NativeControls.Value;
        if (hash == MachinesCatalogHash) return Machines.Value;
        if (hash == FunctionsCatalogHash) return Functions.Value;
        throw new MetaFormatException("Unknown trusted META API catalog version.");
    }

    private const string BaselineCatalogJson = """
        {
          "Types": [
            {
              "Name": "meta.app.AppContext",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "user",
                  "Type": "meta.app.UserRef"
                },
                {
                  "Name": "session",
                  "Type": "meta.app.SessionRef"
                },
                {
                  "Name": "view",
                  "Type": "string"
                },
                {
                  "Name": "presentation",
                  "Type": "string"
                },
                {
                  "Name": "locale",
                  "Type": "string"
                }
              ],
              "Variants": [],
              "ContractHash": "f3bf05e9dd0893af7de9c30c8e801257aef9139e57031ec1e16a25daf129c85a"
            },
            {
              "Name": "meta.app.SessionRef",
              "Kind": 14,
              "Fields": [],
              "Variants": [],
              "ContractHash": "8f74e5e5de8779ccc5fb8e6da7b76a53e59f63fff93a44ea51211188cedf4105"
            },
            {
              "Name": "meta.app.UserRef",
              "Kind": 14,
              "Fields": [],
              "Variants": [],
              "ContractHash": "cd26b1893012344f8c5d6f44caa9fc462561a4767d91d3c1d1d8f521bc6d02a0"
            },
            {
              "Name": "meta.doors.Door",
              "Kind": 13,
              "Fields": [],
              "Variants": [],
              "ContractHash": "eb9affb74c5a05fcd584eeb2f21d6c941ff51f820a84fecfc1d1f70447c9ab28"
            },
            {
              "Name": "meta.doors.DoorPanel",
              "Kind": 13,
              "Fields": [],
              "Variants": [],
              "ContractHash": "83e9fda13cd33cea2349beafe948e32210bed5e6f1b93aa48c261926c5c969b0"
            },
            {
              "Name": "meta.doors.DoorPhase",
              "Kind": 12,
              "Fields": [],
              "Variants": [
                "Closed",
                "Opening",
                "Open",
                "Closing",
                "Blocked"
              ],
              "ContractHash": "20d4de2a2bff6ab1d87105241702ba25d2238e65c73aeb5526a4b7763bc3f917"
            },
            {
              "Name": "meta.doors.DoorRequest",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "open",
                  "Type": "bool"
                },
                {
                  "Name": "requestId",
                  "Type": "string"
                },
                {
                  "Name": "stamp",
                  "Type": "meta.system.SampleStamp"
                }
              ],
              "Variants": [],
              "ContractHash": "7d4305918779fb91e3879962f01fc940bc4521986159cd9538fd2ea240b42b48"
            },
            {
              "Name": "meta.doors.DoorState",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "desiredOpen",
                  "Type": "bool"
                },
                {
                  "Name": "phase",
                  "Type": "meta.doors.DoorPhase"
                },
                {
                  "Name": "stamp",
                  "Type": "meta.system.SampleStamp"
                }
              ],
              "Variants": [],
              "ContractHash": "5364e3963f955219d4b8c2bab12d6d1e668e4e0a0c9e0a8bb1842b0287412771"
            },
            {
              "Name": "meta.drone.DroneController",
              "Kind": 13,
              "Fields": [],
              "Variants": [],
              "ContractHash": "a546f893405f7e5423b941ea28983033893adeba2ae52031e7c226099e79fa33"
            },
            {
              "Name": "meta.drone.NavigationEvent",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "requestId",
                  "Type": "int"
                },
                {
                  "Name": "status",
                  "Type": "meta.drone.NavigationStatus"
                },
                {
                  "Name": "stamp",
                  "Type": "meta.system.SampleStamp"
                }
              ],
              "Variants": [],
              "ContractHash": "32228b27ee49c94f414b3e6bca43b9fda97af7b3f6291d20c8f2e9fe7ae7df76"
            },
            {
              "Name": "meta.drone.NavigationReceipt",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "requestId",
                  "Type": "int"
                },
                {
                  "Name": "stamp",
                  "Type": "meta.system.SampleStamp"
                }
              ],
              "Variants": [],
              "ContractHash": "e40aa4e7bec9732926538f918a008cc30d057dc1188978e08255b03f0294a56c"
            },
            {
              "Name": "meta.drone.NavigationStatus",
              "Kind": 12,
              "Fields": [],
              "Variants": [
                "Arrived",
                "Blocked",
                "Cancelled"
              ],
              "ContractHash": "d79f471976ac1dd7e16c3d3fcafc04390a2e8c31861678f306f03256b8935eff"
            },
            {
              "Name": "meta.drone.WaypointRef",
              "Kind": 14,
              "Fields": [],
              "Variants": [],
              "ContractHash": "c914974a1ec5fad0e140ed7824f036576920303e9f9d78526fa390ead37e6a41"
            },
            {
              "Name": "meta.files.FileStore",
              "Kind": 13,
              "Fields": [],
              "Variants": [],
              "ContractHash": "c7634c1859d74a9212400dd06f15286fe1c7bb80a1105e39adc191638a724fd9"
            },
            {
              "Name": "meta.implants.LightSample",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "normalizedLight",
                  "Type": "float"
                },
                {
                  "Name": "stamp",
                  "Type": "meta.system.SampleStamp"
                }
              ],
              "Variants": [],
              "ContractHash": "d0be68d3e57df7294ca973b857bd5145122ee28b5e81e5b87a2a5d717f36f720"
            },
            {
              "Name": "meta.implants.OpticFilter",
              "Kind": 12,
              "Fields": [],
              "Variants": [
                "Clear",
                "LowLight"
              ],
              "ContractHash": "114614d728fa26a204f514dbdd00bca62c991ade70e873fde6d789d04118da65"
            },
            {
              "Name": "meta.implants.OpticState",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "requestedFilter",
                  "Type": "meta.implants.OpticFilter"
                },
                {
                  "Name": "stamp",
                  "Type": "meta.system.SampleStamp"
                }
              ],
              "Variants": [],
              "ContractHash": "cb33c6d76d1c1d5f04ef9918ec6c020af4da767a4bffbe83b4c402a82f266143"
            },
            {
              "Name": "meta.implants.Optics",
              "Kind": 13,
              "Fields": [],
              "Variants": [],
              "ContractHash": "569cadb15c5092e41981f531bc612cf190e44852b0bc8ed2e04289b9192ca3b1"
            },
            {
              "Name": "meta.industrial.Conveyor",
              "Kind": 13,
              "Fields": [],
              "Variants": [],
              "ContractHash": "b51aa034548a0ec5ea45fdc13f2cc9f4981737a2c91a1079a938ec488ec01627"
            },
            {
              "Name": "meta.industrial.ConveyorState",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "requestedEnabled",
                  "Type": "bool"
                },
                {
                  "Name": "stamp",
                  "Type": "meta.system.SampleStamp"
                }
              ],
              "Variants": [],
              "ContractHash": "3a04e9b0165835faeda613fda760e496402b19b79ddbed224299b873b47bc398"
            },
            {
              "Name": "meta.industrial.LoadSample",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "kilograms",
                  "Type": "float"
                },
                {
                  "Name": "stamp",
                  "Type": "meta.system.SampleStamp"
                }
              ],
              "Variants": [],
              "ContractHash": "4fbf013cb1cd0df4ae1db1dd3d22fb9287e27bc6c4cd8a8e107f8dcc05630568"
            },
            {
              "Name": "meta.industrial.LoadSensor",
              "Kind": 13,
              "Fields": [],
              "Variants": [],
              "ContractHash": "dc9e173ff2641bf618fb0d014a230c795b0ca6e91ec790b6e585b99f21da1e84"
            },
            {
              "Name": "meta.medical.Condition",
              "Kind": 12,
              "Fields": [],
              "Variants": [
                "Stable",
                "Compensating",
                "Critical",
                "ClinicalArrest",
                "Dead"
              ],
              "ContractHash": "a48942f4345c47c0646893017feed9d499bc43084a71a7bbe24f79bc2d9aa931"
            },
            {
              "Name": "meta.medical.Consciousness",
              "Kind": 12,
              "Fields": [],
              "Variants": [
                "Alert",
                "Dazed",
                "Confused",
                "Stuporous",
                "Unconscious",
                "Comatose",
                "BrainDead"
              ],
              "ContractHash": "67367e00e80d06817bc35f19c30c7538d7ee870b6f261beb7179ace68320aaf7"
            },
            {
              "Name": "meta.medical.DiagnosticBand",
              "Kind": 12,
              "Fields": [],
              "Variants": [
                "None",
                "Low",
                "Moderate",
                "High",
                "Critical"
              ],
              "ContractHash": "d94e616b8c7a102e699e4ae1384cea172c4fd57aca024c16b7db89b9e2cf2a23"
            },
            {
              "Name": "meta.medical.DosingController",
              "Kind": 13,
              "Fields": [],
              "Variants": [],
              "ContractHash": "4ae2b03235981a4f60f0cd548b5cfe2db89ac9f0cc4d3d5b1f3a952a2cf4ecbf"
            },
            {
              "Name": "meta.medical.MedicalMonitor",
              "Kind": 13,
              "Fields": [],
              "Variants": [],
              "ContractHash": "aca83b7a75f7a8ad0056df987ee3a7d2eb7bcfe207c3ad6ed1fc68f4fcca4605"
            },
            {
              "Name": "meta.medical.MedicalReport",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "pain",
                  "Type": "meta.medical.DiagnosticBand"
                },
                {
                  "Name": "shock",
                  "Type": "meta.medical.DiagnosticBand"
                },
                {
                  "Name": "consciousness",
                  "Type": "meta.medical.Consciousness"
                },
                {
                  "Name": "condition",
                  "Type": "meta.medical.Condition"
                },
                {
                  "Name": "oxygenationPermille",
                  "Type": "option\u003Cmeta.medical.NumericRange\u003E"
                },
                {
                  "Name": "externalBleeding",
                  "Type": "meta.medical.DiagnosticBand"
                },
                {
                  "Name": "internalBleeding",
                  "Type": "meta.medical.DiagnosticBand"
                },
                {
                  "Name": "stamp",
                  "Type": "meta.system.SampleStamp"
                }
              ],
              "Variants": [],
              "ContractHash": "59e223ba32b321c04be4257e34941c5bc68d8c4bd276b184f8a2d0403c9f8ec8"
            },
            {
              "Name": "meta.medical.NumericRange",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "minimum",
                  "Type": "int"
                },
                {
                  "Name": "maximum",
                  "Type": "int"
                }
              ],
              "Variants": [],
              "ContractHash": "583a4a35721361380d34cbc66cd2fcccaf2b96fa22c6834d9b0cfb6f0776b655"
            },
            {
              "Name": "meta.medical.OperationReceipt",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "operationId",
                  "Type": "string"
                },
                {
                  "Name": "status",
                  "Type": "meta.medical.OperationStatus"
                },
                {
                  "Name": "errorCode",
                  "Type": "option\u003Cmeta.system.ErrorCode\u003E"
                },
                {
                  "Name": "stamp",
                  "Type": "meta.system.SampleStamp"
                }
              ],
              "Variants": [],
              "ContractHash": "09e6384b3348d866e0d9883fc80a80a93799ea041a3854a067837ac736df6c9e"
            },
            {
              "Name": "meta.medical.OperationStatus",
              "Kind": 12,
              "Fields": [],
              "Variants": [
                "Prepared",
                "Committing",
                "Running",
                "Applied",
                "Rejected",
                "OutcomeUnknown"
              ],
              "ContractHash": "238ca0ddc1b86ab35a04d7d10c0e9dbcf3433f5103700ed0b0c8756c3e1dfb7a"
            },
            {
              "Name": "meta.medical.PatientRef",
              "Kind": 14,
              "Fields": [],
              "Variants": [],
              "ContractHash": "ae2796e31286d1a13a4cc63958f430aa190c1086df08cec54af386eaf2ed0000"
            },
            {
              "Name": "meta.medical.ProcedureController",
              "Kind": 13,
              "Fields": [],
              "Variants": [],
              "ContractHash": "f4baf0d274d8d86061928b38766dc6314e161b508291fc524ea9e978669fff59"
            },
            {
              "Name": "meta.net.DeliveryReceipt",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "messageId",
                  "Type": "string"
                },
                {
                  "Name": "accepted",
                  "Type": "bool"
                }
              ],
              "Variants": [],
              "ContractHash": "99279d3c80027cc92047c3ab608a9008b92b36f80a00bb67d87cd83a959bae5d"
            },
            {
              "Name": "meta.net.Message",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "replyTo",
                  "Type": "string"
                },
                {
                  "Name": "topic",
                  "Type": "string"
                },
                {
                  "Name": "payload",
                  "Type": "string"
                },
                {
                  "Name": "stamp",
                  "Type": "meta.system.SampleStamp"
                }
              ],
              "Variants": [],
              "ContractHash": "5b7edd406cb99216cacb61d627e7eae53ea1d6b3cb637e772eda3b2784bd072f"
            },
            {
              "Name": "meta.net.MessagePort",
              "Kind": 13,
              "Fields": [],
              "Variants": [],
              "ContractHash": "74aadfa9fd28aec532c3528b097411c969507d3313927fc252ae38caaa0c5bcc"
            },
            {
              "Name": "meta.netrun.NetNode",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "reference",
                  "Type": "meta.netrun.NetNodeRef"
                },
                {
                  "Name": "label",
                  "Type": "string"
                },
                {
                  "Name": "kind",
                  "Type": "meta.netrun.NetNodeKind"
                }
              ],
              "Variants": [],
              "ContractHash": "c8cd67a035bda4ef84ed26fb64db7167e7c330db50a458b6c5580204de441e0f"
            },
            {
              "Name": "meta.netrun.NetNodeKind",
              "Kind": 12,
              "Fields": [],
              "Variants": [
                "Data",
                "Control",
                "Defense"
              ],
              "ContractHash": "220609e66d3f745bb8b57bc212b04be04fcdfe7c20e16c1fb45e33b31148712b"
            },
            {
              "Name": "meta.netrun.NetNodeRef",
              "Kind": 14,
              "Fields": [],
              "Variants": [],
              "ContractHash": "c3226b888a5799e2433d245c18c72bafdf087c877ddd3c7eb1f3a8d90b9208ca"
            },
            {
              "Name": "meta.netrun.NetSession",
              "Kind": 13,
              "Fields": [],
              "Variants": [],
              "ContractHash": "b976121f78f7556b0c54c1023d5bc93855350a9641eafdcd2aa1de5ebef1cd65"
            },
            {
              "Name": "meta.netrun.ProbeOutcome",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "success",
                  "Type": "bool"
                },
                {
                  "Name": "traceLevel",
                  "Type": "int"
                },
                {
                  "Name": "detail",
                  "Type": "string"
                }
              ],
              "Variants": [],
              "ContractHash": "eaf3ca999f400be5c975c89fd4f1410a25a16050aef3bb7ea03ddbe6adff0a2a"
            },
            {
              "Name": "meta.security.Alarm",
              "Kind": 13,
              "Fields": [],
              "Variants": [],
              "ContractHash": "2e3bac335044a980267ee3621e14b6e8463eee235420f85887a5ddf2b22aae7f"
            },
            {
              "Name": "meta.security.AlarmState",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "requestedActive",
                  "Type": "bool"
                },
                {
                  "Name": "stamp",
                  "Type": "meta.system.SampleStamp"
                }
              ],
              "Variants": [],
              "ContractHash": "e52ca97408b3077d5cde8c4e54b8f5d721df3ac5ee9b09d22f492277e533734c"
            },
            {
              "Name": "meta.security.Camera",
              "Kind": 13,
              "Fields": [],
              "Variants": [],
              "ContractHash": "0eafff42aaf69c1234fd0bf1e88bba3e1d683891cc64d879d85d432bdab3e65a"
            },
            {
              "Name": "meta.security.Detection",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "confidence",
                  "Type": "float"
                },
                {
                  "Name": "stamp",
                  "Type": "meta.system.SampleStamp"
                }
              ],
              "Variants": [],
              "ContractHash": "3e4315b61d401f0830c28bc65a6724333a3ae6ec7ad442edbdc4425db0db7ed7"
            },
            {
              "Name": "meta.sensors.TemperatureSample",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "celsius",
                  "Type": "float"
                },
                {
                  "Name": "stamp",
                  "Type": "meta.system.SampleStamp"
                }
              ],
              "Variants": [],
              "ContractHash": "aa70a7c90102f466f0b7ac2f8bea9ac2553498672fb688ebe33a0aa49d329f7b"
            },
            {
              "Name": "meta.sensors.TemperatureSensor",
              "Kind": 13,
              "Fields": [],
              "Variants": [],
              "ContractHash": "707ebecbeccad33b9d890091ff373d8960cc9ad6d238a62655a0b90339b9e24d"
            },
            {
              "Name": "meta.services.Order",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "id",
                  "Type": "string"
                },
                {
                  "Name": "subject",
                  "Type": "string"
                },
                {
                  "Name": "customer",
                  "Type": "string"
                },
                {
                  "Name": "executor",
                  "Type": "string"
                },
                {
                  "Name": "status",
                  "Type": "meta.services.OrderStatus"
                },
                {
                  "Name": "revision",
                  "Type": "int"
                },
                {
                  "Name": "invoiceId",
                  "Type": "string"
                },
                {
                  "Name": "invoiceHash",
                  "Type": "string"
                },
                {
                  "Name": "amount",
                  "Type": "int"
                },
                {
                  "Name": "paid",
                  "Type": "bool"
                }
              ],
              "Variants": [],
              "ContractHash": "c85554d58c22be85c7139b256e061c626cdeb9cbf7314cefcaaec5482eeeef59"
            },
            {
              "Name": "meta.services.OrderAction",
              "Kind": 12,
              "Fields": [],
              "Variants": [
                "Begin",
                "Ready",
                "Complete",
                "Cancel"
              ],
              "ContractHash": "37d7ef42b7bee0884b89189cc211cee4f3df5eba7f93c7e05e2d145d153d300b"
            },
            {
              "Name": "meta.services.OrderChange",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "id",
                  "Type": "string"
                },
                {
                  "Name": "revision",
                  "Type": "int"
                }
              ],
              "Variants": [],
              "ContractHash": "305bc1f29f89c3b0a49f705cacc9e5d4da0230e2a8422cff9bc38597a7ea8018"
            },
            {
              "Name": "meta.services.OrderStatus",
              "Kind": 12,
              "Fields": [],
              "Variants": [
                "Submitted",
                "Assigned",
                "InProgress",
                "AwaitingPayment",
                "Paid",
                "Ready",
                "Completed",
                "Cancelled"
              ],
              "ContractHash": "6f9306afcb5eb2589d37863fa1ca808ff990735cebc6c3999ac7bd3907690bcb"
            },
            {
              "Name": "meta.services.PaymentRequest",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "confirmationId",
                  "Type": "string"
                },
                {
                  "Name": "pending",
                  "Type": "bool"
                }
              ],
              "Variants": [],
              "ContractHash": "900696a7527f3c2e2efb6cde061b001c4fc8da26b900fa61764146207f0fe1b3"
            },
            {
              "Name": "meta.services.ServiceOrders",
              "Kind": 13,
              "Fields": [],
              "Variants": [],
              "ContractHash": "9e613b032885528c4c8c63180ae6a8cfd703c6034ed93d924f4c7aebc8d9c05c"
            },
            {
              "Name": "meta.system.Error",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "code",
                  "Type": "meta.system.ErrorCode"
                },
                {
                  "Name": "message",
                  "Type": "string"
                },
                {
                  "Name": "retryAfterMs",
                  "Type": "int"
                }
              ],
              "Variants": [],
              "ContractHash": "93635f8a10076b540ae8ec2a41337f6629553b782279b42156c00617687282e4"
            },
            {
              "Name": "meta.system.ErrorCode",
              "Kind": 12,
              "Fields": [],
              "Variants": [
                "InvalidArgument",
                "OutOfRange",
                "NotFound",
                "Conflict",
                "AccessDenied",
                "PermissionRevoked",
                "Disconnected",
                "PowerOff",
                "DeviceDestroyed",
                "Incompatible",
                "Busy",
                "RateLimited",
                "QuotaExceeded",
                "Timeout",
                "Cancelled",
                "Unsupported",
                "IoFailure",
                "StaleState",
                "Interlock",
                "PathUnavailable",
                "ActionRejected",
                "OutcomeUnknown",
                "ControlLost"
              ],
              "ContractHash": "c383e3e238a60f3149d82ecd6b27b56cd59b96a30ce886f858af6c69236c806f"
            },
            {
              "Name": "meta.system.SampleStamp",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "epoch",
                  "Type": "int"
                },
                {
                  "Name": "sequence",
                  "Type": "int"
                },
                {
                  "Name": "tick",
                  "Type": "int"
                }
              ],
              "Variants": [],
              "ContractHash": "2cb089bdffaa891552e335c98c2289d21e1c8b832d60c720afc171a7c57ccb0a"
            },
            {
              "Name": "meta.vehicle.DashboardSample",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "speedMps",
                  "Type": "float"
                },
                {
                  "Name": "fuelLitres",
                  "Type": "float"
                },
                {
                  "Name": "batteryFraction",
                  "Type": "float"
                },
                {
                  "Name": "gear",
                  "Type": "int"
                },
                {
                  "Name": "automatic",
                  "Type": "bool"
                },
                {
                  "Name": "engineRunning",
                  "Type": "bool"
                },
                {
                  "Name": "stamp",
                  "Type": "meta.system.SampleStamp"
                }
              ],
              "Variants": [],
              "ContractHash": "c9cd3f8ea5bc1471d6600ca8fd461aa59e69d9aba06e41af762d09f25e8ec2aa"
            },
            {
              "Name": "meta.vehicle.EngineController",
              "Kind": 13,
              "Fields": [],
              "Variants": [],
              "ContractHash": "717b0100c834226ed7433cab2db281d8865982b0dda7df22d8c30dfadddc0753"
            },
            {
              "Name": "meta.vehicle.EngineMode",
              "Kind": 12,
              "Fields": [],
              "Variants": [
                "Normal",
                "Economy",
                "Safe"
              ],
              "ContractHash": "beba3115fdaecf767961a7e6db93f44f0fedb3d33b251fefaf36df2c5586dadb"
            },
            {
              "Name": "meta.vehicle.EngineState",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "temperatureC",
                  "Type": "float"
                },
                {
                  "Name": "rpm",
                  "Type": "int"
                },
                {
                  "Name": "oilPressureKPa",
                  "Type": "float"
                },
                {
                  "Name": "coolantFraction",
                  "Type": "float"
                },
                {
                  "Name": "faults",
                  "Type": "array\u003Cstring\u003E"
                },
                {
                  "Name": "mode",
                  "Type": "meta.vehicle.EngineMode"
                },
                {
                  "Name": "stamp",
                  "Type": "meta.system.SampleStamp"
                }
              ],
              "Variants": [],
              "ContractHash": "9f7cf5990f358744abb219bda6ec6c720826de7a4b9de519d00128c568f3e249"
            },
            {
              "Name": "meta.vehicle.TransmissionController",
              "Kind": 13,
              "Fields": [],
              "Variants": [],
              "ContractHash": "954566f9c21ef23c8c24e48e2817ffcad772dff27420db963d52b3b3d2df855c"
            },
            {
              "Name": "meta.vehicle.TransmissionState",
              "Kind": 15,
              "Fields": [
                {
                  "Name": "gear",
                  "Type": "int"
                },
                {
                  "Name": "automatic",
                  "Type": "bool"
                },
                {
                  "Name": "strategy",
                  "Type": "meta.vehicle.TransmissionStrategy"
                },
                {
                  "Name": "shiftPending",
                  "Type": "bool"
                },
                {
                  "Name": "stamp",
                  "Type": "meta.system.SampleStamp"
                }
              ],
              "Variants": [],
              "ContractHash": "18d7f17d0c0af494d2f1e4de27dae41dbe121c4ed47b2eca76ac23a4bc7445df"
            },
            {
              "Name": "meta.vehicle.TransmissionStrategy",
              "Kind": 12,
              "Fields": [],
              "Variants": [
                "Normal",
                "Economy",
                "Low"
              ],
              "ContractHash": "16b3cf3c5ecf4d94c5d1155421d0e9233f856d210fde7c1f193b651a57d02271"
            },
            {
              "Name": "meta.vehicle.VehicleDashboard",
              "Kind": 13,
              "Fields": [],
              "Variants": [],
              "ContractHash": "f9273e34020a88262fe8eaac78cd5c09fb515b518ff91df6e846c12c59a5f635"
            }
          ],
          "Members": [
            {
              "Owner": "meta.doors.Door",
              "Name": "setOpen",
              "Parameters": [
                "bool"
              ],
              "ReturnType": "result\u003Cmeta.doors.DoorState\u003E",
              "Effect": "Await",
              "Scope": "door.control",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.doors.Door.setOpen",
              "ContractHash": "0b1cdd04084eea93127c40a5075c85c96929d2c9b307a44d8f21b00234c3090c"
            },
            {
              "Owner": "meta.doors.Door",
              "Name": "snapshot",
              "Parameters": [],
              "ReturnType": "result\u003Cmeta.doors.DoorState\u003E",
              "Effect": "Read",
              "Scope": "door.read",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.doors.Door.snapshot",
              "ContractHash": "7de6eb2a19d0033f4bfcc50c5e6596590b08b865cb135377cfe447b04c3dd15b"
            },
            {
              "Owner": "meta.doors.Door",
              "Name": "stateChanged",
              "Parameters": [
                "meta.doors.DoorState"
              ],
              "ReturnType": "void",
              "Effect": "Event",
              "Scope": "door.observe",
              "Delivery": "Latest",
              "Capacity": 1,
              "QualifiedName": "meta.doors.Door.stateChanged",
              "ContractHash": "cf404f9049bef54979e81b50e8569db090752f6b4db7dce1eefe1bf1796e58f8"
            },
            {
              "Owner": "meta.doors.DoorPanel",
              "Name": "requested",
              "Parameters": [
                "meta.doors.DoorRequest"
              ],
              "ReturnType": "void",
              "Effect": "Event",
              "Scope": "doorpanel.observe",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.doors.DoorPanel.requested",
              "ContractHash": "3dd8b5cbafdc870e2f01d74f97611d11777ce07114795472f4f0be5df5aace3f"
            },
            {
              "Owner": "meta.drone.DroneController",
              "Name": "navigationFinished",
              "Parameters": [
                "meta.drone.NavigationEvent"
              ],
              "ReturnType": "void",
              "Effect": "Event",
              "Scope": "drone.navigation.observe",
              "Delivery": "Ordered",
              "Capacity": 8,
              "QualifiedName": "meta.drone.DroneController.navigationFinished",
              "ContractHash": "c78554ac7ce552bc1a3e47d23cd2edc4fd4c847623783cb0301ea53522d78820"
            },
            {
              "Owner": "meta.drone.DroneController",
              "Name": "resolveWaypoint",
              "Parameters": [
                "string"
              ],
              "ReturnType": "result\u003Cmeta.drone.WaypointRef\u003E",
              "Effect": "Read",
              "Scope": "drone.route.read",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.drone.DroneController.resolveWaypoint",
              "ContractHash": "7cbb8575a7a84e63d17c3e2f2dfa5b6d28472f1f89c70561b76fd5063e669b5b"
            },
            {
              "Owner": "meta.drone.DroneController",
              "Name": "setDestination",
              "Parameters": [
                "meta.drone.WaypointRef"
              ],
              "ReturnType": "result\u003Cmeta.drone.NavigationReceipt\u003E",
              "Effect": "Await",
              "Scope": "drone.navigate",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.drone.DroneController.setDestination",
              "ContractHash": "e73337571dcbe228962915304cd5256ebf1fc5eb1009ae0b617ce8e0a7600c51"
            },
            {
              "Owner": "meta.files.FileStore",
              "Name": "readText",
              "Parameters": [
                "string"
              ],
              "ReturnType": "result\u003Cstring\u003E",
              "Effect": "Await",
              "Scope": "files.read",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.files.FileStore.readText",
              "ContractHash": "68736fcf3c34c623d46c7cbaf9b3987b1a5d1bc99a080611d23ae7d0f32efd08"
            },
            {
              "Owner": "meta.files.FileStore",
              "Name": "writeTextAtomic",
              "Parameters": [
                "string",
                "string"
              ],
              "ReturnType": "result\u003Cvoid\u003E",
              "Effect": "Await",
              "Scope": "files.write",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.files.FileStore.writeTextAtomic",
              "ContractHash": "e0540d39d6d67411c139b3e704a9d9a06975f466a16f76b27b13676e7f6ab2ed"
            },
            {
              "Owner": "meta.implants.Optics",
              "Name": "lightSampled",
              "Parameters": [
                "meta.implants.LightSample"
              ],
              "ReturnType": "void",
              "Effect": "Event",
              "Scope": "implant.optics.observe",
              "Delivery": "Latest",
              "Capacity": 1,
              "QualifiedName": "meta.implants.Optics.lightSampled",
              "ContractHash": "54897f019e2060388a98ef92f0c74bf3f7e13baf02bed178b3b0a78bf27f39e5"
            },
            {
              "Owner": "meta.implants.Optics",
              "Name": "setFilter",
              "Parameters": [
                "meta.implants.OpticFilter"
              ],
              "ReturnType": "result\u003Cmeta.implants.OpticState\u003E",
              "Effect": "Await",
              "Scope": "implant.optics.filter",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.implants.Optics.setFilter",
              "ContractHash": "605dce3ed7614adbc258a2bf63c776734e044455c4f90c38213892b3b8a737a8"
            },
            {
              "Owner": "meta.industrial.Conveyor",
              "Name": "setEnabled",
              "Parameters": [
                "bool"
              ],
              "ReturnType": "result\u003Cmeta.industrial.ConveyorState\u003E",
              "Effect": "Await",
              "Scope": "conveyor.control",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.industrial.Conveyor.setEnabled",
              "ContractHash": "5f02e1097e89363c390b4b10daa74e52e984319b3d8b49f5080c4cca7348abb0"
            },
            {
              "Owner": "meta.industrial.LoadSensor",
              "Name": "loadSampled",
              "Parameters": [
                "meta.industrial.LoadSample"
              ],
              "ReturnType": "void",
              "Effect": "Event",
              "Scope": "sensor.load.observe",
              "Delivery": "Latest",
              "Capacity": 1,
              "QualifiedName": "meta.industrial.LoadSensor.loadSampled",
              "ContractHash": "6afc7f5367af947b5241663f71b5db87f470c899159cd09b7a0ab575e3fcfc77"
            },
            {
              "Owner": "meta.medical.DosingController",
              "Name": "dose",
              "Parameters": [
                "meta.medical.PatientRef",
                "string"
              ],
              "ReturnType": "result\u003Cmeta.medical.OperationReceipt\u003E",
              "Effect": "Await",
              "Scope": "medical.dose",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.medical.DosingController.dose",
              "ContractHash": "44195310fcfab148f8c8c0be5442eb356037ca927272a4bd9d9e77ce215fb3f9"
            },
            {
              "Owner": "meta.medical.DosingController",
              "Name": "operationStatus",
              "Parameters": [
                "string"
              ],
              "ReturnType": "result\u003Cmeta.medical.OperationReceipt\u003E",
              "Effect": "Await",
              "Scope": "medical.operation.read",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.medical.DosingController.operationStatus",
              "ContractHash": "7c919810ac93173a6276aa384422d2b7aa347e63a8e690c975b2e3d380807e70"
            },
            {
              "Owner": "meta.medical.MedicalMonitor",
              "Name": "patient",
              "Parameters": [],
              "ReturnType": "result\u003Cmeta.medical.PatientRef\u003E",
              "Effect": "Read",
              "Scope": "medical.patient.read",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.medical.MedicalMonitor.patient",
              "ContractHash": "e2b5661ec68df0ee55ac212887050a099fa8faa0d8c1afbbaae426249bdeda50"
            },
            {
              "Owner": "meta.medical.MedicalMonitor",
              "Name": "snapshot",
              "Parameters": [
                "meta.medical.PatientRef"
              ],
              "ReturnType": "result\u003Cmeta.medical.MedicalReport\u003E",
              "Effect": "Read",
              "Scope": "medical.diagnostics.read",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.medical.MedicalMonitor.snapshot",
              "ContractHash": "60d5aa9d93afa0f415f76e841bd402d290f1ca67ff3122a4e5321b6ecca7e0e8"
            },
            {
              "Owner": "meta.medical.ProcedureController",
              "Name": "operationChanged",
              "Parameters": [
                "meta.medical.OperationReceipt"
              ],
              "ReturnType": "void",
              "Effect": "Event",
              "Scope": "medical.operation.observe",
              "Delivery": "Ordered",
              "Capacity": 8,
              "QualifiedName": "meta.medical.ProcedureController.operationChanged",
              "ContractHash": "2f8f6753a8b03ab29e407e89745fe8a995f47f24b8c22a3652990db579d966af"
            },
            {
              "Owner": "meta.medical.ProcedureController",
              "Name": "operationStatus",
              "Parameters": [
                "string"
              ],
              "ReturnType": "result\u003Cmeta.medical.OperationReceipt\u003E",
              "Effect": "Await",
              "Scope": "medical.operation.read",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.medical.ProcedureController.operationStatus",
              "ContractHash": "96b43cd57b09db1faf1214d13196748289d6f34b00f0b6a17201bcd693f06d2f"
            },
            {
              "Owner": "meta.medical.ProcedureController",
              "Name": "startSelectedStep",
              "Parameters": [
                "meta.medical.PatientRef",
                "string"
              ],
              "ReturnType": "result\u003Cmeta.medical.OperationReceipt\u003E",
              "Effect": "Await",
              "Scope": "medical.procedure",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.medical.ProcedureController.startSelectedStep",
              "ContractHash": "1be6a834240be7ba0548f8e0db885b6453081e5015498b1b2400ed612ffb43b9"
            },
            {
              "Owner": "meta.net.MessagePort",
              "Name": "messageReceived",
              "Parameters": [
                "meta.net.Message"
              ],
              "ReturnType": "void",
              "Effect": "Event",
              "Scope": "net.receive",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.net.MessagePort.messageReceived",
              "ContractHash": "6e61e8cec8502bbc9ceff7de0853e278c1419f813208efc2eff439e33a2b8a72"
            },
            {
              "Owner": "meta.net.MessagePort",
              "Name": "send",
              "Parameters": [
                "string",
                "string",
                "string"
              ],
              "ReturnType": "result\u003Cmeta.net.DeliveryReceipt\u003E",
              "Effect": "Await",
              "Scope": "net.send",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.net.MessagePort.send",
              "ContractHash": "c899e7cbda77119079684846646a70a7eb67e2b74494c5a9aae61bb7784b26f1"
            },
            {
              "Owner": "meta.netrun.NetSession",
              "Name": "listVisibleNodes",
              "Parameters": [],
              "ReturnType": "result\u003Carray\u003Cmeta.netrun.NetNode\u003E\u003E",
              "Effect": "Await",
              "Scope": "netrun.observe",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.netrun.NetSession.listVisibleNodes",
              "ContractHash": "073e44f68df3c47fa903ffb43593e21b9b540e0a1fb80bc5ae19c9982972ce9b"
            },
            {
              "Owner": "meta.netrun.NetSession",
              "Name": "probe",
              "Parameters": [
                "meta.netrun.NetNodeRef"
              ],
              "ReturnType": "result\u003Cmeta.netrun.ProbeOutcome\u003E",
              "Effect": "Await",
              "Scope": "netrun.probe",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.netrun.NetSession.probe",
              "ContractHash": "f29428ccbc0d161ef0b2c31995ff25fd542e5fadad47220bb5fb9abc00f4104d"
            },
            {
              "Owner": "meta.security.Alarm",
              "Name": "setActive",
              "Parameters": [
                "bool"
              ],
              "ReturnType": "result\u003Cmeta.security.AlarmState\u003E",
              "Effect": "Await",
              "Scope": "alarm.control",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.security.Alarm.setActive",
              "ContractHash": "a1c9aa3f87a54935bbdfcd43853813ba2a9482aed6e3cfed662a466f0ff6a346"
            },
            {
              "Owner": "meta.security.Camera",
              "Name": "motionDetected",
              "Parameters": [
                "meta.security.Detection"
              ],
              "ReturnType": "void",
              "Effect": "Event",
              "Scope": "camera.motion.observe",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.security.Camera.motionDetected",
              "ContractHash": "493b9d6eb6998a49103e1d2d9ed5f4c2a57101c655bde47351dbb00c2a057203"
            },
            {
              "Owner": "meta.sensors.TemperatureSensor",
              "Name": "temperatureChanged",
              "Parameters": [
                "meta.sensors.TemperatureSample"
              ],
              "ReturnType": "void",
              "Effect": "Event",
              "Scope": "sensor.temperature.observe",
              "Delivery": "Latest",
              "Capacity": 1,
              "QualifiedName": "meta.sensors.TemperatureSensor.temperatureChanged",
              "ContractHash": "e63d47284b134f9f62af2bc50539d1ec819cf86e9c0140fcf1f96a7cef7666c5"
            },
            {
              "Owner": "meta.services.ServiceOrders",
              "Name": "assign",
              "Parameters": [
                "meta.app.UserRef",
                "meta.app.SessionRef",
                "string",
                "string",
                "int",
                "string"
              ],
              "ReturnType": "result\u003Cmeta.services.Order\u003E",
              "Effect": "Await",
              "Scope": "services.assign",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.services.ServiceOrders.assign",
              "ContractHash": "9c209a7ac6fbc6002dbf9c1aca7b42f6f640fc6d2882cfbbd1d4aee08e58e5ab"
            },
            {
              "Owner": "meta.services.ServiceOrders",
              "Name": "cachedList",
              "Parameters": [
                "meta.app.UserRef",
                "meta.app.SessionRef"
              ],
              "ReturnType": "result\u003Carray\u003Cmeta.services.Order\u003E\u003E",
              "Effect": "Read",
              "Scope": "services.read",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.services.ServiceOrders.cachedList",
              "ContractHash": "3924e6b0a215823cb1d56c7134841aec5ac4ff8039c4ff51aa8623cb4f32cf83"
            },
            {
              "Owner": "meta.services.ServiceOrders",
              "Name": "create",
              "Parameters": [
                "meta.app.UserRef",
                "meta.app.SessionRef",
                "string",
                "string"
              ],
              "ReturnType": "result\u003Cmeta.services.Order\u003E",
              "Effect": "Await",
              "Scope": "services.create",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.services.ServiceOrders.create",
              "ContractHash": "45cf9f18b1af842a63653cc4afa71bbab14f2ba8a4eb33f50b5433af81def867"
            },
            {
              "Owner": "meta.services.ServiceOrders",
              "Name": "get",
              "Parameters": [
                "meta.app.UserRef",
                "meta.app.SessionRef",
                "string"
              ],
              "ReturnType": "result\u003Cmeta.services.Order\u003E",
              "Effect": "Await",
              "Scope": "services.read",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.services.ServiceOrders.get",
              "ContractHash": "04112b5611ace5900e027c57cf47b8d70a363c2a3a0ecc28fa0c7346ed840d2f"
            },
            {
              "Owner": "meta.services.ServiceOrders",
              "Name": "invoice",
              "Parameters": [
                "meta.app.UserRef",
                "meta.app.SessionRef",
                "string",
                "int",
                "string",
                "int",
                "string"
              ],
              "ReturnType": "result\u003Cmeta.services.Order\u003E",
              "Effect": "Await",
              "Scope": "services.invoice",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.services.ServiceOrders.invoice",
              "ContractHash": "451f5559b34743c1b8f0ec1d7cdebb2865e7f4bfbff19133fadb0f51c79cde19"
            },
            {
              "Owner": "meta.services.ServiceOrders",
              "Name": "listOrders",
              "Parameters": [
                "meta.app.UserRef",
                "meta.app.SessionRef"
              ],
              "ReturnType": "result\u003Carray\u003Cmeta.services.Order\u003E\u003E",
              "Effect": "Await",
              "Scope": "services.read",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.services.ServiceOrders.listOrders",
              "ContractHash": "1c3234c57c732992b1551a29a7007b240ba9d4d23652eab7d74103bedd82c0e9"
            },
            {
              "Owner": "meta.services.ServiceOrders",
              "Name": "orderChanged",
              "Parameters": [
                "meta.services.OrderChange"
              ],
              "ReturnType": "void",
              "Effect": "Event",
              "Scope": "services.observe",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.services.ServiceOrders.orderChanged",
              "ContractHash": "c9b128919ebeba25a579af6da84501c206d3fde2491862d99f72b4e5d746ca04"
            },
            {
              "Owner": "meta.services.ServiceOrders",
              "Name": "requestPayment",
              "Parameters": [
                "meta.app.UserRef",
                "meta.app.SessionRef",
                "string"
              ],
              "ReturnType": "result\u003Cmeta.services.PaymentRequest\u003E",
              "Effect": "Await",
              "Scope": "services.pay",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.services.ServiceOrders.requestPayment",
              "ContractHash": "97e6a3c63168a9b9c521a90124a2b93b758f4941f90f92ae45e14ff469aae217"
            },
            {
              "Owner": "meta.services.ServiceOrders",
              "Name": "transition",
              "Parameters": [
                "meta.app.UserRef",
                "meta.app.SessionRef",
                "string",
                "meta.services.OrderAction",
                "int",
                "string"
              ],
              "ReturnType": "result\u003Cmeta.services.Order\u003E",
              "Effect": "Await",
              "Scope": "services.control",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.services.ServiceOrders.transition",
              "ContractHash": "6802d4947146958c78a8b87637527cd053e0429b7393483dfe191fcaf1258c63"
            },
            {
              "Owner": "meta.vehicle.EngineController",
              "Name": "setMode",
              "Parameters": [
                "meta.vehicle.EngineMode"
              ],
              "ReturnType": "result\u003Cmeta.vehicle.EngineState\u003E",
              "Effect": "Await",
              "Scope": "vehicle.engine.mode",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.vehicle.EngineController.setMode",
              "ContractHash": "db9653275318327f47e9c881cdaca55e1f9a78fa4c27c66e53b09070d54380b1"
            },
            {
              "Owner": "meta.vehicle.EngineController",
              "Name": "snapshot",
              "Parameters": [],
              "ReturnType": "result\u003Cmeta.vehicle.EngineState\u003E",
              "Effect": "Read",
              "Scope": "vehicle.engine.read",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.vehicle.EngineController.snapshot",
              "ContractHash": "a40c37ee6870c39518cc5d27db512821d78da71b7be03494ffda0220b63736f5"
            },
            {
              "Owner": "meta.vehicle.EngineController",
              "Name": "telemetryChanged",
              "Parameters": [
                "meta.vehicle.EngineState"
              ],
              "ReturnType": "void",
              "Effect": "Event",
              "Scope": "vehicle.engine.observe",
              "Delivery": "Latest",
              "Capacity": 1,
              "QualifiedName": "meta.vehicle.EngineController.telemetryChanged",
              "ContractHash": "ed56ca8fb38bd17d72d1ad559db4c4822718b2e299f07bfde853dc194fae9f98"
            },
            {
              "Owner": "meta.vehicle.TransmissionController",
              "Name": "requestGear",
              "Parameters": [
                "int"
              ],
              "ReturnType": "result\u003Cmeta.vehicle.TransmissionState\u003E",
              "Effect": "Await",
              "Scope": "vehicle.transmission.gear",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.vehicle.TransmissionController.requestGear",
              "ContractHash": "52c9157c1696772054127f58aaf65c4270ba8051ab049004fdcc9e48e8270954"
            },
            {
              "Owner": "meta.vehicle.TransmissionController",
              "Name": "setStrategy",
              "Parameters": [
                "meta.vehicle.TransmissionStrategy"
              ],
              "ReturnType": "result\u003Cmeta.vehicle.TransmissionState\u003E",
              "Effect": "Await",
              "Scope": "vehicle.transmission.strategy",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.vehicle.TransmissionController.setStrategy",
              "ContractHash": "0caef68d66c18d6f2e74505f35c1f6a36410ba07a4100450fe78ad427769d75b"
            },
            {
              "Owner": "meta.vehicle.TransmissionController",
              "Name": "snapshot",
              "Parameters": [],
              "ReturnType": "result\u003Cmeta.vehicle.TransmissionState\u003E",
              "Effect": "Read",
              "Scope": "vehicle.transmission.read",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.vehicle.TransmissionController.snapshot",
              "ContractHash": "0524f3daef62d40ba69ff9b74886df748b8d2337b26b4c82c7f69d3de2bc819c"
            },
            {
              "Owner": "meta.vehicle.TransmissionController",
              "Name": "stateChanged",
              "Parameters": [
                "meta.vehicle.TransmissionState"
              ],
              "ReturnType": "void",
              "Effect": "Event",
              "Scope": "vehicle.transmission.observe",
              "Delivery": "Latest",
              "Capacity": 1,
              "QualifiedName": "meta.vehicle.TransmissionController.stateChanged",
              "ContractHash": "0b3a1de5c6a637d7abb59017a39b85b522c4e3bc80ad74607173e3639de6b02c"
            },
            {
              "Owner": "meta.vehicle.VehicleDashboard",
              "Name": "snapshot",
              "Parameters": [],
              "ReturnType": "result\u003Cmeta.vehicle.DashboardSample\u003E",
              "Effect": "Read",
              "Scope": "vehicle.telemetry.read",
              "Delivery": "Ordered",
              "Capacity": 16,
              "QualifiedName": "meta.vehicle.VehicleDashboard.snapshot",
              "ContractHash": "ad1266e245ba4084940eee512c3e7e0b8b595a050eb15ccb794ddfe9976ac629"
            },
            {
              "Owner": "meta.vehicle.VehicleDashboard",
              "Name": "telemetryChanged",
              "Parameters": [
                "meta.vehicle.DashboardSample"
              ],
              "ReturnType": "void",
              "Effect": "Event",
              "Scope": "vehicle.telemetry.observe",
              "Delivery": "Latest",
              "Capacity": 1,
              "QualifiedName": "meta.vehicle.VehicleDashboard.telemetryChanged",
              "ContractHash": "cbfa7afbbe038f7fa02897542f011665d2964a26aeaf8fe564d6fea5e932dfdb"
            }
          ],
          "Intrinsics": [
            {
              "Module": "meta.system",
              "Name": "log",
              "Id": 1,
              "Parameters": [
                "string"
              ],
              "ReturnType": "void",
              "Effects": 32
            },
            {
              "Module": "meta.system",
              "Name": "isFresh",
              "Id": 2,
              "Parameters": [
                "meta.system.SampleStamp",
                "int"
              ],
              "ReturnType": "bool",
              "Effects": 4
            },
            {
              "Module": "meta.system",
              "Name": "nowMs",
              "Id": 3,
              "Parameters": [],
              "ReturnType": "int",
              "Effects": 4
            },
            {
              "Module": "meta.system",
              "Name": "error",
              "Id": 4,
              "Parameters": [
                "meta.system.ErrorCode",
                "string"
              ],
              "ReturnType": "meta.system.Error",
              "Effects": 0
            },
            {
              "Module": "meta.text",
              "Name": "fromInt",
              "Id": 16,
              "Parameters": [
                "int"
              ],
              "ReturnType": "string",
              "Effects": 0
            },
            {
              "Module": "meta.text",
              "Name": "fromFloat",
              "Id": 17,
              "Parameters": [
                "float"
              ],
              "ReturnType": "string",
              "Effects": 0
            },
            {
              "Module": "meta.text",
              "Name": "parseInt",
              "Id": 18,
              "Parameters": [
                "string"
              ],
              "ReturnType": "result\u003Cint\u003E",
              "Effects": 0
            },
            {
              "Module": "meta.text",
              "Name": "parseFloat",
              "Id": 19,
              "Parameters": [
                "string"
              ],
              "ReturnType": "result\u003Cfloat\u003E",
              "Effects": 0
            },
            {
              "Module": "meta.text",
              "Name": "slice",
              "Id": 20,
              "Parameters": [
                "string",
                "int",
                "int"
              ],
              "ReturnType": "result\u003Cstring\u003E",
              "Effects": 16
            },
            {
              "Module": "meta.text",
              "Name": "byteLength",
              "Id": 21,
              "Parameters": [
                "string"
              ],
              "ReturnType": "int",
              "Effects": 0
            },
            {
              "Module": "meta.numeric",
              "Name": "toFloat",
              "Id": 32,
              "Parameters": [
                "int"
              ],
              "ReturnType": "float",
              "Effects": 0
            },
            {
              "Module": "meta.numeric",
              "Name": "toInt",
              "Id": 33,
              "Parameters": [
                "float"
              ],
              "ReturnType": "result\u003Cint\u003E",
              "Effects": 0
            },
            {
              "Module": "meta.numeric",
              "Name": "toByte",
              "Id": 34,
              "Parameters": [
                "int"
              ],
              "ReturnType": "result\u003Cbyte\u003E",
              "Effects": 0
            },
            {
              "Module": "meta.numeric",
              "Name": "absInt",
              "Id": 35,
              "Parameters": [
                "int"
              ],
              "ReturnType": "int",
              "Effects": 0
            },
            {
              "Module": "meta.numeric",
              "Name": "absFloat",
              "Id": 36,
              "Parameters": [
                "float"
              ],
              "ReturnType": "float",
              "Effects": 0
            },
            {
              "Module": "meta.numeric",
              "Name": "byteToInt",
              "Id": 37,
              "Parameters": [
                "byte"
              ],
              "ReturnType": "int",
              "Effects": 0
            },
            {
              "Module": "meta.app",
              "Name": "sameSession",
              "Id": 48,
              "Parameters": [
                "meta.app.SessionRef",
                "meta.app.SessionRef"
              ],
              "ReturnType": "bool",
              "Effects": 64
            }
          ]
        }
        """;
}
