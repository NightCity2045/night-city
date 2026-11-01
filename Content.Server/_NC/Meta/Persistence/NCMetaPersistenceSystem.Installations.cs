// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Bytecode;
using Content.Server.Database;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.Meta.Persistence;

public sealed partial class NCMetaPersistenceSystem
{
    private const string InstallationIndexType = "meta.installation-index";
    private const int MaximumInstallations = 64;
    private sealed record InstallationReference(Guid Id, string Package, string Slot);
    private sealed record InstallationIndex(string DeviceId, string Owner, long Revision, InstallationReference[] Entries);
    private sealed record InstallationStage(Guid Id, string Owner, NCMetaInstallation Candidate, string Phase, long Revision);
    private sealed record PreparedInstallation(NCMetaInstallation Installation, List<NCPersistentAggregateWrite> Writes);

    private async Task<InstallationIndex?> ReadInstallationIndexAsync(string hostId, string owner)
    {
        var snapshot = await _snapshots.ReadCurrentAsync(InstallationIndexType, hostId);
        if (snapshot == null) return new(hostId, owner, 0, []);
        if (snapshot.Payload.Length > 32768) return null;
        try
        {
            var index = JsonSerializer.Deserialize<InstallationIndex>(snapshot.Payload, Json);
            return index is { Entries.Length: <= MaximumInstallations } && index.DeviceId == hostId &&
                index.Owner == owner && index.Revision == snapshot.AggregateVersion &&
                index.Entries.All(entry => entry is { Id: var id, Package.Length: > 0 and <= 192, Slot: "User" or "Firmware" } && id != Guid.Empty) &&
                index.Entries.Select(entry => entry.Id).Distinct().Count() == index.Entries.Length &&
                index.Entries.Select(entry => (entry.Slot, entry.Package)).Distinct().Count() == index.Entries.Length
                ? index : null;
        }
        catch (JsonException) { return null; }
    }

    public async Task<NCMetaInstallation[]> ReadInstallationsAsync(string hostId, string owner)
    {
        var index = await ReadInstallationIndexAsync(hostId, owner);
        if (index == null) return [];
        if (index.Revision == 0)
        {
            var legacy = await ReadInstallationRecordAsync(hostId, hostId, owner);
            return legacy == null ? [] : [legacy];
        }
        var result = new List<NCMetaInstallation>(index.Entries.Length);
        foreach (var reference in index.Entries)
        {
            var installation = await ReadInstallationRecordAsync(reference.Id.ToString("D"), hostId, owner);
            if (installation == null || installation.Id != reference.Id || installation.PackageIdentity != reference.Package ||
                installation.SlotKind != reference.Slot) return [];
            result.Add(installation);
        }
        return result.ToArray();
    }

    public async Task<NCMetaInstallation?> ReadInstallationAsync(string hostId, string owner, Guid installationId) =>
        (await ReadInstallationsAsync(hostId, owner)).FirstOrDefault(value => value.Id == installationId);

    private async Task<string?> InstallationKeyAsync(NCMetaInstallation installation)
    {
        var index = await ReadInstallationIndexAsync(installation.HostDeviceId, installation.Owner);
        return index == null ? null : index.Revision == 0 ? installation.HostDeviceId :
            index.Entries.Any(entry => entry.Id == installation.Id) ? installation.Id.ToString("D") : null;
    }

