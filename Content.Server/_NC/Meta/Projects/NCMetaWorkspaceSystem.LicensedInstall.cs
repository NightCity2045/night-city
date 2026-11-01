// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.Meta.Persistence;

namespace Content.Server._NC.Meta.Projects;

public sealed partial class NCMetaWorkspaceSystem
{
    private async Task<NCMetaMutationOutcome> InstallLicensedReleaseAsync(Context context, EntityUid host,
        string deviceId, Guid requestId, Guid productId, Guid releaseId, NCMetaBinding[] bindings, bool scopeConsent)
    {
        var admission = await _marketplace.PrepareActivationAsync(context.Owner, deviceId, productId, releaseId,
            scopeConsent, context.DeviceId);
        if (admission == null) return new(false, "nc-meta-error-access");
        var artifact = await ReadVerifiedAsync(admission.LicensedRelease.Release);
        if (artifact == null) return new(false, "nc-meta-error-artifact");
        var app = await ReadApplicationAsync(admission.LicensedRelease.Release, artifact);
        if (app.Declared && app.Application == null) return new(false, "nc-meta-error-artifact");
        if (!await OnMainAsync(() => IsCurrent(context) && _devices.TryGetEntry(host, out var device) &&
                device.Id == deviceId && device.Owner == context.Owner && device.AuthorityEpoch == admission.DeviceAuthorityEpoch &&
                _runtime.CanReach(context.Terminal, host) && _runtime.CanInstall(host, context.User, artifact, bindings)))
            return new(false, "nc-meta-error-access");
        var limits = await OnMainAsync(() => IsCurrent(context) ? InstallationLimits(host) : null);
        if (limits == null) return new(false, "nc-meta-error-access");
        var installedBefore = await _persistence.ReadInstallationsAsync(deviceId, context.Owner);
        var previous = installedBefore.FirstOrDefault(value =>
            value.SlotKind == "User" && value.PackageIdentity == artifact.Manifest.PackageName);
        previous ??= installedBefore.Length == 1 && installedBefore[0].PackageIdentity.Length == 0 &&
            installedBefore[0].ProjectId == admission.LicensedRelease.ProjectId ? installedBefore[0] : null;
        var installAuthority = await CaptureInstallationAuthorityAsync(context, host);
        if (installAuthority == null) return new(false, "nc-meta-error-access");
        return await TryCommitInstallationSwapAsync(host, context.User, context.Terminal, artifact, bindings, "User", previous,
            guard => _persistence.InstallLicensedAsync(requestId, deviceId, context.Owner, productId, admission, bindings,
                context.Epoch, context.Time, limits.Value.StorageBytes, limits.Value.Slots, guard, authority: installAuthority),
            () => IsCurrent(context) && _devices.TryGetEntry(host, out var device) &&
                device.Id == deviceId && device.AuthorityEpoch == admission.DeviceAuthorityEpoch);
    }

    private async Task<bool> CanRunReleaseAsync(string owner, string deviceId, NCMetaRelease release, bool online = false,
        string? accessDeviceId = null)
    {
        if (release.CommercialOrigin is { } origin)
        {
            var licensed = online
                ? await _marketplace.ResolveOnlineInstalledReleaseAsync(owner, deviceId, origin.ProductId, origin.ReleaseId, accessDeviceId)
                : await _marketplace.ResolveInstalledReleaseAsync(owner, deviceId, origin.ProductId, origin.ReleaseId);
            return licensed != null && licensed.ProjectId == origin.PublisherProjectId &&
                   licensed.Release.Digest == release.Digest && licensed.Release.Id == release.Id;
        }
        if (release.BuildAuthorization is { } build && build.Owner == owner && build.ArtifactDigest == release.Digest &&
            build.DependencyFingerprint == NCMetaPersistenceSystem.Fingerprint(System.Text.Json.JsonSerializer.Serialize(
                release.Dependencies ?? [], new System.Text.Json.JsonSerializerOptions { MaxDepth = 16 }))) return true;
        foreach (var dependency in release.Dependencies ?? [])
        {
            var resolved = await _marketplace.ResolveBuildDependencyAsync(owner, deviceId, dependency.ProductId, dependency.ReleaseId);
            if (resolved?.Reference != dependency) return false;
        }
        return true;
    }
}
