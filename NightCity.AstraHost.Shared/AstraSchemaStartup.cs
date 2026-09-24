// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.IO;
using AstraGraph.Robust.Shared;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;

namespace Content.Shared._NC.AstraGraph;

/// <summary>
/// Loads component schemas from graph files before component net ids freeze,
/// and registers their YAML readers on the first prototype document.
/// </summary>
public static class AstraSchemaStartup
{
    public static void Install(IComponentFactory factory, IDependencyCollection dependencies)
    {
        if (!AstraConsumerLayout.TryLocate(out var root))
            return;

        var data = Path.Combine(root, AstraConsumerLayout.DataRelativePath);
        AstraSchemaBootstrap.RegisterProxies(
            factory,
            Path.Combine(root, AstraConsumerLayout.ProjectRelativePath),
            Path.Combine(data, "Live"),
            Path.Combine(data, "Overrides"));

        if (dependencies.Resolve<IPrototypeManager>() is PrototypeManager prototypes)
        {
            prototypes.LoadedData += _ =>
                AstraSchemaBootstrap.RegisterSerializers(dependencies.Resolve<ISerializationManager>());
        }
    }
}
