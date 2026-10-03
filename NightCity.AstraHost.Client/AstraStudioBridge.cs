// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.AstraGraph.Robust.Client;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace Content.Client._NC.AstraGraph;

/// <summary>
/// Opens the server-hosted Studio through the engine URI service. No local HTTP listener runs in Content.
/// </summary>
public static class AstraStudioBridge
{
    public static bool TryOpen(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http"))
        {
            return false;
        }

        var systems = IoCManager.Resolve<IEntitySystemManager>();
        systems.GetEntitySystem<ClientAstraGraphSystem>().OpenStudio(uri);
        return true;
    }
}
