// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Persistence;
using Content.Shared._NC.Meta;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Meta.Persistence;

public sealed partial class NCMetaPersistenceSystem
{
    [Dependency] private IPrototypeManager _statePrototypes = default!;
    [Dependency] private ITaskManager _stateTasks = default!;

    private sealed class StateMigrationRequiredException : Exception { }

    private async Task<int> ReadStateSchemaVersionAsync(NCMetaRelease release)
    {
        if (release.Resources?.Any(value => value.Path == "manifest.json") != true) return 1;
        var manifest = await ReadReleaseResourceAsync(release, "manifest.json");
        if (manifest == null || manifest.Length > 65536) throw new StateMigrationRequiredException();
        try
        {
            using var document = JsonDocument.Parse(manifest, new() { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new StateMigrationRequiredException();
            var version = 1;
            var found = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name != "stateSchemaVersion") continue;
                if (found || property.Value.ValueKind != JsonValueKind.Number ||
                    !property.Value.TryGetInt32(out version) || version is < 1 or > 65535)
                    throw new StateMigrationRequiredException();
                found = true;
            }
            return version;
        }
        catch (JsonException) { throw new StateMigrationRequiredException(); }
    }

    private async Task<NCMetaUserFile[]> MigrateStateAsync(string fromPackage, string package, NCMetaVolume volume,
        int fromVersion, int toVersion, int capacity, bool persistBlobs)
    {
        if (fromPackage == package && fromVersion == toVersion)
        {
            if (volume.Files.Sum(value => (long) value.Bytes) > capacity) throw new StateMigrationRequiredException();
            return volume.Files.ToArray();
        }
        // No implicit downgrade and no guessed intermediate schema. A direct registered transition is required.
        if (volume.Files.Sum(value => (long) value.Bytes) > MetaStateTransformer.MaximumBytes)
            throw new StateMigrationRequiredException();
        // Prototype reload belongs to the game thread. Capture immutable steps before asynchronous blob reads.
        var captured = new TaskCompletionSource<MetaStateTransformStep[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _stateTasks.RunOnMainThread(() =>
        {
            try
            {
                var candidates = _statePrototypes.EnumeratePrototypes<NCMetaStateTransformerPrototype>().Where(value =>
                    value.Package == package && (value.FromPackage.Length == 0 ? value.Package : value.FromPackage) == fromPackage &&
                    value.FromVersion == fromVersion && value.ToVersion == toVersion).Take(2).ToArray();
                captured.SetResult(candidates is [var transformer] && transformer.Steps.Count <= MetaStateTransformer.MaximumSteps
                    ? transformer.Steps.Select(value => new MetaStateTransformStep(value.Kind, value.Path, value.Target, value.Text)).ToArray()
                    : null);
            }
            catch (Exception exception) { captured.SetException(exception); }
        });
        var steps = await captured.Task;
        if (steps == null) throw new StateMigrationRequiredException();
        var source = new List<MetaStateTransformFile>(volume.Files.Length);
        foreach (var file in volume.Files)
        {
            var bytes = await _database.ReadNCMetaBlobAsync(file.Digest);
            if (bytes == null || bytes.Length != file.Bytes) throw new StateMigrationRequiredException();
            source.Add(new(file.Path, bytes, file.Revision));
        }
        if (!MetaStateTransformer.TryTransform(source, steps, capacity, out var transformed))
            throw new StateMigrationRequiredException();
        var result = new List<NCMetaUserFile>(transformed.Length);
        foreach (var file in transformed)
            result.Add(new(file.Path, persistBlobs ? await _database.StoreNCMetaBlobAsync(file.Bytes) :
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file.Bytes)), file.Bytes.Length, file.Revision));
        return result.ToArray();
    }
}
