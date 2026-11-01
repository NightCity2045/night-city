// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;

namespace Content.Server._NC.Meta.Persistence;

public sealed partial class NCMetaPersistenceSystem
{
    /// <summary>Record equality compares nested arrays by reference; durable release identity compares their ordered contents.</summary>
    private static bool TryResolveProjectRelease(NCMetaProject project, NCMetaRelease requested, out NCMetaRelease canonical)
    {
        canonical = default!;
        if (requested == null || project.Releases is not { Length: <= 128 }) return false;
        NCMetaRelease? selected = null;
        foreach (var candidate in project.Releases)
        {
            if (candidate == null || candidate.Id != requested.Id) continue;
            if (selected != null) return false;
            selected = candidate;
        }
        if (selected == null || selected.Id != requested.Id || selected.Digest != requested.Digest ||
            selected.SourceRevision != requested.SourceRevision || selected.CompilerBuild != requested.CompilerBuild ||
            selected.CommercialOrigin != requested.CommercialOrigin || selected.BuildAuthorization != requested.BuildAuthorization ||
            !SameExportCertificate(selected.Exports, requested.Exports) ||
            !SameReleaseArray(selected.Resources, requested.Resources, static (left, right) => left == right) ||
            !SameReleaseArray(selected.Sources, requested.Sources, static (left, right) => left == right) ||
            !SameReleaseArray(selected.Dependencies, requested.Dependencies, static (left, right) => left == right)) return false;
        canonical = selected;
        return true;
    }

    private static bool SameExportCertificate(MetaExportCertificate? left, MetaExportCertificate? right)
    {
        if (ReferenceEquals(left, right)) return true;
        return left != null && right != null && left.SourceTreeHash == right.SourceTreeHash && left.ArtifactDigest == right.ArtifactDigest &&
            SameReleaseArray(left.Types, right.Types, static (first, second) => first == second) &&
            SameReleaseArray(left.Constants, right.Constants, static (first, second) => first == second) &&
            SameReleaseArray(left.Functions, right.Functions, static (first, second) => ReferenceEquals(first, second) ||
                first != null && second != null && first.FunctionId == second.FunctionId && first.Name == second.Name &&
                first.ReturnType == second.ReturnType && first.Async == second.Async &&
                SameReleaseArray(first.ParameterTypes, second.ParameterTypes, static (one, two) => one == two));
    }

    private static bool SameReleaseArray<T>(T[]? left, T[]? right, Func<T, T, bool> equal)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null || left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++)
            if (!equal(left[index], right[index])) return false;
        return true;
    }
}
