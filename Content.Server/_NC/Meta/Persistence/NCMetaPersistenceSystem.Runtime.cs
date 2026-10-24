// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.Json;
using Content.Meta.Hosting;
using Content.Server.Database;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.Meta.Persistence;

public sealed partial class NCMetaPersistenceSystem
{
    public async Task<bool> SetAutoStartAsync(Guid requestId, NCMetaInstallation installation, bool enabled,
        NCSessionEpoch epoch, NCActiveWorldTime time, NCMetaAuthorityConstraints? authority = null)
    {
        using var lease = TryBeginMutation();
        if (lease == null) return false;
        var key = await InstallationKeyAsync(installation);
        if (key == null) return false;
        var updated = installation with { AutoStart = enabled, Revision = checked(installation.Revision + 1) };
        var result = await _database.ApplyNCPersistentMutationAsync(new(requestId, "meta.installation.autostart", "meta.owner", installation.Owner,
            Guid.NewGuid(), "meta.installation.autostart-changed", InstallationType, key, installation.Revision,
            1, JsonSerializer.Serialize(updated, Json), "{}", "{}", requestId, null, epoch, time, [],
            RequestFingerprint: Fingerprint(installation.Owner, installation.Id.ToString("D"), installation.Active.Digest, enabled.ToString()), Authority: authority));
        return result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted;
    }
    public Task<byte[]?> ReadArtifactAsync(string digest) => _database.ReadNCMetaBlobAsync(digest);

    public async Task<string?> ReadReleaseResourceAsync(NCMetaRelease release, string path)
    {
        var resource = release.Resources?.FirstOrDefault(value => value.Path == path);
        if (resource == null || resource.Bytes is < 0 or > 256 * 1024 || !MetaPaths.IsValid(path)) return null;
        var bytes = await _database.ReadNCMetaBlobAsync(resource.Digest);
        if (bytes == null || bytes.Length != resource.Bytes) return null;
        try { return Utf8.GetString(bytes); }
        catch (System.Text.DecoderFallbackException) { return null; }
    }

    /// <summary>Compatibility lookup is deliberately ambiguous when a host has multiple installations.</summary>
    public async Task<NCMetaInstallation?> ReadInstallationAsync(string hostId, string owner)
    {
        var installations = await ReadInstallationsAsync(hostId, owner);
        return installations.Length == 1 ? installations[0] : null;
    }

