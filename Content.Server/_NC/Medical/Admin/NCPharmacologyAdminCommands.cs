// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._NC.Medical.Admin;

/// <summary>Prints the exact tolerance, dependence and withdrawal state of one authoritative body.</summary>
[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCPharmaExposureCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "ncpharmaexposure";
    public string Description => "Show the exact tolerance, dependence and withdrawal stage of a medical body.";
    public string Help => "ncpharmaexposure <net entity uid>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!NCMedicalAdminCommandHelpers.TryResolveTarget(shell, args, _entities, out var target))
            return;

        if (!_entities.TryGetComponent(target, out NCPharmacologyComponent? state))
        {
            shell.WriteLine("The body carries no pharmacology state.");
            return;
        }

        shell.WriteLine($"{_entities.ToPrettyString(target)} | overdose burden {state.OverdoseBurden:F2} | " +
                        $"{state.ActiveDrugs.Count} active, {state.Dependence.Count} habituating compounds");
        foreach (var (drug, exposure) in state.ActiveDrugs.OrderBy(entry => entry.Key.Id, StringComparer.Ordinal))
            shell.WriteLine($"  active {drug.Id}: concentration {exposure.Concentration:F2}, cumulative {exposure.CumulativeDose:F1}");
        foreach (var (drug, record) in state.Dependence.OrderBy(entry => entry.Key.Id, StringComparer.Ordinal))
        {
            shell.WriteLine($"  habit {drug.Id}: tolerance {record.Tolerance:P1}, dependence {record.Dependence:P1}, " +
                            $"craving {record.Craving:P1}, stage {record.Stage}");
        }
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args) =>
        NCMedicalAdminCommandHelpers.GetCompletion(args, _entities);
}
