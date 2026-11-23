// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Hosting;
using Content.Server.Database;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Meta.UI;
using Content.Shared.Interaction;

namespace Content.Server._NC.Meta.Projects;

[RegisterComponent]
public sealed partial class NCMetaRemovableMediaComponent : Component
{
    [DataField] public int CapacityBytes = 2 * 1024 * 1024;
    /// <summary>The author's choice: an open chip can be copied by anyone who holds it, a closed one only by its owner.</summary>
    [DataField] public bool Open;
}

public sealed partial class NCMetaWorkspaceSystem
{
    private sealed record MediaPackage(string DeviceId, string Name, NCMetaProjectFile[] Files,
        NCMetaRelease[] Releases, long Revision);
    [Dependency] private SharedInteractionSystem _mediaInteraction = default!;

    [Dependency] private MetaDataSystem _chipMeta = default!;
    [Dependency] private Content.Shared.Hands.EntitySystems.SharedHandsSystem _chipHands = default!;

    /// <summary>Makes a program chip next to the terminal, in the user's hand if it is free, owned by the user.</summary>
    private async Task<string?> CreateChipAsync(Context context, NCMetaChipPayload spec)
    {
        var name = spec.Name.Trim();
        var description = spec.Description.Trim();
        if (name.Length is < 1 or > 40 || description.Length > 200) return null;
        var made = await OnMainAsync(() =>
        {
            if (!IsCurrent(context)) return default((EntityUid Chip, string Id, Content.Shared._NC.Persistence.Identity.NCItemId? Item)?);
            var chip = Spawn("NCMetaProgramChip", Transform(context.Terminal).Coordinates);
            _chipMeta.SetEntityName(chip, name);
            _chipMeta.SetEntityDescription(chip, description);
            if (TryComp<NCMetaRemovableMediaComponent>(chip, out var made)) made.Open = spec.Open;
            _chipHands.TryPickupAnyHand(context.User, chip);
            return _devices.TryGetEntry(chip, out var entry)
                ? (Chip: chip, Id: entry.Id, Item: _devices.GetEconomicItemId(chip))
                : default((EntityUid Chip, string Id, Content.Shared._NC.Persistence.Identity.NCItemId? Item)?);
        });
        if (made is not { } value) return null;
        // A new device becomes ready only after the registry has read its durable record.
        var ready = false;
        for (var attempt = 0; attempt < 30 && !ready; attempt++)
        {
            ready = await OnMainAsync(() => _devices.TryGetEntry(value.Chip, out var current) && current.Ready);
            if (!ready) await Task.Delay(100);
        }
        if (!ready)
        {
            await OnMainAsync(() => { if (Exists(value.Chip)) Del(value.Chip); return true; });
            return null;
        }
        var record = await _persistence.ClaimDeviceAsync(Guid.NewGuid(), value.Id, context.Owner,
            Loc.GetString("nc-meta-default-project"), context.Epoch, context.Time, value.Item);
        var owned = record != null && await OnMainAsync(() => Exists(value.Chip) &&
            _devices.TryApplyOwner(value.Chip, value.Id, string.Empty, context.Owner, record.AuthorityEpoch));
        if (!owned) await OnMainAsync(() => { if (Exists(value.Chip)) Del(value.Chip); return true; });
        return owned ? value.Id : null;
    }

    /// <summary>The owner may do anything with a chip; anyone may read an open one but only the owner may write to it.</summary>
    private bool MediaAccess(Context context, EntityUid media, NCMetaWorkspaceOperation operation) =>
        _devices.CanManage(media, context.User) ||
        operation == NCMetaWorkspaceOperation.ImportMedia && TryComp<NCMetaRemovableMediaComponent>(media, out var component) && component.Open;

    /// <summary>The chip in the terminal's slot, shown to the player so code can be copied to or from it.</summary>
    private NCMetaChipSummary? ReadChip(Context context)
    {
        if (!_chipSlots.TryGetSlot(context.Terminal, NCMetaChipSlot.Id, out var slot) || slot.Item is not { } chip ||
            !_devices.TryGetEntry(chip, out var entry) || !TryComp<NCMetaRemovableMediaComponent>(chip, out var media)) return null;
        var owned = _devices.CanManage(chip, context.User, requireRange: false);
        return owned || media.Open ? new(entry.Id, Name(chip), media.Open, owned) : new(entry.Id, Name(chip), false, false);
    }

