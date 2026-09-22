// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Reflection;
using Robust.Shared.IoC;
using Robust.Shared.Reflection;

namespace Content.Shared._NC.AstraGraph;

/// <summary>
/// Robust discovers entity systems only in assemblies handed to <see cref="IReflectionManager"/>.
/// AstraGraph is a referenced library, not a content module, so it has to be loaded explicitly.
/// </summary>
public static class AstraGraphAssemblyLoad
{
    public static void Load(IDependencyCollection dependencies, params Assembly?[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        var reflection = dependencies.Resolve<IReflectionManager>();
        var loaded = new HashSet<Assembly>(reflection.Assemblies);
        var pending = new List<Assembly>();
        foreach (var assembly in assemblies)
        {
            if (assembly is null || !loaded.Add(assembly))
                continue;

            pending.Add(assembly);
        }

        if (pending.Count > 0)
            reflection.LoadAssemblies(pending);
    }
}
