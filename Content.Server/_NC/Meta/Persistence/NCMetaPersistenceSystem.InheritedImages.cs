// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Commerce;
using Content.Server.Database;

namespace Content.Server._NC.Meta.Persistence;

/// <summary>Physical immutable image retained after custody changes; it carries no source, state or execution grants.</summary>
public sealed record NCMetaInheritedImage(Guid InstallationId, Guid ReleaseId, string Digest, string Package, string Slot, long Bytes);

public sealed partial class NCMetaPersistenceSystem
{
    private const string InheritedImagesType = "meta.inherited-images";
    private sealed record InheritedImages(string DeviceId, string Owner, long Revision, NCMetaInheritedImage[] Entries);

    private async Task<InheritedImages?> ReadInheritedImagesRecordAsync(string deviceId, string owner)
    {
        var row = await _snapshots.ReadCurrentAsync(InheritedImagesType, deviceId);
        if (row == null) return await _database.GetNCAggregateSnapshotAsync(InheritedImagesType, deviceId) == null
            ? new(deviceId, owner, 0, []) : null;
        if (row.Payload.Length > 32768 || row.FormatVersion != 1) return null;
        try
        {
            var images = JsonSerializer.Deserialize<InheritedImages>(row.Payload, Json);
            return images != null && images.DeviceId == deviceId && images.Owner == owner && images.Revision == row.AggregateVersion &&
                images.Entries is { Length: <= MaximumInstallations } && images.Entries.All(image => image != null &&
                    image.InstallationId != Guid.Empty && image.ReleaseId != Guid.Empty && MetaSoftwarePolicy.Digest(image.Digest) &&
                    image is { Package.Length: > 0 and <= 192, Slot: "User" or "Firmware", Bytes: > 0 and <= 33554432 }) &&
                images.Entries.Select(image => image.InstallationId).Distinct().Count() == images.Entries.Length &&
                images.Entries.Select(image => (image.Package, image.Slot)).Distinct().Count() == images.Entries.Length &&
                images.Entries.Sum(image => image.Bytes) <= 256 * 1024 * 1024 ? images : null;
        }
        catch (JsonException) { return null; }
    }

    public async Task<NCMetaInheritedImage[]?> ReadInheritedImagesAsync(string deviceId, string owner) =>
        (await ReadInheritedImagesRecordAsync(deviceId, owner))?.Entries.ToArray();

    public async Task<long?> ReadReservedInheritedBytesAsync(string deviceId, string owner) =>
        (await ReadInheritedImagesRecordAsync(deviceId, owner))?.Entries.Sum(image => image.Bytes);

    public async Task<bool> HasInheritedImageAsync(string deviceId, string owner, Guid installationId, Guid releaseId, string digest) =>
        (await ReadInheritedImagesRecordAsync(deviceId, owner))?.Entries.Any(image => image.InstallationId == installationId &&
            image.ReleaseId == releaseId && image.Digest == digest) == true;

    private async Task<NCPersistentAggregateWrite?> PrepareInheritedOwnerTransferAsync(string deviceId, string oldOwner,
        string newOwner, Guid[] preserved)
    {
        var previous = await ReadInheritedImagesRecordAsync(deviceId, oldOwner);
        var index = await ReadInstallationIndexAsync(deviceId, oldOwner);
        if (previous == null || index == null || preserved.Length > MaximumInstallations ||
            preserved.Any(id => id == Guid.Empty) || preserved.Distinct().Count() != preserved.Length) return null;
        var installed = await ReadInstallationsAsync(deviceId, oldOwner);
        if (index.Revision > 0 && index.Entries.Length != installed.Length) return null;
        var images = new List<NCMetaInheritedImage>(preserved.Length);
        foreach (var id in preserved)
        {
            if (previous.Entries.FirstOrDefault(image => image.InstallationId == id) is { } dormant)
            { images.Add(dormant); continue; }
            var installation = installed.FirstOrDefault(value => value.Id == id);
            if (installation?.Active.CommercialOrigin == null) return null;
            var bytes = await _database.ReadNCMetaBlobAsync(installation.Active.Digest);
            if (bytes == null || installation.PackageIdentity.Length == 0) return null;
            images.Add(new(id, installation.Active.Id, installation.Active.Digest, installation.PackageIdentity,
                installation.SlotKind, checked(bytes.LongLength + (installation.Active.Resources ?? []).Sum(resource => (long) resource.Bytes))));
        }
        if (images.Select(image => (image.Package, image.Slot)).Distinct().Count() != images.Count ||
            images.Sum(image => image.Bytes) > 256 * 1024 * 1024) return null;
        var updated = new InheritedImages(deviceId, newOwner, checked(previous.Revision + 1), images.OrderBy(image => image.InstallationId).ToArray());
        return new(Guid.NewGuid(), "meta.images.custody-transferred", InheritedImagesType, deviceId, previous.Revision, 1,
            JsonSerializer.Serialize(updated, Json), "{}");
    }
}
