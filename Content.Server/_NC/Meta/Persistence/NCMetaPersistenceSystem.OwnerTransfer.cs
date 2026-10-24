// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Server.Database;

namespace Content.Server._NC.Meta.Persistence;

public sealed partial class NCMetaPersistenceSystem
{
    /// <summary>
    /// Joins the physical asset/license transaction. Old private source/state stays owned by the previous party;
    /// the recipient gets no unattended execution, device bindings or implicit permission consent.
    /// </summary>
    public async Task<NCPersistentAggregateWrite[]?> PrepareInstallationOwnerTransferAsync(string hostId, string oldOwner, string newOwner,
        Guid[]? preserveInstallationIds = null)
    {
        var index = await ReadInstallationIndexAsync(hostId, oldOwner);
        var copied = await ReadReservedSoftwareCopyBytesAsync(hostId, oldOwner);
        if (index == null || copied == null || !Content.Shared._NC.Persistence.Identity.NCPartyRef.TryParse(newOwner, out _)) return null;
        var inherited = await PrepareInheritedOwnerTransferAsync(hostId, oldOwner, newOwner, preserveInstallationIds ?? []);
        if (inherited == null) return null;
        var updated = new InstallationIndex(hostId, newOwner, checked(index.Revision + 1), []);
        var writes = new List<NCPersistentAggregateWrite>
        {
            inherited,
            new(Guid.NewGuid(), "meta.installation.owner-reset", InstallationIndexType, hostId, index.Revision, 1,
                JsonSerializer.Serialize(updated, Json), "{}"),
        };
        var copyReset = await PrepareSoftwareCopyOwnerTransferAsync(hostId, oldOwner, newOwner);
        if (copyReset != null) writes.Add(copyReset);
        var externalVolumeId = StableGuid("volume:" + hostId);
        var external = await ReadVolumeAsync(externalVolumeId, oldOwner);
        if (external != null)
            writes.Add(new(Guid.NewGuid(), "meta.volume.owner-reset", "meta.volume", externalVolumeId.ToString("D"),
                external.Revision, 1, JsonSerializer.Serialize(external with { Owner = newOwner, Files = [],
                    Revision = checked(external.Revision + 1) }, Json), "{}"));
        return writes.ToArray();
    }
}
