// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Globalization;
using System.Linq;
using Content.Server.Administration;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// Admin tools for the pharmaceutical batch layer: create a test batch, list batches, read the hidden truth
/// and run laboratory checks. Reading hidden data is recorded in the batch history.
/// </summary>
internal static class NCPharmaAdminCommandHelpers
{
    public static readonly EntProtoId DefaultContainer = "NCPharmaAutoinjector";

    public static bool TryParseBatch(IConsoleShell shell, string[] args, int index, NCPharmaBatchSystem batches,
        out NCPharmaBatchRecord batch)
    {
        batch = default!;
        if (args.Length <= index || !Guid.TryParse(args[index], out var id))
        {
            shell.WriteError("Expected a batch id (GUID). Use ncpharmabatches to list them.");
            return false;
        }

        if (!batches.TryGetBatch(id, out var found))
        {
            shell.WriteError($"Unknown batch {id}.");
            return false;
        }

        batch = found;
        return true;
    }

    public static bool TryParseFloat(IConsoleShell shell, string[] args, int index, float fallback, out float value)
    {
        value = fallback;
        if (args.Length <= index)
            return true;
        if (float.TryParse(args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            float.IsFinite(value))
        {
            return true;
        }

        shell.WriteError($"Argument {index + 1} must be a number.");
        return false;
    }
}

[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCPharmaCreateBatchCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "ncpharmacreate";
    public string Description => "Produce a test batch of a formula and spawn a dose pack at your feet.";
    public string Help =>
        "ncpharmacreate <formulaId> [productionProfileId] [units=10] [inputPurity=0.9] [sterility=0.9] [mixing=0.9] [calibration=0]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1)
        {
            shell.WriteError(Help);
            return;
        }

        if (shell.Player?.AttachedEntity is not { } player)
        {
            shell.WriteError("You need an attached entity to receive the dose pack.");
            return;
        }

        string? profile = args.Length > 1 && !char.IsDigit(args[1][0]) ? args[1] : null;
        var offset = profile == null ? 1 : 2;
        if (!NCPharmaAdminCommandHelpers.TryParseFloat(shell, args, offset, 10f, out var units) ||
            !NCPharmaAdminCommandHelpers.TryParseFloat(shell, args, offset + 1, 0.9f, out var purity) ||
            !NCPharmaAdminCommandHelpers.TryParseFloat(shell, args, offset + 2, 0.9f, out var sterility) ||
            !NCPharmaAdminCommandHelpers.TryParseFloat(shell, args, offset + 3, 0.9f, out var mixing) ||
            !NCPharmaAdminCommandHelpers.TryParseFloat(shell, args, offset + 4, 0f, out var calibration))
        {
            return;
        }

        var batches = _entities.System<NCPharmaBatchSystem>();
        var inputs = new NCPharmaProductionInputs(units, purity, sterility, mixing, calibration);
        var actor = _entities.ToPrettyString(player);
        if (!batches.TryCreateBatch(
                args[0],
                profile,
                inputs,
                actor,
                "admin command",
                out var batch))
        {
            shell.WriteError("The batch could not be produced. Check the formula, profile and input ranges (0-1).");
            return;
        }

        var coordinates = _entities.GetComponent<TransformComponent>(player).Coordinates;
        var container = batches.TrySpawnContainer(
            NCPharmaAdminCommandHelpers.DefaultContainer,
            coordinates,
            batch.BatchId,
            batch.DosesProduced,
            actor);
        shell.WriteLine($"Batch {batch.BatchCode} {batch.BatchId} produced: {batch.DosesProduced} doses" +
                        $"{(container == null ? " (could not spawn a pack)" : string.Empty)}.");
    }
}