    private async Task<PreparedInstallation?> PrepareInstallationAsync(Guid requestId, string hostId, string owner,
        Guid projectId, NCMetaRelease release, NCMetaBinding[] bindings, bool autoStart,
        int storageBytes, int maximumInstallations, string slotKind = "User", long? memoryLimitBytes = null,
        Guid? requiredInheritedInstallationId = null, bool persistStateBlobs = true,
        Guid? expectedInstallationId = null, long? expectedInstallationRevision = null)
    {
        if (maximumInstallations is < 1 or > MaximumInstallations || storageBytes is < 1 or > 256 * 1024 * 1024 ||
            slotKind is not ("User" or "Firmware") || !ValidRelease(release)) return null;
        var bytes = await _database.ReadNCMetaBlobAsync(release.Digest);
        if (bytes == null || bytes.Length > 4 * 1024 * 1024) return null;
        MetaModule module;
        try { module = MetaBytecodeLoader.Load(bytes); }
        catch (MetaFormatException) { return null; }
        var package = module.Manifest.PackageName;
        if (package.Length is < 1 or > 192) return null;
        var stateSchemaVersion = await ReadStateSchemaVersionAsync(release);
        var statePackage = package;
        if (slotKind == "Firmware") package = "firmware";
        var index = await ReadInstallationIndexAsync(hostId, owner);
        if (index == null) return null;
        var installed = await ReadInstallationsAsync(hostId, owner);
        // A corrupt or missing indexed member must never be overwritten as an empty device.
        if (index.Revision > 0 && installed.Length != index.Entries.Length) return null;
        var old = installed.FirstOrDefault(value => value.PackageIdentity == package && value.SlotKind == slotKind);
        if (index.Revision == 0 && installed.Length == 1 && installed[0].PackageIdentity.Length == 0 && installed[0].ProjectId == projectId)
            old = installed[0];
        if (expectedInstallationId is { } expectedId && (old?.Id ?? Guid.Empty) != expectedId ||
            expectedInstallationRevision is { } expectedRevision && (old?.Revision ?? 0) != expectedRevision)
            throw new InstallationRevisionException();
        if (old == null && installed.Length >= maximumInstallations) return null;
        var stageSnapshot = old == null ? null : await _snapshots.ReadCurrentAsync("meta.installation-stage", old.Id.ToString("D"));
        InstallationStage? previousStage = null;
        if (stageSnapshot != null)
        {
            try { previousStage = JsonSerializer.Deserialize<InstallationStage>(stageSnapshot.Payload, Json); }
            catch (JsonException) { return null; }
            if (previousStage == null || previousStage.Owner != owner || previousStage.Revision != stageSnapshot.AggregateVersion)
                return null;
        }
        var activeHealthy = old != null && previousStage is { Phase: "Healthy" or "Recovered" } &&
            previousStage.Candidate.Active.Digest == old.Active.Digest && previousStage.Candidate.StateVolumeId == old.StateVolumeId;
        // Installing several candidates without booting them must not evict the last proven code/state pair.
        var recovery = activeHealthy ? old!.Active : old?.Recovery;
        var recoveryStateId = activeHealthy ? old!.StateVolumeId : old?.RecoveryStateVolumeId ?? Guid.Empty;
        var recoveryBindings = activeHealthy ? old!.Bindings : old?.RecoveryBindings;
        var recoveryState = recoveryStateId == Guid.Empty ? null : await ReadVolumeAsync(recoveryStateId, owner);
        if (recovery != null && (recoveryStateId == Guid.Empty || recoveryState == null)) return null;
        var copied = await ReadReservedSoftwareCopyBytesAsync(hostId, owner);
        var inherited = await ReadInheritedImagesRecordAsync(hostId, owner);
        if (copied == null || inherited == null) return null;
        if (requiredInheritedInstallationId is { } inheritedId && !inherited.Entries.Any(image => image.InstallationId == inheritedId &&
                image.ReleaseId == release.Id && image.Digest == release.Digest && image.Package == package && image.Slot == slotKind)) return null;
        var retainedImages = inherited.Entries.Where(image => image.Package != package || image.Slot != slotKind).ToArray();
        long used = checked(bytes.Length + (release.Resources ?? []).Sum(resource => (long) resource.Bytes) + copied.Value +
            retainedImages.Sum(image => image.Bytes));
        foreach (var previous in installed.Where(value => value != old).SelectMany(value =>
                     value.Recovery == null ? new[] { value.Active } : new[] { value.Active, value.Recovery }))
        {
            var image = await _database.ReadNCMetaBlobAsync(previous.Digest);
            if (image == null) return null;
            used = checked(used + image.Length + (previous.Resources ?? []).Sum(resource => (long) resource.Bytes));
        }
        if (recovery != null)
        {
            var recoveryBytes = await _database.ReadNCMetaBlobAsync(recovery.Digest);
            if (recoveryBytes == null) return null;
            used = checked(used + recoveryBytes.Length + (recovery.Resources ?? []).Sum(resource => (long) resource.Bytes));
        }
        // State capacities are reserved, rather than allowing every package to
        // independently consume the full physical disk after installation.
        foreach (var volumeId in installed.Where(value => value != old).SelectMany(value =>
                     new[] { value.StateVolumeId, value.RecoveryStateVolumeId }).Where(value => value != Guid.Empty).Distinct())
        {
            var reserved = await ReadVolumeAsync(volumeId, owner);
            if (reserved == null) return null;
            used = checked(used + reserved.CapacityBytes);
        }
        if (used > storageBytes) return null;
        var id = old?.Id ?? StableGuid("installation:" + hostId + ":" + slotKind + ":" + package + ":" + requestId);
        var stateId = StableGuid("meta-state:" + id + ":" + requestId);
        var oldState = old is { StateVolumeId: var state } && state != Guid.Empty
            ? await ReadVolumeAsync(state, owner) : null;
        if (old is { StateVolumeId: var oldVolume } && oldVolume != Guid.Empty && oldState == null) return null;
        used += recoveryState?.CapacityBytes ?? 0;
        var stateCapacity = (int) Math.Min(oldState?.CapacityBytes ?? 256 * 1024, storageBytes - used);
        if (stateCapacity < 0) return null;
        var oldStatePackage = statePackage;
        if (oldState != null)
        {
            var oldImage = await _database.ReadNCMetaBlobAsync(old!.Active.Digest);
            if (oldImage == null) return null;
            try { oldStatePackage = MetaBytecodeLoader.Load(oldImage).Manifest.PackageName; }
            catch (MetaFormatException) { return null; }
        }
        var files = oldState == null ? [] : await MigrateStateAsync(oldStatePackage, statePackage, oldState,
            old!.StateSchemaVersion, stateSchemaVersion, stateCapacity, persistStateBlobs);
        var volume = new NCMetaVolume(stateId, owner, stateCapacity, files, 1);
        var installation = new NCMetaInstallation(id, hostId, owner, projectId, release, recovery,
            bindings.ToArray(), index.Revision == 0 ? 1 : checked((old?.Revision ?? 0) + 1), autoStart, package,
            memoryLimitBytes ?? old?.MemoryLimitBytes ?? 0, slotKind, stateId, stateSchemaVersion, recoveryStateId, recoveryBindings,
            activeHealthy ? old!.StateSchemaVersion : old?.RecoveryStateSchemaVersion ?? 1);
        var entries = installed.Where(value => value != old).Select(value =>
                new InstallationReference(value.Id, value.PackageIdentity, value.SlotKind))
            .Append(new(id, package, slotKind)).OrderBy(value => value.Id).ToArray();
        // Legacy installations are copied into their exact ID keys in the same migration transaction.
        if (entries.Any(entry => entry.Package.Length == 0)) return null;
        var updatedIndex = new InstallationIndex(hostId, owner, checked(index.Revision + 1), entries);
        var writes = new List<NCPersistentAggregateWrite>
        {
            new(Guid.NewGuid(), "meta.installation.activated", InstallationType, id.ToString("D"),
                index.Revision == 0 ? 0 : old?.Revision ?? 0, 1, JsonSerializer.Serialize(installation, Json), "{}"),
            new(Guid.NewGuid(), "meta.installation.indexed", InstallationIndexType, hostId, index.Revision, 1,
                JsonSerializer.Serialize(updatedIndex, Json), "{}"),
            new(Guid.NewGuid(), "meta.state.staged", "meta.volume", stateId.ToString("D"), 0, 1,
                JsonSerializer.Serialize(volume, Json), "{}"),
        };
        if (inherited.Revision > 0)
            writes.Add(new(Guid.NewGuid(), "meta.images.install-consented", InheritedImagesType, hostId, inherited.Revision, 1,
                JsonSerializer.Serialize(inherited with { Entries = retainedImages, Revision = checked(inherited.Revision + 1) }, Json), "{}"));
        if (oldState != null)
            writes.Add(new(Guid.NewGuid(), "meta.state.upgrade-attested", "meta.volume", oldState.Id.ToString("D"), oldState.Revision, 1,
                JsonSerializer.Serialize(oldState with { Revision = checked(oldState.Revision + 1) }, Json), "{}"));
        if (recoveryState != null && recoveryState.Id != oldState?.Id)
            writes.Add(new(Guid.NewGuid(), "meta.state.recovery-attested", "meta.volume", recoveryState.Id.ToString("D"), recoveryState.Revision, 1,
                JsonSerializer.Serialize(recoveryState with { Revision = checked(recoveryState.Revision + 1) }, Json), "{}"));
        foreach (var legacy in index.Revision == 0 ? installed.Where(value => value != old) : [])
            writes.Add(new(Guid.NewGuid(), "meta.installation.migrated", InstallationType, legacy.Id.ToString("D"),
                0, 1, JsonSerializer.Serialize(legacy with { Revision = 1 }, Json), "{}"));
        stageSnapshot ??= await _snapshots.ReadCurrentAsync("meta.installation-stage", id.ToString("D"));
        var stage = new InstallationStage(requestId, owner, installation, "HealthCheck", checked((stageSnapshot?.AggregateVersion ?? 0) + 1));
        writes.Add(new(Guid.NewGuid(), "meta.installation.health-pending", "meta.installation-stage", id.ToString("D"),
            stageSnapshot?.AggregateVersion ?? 0, 1, JsonSerializer.Serialize(stage, Json), "{}"));
        return new(installation, writes);
    }

