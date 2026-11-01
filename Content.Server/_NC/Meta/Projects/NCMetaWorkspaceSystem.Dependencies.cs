// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Commerce;
using Content.Meta.Language;
using Content.Server._NC.Meta.Commerce;
using Content.Server._NC.Meta.Persistence;

namespace Content.Server._NC.Meta.Projects;

public sealed partial class NCMetaWorkspaceSystem
{
    [Dependency] private NCMetaMarketplaceSystem _marketplace = default!;
    private sealed record CompilationPackage(string RootModule, string Name, string Version, bool Library,
        MetaBinaryLibrary[] Libraries, NCMetaDependencyRef[] Dependencies);

    /// <summary>Only product/release locators enter the guarded resolver. A supplied digest never authorizes a blob read.</summary>
    private async Task<CompilationPackage?> ResolveCompilationPackageAsync(Context context, NCMetaProject project,
        string inferredRoot)
    {
        var root = inferredRoot;
        var name = "user." + project.Id.ToString("N");
        var version = "1.0.0";
        var library = false;
        try
        {
            if (project.Files.FirstOrDefault(file => file.Path == "manifest.json") is { } manifestFile)
            {
                var text = await _persistence.ReadSourceAsync(project, manifestFile.Id);
                if (text == null) return null;
                using var manifest = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
                var value = manifest.RootElement;
                if (value.ValueKind != JsonValueKind.Object) return null;
                if (value.TryGetProperty("rootModule", out var module)) root = module.GetString() ?? "";
                if (value.TryGetProperty("packageName", out var package)) name = package.GetString() ?? "";
                if (value.TryGetProperty("packageVersion", out var release)) version = release.GetString() ?? "";
                if (value.TryGetProperty("library", out var isLibrary)) library = isLibrary.GetBoolean();
            }
            if (root.Length is < 1 or > 128 || name.Length is < 1 or > 128 ||
                !MetaSoftwareVersion.TryParse(version, out _)) return null;
            if (project.Files.FirstOrDefault(file => file.Path == "meta.lock.json") is not { } lockFile)
                return new(root, name, version, library, [], []);
            var lockText = await _persistence.ReadSourceAsync(project, lockFile.Id);
            if (lockText == null || Utf8.GetByteCount(lockText) > 32768) return null;
            using var document = JsonDocument.Parse(lockText, new JsonDocumentOptions { MaxDepth = 8 });
            var locked = document.RootElement;
            if (locked.ValueKind != JsonValueKind.Object || locked.EnumerateObject().Any(property => property.Name is not ("version" or "dependencies")) ||
                locked.GetProperty("version").GetString() != "1.0" ||
                locked.GetProperty("dependencies").ValueKind != JsonValueKind.Array) return null;
            var entries = locked.GetProperty("dependencies").EnumerateArray().ToArray();
            if (entries.Length > 16) return null;
            var dependencies = new List<NCMetaDependencyRef>();
            var libraries = new List<MetaBinaryLibrary>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            var releases = new HashSet<(Guid, Guid)>();
            long totalBytes = 0;
            foreach (var entry in entries)
            {
                if (entry.ValueKind != JsonValueKind.Object || entry.EnumerateObject().Count() != 6 ||
                    entry.EnumerateObject().Any(property => property.Name is not ("productId" or "releaseId" or "blobDigest" or "artifactDigest" or "packageIdentity" or "version")) ||
                    !Guid.TryParseExact(entry.GetProperty("productId").GetString(), "D", out var productId) ||
                    !Guid.TryParseExact(entry.GetProperty("releaseId").GetString(), "D", out var releaseId) ||
                    !releases.Add((productId, releaseId))) return null;
                var resolved = await _marketplace.ResolveBuildDependencyAsync(context.Owner, context.DeviceId, productId, releaseId);
                if (resolved == null) return null;
                var reference = resolved.Value.Reference;
                if (reference.BlobDigest != entry.GetProperty("blobDigest").GetString() ||
                    reference.ArtifactDigest != entry.GetProperty("artifactDigest").GetString() ||
                    reference.PackageIdentity != entry.GetProperty("packageIdentity").GetString() ||
                    reference.Version != entry.GetProperty("version").GetString() || !identities.Add(reference.PackageIdentity) ||
                    (totalBytes += resolved.Value.Library.Artifact.Length) > 4 * 1024 * 1024) return null;
                dependencies.Add(reference);
                libraries.Add(resolved.Value.Library);
            }
            return new(root, name, version, library, libraries.ToArray(), dependencies.ToArray());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return null; }
    }

    private async Task<bool> RevalidateBuildDependenciesAsync(Context context, IEnumerable<NCMetaDependencyRef> dependencies)
    {
        foreach (var reference in dependencies)
        {
            var fresh = await _marketplace.ResolveBuildDependencyAsync(context.Owner, context.DeviceId,
                reference.ProductId, reference.ReleaseId);
            if (fresh?.Reference != reference) return false;
        }
        return await OnMainAsync(() => IsCurrent(context, afterClaim: true));
    }
}