[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCPharmaListBatchesCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "ncpharmabatches";
    public string Description => "List every pharmaceutical batch of this round with its public label and QC state.";
    public string Help => "ncpharmabatches";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var batches = _entities.System<NCPharmaBatchSystem>().Batches;
        foreach (var batch in batches.OrderBy(value => value.CreatedAt))
        {
            shell.WriteLine($"{batch.BatchCode} {batch.BatchId} | {batch.Formula.Id} v{batch.FormulaVersion} | " +
                            $"label {batch.Label.Grade} | QC {batch.QcState} | " +
                            $"doses {batch.DosesAdministered}/{batch.DosesProduced} used");
        }

        shell.WriteLine($"{batches.Count} batches.");
    }
}

[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCPharmaInspectBatchCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "ncpharmainspect";
    public string Description => "Show the hidden real composition, the label and the full history of a batch.";
    public string Help => "ncpharmainspect <batchId>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var batches = _entities.System<NCPharmaBatchSystem>();
        if (!NCPharmaAdminCommandHelpers.TryParseBatch(shell, args, 0, batches, out var batch))
            return;

        var actual = batch.Actual;
        var label = batch.Label;
        shell.WriteLine($"{batch.BatchCode} {batch.BatchId} | {batch.Formula.Id} v{batch.FormulaVersion} | QC {batch.QcState}");
        shell.WriteLine($"label : conc {label.Concentration:F2} | purity {label.Purity:P1} | " +
                        $"{(label.Sterile ? "sterile" : "not sterile")} | {label.Grade}");
        shell.WriteLine($"actual: conc {actual.Concentration:F2} | purity {actual.Purity:P1} | " +
                        $"sterility {actual.Sterility:P1} | homogeneity {actual.Homogeneity:P1} | " +
                        $"stability {actual.Stability:P1} | toxicity x{batch.ModifierToxicity:F2}");
        shell.WriteLine($"producer {batch.Producer} @ {batch.Facility} | doses produced {batch.DosesProduced}, " +
                        $"packed {batch.DosesPacked}, administered {batch.DosesAdministered}");
        foreach (var entry in batch.Audit)
            shell.WriteLine($"  [{entry.Time.Elapsed:d\\.hh\\:mm\\:ss}] {entry.Kind} by {entry.Actor}: {entry.Detail}");

        var actor = shell.Player?.AttachedEntity is { } player ? _entities.ToPrettyString(player) : "server console";
        batches.RecordInspection(batch, actor);
    }
}

[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCPharmaQcCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "ncpharmaqc";
    public string Description => "Run laboratory checks on a batch. Only the requested tests reveal values.";
    public string Help => "ncpharmaqc <batchId> [concentration] [purity] [sterility] [homogeneity] [stability] (none = all)";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var batches = _entities.System<NCPharmaBatchSystem>();
        if (!NCPharmaAdminCommandHelpers.TryParseBatch(shell, args, 0, batches, out var batch))
            return;

        var tests = NCPharmaQcTests.None;
        foreach (var name in args.Skip(1))
        {
            if (!Enum.TryParse<NCPharmaQcTests>(name, true, out var test) || test == NCPharmaQcTests.All)
            {
                shell.WriteError($"Unknown test '{name}'.");
                return;
            }

            tests |= test;
        }

        if (tests == NCPharmaQcTests.None)
            tests = NCPharmaQcTests.All;

        var actor = shell.Player?.AttachedEntity is { } player ? _entities.ToPrettyString(player) : "server console";
        if (!batches.TryPerformQc(batch.BatchId, actor, tests, out var report))
        {
            shell.WriteError("The check could not be performed.");
            return;
        }

        shell.WriteLine($"QC {batch.BatchCode}: performed {report.Performed}, failed {report.Failed}, state {batch.QcState}");
        shell.WriteLine($"conc {Format(report.Concentration)} | purity {Format(report.Purity)} | " +
                        $"sterility {Format(report.Sterility)} | homogeneity {Format(report.Homogeneity)} | " +
                        $"stability {Format(report.Stability)}");
    }

    private static string Format(float? value) => value is { } v ? v.ToString("F3", CultureInfo.InvariantCulture) : "-";
}
