// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Medical.Diagnostics.Detail;

namespace Content.Server._NC.Meta.Domains;

public sealed partial class NCMetaClinicalSystem
{
    /// <summary>Physical commissioning captures both registry generations and the scanner's actual strapped patient.</summary>
    public bool TryBindStationaryScanner(Entity<NCMetaClinicalComponent?> ent, EntityUid user, EntityUid scanner)
    {
        if (!Resolve(ent, ref ent.Comp) || !_devices.CanManage(ent.Owner, user) || !_devices.CanManage(scanner, user) ||
            !_interaction.InRangeUnobstructed(ent.Owner, scanner) || ent.Comp.Patient is not { } patient ||
            !_devices.TryGetEntry(scanner, out var entry) ||
            !_diagnostics.TryBuildForStationaryScanner(scanner, patient, ent.Comp.DiagnosticTier, out _, out _)) return false;
        Invalidate(ent.Owner, ent.Comp); ent.Comp.DiagnosticTool = scanner; ent.Comp.StationaryScannerId = entry.Id;
        ent.Comp.ScannerDeviceEpoch = entry.DeviceEpoch; ent.Comp.ScannerAuthorityEpoch = entry.AuthorityEpoch;
        return true;
    }
    private bool CanObserveStationary(EntityUid device, out NCMetaClinicalComponent context, out EntityUid patient)
    {
        patient = default;
        if (!TryComp(device, out context!) || context.Interlocked || context.StationaryScannerId is not { } id ||
            context.Patient is not { } target || context.DiagnosticTool is not { } scanner ||
            !_devices.TryGetEntry(device, out var host) || !_devices.TryResolve(id, out var entry) || entry.Entity != scanner ||
            entry.Owner != host.Owner || entry.DeviceEpoch != context.ScannerDeviceEpoch || entry.AuthorityEpoch != context.ScannerAuthorityEpoch ||
            !_devices.IsPowered(scanner) || !_interaction.InRangeUnobstructed(device, scanner) ||
            !_diagnostics.CanObserveStationaryScanner(scanner, target, context.DiagnosticTier)) return false;
        patient = target; return true;
    }
}
