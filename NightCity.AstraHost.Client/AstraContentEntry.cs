// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.AstraGraph.Robust.Client;
using Content.Shared._NC.AstraGraph;
using Robust.Shared.IoC;

namespace Content.Client._NC.AstraGraph;

public static class AstraContentEntry
{
    public static void Load(IDependencyCollection dependencies)
    {
        AstraGraphAssemblyLoad.Load(
            dependencies,
            typeof(ClientAstraGraphSystem).Assembly,
            typeof(ClientAstraGraphSystem).BaseType?.Assembly);
    }

    public static void InstallSchemas(Robust.Shared.GameObjects.IComponentFactory factory, IDependencyCollection dependencies)
    {
        AstraSchemaStartup.Install(factory, dependencies);
    }
}
