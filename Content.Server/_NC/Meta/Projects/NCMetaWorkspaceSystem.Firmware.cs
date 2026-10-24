// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Language;
using Content.Server._NC.Meta.Commerce;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.UI;

namespace Content.Server._NC.Meta.Projects;

public sealed partial class NCMetaWorkspaceSystem
{
    private async Task FirmwareRequestAsync(Context context, Persistence.NCMetaProject project,
        NCMetaWorkspaceRequestMessage request, NCMetaWorkspaceState state)
    {
        var deviceId = request.DeviceId.Length == 0 ? context.DeviceId : request.DeviceId;
        NCMetaFirmwareInstallRequest? consent = null;
        Guid productId;
        try
        {
            if (request.Operation == NCMetaWorkspaceOperation.InstallFirmware)
            {
                consent = JsonSerializer.Deserialize<NCMetaFirmwareInstallRequest>(request.Source, CommerceJson);
                if (consent == null || !consent.ScopeConsent || !consent.ResourceConsent ||
                    consent.ApprovedScopes is not { Count: <= 64 } || consent.Bindings is not { Count: <= 64 } ||
                    consent.Bindings.Any(binding => binding == null || binding.Slot is not { Length: > 0 and <= 192 } ||
                        binding.DeviceId is not { Length: > 0 and <= 128 } || binding.ContractHash is not { Length: 64 } ||
                        binding.Scopes is not { Count: <= 64 } || binding.Scopes.Any(scope => scope is not { Length: > 0 and <= 128 })) ||
                    consent.ApprovedScopes.Any(scope => scope is not { Length: > 0 and <= 128 }) ||
                    consent.ExpectedInstallationRevision < 0 ||
                    (consent.ExpectedInstallationId == Guid.Empty) != (consent.ExpectedInstallationRevision == 0) ||
                    request.InstallationId != consent.ExpectedInstallationId) throw new JsonException();
                productId = consent.ProductId;
            }
            else productId = request.Source.Length == 0 ? Guid.Empty :
                (JsonSerializer.Deserialize<NCMetaFirmwareSelection>(request.Source, CommerceJson) ?? throw new JsonException()).ProductId;
        }
        catch (JsonException) { state.Notice = Loc.GetString("nc-meta-error-input"); return; }
        var licensed = productId == Guid.Empty ? null :
            await _marketplace.ResolveLicensedReleaseAsync(context.Owner, deviceId, productId, request.ReleaseId);
        var release = productId == Guid.Empty ? project.Releases.FirstOrDefault(value => value.Id == request.ReleaseId) : licensed?.Release;
        if (release == null) { state.Notice = Loc.GetString("nc-meta-error-artifact"); return; }
        if (productId == Guid.Empty && release.CommercialOrigin != null)
        { state.Notice = Loc.GetString("nc-meta-error-access"); return; }
        var bindings = consent?.Bindings.Select(binding => new NCMetaBinding(binding.Slot, binding.DeviceId,
            binding.ContractHash, binding.Scopes.ToArray())).ToArray() ?? [];
        var consentFingerprint = consent == null ? null : NCMetaPersistenceSystem.Fingerprint(JsonSerializer.Serialize(consent, CommerceJson));
        if (consent != null)
        {
            // Read the immutable receipt before maintenance/capacity/replacement checks or native side effects.
            var replay = await _persistence.ReadFirmwareInstallReplayAsync(request.RequestId, deviceId, context.Owner,
                licensed?.ProjectId ?? project.Id, release, bindings, consent.ApprovedProcessMemoryBytes,
                consent.ExpectedInstallationId, consent.ExpectedInstallationRevision, productId, consentFingerprint);
            if (replay != null) { state.Notice = Loc.GetString(replay.MessageKey); return; }
        }
        var artifact = await ReadVerifiedAsync(release);
        var bytes = artifact == null ? null : await _persistence.ReadArtifactAsync(release.Digest);
        if (artifact == null || bytes == null) { state.Notice = Loc.GetString("nc-meta-error-artifact"); return; }
        var application = await ReadApplicationAsync(release, artifact);
        if (application.Declared && application.Application == null)
        { state.Notice = Loc.GetString("nc-meta-error-artifact"); return; }
        var capture = await OnMainAsync(() =>
        {
            if (!IsCurrent(context) || !_devices.TryResolve(deviceId, out var device) || device.Owner != context.Owner ||
                !_devices.CanManage(device.Entity, context.User, requireRange: false) || !_runtime.CanReach(context.Terminal, device.Entity) ||
                !TryComp<NCMetaHostComponent>(device.Entity, out var host) || !_hardwareProfiles.TryIndex(host.Profile, out var profile)) return null;
            var memory = profile.MemoryBytes / Math.Max(1, profile.MaxProcesses) - artifact.ResidentMemoryBytes;
            var fits = profile.AllowFirmwareUpdates && profile.MaxProcesses > 0 && artifact.Manifest.RuntimeAbi == profile.RuntimeAbi &&
                artifact.Manifest.LanguageVersion == profile.LanguageVersion && memory >= Math.Max(32768, artifact.MinimumProcessMemoryBytes) &&
                artifact.MaximumConstantStringBytes <= 32768;
            _hardware.TryCaptureFirmwareService(device.Entity, context.User, out var stamp);
            var storage = TryComp<NCMetaStorageComponent>(device.Entity, out var volume)
                ? Math.Min(profile.StorageBytes, volume.CapacityBytes) : profile.StorageBytes;
            return new FirmwareCapture(device.Entity, memory, storage, fits, _hardware.IsMaintenance(device.Entity), stamp);
        });
        if (capture == null) { state.Notice = Loc.GetString("nc-meta-error-access"); return; }
        var previous = (await _persistence.ReadInstallationsAsync(deviceId, context.Owner)).FirstOrDefault(value => value.SlotKind == "Firmware");
        if (consent == null && request.InstallationId != Guid.Empty && previous?.Id != request.InstallationId)
        { state.Notice = Loc.GetString("nc-meta-error-revision"); return; }
        var links = artifact.GetLinks().Select(link => new NCMetaLinkSummary(link.SlotName,
            artifact.GetType(link.CapabilityTypeId).Name, link.ContractHash,
            MetaApiCatalog.Default.Members.Where(member => member.Owner == artifact.GetType(link.CapabilityTypeId).Name &&
                artifact.Manifest.RequiredScopes.Contains(member.Scope)).Select(member => member.Scope).Distinct().ToList())).ToList();
        var packageBytes = checked(bytes.LongLength + (release.Resources ?? []).Sum(resource => (long) resource.Bytes));
        var fits = capture.Fits && await _persistence.CanStageInstallationAsync(deviceId, context.Owner,
            licensed?.ProjectId ?? project.Id, release, capture.StorageBytes, "Firmware");
        state.Firmware = new(release.Id, artifact.ResidentMemoryBytes, capture.MemoryBytes, packageBytes,
            artifact.Manifest.RequiredScopes.ToList(), links, fits && capture.Stamp != null, capture.Maintenance,
            !fits ? "nc-meta-error-quota" : capture.Stamp == null ? "nc-meta-error-access" : "nc-meta-ready",
            productId, previous?.Id ?? Guid.Empty, previous?.Revision ?? 0, artifact.Manifest.PackageName, artifact.Manifest.PackageVersion);
        if (consent == null) return;
        if ((previous?.Id ?? Guid.Empty) != consent.ExpectedInstallationId || (previous?.Revision ?? 0) != consent.ExpectedInstallationRevision)
        { state.Notice = Loc.GetString("nc-meta-error-revision"); return; }
        if (!fits || capture.Stamp == null ||
            consent.ApprovedArtifactResidentBytes != artifact.ResidentMemoryBytes || consent.ApprovedProcessMemoryBytes != capture.MemoryBytes ||
            consent.ApprovedPackageBytes != packageBytes || consent.ApprovedScopes is not { Count: <= 64 } ||
            !consent.ApprovedScopes.Order(StringComparer.Ordinal).SequenceEqual(artifact.Manifest.RequiredScopes.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        { state.Notice = Loc.GetString("nc-meta-error-access"); return; }
        NCMetaSoftwareActivationAdmission? admission = null;
        if (licensed != null)
        {
            admission = await _marketplace.PrepareActivationAsync(context.Owner, deviceId, productId, release.Id, true, context.DeviceId);
            if (admission == null || admission.LicensedRelease.Release.Digest != release.Digest ||
                admission.LicensedRelease.ProjectId != licensed.ProjectId)
            { state.Notice = Loc.GetString("nc-meta-error-access"); return; }
        }
        else if (!await CanRunReleaseAsync(context.Owner, deviceId, release))
        { state.Notice = Loc.GetString("nc-meta-error-access"); return; }
        var installAuthority = await CaptureInstallationAuthorityAsync(context, capture.Host);
        if (installAuthority == null) { state.Notice = Loc.GetString("nc-meta-error-access"); return; }
        var outcome = await TryCommitInstallationSwapAsync(capture.Host, context.User, context.Terminal, artifact, bindings, "Firmware", previous,
            guard => admission == null
                ? _persistence.InstallAsync(request.RequestId, deviceId, context.Owner, project, release, bindings,
                    true, context.Epoch, context.Time, capture.StorageBytes, 64, "Firmware", guard, capture.MemoryBytes,
                    consent.ExpectedInstallationId, consent.ExpectedInstallationRevision, consentFingerprint, authority: installAuthority)
                : _persistence.InstallLicensedAsync(request.RequestId, deviceId, context.Owner, productId, admission,
                    bindings, context.Epoch, context.Time, capture.StorageBytes, 64, guard, "Firmware", true, capture.MemoryBytes,
                    consent.ExpectedInstallationId, consent.ExpectedInstallationRevision, consentFingerprint, authority: installAuthority),
            () => IsCurrent(context) && (admission == null || _devices.TryGetEntry(capture.Host, out var device) &&
                device.AuthorityEpoch == admission.DeviceAuthorityEpoch), capture.Stamp);
        state.Notice = Loc.GetString(outcome.MessageKey);
    }

    private sealed record FirmwareCapture(EntityUid Host, long MemoryBytes, int StorageBytes, bool Fits,
        bool Maintenance, NCMetaFirmwareServiceStamp? Stamp);
}
