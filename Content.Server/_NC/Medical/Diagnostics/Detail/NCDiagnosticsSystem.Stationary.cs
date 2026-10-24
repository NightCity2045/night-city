// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.ActionResolution;
using Content.Server._NC.Medical.Diagnostics.Scanner;
using Content.Shared._NC.Medical.Diagnostics.Detail;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Diagnostics.Detail;

public sealed partial class NCDiagnosticsSystem
{
    /// <summary>Unattended reads require the real scanner's wired operating-table occupant, never an arbitrary patient.</summary>
    public bool TryBuildForStationaryScanner(EntityUid scanner, EntityUid expectedPatient, NCDiagnosticTier tier,
        out NCMedicalSnapshot snapshot, out NCDiagnosticAccessFailure failure)
    {
        snapshot = default!;
        if (!TryResolveStationaryScanner(scanner, expectedPatient, tier, out var capabilityId, out failure)) return false;
        if (!TryBuildSnapshot(expectedPatient, capabilityId, NCActionResolutionId.New(), _timing.CurTime, out snapshot))
        { failure = NCDiagnosticAccessFailure.TierUnavailable; return false; }
        failure = NCDiagnosticAccessFailure.None; return true;
    }
    public bool CanObserveStationaryScanner(EntityUid scanner, EntityUid expectedPatient, NCDiagnosticTier tier) =>
        TryResolveStationaryScanner(scanner, expectedPatient, tier, out _, out _);
    private bool TryResolveStationaryScanner(EntityUid scanner, EntityUid expectedPatient, NCDiagnosticTier tier,
        out ProtoId<NCDiagnosticCapabilityPrototype> capabilityId, out NCDiagnosticAccessFailure failure)
    {
        capabilityId = default; failure = NCDiagnosticAccessFailure.CapabilityUnavailable;
        if (tier == NCDiagnosticTier.AdminExact) { failure = NCDiagnosticAccessFailure.AdminServerOnly; return false; }
        if (!Exists(scanner) || !Exists(expectedPatient) || !TryComp<NCBodyScannerConsoleComponent>(scanner, out var console) ||
            !TryComp<NCDiagnosticCapabilityComponent>(scanner, out var observed) || !observed.Operational ||
            !TryResolveCapability(scanner, tier, out capabilityId, out var capability, out failure)) return false;
        if (capability.RequiresHeld || capability.SelfOnly || !float.IsFinite(capability.Range) || capability.Range is <= 0 or > 10)
        { failure = NCDiagnosticAccessFailure.TierUnavailable; return false; }
        if (capability.RequiresPower && !_power.IsPowered(scanner)) { failure = NCDiagnosticAccessFailure.ToolUnpowered; return false; }
        if (!_bodyScanner.TryGetPatient((scanner, console), out var patient, out failure)) return false;
        if (patient != expectedPatient) { failure = NCDiagnosticAccessFailure.OperatingTablePatientChanged; return false; }
        if (!_interaction.InRangeUnobstructed(scanner, patient, capability.Range)) { failure = NCDiagnosticAccessFailure.OutOfRange; return false; }
        failure = NCDiagnosticAccessFailure.None; return true;
    }
}
