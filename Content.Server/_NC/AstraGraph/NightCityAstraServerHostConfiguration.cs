// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.IO;
using AstraGraph.HotReload;
using AstraGraph.Persistence;
using Content.Server.Administration.Managers;
using Content.Shared._NC.AstraGraph;
using Content.Shared.Administration;
using Robust.Shared.IoC;
using Robust.Shared.Player;

namespace Content.Server._NC.AstraGraph;

/// <summary>
/// Supplies consumer paths and the admin directory before <c>ServerAstraGraphSystem</c> builds its authoring service.
/// </summary>
public sealed partial class NightCityAstraServerHostConfiguration : IAstraServerHostConfiguration
{
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private ISharedPlayerManager _players = default!;

    public AstraServerHostOptions GetOptions()
    {
        if (!AstraConsumerLayout.TryLocate(out var root))
        {
            throw new InvalidOperationException(
                "AstraGraph consumer root was not found. Expected AstraGraph/Compatibility.json beside RobustToolbox.");
        }

        var robustRoot = Path.Combine(root, "RobustToolbox");
        if (!AstraConsumerLayout.TryReadGitCommit(robustRoot, out var commit))
        {
            throw new InvalidOperationException(
                $"AstraGraph could not read the RobustToolbox commit at '{robustRoot}'.");
        }

        return new AstraServerHostOptions
        {
            Storage = new StorageLayout(
                Path.Combine(root, AstraConsumerLayout.ProjectRelativePath),
                Path.Combine(root, AstraConsumerLayout.DataRelativePath)),
            CompatibilityManifestPath = Path.Combine(root, "AstraGraph", AstraConsumerLayout.ManifestFileName),
            RobustToolboxRoot = robustRoot,
            RobustCommit = commit,
            AdminDirectory = new NightCityAstraAdminDirectory(_admins, _players),
            RequiredAdminFlag = (uint)AdminFlags.AstraGraph
        };
    }
}
