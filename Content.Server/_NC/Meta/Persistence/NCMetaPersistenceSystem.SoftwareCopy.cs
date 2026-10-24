// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Server.Database;

namespace Content.Server._NC.Meta.Persistence;

public sealed record NCMetaSoftwareCopyPreparation(Guid CopyId, NCPersistentAggregateWrite[] Writes);
public sealed record NCMetaSoftwareStorageUsage(long Bytes, long Revision, Dictionary<string, long> UpgradeStateBytes)
{
    public Dictionary<string, long> InheritedImageBytes { get; init; } = [];
}

public sealed partial class NCMetaPersistenceSystem
{
    private const string SoftwareCopiesType = "meta.software-copy-index";
    private sealed record SoftwareCopyReservation(Guid Id, long Bytes);
    private sealed record SoftwareCopies(string DeviceId, string Owner, long Revision, SoftwareCopyReservation[] Entries);

    private async Task<SoftwareCopies?> ReadSoftwareCopiesAsync(string hostId, string owner)
    {
        var snapshot = await _snapshots.ReadCurrentAsync(SoftwareCopiesType, hostId);
        if (snapshot == null) return new(hostId, owner, 0, []);
        if (snapshot.Payload.Length > 8192 || snapshot.FormatVersion != 1) return null;
        try
        {
            var value = JsonSerializer.Deserialize<SoftwareCopies>(snapshot.Payload, Json);
            return value != null && value.DeviceId == hostId && value.Owner == owner && value.Revision == snapshot.AggregateVersion &&
                value.Entries is { Length: <= 8 } && value.Entries.All(entry => entry != null && entry.Id != Guid.Empty && entry.Bytes is > 0 and <= 33554432) &&
                value.Entries.Select(entry => entry.Id).Distinct().Count() == value.Entries.Length ? value : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Logical bytes remain charged even though immutable blobs are deduplicated. Null means invalid storage metadata.</summary>
    public async Task<long?> ReadReservedSoftwareCopyBytesAsync(string hostId, string owner)
    {
        var copies = await ReadSoftwareCopiesAsync(hostId, owner);
        return copies == null ? null : copies.Entries.Sum(value => value.Bytes);
    }
    public async Task<bool> HasReservedSoftwareCopyAsync(string hostId, string owner, Guid copyId, long bytes)
    {
        var copies = await ReadSoftwareCopiesAsync(hostId, owner);
        return copies?.Entries.Any(value => value.Id == copyId && value.Bytes == bytes) == true;
    }

    public async Task<NCMetaSoftwareStorageUsage?> ReadSoftwareStorageUsageAsync(string hostId, string owner, string slotKind = "User")
    {
        if (slotKind is not ("User" or "Firmware")) return null;
        var index = await ReadInstallationIndexAsync(hostId, owner);
        if (index == null) return null;
        var installed = await ReadInstallationsAsync(hostId, owner);
        if (index.Revision > 0 && installed.Length != index.Entries.Length) return null;
        var copies = await ReadReservedSoftwareCopyBytesAsync(hostId, owner);
        var inherited = await ReadInheritedImagesRecordAsync(hostId, owner);
        if (copies == null || inherited == null) return null;
        long used = checked(copies.Value + inherited.Entries.Sum(value => value.Bytes));
        foreach (var image in installed.SelectMany(value => value.Recovery == null ? new[] { value.Active } : new[] { value.Active, value.Recovery }))
        {
            var bytes = await _database.ReadNCMetaBlobAsync(image.Digest);
            if (bytes == null || bytes.Length > 4 * 1024 * 1024) return null;
            used = checked(used + bytes.Length + (image.Resources ?? []).Sum(value => (long) value.Bytes));
        }
        var capacities = new Dictionary<Guid, long>();
        foreach (var id in installed.SelectMany(value => new[] { value.StateVolumeId, value.RecoveryStateVolumeId }).Where(value => value != Guid.Empty).Distinct())
        {
            var volume = await ReadVolumeAsync(id, owner);
            if (volume == null) return null;
            capacities[id] = volume.CapacityBytes; used = checked(used + volume.CapacityBytes);
        }
        var final = await ReadInstallationIndexAsync(hostId, owner);
        var finalInherited = await ReadInheritedImagesRecordAsync(hostId, owner);
        if (final == null || final.Revision != index.Revision || finalInherited?.Revision != inherited.Revision) return null;
        var upgrade = installed.Where(value => value.SlotKind == slotKind && value.PackageIdentity.Length > 0)
            .ToDictionary(value => value.PackageIdentity, value => capacities.GetValueOrDefault(value.StateVolumeId), StringComparer.Ordinal);
        return new(used, index.Revision, upgrade)
        { InheritedImageBytes = inherited.Entries.Where(value => value.Slot == slotKind).ToDictionary(value => value.Package, value => value.Bytes, StringComparer.Ordinal) };
    }

    public async Task<NCPersistentAggregateWrite?> PrepareSoftwareStorageAttestationAsync(string hostId, string owner, long expectedRevision)
    {
        var index = await ReadInstallationIndexAsync(hostId, owner);
        // An occupied legacy singleton must migrate through the normal installer before a quota-index attestation.
        if (index == null || index.Revision != expectedRevision || index.Revision == 0 && (await ReadInstallationsAsync(hostId, owner)).Length > 0) return null;
        return new(Guid.NewGuid(), "meta.installation.purchase-admitted", InstallationIndexType, hostId, index.Revision, 1,
            JsonSerializer.Serialize(index with { Revision = checked(index.Revision + 1) }, Json), "{}");
    }

    /// <summary>Append these writes to the evidence/license transaction; never commit the reservation separately.</summary>
    public async Task<NCMetaSoftwareCopyPreparation?> PrepareSoftwareCopyAsync(string targetId, string targetOwner,
        long copyBytes, NCMetaInstallation sourceInstallation, Guid copyId, int capacityBytes)
    {
        if (copyId == Guid.Empty || copyBytes is < 1 or > 33554432 || capacityBytes is < 1 or > 268435456) return null;
        var copies = await ReadSoftwareCopiesAsync(targetId, targetOwner);
        var inherited = await ReadReservedInheritedBytesAsync(targetId, targetOwner);
        var targetIndex = await ReadInstallationIndexAsync(targetId, targetOwner);
        var targetInstalled = await ReadInstallationsAsync(targetId, targetOwner);
        if (copies == null || inherited == null || targetIndex == null || copies.Entries.Length >= 8 || copies.Entries.Any(value => value.Id == copyId) ||
            targetIndex.Revision > 0 && targetInstalled.Length != targetIndex.Entries.Length ||
            targetIndex.Revision == 0 && targetInstalled.Length > 0) return null;
        long used = checked(copyBytes + copies.Entries.Sum(value => value.Bytes) + inherited.Value);
        foreach (var image in targetInstalled.SelectMany(value => value.Recovery == null ? new[] { value.Active } : new[] { value.Active, value.Recovery }))
        {
            var bytes = await _database.ReadNCMetaBlobAsync(image.Digest);
            if (bytes == null || bytes.Length > 4 * 1024 * 1024) return null;
            used = checked(used + bytes.Length + (image.Resources ?? []).Sum(value => (long) value.Bytes));
        }
        foreach (var volumeId in targetInstalled.SelectMany(value => new[] { value.StateVolumeId, value.RecoveryStateVolumeId })
                     .Where(value => value != Guid.Empty).Distinct())
        {
            var volume = await ReadVolumeAsync(volumeId, targetOwner);
            if (volume == null) return null;
            used = checked(used + volume.CapacityBytes);
        }
        if (used > capacityBytes) return null;
        var source = await ReadInstallationAsync(sourceInstallation.HostDeviceId, sourceInstallation.Owner, sourceInstallation.Id);
        if (source == null || source.Revision != sourceInstallation.Revision || source.Active.Digest != sourceInstallation.Active.Digest ||
            source.Active.Id != sourceInstallation.Active.Id) return null;
        var sourceKey = await InstallationKeyAsync(source);
        if (sourceKey == null) return null;
        var updated = copies with { Entries = [.. copies.Entries, new(copyId, copyBytes)], Revision = checked(copies.Revision + 1) };
        var writes = new NCPersistentAggregateWrite[]
        {
            new(Guid.NewGuid(), "meta.software-copy.reserved", SoftwareCopiesType, targetId, copies.Revision, 1, JsonSerializer.Serialize(updated, Json), "{}"),
            new(Guid.NewGuid(), "meta.installation.copy-admitted", InstallationIndexType, targetId, targetIndex.Revision, 1,
                JsonSerializer.Serialize(targetIndex with { Revision = checked(targetIndex.Revision + 1) }, Json), "{}"),
            new(Guid.NewGuid(), "meta.installation.software-copied", InstallationType, sourceKey, source.Revision, 1,
                JsonSerializer.Serialize(source with { Revision = checked(source.Revision + 1) }, Json), "{}"),
        };
        return new(copyId, writes);
    }

    public async Task<NCPersistentAggregateWrite[]?> PrepareDiscardSoftwareCopyAsync(string hostId, string owner, Guid copyId)
    {
        var copies = await ReadSoftwareCopiesAsync(hostId, owner);
        var index = await ReadInstallationIndexAsync(hostId, owner);
        if (copies == null || index == null || !copies.Entries.Any(value => value.Id == copyId)) return null;
        return [new(Guid.NewGuid(), "meta.software-copy.discarded", SoftwareCopiesType, hostId, copies.Revision, 1,
                JsonSerializer.Serialize(copies with { Entries = copies.Entries.Where(value => value.Id != copyId).ToArray(), Revision = checked(copies.Revision + 1) }, Json), "{}"),
            new(Guid.NewGuid(), "meta.installation.copy-released", InstallationIndexType, hostId, index.Revision, 1,
                JsonSerializer.Serialize(index with { Revision = checked(index.Revision + 1) }, Json), "{}")];
    }

    public async Task<NCPersistentAggregateWrite?> PrepareSoftwareCopyOwnerTransferAsync(string hostId, string oldOwner, string newOwner)
    {
        var copies = await ReadSoftwareCopiesAsync(hostId, oldOwner);
        return copies == null ? null : copies.Revision == 0 ? null : new(Guid.NewGuid(), "meta.software-copy.owner-reset", SoftwareCopiesType,
            hostId, copies.Revision, 1, JsonSerializer.Serialize(copies with { Owner = newOwner, Entries = [], Revision = checked(copies.Revision + 1) }, Json), "{}");
    }
}
