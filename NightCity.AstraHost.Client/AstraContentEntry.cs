// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.IoC;

namespace Content.Client._NC.AstraGraph;

public static class AstraContentEntry
{
    public static void Load(IDependencyCollection dependencies)
    {
        // NC: Content.AstraGraph.Client is discovered as a regular sandboxed content assembly.
    }

    public static void InstallSchemas(Robust.Shared.GameObjects.IComponentFactory factory, IDependencyCollection dependencies)
    {
        // NC: Dynamic schema CLR proxies are server-only; clients receive portable schema metadata.
    }
}