    private async Task<NCMetaMutationOutcome> TransferMediaAsync(Context context, NCMetaDeviceRecord device,
        NCMetaProject project, NCMetaWorkspaceRequestMessage request)
    {
        using var lease = _persistence.TryBeginNativeMutation();
        if (lease == null) return new(false, "nc-meta-error-busy");
        var mediaId = request.DeviceId;
        var programName = project.Name;
        if (request.Operation == NCMetaWorkspaceOperation.ExportMedia)
        {
            // A chip is made from the project: the player only names and describes it.
            // The request payload arrives as JSON in Source, like every other typed workspace payload.
            NCMetaChipPayload? spec = null;
            try { spec = JsonSerializer.Deserialize<NCMetaChipPayload>(request.Source); }
            catch (JsonException) { }
            if (spec == null) return new(false, "nc-meta-error-access");
            if (request.DeviceId.Length == 0)
            {
                if (await CreateChipAsync(context, spec) is not { } created) return new(false, "nc-meta-error-access");
                mediaId = created;
                programName = spec.Name.Trim();
            }
            else
            {
                // Writing into a chip that sits in the terminal's slot: its flag follows the author's choice.
                programName = spec.Name.Trim().Length > 0 ? spec.Name.Trim() : programName;
                await OnMainAsync(() =>
                {
                    if (_devices.TryResolve(mediaId, out var existing) && _devices.CanManage(existing.Entity, context.User) &&
                        TryComp<NCMetaRemovableMediaComponent>(existing.Entity, out var flag)) flag.Open = spec.Open;
                    return true;
                });
            }
        }
        var target = await OnMainAsync(() =>
        {
            if (!IsCurrent(context) || !_devices.TryResolve(mediaId, out var media) ||
                !MediaAccess(context, media.Entity, request.Operation) ||
                !_mediaInteraction.InRangeUnobstructed(context.Terminal, media.Entity, range: 2) ||
                !TryComp<NCMetaRemovableMediaComponent>(media.Entity, out var component)) return null;
            return new { media.Id, media.Entity, media.DeviceEpoch, media.AuthorityEpoch, component.CapacityBytes };
        });
        if (target == null || target.CapacityBytes is < 1 or > 16 * 1024 * 1024) return new(false, "nc-meta-error-access");
        // Reading an open chip that belongs to someone else writes only to this terminal, so the terminal carries the authority.
        var owned = await OnMainAsync(() => _devices.CanManage(target.Entity, context.User));
        var authority = await CaptureInstallationAuthorityAsync(context, owned ? target.Entity : context.Terminal);
        if (authority == null) return new(false, "nc-meta-error-access");
        bool Current() => IsCurrent(context) && _devices.TryGetEntry(target.Entity, out var current) &&
            current.Id == target.Id && current.DeviceEpoch == target.DeviceEpoch && current.AuthorityEpoch == target.AuthorityEpoch &&
            MediaAccess(context, target.Entity, request.Operation) &&
            _mediaInteraction.InRangeUnobstructed(context.Terminal, target.Entity, range: 2);
        var stored = await _persistence.ReadSnapshotAsync("meta.media", target.Id);
        if (request.Operation == NCMetaWorkspaceOperation.ExportMedia)
        {
            var releases = project.Releases.TakeLast(1).ToArray();
            var references = project.Files.Select(file => (file.Digest, file.Bytes))
                .Concat(releases.SelectMany(release => (release.Resources ?? []).Concat(release.Sources ?? []))
                    .Select(file => (file.Digest, file.Bytes))).ToArray();
            var bytes = references.DistinctBy(file => file.Digest).Sum(file => (long) file.Bytes);
            foreach (var release in releases)
            {
                var artifact = await _persistence.ReadArtifactAsync(release.Digest);
                if (artifact == null) return new(false, "nc-meta-error-artifact");
                bytes += artifact.Length;
            }
            if (bytes > target.CapacityBytes) return new(false, "nc-meta-error-quota");
            var package = new MediaPackage(target.Id, programName, project.Files, releases, checked((stored?.AggregateVersion ?? 0) + 1));
            var payload = JsonSerializer.Serialize(package);
            if (payload.Length > 256 * 1024 || !await OnMainAsync(Current)) return new(false, "nc-meta-error-access");
            var result = await _contactDatabase.ApplyNCPersistentMutationAsync(new(request.RequestId, "meta.media.export", "meta.owner", context.Owner,
                Guid.NewGuid(), "meta.media.exported", "meta.media", target.Id, stored?.AggregateVersion ?? 0, 1, payload, "{}", "{}",
                request.RequestId, null, context.Epoch, context.Time, [], RequestFingerprint: NCMetaPersistenceSystem.Fingerprint(
                    context.Owner, target.Id, project.Id.ToString("D"), project.Revision.ToString()), Authority: authority));
            var applied = result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted;
            return new(applied, applied ? "nc-meta-media-exported" : "nc-meta-error-revision");
        }
        if (stored == null || stored.Payload.Length > 256 * 1024) return new(false, "nc-meta-error-storage");
        MediaPackage? imported;
        try { imported = JsonSerializer.Deserialize<MediaPackage>(stored.Payload, new JsonSerializerOptions { MaxDepth = 16 }); }
        catch (JsonException) { return new(false, "nc-meta-error-artifact"); }
        if (imported is not { Files.Length: > 0 and <= 64, Releases.Length: <= 1 } || imported.DeviceId != target.Id ||
            imported.Revision != stored.AggregateVersion || imported.Files.Any(file => file == null || !MetaPaths.IsValid(file.Path) ||
                file.Bytes is < 0 or > 256 * 1024 || file.Digest.Length != 64) ||
            imported.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != imported.Files.Length)
            return new(false, "nc-meta-error-artifact");
        var sourceProject = new NCMetaProject(Guid.Empty, context.Owner, imported.Name, 1, imported.Files, []);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in imported.Files)
        {
            var source = await _persistence.ReadSourceAsync(sourceProject, file.Id);
            if (source == null) return new(false, "nc-meta-error-artifact");
            files.Add(file.Path, source);
        }
        foreach (var release in imported.Releases)
            if (await ReadVerifiedAsync(release) == null) return new(false, "nc-meta-error-artifact");
        if (!await OnMainAsync(Current)) return new(false, "nc-meta-error-access");
        return await _persistence.CloneProjectAsync(request.RequestId, device, imported.Name, files,
            context.Epoch, context.Time, imported.Releases, authority);
    }
}
