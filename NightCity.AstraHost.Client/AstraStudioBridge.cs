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
        // NC: Content sandbox does not expose UriKind; the trusted engine URI service performs final validation.
        if (!address.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !address.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var systems = IoCManager.Resolve<IEntitySystemManager>();
        systems.GetEntitySystem<ClientAstraGraphSystem>().OpenStudio(address);
        return true;
    }
}
