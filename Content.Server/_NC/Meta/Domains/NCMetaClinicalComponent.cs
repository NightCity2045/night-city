// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Medical.Diagnostics.Detail;

namespace Content.Server._NC.Meta.Domains;

/// <summary>Server-commissioned physical patient context; guest code never selects an entity or dose.</summary>
[RegisterComponent]
public sealed partial class NCMetaClinicalComponent : Component
{
    [DataField] public EntityUid? Patient;
    [DataField] public EntityUid? Operator;
    [DataField] public EntityUid? DiagnosticTool;
    [DataField] public NCDiagnosticTier DiagnosticTier = NCDiagnosticTier.HandheldApproximate;
    [DataField] public bool DosingHardware;
    [DataField] public bool ProcedureHardware;
    [DataField] public string ReservoirSolution = "chemicals";
    [DataField] public float ConfiguredDose = 1;
    [DataField] public float MaximumDose = 5;
    [DataField] public bool Interlocked;
    [DataField] public bool AutomationEnabled;
    [DataField] public float MaximumStepSeconds = 120;
    public Guid PatientEpoch = Guid.NewGuid();
    public EntityUid? LastPatient;
    public EntityUid? LastOperator;
    public TimeSpan NextCommand;
    public string? StationaryScannerId;
    public ulong ScannerDeviceEpoch;
    public ulong ScannerAuthorityEpoch;
}
