// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Verification;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;

namespace Content.Server._NC.Meta.Projects;

public sealed partial class NCMetaWorkspaceSystem
{
    /// <summary>
    /// Trusted native composition API shared by the private, licensed and firmware installers.
    /// Persistence must await the supplied final guard inside its existing atomic staging path.
    /// Additional native checks can restrict admission; they cannot replace actor/hardware/service checks.
    /// This method is neither a network message nor a guest API.
    /// </summary>
    public async Task<NCMetaMutationOutcome> TryCommitInstallationSwapAsync(EntityUid host, EntityUid user,
        EntityUid terminal, VerifiedArtifact artifact, NCMetaBinding[] bindings, string slotKind,
        NCMetaInstallation? expectedPrevious, Func<Func<Task<bool>>, Task<NCMetaMutationOutcome>> persist,
        Func<bool>? additionalAdmission = null, NCMetaFirmwareServiceStamp? firmwareStamp = null)
    {
        if (slotKind is not ("User" or "Firmware") || bindings is not { Length: <= 64 } ||
            slotKind == "Firmware" && firmwareStamp == null) return new(false, "nc-meta-error-access");
        var owner = await OnMainAsync(() => _devices.TryGetEntry(terminal, out var device) ? device.Owner : null);
        if (owner == null || await _authority.CaptureManagementAsync(user, owner) == null)
            return new(false, "nc-meta-error-access");
        var capture = await OnMainAsync(() =>
        {
            var context = CaptureWorkspaceContext(terminal, user);
            return context != null && _devices.TryGetEntry(host, out var device) && device.Ready && device.Owner == context.Owner
                ? new InstallationSwapCapture(context, device.Id, device.DeviceEpoch, device.AuthorityEpoch) : null;
        });
        var package = slotKind == "Firmware" ? "firmware" : artifact.Manifest.PackageName;
        if (capture == null || expectedPrevious != null && (expectedPrevious.Owner != capture.Context.Owner ||
            expectedPrevious.HostDeviceId != capture.DeviceId || expectedPrevious.SlotKind != slotKind ||
            expectedPrevious.PackageIdentity.Length != 0 && expectedPrevious.PackageIdentity != package))
            return new(false, "nc-meta-error-access");
        bool NativeAdmission()
        {
            if (!IsCurrent(capture.Context) || !_devices.TryGetEntry(host, out var device) || !device.Ready ||
                device.Id != capture.DeviceId || device.Owner != capture.Context.Owner ||
                device.DeviceEpoch != capture.DeviceEpoch || device.AuthorityEpoch != capture.AuthorityEpoch ||
                !_runtime.CanReach(terminal, host)) return false;
            var hardware = slotKind == "Firmware"
                ? _hardware.CanActivateFirmware(host, user, firmwareStamp!) && _runtime.CanInstallFirmware(host, user, artifact, bindings)
                : _runtime.CanInstall(host, user, artifact, bindings);
            return hardware && (additionalAdmission?.Invoke() ?? true);
        }
        if (!await OnMainAsync(NativeAdmission)) return new(false, "nc-meta-error-access");
        var expectedPolicy = expectedPrevious == null ? null : InstallationPolicy(expectedPrevious);
        InstallationSwap? stopped = null;
        async Task<bool> CanActivate()
        {
            // Recheck after preparation and before stopping a generation. The prepared SQL writes still
            // provide the final CAS if another mutation commits after this read.
            var installations = await _persistence.ReadInstallationsAsync(capture.DeviceId, capture.Context.Owner);
            var current = expectedPrevious != null ? installations.SingleOrDefault(value => value.Id == expectedPrevious.Id) :
                installations.SingleOrDefault(value => value.SlotKind == slotKind && (value.PackageIdentity == package || value.PackageIdentity.Length == 0));
            if ((current == null ? null : InstallationPolicy(current)) != expectedPolicy) return false;
            return await OnMainAsync(() => NativeAdmission() && TryStopForInstallationSwap(host, expectedPrevious, out stopped));
        }
        return await CommitInstallationSwapAsync(capture.Context, () => persist(CanActivate), () => stopped);
    }

    private sealed record InstallationSwapCapture(Context Context, string DeviceId, ulong DeviceEpoch, ulong AuthorityEpoch);
}