    /// <summary>Uses the exact install quota calculation without writing a stage, state volume or command receipt.</summary>
    public async Task<bool> CanStageInstallationAsync(string hostId, string owner, Guid projectId, NCMetaRelease release,
        int storageBytes, string slotKind)
    {
        try { return await PrepareInstallationAsync(Guid.NewGuid(), hostId, owner, projectId,
            release, [], false, storageBytes, MaximumInstallations, slotKind, persistStateBlobs: false) != null; }
        catch (StateMigrationRequiredException) { return false; }
    }

    public async Task<bool> CompleteInstallationHealthAsync(NCMetaInstallation installation, bool healthy,
        NCSessionEpoch epoch, NCActiveWorldTime time, bool allowRecovery = true)
    {
        using var lease = TryBeginMutation();
        if (lease == null) return false;
        var current = await ReadInstallationAsync(installation.HostDeviceId, installation.Owner, installation.Id);
        if (current == null || current.Active.Digest != installation.Active.Digest || current.StateVolumeId != installation.StateVolumeId) return false;
        var stageSnapshot = await _snapshots.ReadCurrentAsync("meta.installation-stage", installation.Id.ToString("D"));
        if (stageSnapshot == null) return false;
        InstallationStage? stage;
        try { stage = JsonSerializer.Deserialize<InstallationStage>(stageSnapshot.Payload, Json); }
        catch (JsonException) { return false; }
        if (stage == null || stage.Phase != "HealthCheck" || stage.Candidate.Active.Digest != current.Active.Digest) return false;
        var writes = new List<NCPersistentAggregateWrite>();
        var completed = stage with { Phase = healthy ? "Healthy" : "Failed", Revision = checked(stageSnapshot.AggregateVersion + 1) };
        if (!healthy && allowRecovery && current.Recovery != null && current.RecoveryStateVolumeId != Guid.Empty)
        {
            var restored = current with { Active = current.Recovery, Recovery = null,
                StateVolumeId = current.RecoveryStateVolumeId, RecoveryStateVolumeId = Guid.Empty,
                StateSchemaVersion = current.RecoveryStateSchemaVersion, RecoveryStateSchemaVersion = 1,
                Bindings = current.RecoveryBindings ?? [], RecoveryBindings = null,
                Revision = checked(current.Revision + 1), AutoStart = current.SlotKind == "Firmware" && current.AutoStart };
            writes.Add(new(Guid.NewGuid(), "meta.installation.recovered", InstallationType, current.Id.ToString("D"),
                current.Revision, 1, JsonSerializer.Serialize(restored, Json), "{}"));
            completed = completed with { Candidate = restored, Phase = "Recovered" };
        }
        writes.Add(new(Guid.NewGuid(), "meta.installation.health-completed", "meta.installation-stage", installation.Id.ToString("D"),
            stageSnapshot.AggregateVersion, 1, JsonSerializer.Serialize(completed, Json), "{}"));
        var result = await _database.ApplyNCPersistentBatchMutationAsync(new(StableGuid("meta-health:" + stage.Id),
            "meta.installation.health", "meta.owner", installation.Owner, writes, "{}", stage.Id, null, epoch, time, [],
            RequestFingerprint: Fingerprint(installation.Id.ToString("D"), installation.Active.Digest, healthy.ToString())));
        return result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted;
    }

