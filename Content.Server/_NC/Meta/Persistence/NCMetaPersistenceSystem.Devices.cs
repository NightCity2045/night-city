// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Security.Cryptography;
using System.Text.Json;
using Content.Server.Database;
using Content.Shared._NC.Persistence.Time;
using Content.Shared._NC.Persistence.Identity;
using Content.Server._NC.LivingCity.Economy;

namespace Content.Server._NC.Meta.Persistence;

public sealed partial class NCMetaPersistenceSystem
{
    [Dependency] private NCEconomicAssetRegistrySystem _assets = default!;
    public async Task<NCMetaDeviceRecord?> ReadDeviceAsync(string deviceId)
    {
        var snapshot = await _snapshots.ReadCurrentAsync("meta.device", deviceId);
        if (snapshot == null || snapshot.Payload.Length > 16384)
            return null;
        try
        {
            var record = JsonSerializer.Deserialize<NCMetaDeviceRecord>(snapshot.Payload, Json);
            return record is { Owner.Length: > 0 and <= 128, Projects.Length: <= 64 } && record.DeviceId == deviceId &&
                   Content.Shared._NC.Persistence.Identity.NCPartyRef.TryParse(record.Owner, out _) &&
                   record.Projects.All(id => id != Guid.Empty) && record.Projects.Distinct().Count() == record.Projects.Length &&
                   (record.Projects.Length == 0 ? record.ActiveProject == Guid.Empty : record.Projects.Contains(record.ActiveProject)) &&
                   record.Revision == snapshot.AggregateVersion &&
                   record.AuthorityEpoch is >= 2 and < ulong.MaxValue ? record : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A transferred device starts with a new private workspace, never the previous owner's source.</summary>
    public async Task<NCMetaDeviceRecord?> EnsureWorkspaceAsync(Guid requestId, NCMetaDeviceRecord device,
        string name, NCSessionEpoch epoch, NCActiveWorldTime time, NCMetaAuthorityConstraints? authority = null)
    {
        if (device.Projects.Length != 0) return device;
        using var lease = TryBeginMutation();
        if (lease == null) return null;
        var projectId = StableGuid("workspace:" + device.DeviceId + ":" + device.Owner + ":" + device.AuthorityEpoch);
        var source = Utf8.GetBytes("module program.main;\nimport meta.system;\nfn main() -> void { system.log(\"META\"); }\n");
        var digest = await _database.StoreNCMetaBlobAsync(source);
        var project = new NCMetaProject(projectId, device.Owner, name, 1,
            [new(StableGuid("file:" + projectId), "src/main.meta", 1, digest, source.Length)], []);
        var updated = device with { Projects = [projectId], ActiveProject = projectId, Revision = checked(device.Revision + 1) };
        var result = await _database.ApplyNCPersistentBatchMutationAsync(new(StableGuid("workspace-open:" + requestId),
            "meta.device.workspace", "meta.owner", device.Owner,
            [ProjectWrite(project, 0), new(Guid.NewGuid(), "meta.device.workspace-created", "meta.device", device.DeviceId,
                device.Revision, 1, JsonSerializer.Serialize(updated, Json), "{}")], "{}", requestId, null, epoch, time, [],
            RequestFingerprint: Fingerprint(device.DeviceId, device.Owner, device.AuthorityEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Authority: authority));
        return result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted
            ? await ReadDeviceAsync(device.DeviceId) : null;
    }

    public async Task<NCMetaDeviceRecord?> ClaimDeviceAsync(Guid requestId, string deviceId, string owner,
        string projectName, NCSessionEpoch epoch, NCActiveWorldTime time, NCItemId? economicItemId = null)
    {
        using var lease = TryBeginMutation();
        if (lease == null) return null;
        var existing = await ReadDeviceAsync(deviceId);
        if (existing != null && existing.Owner != owner) return null;
        NCPersistentAggregateWrite? assetGuard = null;
        NCPersistentAggregateWrite? identityWrite = null;
        if (economicItemId is { } itemId)
        {
            var mapping = await PrepareEconomicDeviceMappingAsync(itemId, deviceId);
            if (!mapping.Valid) return null;
            identityWrite = mapping.Write;
            var item = await _assets.GetItemAsync(itemId, true);
            if (item == null || item.Snapshot.Owner.ToString() != owner ||
                item.Snapshot.Lifecycle != Content.Shared._NC.LivingCity.Economy.NCEconomicAssetLifecycle.Active) return null;
            var eventId = Guid.NewGuid();
            assetGuard = new NCPersistentAggregateWrite(eventId, "meta.device.ownership-attested",
                NCEconomicAssetRegistrySystem.ItemInstanceAggregateType, itemId.ToString(), item.AggregateVersion,
                1, JsonSerializer.Serialize(item.Snapshot with { LastAppliedEventId = eventId, UpdatedAt = time,
                    DataVersion = checked(item.Snapshot.DataVersion + 1) }, Json), "{}");
        }
        if (existing != null)
        {
            if (identityWrite == null) return existing;
            var backfill = await _database.ApplyNCPersistentBatchMutationAsync(new(StableGuid("economic-device-map:" + requestId),
                "meta.device.map-economic-item", "meta.owner", owner,
                [identityWrite, assetGuard!, new(Guid.NewGuid(), "meta.device.identity-attested", "meta.device", deviceId,
                    existing.Revision, 1, JsonSerializer.Serialize(existing with { Revision = checked(existing.Revision + 1) }, Json), "{}")],
                "{}", requestId, null, epoch, time, [], RequestFingerprint: Fingerprint(deviceId, owner, economicItemId!.Value.ToString())));
            return backfill.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted
                ? await ReadDeviceAsync(deviceId) : null;
        }
        var projectId = StableGuid("project:" + deviceId + ":" + owner);
        var fileId = StableGuid("file:" + projectId.ToString("D"));
        var source = Utf8.GetBytes("module program.main;\nimport meta.system;\n\nfn main() -> void {\n    system.log(\"META\");\n}\n");
        var digest = await _database.StoreNCMetaBlobAsync(source);
        var project = new NCMetaProject(projectId, owner, projectName, 1,
            [new NCMetaProjectFile(fileId, "src/main.meta", 1, digest, source.Length)], []);
        var device = new NCMetaDeviceRecord(deviceId, owner, [projectId], projectId, 1);
        var writes = new List<NCPersistentAggregateWrite>
        {
            new(Guid.NewGuid(), "meta.device.claimed", "meta.device", deviceId,
                0, 1, JsonSerializer.Serialize(device, Json), "{}"),
            new(Guid.NewGuid(), "meta.project.created", ProjectType, projectId.ToString("D"),
                0, 1, JsonSerializer.Serialize(project, Json), "{}"),
        };
        if (assetGuard != null) writes.Add(assetGuard);
        if (identityWrite != null) writes.Add(identityWrite);
        var result = await _database.ApplyNCPersistentBatchMutationAsync(new NCPersistentBatchMutationRequest(
            requestId, "meta.device.claim", "meta.owner", owner,
            writes,
            JsonSerializer.Serialize(device, Json), requestId, null, epoch, time, [],
            RequestFingerprint: Fingerprint(deviceId, owner, economicItemId?.ToString() ?? string.Empty)));
        if (result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted)
            return JsonSerializer.Deserialize<NCMetaDeviceRecord>(result.ResultPayload!, Json);
        // Concurrent claimant: the authoritative record must still belong to this principal.
        existing = await ReadDeviceAsync(deviceId);
        return existing?.Owner == owner && (economicItemId == null || await ReadEconomicDeviceIdAsync(economicItemId.Value) == deviceId)
            ? existing : null;
    }

    public static Guid StableGuid(string key) => new(SHA256.HashData(Utf8.GetBytes(key)).AsSpan(0, 16));
}