    private async Task<NCMetaInstallation?> ReadInstallationRecordAsync(string key, string hostId, string owner)
    {
        var snapshot = await _snapshots.ReadCurrentAsync(InstallationType, key);
        if (snapshot == null || snapshot.Payload.Length > 256 * 1024)
            return null;
        try
        {
            var installation = JsonSerializer.Deserialize<NCMetaInstallation>(snapshot.Payload, Json);
            return installation is { Bindings.Length: <= 64 } && installation.HostDeviceId == hostId &&
                   installation.Owner == owner && installation.Revision == snapshot.AggregateVersion &&
                   installation.Id != Guid.Empty && installation.SlotKind is "User" or "Firmware" &&
                   installation.MemoryLimitBytes >= 0 && installation.StateSchemaVersion is > 0 and <= 65535 &&
                   installation.RecoveryStateSchemaVersion is > 0 and <= 65535 &&
                   ValidRelease(installation.Active) && (installation.Recovery == null || ValidRelease(installation.Recovery)) &&
                   installation.Bindings.All(binding =>
                       binding is { Slot.Length: > 0 and <= 128, Scopes.Length: <= 64, ContractHash.Length: 64 } &&
                       Guid.TryParseExact(binding.DeviceId, "D", out _)) ? installation : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Bytes and permissions have been validated by the broker before this atomic pointer swap.</summary>
    public async Task<NCMetaMutationOutcome> InstallAsync(Guid requestId, string hostId, string owner,
        NCMetaProject project, NCMetaRelease release, NCMetaBinding[] bindings, bool autoStart,
        NCSessionEpoch epoch, NCActiveWorldTime time, int storageBytes = 256 * 1024 * 1024,
        int maximumInstallations = MaximumInstallations, string slotKind = "User", Func<Task<bool>>? activationGuard = null,
        long? memoryLimitBytes = null, Guid? expectedInstallationId = null, long? expectedInstallationRevision = null,
        string? consentFingerprint = null, NCMetaAuthorityConstraints? authority = null)
    {
        using var lease = TryBeginMutation();
        if (lease == null) return new(false, "nc-meta-error-busy");
        if (owner != project.Owner || !TryResolveProjectRelease(project, release, out var canonicalRelease) || bindings.Length > 64 ||
            !Guid.TryParseExact(hostId, "D", out _))
            return new(false, "nc-meta-error-access");
        release = canonicalRelease;
        var fingerprint = InstallationFingerprint(hostId, owner, project.Id, release, bindings, autoStart, slotKind,
            memoryLimitBytes, expectedInstallationId, expectedInstallationRevision, consentFingerprint);
        var replay = await ReadReplayAsync(requestId, owner, fingerprint, "meta.installation.activate");
        if (replay != null) return replay;
        PreparedInstallation? prepared;
        try { prepared = await PrepareInstallationAsync(requestId, hostId, owner, project.Id, release, bindings,
            autoStart, storageBytes, maximumInstallations, slotKind, memoryLimitBytes,
            expectedInstallationId: expectedInstallationId, expectedInstallationRevision: expectedInstallationRevision); }
        catch (StateMigrationRequiredException) { return new(false, "nc-meta-error-state-migration"); }
        catch (InstallationRevisionException) { return new(false, "nc-meta-error-revision"); }
        if (prepared == null) return new(false, "nc-meta-error-quota");
        if (activationGuard != null && !await activationGuard()) return new(false, "nc-meta-error-access");
        var outcome = new NCMetaMutationOutcome(true, "nc-meta-installed", prepared.Installation.Revision);
        var result = await _database.ApplyNCPersistentBatchMutationAsync(new NCPersistentBatchMutationRequest(
            requestId, "meta.installation.activate", "meta.owner", owner, prepared.Writes, JsonSerializer.Serialize(outcome, Json),
            requestId, null, epoch, time, [], RequestFingerprint: fingerprint, Authority: authority));
        return DecodeOutcome(result);
    }

    public async Task<string?> ReadTextAsync(Guid volumeId, string owner, string path)
    {
        if (!MetaPaths.IsValid(path)) return null;
        var volume = await ReadVolumeAsync(volumeId, owner);
        var file = volume?.Files.FirstOrDefault(value => value.Path == path);
        if (file == null) return null;
        var bytes = await _database.ReadNCMetaBlobAsync(file.Digest);
        if (bytes == null || bytes.Length != file.Bytes || bytes.Length > 32768) return null;
        try { return Utf8.GetString(bytes); }
        catch (System.Text.DecoderFallbackException) { return null; }
    }

    public async Task<NCMetaMutationOutcome> WriteTextAsync(Guid requestId, Guid volumeId, string owner,
        string path, string text, int capacity, NCSessionEpoch epoch, NCActiveWorldTime time,
        Func<Task<bool>>? commitGuard = null, NCMetaFileWriteAuthorization? authorization = null)
    {
        using var lease = TryBeginMutation();
        if (lease == null) return new(false, "nc-meta-error-busy");
        if (!MetaPaths.IsValid(path) || text.Length > 32768 || capacity is < 0 or > 256 * 1024 * 1024)
            return new(false, "nc-meta-error-input");
        byte[] bytes;
        try { bytes = Utf8.GetBytes(text); }
        catch (System.Text.EncoderFallbackException) { return new(false, "nc-meta-error-input"); }
        if (bytes.Length > 32768) return new(false, "nc-meta-error-quota");
        var old = await ReadVolumeAsync(volumeId, owner);
        capacity = Math.Min(capacity, old?.CapacityBytes ?? capacity);
        var files = old?.Files ?? [];
        var previous = files.FirstOrDefault(file => file.Path == path);
        if (previous == null && files.Length >= 1024 ||
            files.Sum(file => (long) file.Bytes) - (previous?.Bytes ?? 0) + bytes.Length > capacity)
            return new(false, "nc-meta-error-quota");
        var digest = await _database.StoreNCMetaBlobAsync(bytes);
        var updated = new NCMetaVolume(volumeId, owner, capacity,
            files.Where(file => file.Path != path).Append(new NCMetaUserFile(path, digest, bytes.Length,
                checked((previous?.Revision ?? 0) + 1))).OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(),
            checked((old?.Revision ?? 0) + 1));
        var outcome = new NCMetaMutationOutcome(true, "nc-meta-saved", updated.Revision);
        var writes = new List<NCPersistentAggregateWrite>
        {
            new(Guid.NewGuid(), "meta.volume.written", "meta.volume", volumeId.ToString("D"), old?.Revision ?? 0, 1,
                JsonSerializer.Serialize(updated, Json), "{}"),
        };
        if (authorization != null)
        {
            var device = await ReadDeviceAsync(authorization.DeviceId);
            if (device == null || device.Owner != owner || device.AuthorityEpoch != authorization.AuthorityEpoch)
                return new(false, "nc-meta-error-access");
            writes.Add(new(Guid.NewGuid(), "meta.device.file-attested", "meta.device", device.DeviceId, device.Revision, 1,
                JsonSerializer.Serialize(device with { Revision = checked(device.Revision + 1) }, Json), "{}"));
            var expectedVolume = StableGuid("volume:" + authorization.DeviceId);
            if (authorization.Installation is { } admitted)
            {
                var current = await ReadInstallationAsync(admitted.HostDeviceId, owner, admitted.Id);
                if (current == null || current.Active.Digest != admitted.Active.Digest || current.StateVolumeId != admitted.StateVolumeId)
                    return new(false, "nc-meta-error-access");
                var key = await InstallationKeyAsync(current);
                if (key == null) return new(false, "nc-meta-error-access");
                writes.Add(new(Guid.NewGuid(), "meta.installation.file-attested", InstallationType, key, current.Revision, 1,
                    JsonSerializer.Serialize(current with { Revision = checked(current.Revision + 1) }, Json), "{}"));
                if (admitted.HostDeviceId == authorization.DeviceId && admitted.StateVolumeId != Guid.Empty)
                    expectedVolume = admitted.StateVolumeId;
                if (admitted.HostDeviceId != authorization.DeviceId)
                {
                    var host = await ReadDeviceAsync(admitted.HostDeviceId);
                    if (host == null || host.Owner != owner || host.AuthorityEpoch != authorization.HostAuthorityEpoch)
                        return new(false, "nc-meta-error-access");
                    writes.Add(new(Guid.NewGuid(), "meta.device.file-host-attested", "meta.device", host.DeviceId, host.Revision, 1,
                        JsonSerializer.Serialize(host with { Revision = checked(host.Revision + 1) }, Json), "{}"));
                }
            }
            if (volumeId != expectedVolume) return new(false, "nc-meta-error-access");
        }
        if (commitGuard != null && !await commitGuard()) return new(false, "nc-meta-error-access");
        var result = await _database.ApplyNCPersistentBatchMutationAsync(new(requestId, "meta.volume.write", "meta.owner", owner,
            writes, JsonSerializer.Serialize(outcome, Json), requestId, null, epoch, time, [],
            RequestFingerprint: Fingerprint(owner, volumeId.ToString("D"), path, digest)));
        return DecodeOutcome(result);
    }

    private async Task<NCMetaVolume?> ReadVolumeAsync(Guid id, string owner)
    {
        var snapshot = await _snapshots.ReadCurrentAsync("meta.volume", id.ToString("D"));
        if (snapshot == null || snapshot.Payload.Length > 512 * 1024) return null;
        try
        {
            var volume = JsonSerializer.Deserialize<NCMetaVolume>(snapshot.Payload, Json);
            return volume is { Files.Length: <= 1024, CapacityBytes: >= 0 and <= 256 * 1024 * 1024 } &&
                   volume.Id == id && volume.Owner == owner &&
                   volume.Revision == snapshot.AggregateVersion && volume.Files.All(file => file is
                       { Digest.Length: 64, Bytes: >= 0 and <= 32768, Revision: > 0 } && MetaPaths.IsValid(file.Path)) &&
                   volume.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() == volume.Files.Length &&
                   volume.Files.Sum(file => (long) file.Bytes) <= volume.CapacityBytes
                ? volume : null;
        }
        catch (JsonException) { return null; }
    }

    private static NCMetaMutationOutcome DecodeOutcome(NCPersistentMutationResult result) =>
        result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted &&
        result.ResultPayload != null
            ? JsonSerializer.Deserialize<NCMetaMutationOutcome>(result.ResultPayload, Json) ?? new(false, "nc-meta-error-storage")
            : new(false, result.Status == NCPersistentMutationStatus.VersionConflict ? "nc-meta-error-revision" : "nc-meta-error-storage");

    private static NCMetaMutationOutcome DecodeOutcome(NCPersistentBatchMutationResult result) =>
        result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted && result.ResultPayload != null
            ? JsonSerializer.Deserialize<NCMetaMutationOutcome>(result.ResultPayload, Json) ?? new(false, "nc-meta-error-storage")
            : new(false, result.Status == NCPersistentMutationStatus.VersionConflict ? "nc-meta-error-revision" : "nc-meta-error-storage");
}