    public async Task<NCMetaMutationOutcome> RollbackInstallationAsync(Guid requestId, NCMetaInstallation installation,
        NCSessionEpoch epoch, NCActiveWorldTime time, NCMetaAuthorityConstraints? authority = null)
    {
        using var lease = TryBeginMutation();
        if (lease == null) return new(false, "nc-meta-error-busy");
        if (installation.Recovery == null || installation.RecoveryStateVolumeId == Guid.Empty ||
            await ReadVolumeAsync(installation.RecoveryStateVolumeId, installation.Owner) == null)
            return new(false, "nc-meta-error-artifact");
        var key = await InstallationKeyAsync(installation);
        if (key == null) return new(false, "nc-meta-error-access");
        var stageSnapshot = await _snapshots.ReadCurrentAsync("meta.installation-stage", installation.Id.ToString("D"));
        InstallationStage? stage = null;
        if (stageSnapshot != null)
        {
            try { stage = JsonSerializer.Deserialize<InstallationStage>(stageSnapshot.Payload, Json); }
            catch (JsonException) { return new(false, "nc-meta-error-storage"); }
            if (stage == null || stage.Owner != installation.Owner || stage.Revision != stageSnapshot.AggregateVersion)
                return new(false, "nc-meta-error-storage");
        }
        var activeHealthy = stage is { Phase: "Healthy" or "Recovered" } &&
            stage.Candidate.Active.Digest == installation.Active.Digest && stage.Candidate.StateVolumeId == installation.StateVolumeId;
        var updated = installation with { Active = installation.Recovery, Recovery = activeHealthy ? installation.Active : null,
            StateVolumeId = installation.RecoveryStateVolumeId, RecoveryStateVolumeId = activeHealthy ? installation.StateVolumeId : Guid.Empty,
            StateSchemaVersion = installation.RecoveryStateSchemaVersion,
            RecoveryStateSchemaVersion = activeHealthy ? installation.StateSchemaVersion : 1,
            Bindings = installation.RecoveryBindings ?? [], RecoveryBindings = activeHealthy ? installation.Bindings : null,
            AutoStart = false, Revision = checked(installation.Revision + 1) };
        var outcome = new NCMetaMutationOutcome(true, "nc-meta-installed", updated.Revision);
        var recovered = new InstallationStage(requestId, installation.Owner, updated, "Recovered", checked((stageSnapshot?.AggregateVersion ?? 0) + 1));
        var result = await _database.ApplyNCPersistentBatchMutationAsync(new(requestId, "meta.installation.rollback",
            "meta.owner", installation.Owner,
            [new(Guid.NewGuid(), "meta.installation.rolled-back", InstallationType, key, installation.Revision, 1,
                JsonSerializer.Serialize(updated, Json), "{}"),
             new(Guid.NewGuid(), "meta.installation.rollback-validated", "meta.installation-stage", installation.Id.ToString("D"),
                stageSnapshot?.AggregateVersion ?? 0, 1, JsonSerializer.Serialize(recovered, Json), "{}")], JsonSerializer.Serialize(outcome, Json),
            requestId, null, epoch, time, [], RequestFingerprint: Fingerprint(installation.Owner, installation.Id.ToString("D"),
                installation.Active.Digest, installation.Recovery.Digest, installation.StateVolumeId.ToString("D")), Authority: authority));
        return DecodeOutcome(result);
    }
}
