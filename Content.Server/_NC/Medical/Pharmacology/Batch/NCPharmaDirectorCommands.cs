// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server.Administration;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

internal static class NCPharmaDirectorHelpers
{
    public static string Actor(IConsoleShell shell, IEntityManager entities) =>
        shell.Player?.AttachedEntity is { } player ? entities.ToPrettyString(player).ToString() : "server console";
}

/// <summary>The director's overview: the state of the whole pharmacopoeia in a few lines.</summary>
[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCPharmaDirectorCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public string Command => "ncpharmadirector";
    public string Description => "Overview of formulas, batches, recalls and what the laboratories know.";
    public string Help => "ncpharmadirector";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var batches = _entities.System<NCPharmaBatchSystem>();
        var formulas = batches.AllFormulas.ToList();
        shell.WriteLine($"formulas: {formulas.Count} ({formulas.Count(f => f.DerivedFrom != null)} derived, " +
                        $"{formulas.Count(f => f.RegulatoryStatus == NCPharmaRegulatoryStatus.Experimental)} experimental)");
        shell.WriteLine($"batches: {batches.Batches.Count}");
        foreach (var group in batches.Batches.GroupBy(batch => batch.QcState).OrderBy(group => group.Key))
            shell.WriteLine($"  QC {group.Key}: {group.Count()}");

        shell.WriteLine($"journal entries: {batches.Journal.Count}");
        shell.WriteLine("compounds under research:");
        foreach (var drug in _prototypes.EnumeratePrototypes<NCDrugProfilePrototype>()
                     .Where(candidate => candidate.Codename != null)
                     .OrderBy(candidate => candidate.ID, StringComparer.Ordinal))
        {
            shell.WriteLine($"  {drug.ID}: reached {batches.GetKnowledge(drug.ID)}");
        }
    }
}

/// <summary>Rebuilds FormulaId → BatchId → holder → administrations for an investigation.</summary>
[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCPharmaChainCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "ncpharmachain";
    public string Description => "Trace a batch: formula, producer, where its packs are and who received doses.";
    public string Help => "ncpharmachain <batchId>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var batches = _entities.System<NCPharmaBatchSystem>();
        if (!NCPharmaAdminCommandHelpers.TryParseBatch(shell, args, 0, batches, out var batch))
            return;

        var containers = _entities.System<SharedContainerSystem>();
        shell.WriteLine($"{batch.Formula} v{batch.FormulaVersion} -> {batch.BatchCode} ({batch.BatchId})");
        shell.WriteLine($"made by {batch.Producer} @ {batch.Facility}");
        var query = _entities.EntityQueryEnumerator<NCPharmaContainerComponent>();
        while (query.MoveNext(out var uid, out var pack))
        {
            if (pack.BatchId != batch.BatchId)
                continue;

            var holder = containers.TryGetContainingContainer(uid, out var container)
                ? _entities.ToPrettyString(container.Owner).ToString()
                : "on the ground";
            shell.WriteLine($"  pack {_entities.ToPrettyString(uid)}: {pack.Doses} doses, held by {holder}");
        }

        foreach (var entry in batch.Audit.Where(entry => entry.Kind == NCPharmaAuditKind.Administered))
            shell.WriteLine($"  dose [{entry.Time.Elapsed:d\\.hh\\:mm\\:ss}] by {entry.Actor}: {entry.Detail}");
    }
}

/// <summary>Director event: change the real composition of a batch (contamination, bad line, sabotage).</summary>
[AdminCommand(AdminFlags.Fun)]
public sealed partial class NCPharmaContaminateCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "ncpharmacontaminate";
    public string Description => "Set the real purity and/or sterility of a batch. The change is written to its history.";
    public string Help => "ncpharmacontaminate <batchId> [purity] [sterility]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var batches = _entities.System<NCPharmaBatchSystem>();
        if (!NCPharmaAdminCommandHelpers.TryParseBatch(shell, args, 0, batches, out var batch))
            return;
        if (!NCPharmaAdminCommandHelpers.TryParseFloat(shell, args, 1, batch.Actual.Purity, out var purity) ||
            !NCPharmaAdminCommandHelpers.TryParseFloat(shell, args, 2, batch.Actual.Sterility, out var sterility))
        {
            return;
        }

        var actor = NCPharmaDirectorHelpers.Actor(shell, _entities);
        shell.WriteLine(batches.TryAdminAdjust(batch.BatchId, actor, purity, sterility)
            ? $"Batch {batch.BatchCode} adjusted."
            : "The batch could not be adjusted.");
    }
}

/// <summary>Director event: a corporate recall of a batch.</summary>
[AdminCommand(AdminFlags.Fun)]
public sealed partial class NCPharmaRecallCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "ncpharmarecall";
    public string Description => "Recall a batch: its packs are marked recalled in their label.";
    public string Help => "ncpharmarecall <batchId>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var batches = _entities.System<NCPharmaBatchSystem>();
        if (!NCPharmaAdminCommandHelpers.TryParseBatch(shell, args, 0, batches, out var batch))
            return;

        shell.WriteLine(batches.TryRecall(batch.BatchId, NCPharmaDirectorHelpers.Actor(shell, _entities))
            ? $"Batch {batch.BatchCode} recalled."
            : "The batch could not be recalled.");
    }
}

/// <summary>Director tool: read or grant what the laboratories know about a compound.</summary>
[AdminCommand(AdminFlags.Fun)]
public sealed partial class NCPharmaKnowledgeCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public string Command => "ncpharmaknowledge";
    public string Description => "Show or grant the laboratory knowledge of a compound (chemical, invitro, invivo).";
    public string Help => "ncpharmaknowledge <compoundId> [chemical|invitro|invivo]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1 || !_prototypes.HasIndex<NCDrugProfilePrototype>(args[0]))
        {
            shell.WriteError(Help);
            return;
        }

        var batches = _entities.System<NCPharmaBatchSystem>();
        ProtoId<NCDrugProfilePrototype> compound = args[0];
        if (args.Length > 1)
        {
            var tier = args[1].ToLowerInvariant() switch
            {
                "chemical" => NCAnalysisTier.Chemical,
                "invitro" => NCAnalysisTier.InVitro,
                "invivo" => NCAnalysisTier.InVivo,
                _ => NCAnalysisTier.None,
            };
            if (tier == NCAnalysisTier.None)
            {
                shell.WriteError(Help);
                return;
            }

            batches.RecordAnalysis(compound, tier);
        }

        shell.WriteLine($"{compound}: reached {batches.GetKnowledge(compound)}");
        foreach (var row in batches.ReadDossier(compound))
            shell.WriteLine($"  {row.Kind} = {row.Value} [{row.State}]");
    }
}
