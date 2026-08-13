// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Globalization;
using System.Linq;
using Content.Server._NC.Medical.Diagnostics.Detail;
using Content.Server._NC.Medical.Physiology;
using Content.Server.Administration;
using Content.Server.Administration.Logs;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared.Administration;
using Content.Shared.Database;
using Robust.Shared.Console;

namespace Content.Server._NC.Medical.Admin;

/// <summary>
/// Prints the trusted server-only medical state and current-round audit trail for one authoritative body.
/// </summary>
[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCMedicalStatusCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "ncmedstatus";
    public string Description => "Show the exact authoritative Night City medical state for an entity.";
    public string Help => "ncmedstatus <net entity uid>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!NCMedicalAdminCommandHelpers.TryResolveTarget(shell, args, _entities, out var target))
            return;

        var diagnostics = _entities.System<NCDiagnosticsSystem>();
        if (!diagnostics.TryBuildAdminExactSnapshot(target, out var snapshot))
        {
            shell.WriteError("Target is not an authoritative Night City medical body.");
            return;
        }

        shell.WriteLine($"{_entities.ToPrettyString(target)} | revision {snapshot.MedicalRevision} | " +
                        $"{snapshot.Condition}/{snapshot.Consciousness}");
        shell.WriteLine($"oxygen {Percent(snapshot.Oxygenation)} | perfusion {Percent(snapshot.Perfusion)} | " +
                        $"pain {snapshot.TotalPain:F1} | shock {snapshot.Shock:F1}");
        shell.WriteLine($"blood {snapshot.BloodVolume:F2}u (effective {snapshot.EffectiveCirculatingVolume:F2}u) | " +
                        $"bleeding external {snapshot.ExternalBleedRate:F3}u/s, internal {snapshot.InternalBleedRate:F3}u/s");
        shell.WriteLine($"wounds {snapshot.Wounds.Count} | scars {snapshot.Scars.Count} | " +
                        $"organs {snapshot.Organs.Count} | cyberware {snapshot.Cyberware.Count} | drugs {snapshot.Drugs.Count}");

        if (_entities.TryGetComponent(target, out NCAutopsyRecordComponent? autopsy))
        {
            foreach (var record in autopsy.Regions.Values.OrderBy(record => record.Region.Id, StringComparer.Ordinal))
            {
                shell.WriteLine($"autopsy {record.Region.Id}: {record.WoundCount} wounds, " +
                                $"integrity damage {record.TotalIntegrityDamage:F1}, " +
                                $"types [{string.Join(", ", record.WoundTypes)}]");
            }
        }

        if (_entities.TryGetComponent(target, out NCMedicalHistoryComponent? history))
        {
            shell.WriteLine($"round history {history.Records.Count}/{history.Capacity} records");
            foreach (var record in history.Records.TakeLast(20))
                shell.WriteLine($"  {record.Timestamp:hh\\:mm\\:ss} {record.Kind}");
        }
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args) =>
        NCMedicalAdminCommandHelpers.GetCompletion(args, _entities);

    private static string Percent(float value) =>
        (value * 100f).ToString("F1", CultureInfo.InvariantCulture) + "%";
}

/// <summary>
/// Executes one zero-time authoritative scheduler boundary without healing, advancing timers or touching HP.
/// </summary>
[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCMedicalRecalculateCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IAdminLogManager _adminLog = default!;

    public string Command => "ncmedrecalculate";
    public string Description => "Recalculate one authoritative Night City medical body without advancing time.";
    public string Help => "ncmedrecalculate <net entity uid>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!NCMedicalAdminCommandHelpers.TryResolveTarget(shell, args, _entities, out var target))
            return;

        if (!_entities.System<NCPhysiologySchedulerSystem>().ProcessNow(target, TimeSpan.Zero))
        {
            shell.WriteError("Target is not an authoritative Night City medical body or is already being processed.");
            return;
        }

        var administrator = shell.Player?.Name ?? "server console";
        _adminLog.Add(
            LogType.Action,
            LogImpact.Medium,
            $"{administrator} recalculated authoritative medical state for {_entities.ToPrettyString(target)}");
        shell.WriteLine($"Recalculated {_entities.ToPrettyString(target)} without advancing medical time.");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args) =>
        NCMedicalAdminCommandHelpers.GetCompletion(args, _entities);
}

internal static class NCMedicalAdminCommandHelpers
{
    public static bool TryResolveTarget(
        IConsoleShell shell,
        string[] args,
        IEntityManager entities,
        out EntityUid target)
    {
        target = default;
        if (args.Length != 1)
        {
            shell.WriteError("Expected exactly one network entity uid.");
            return false;
        }

        if (!NetEntity.TryParse(args[0], out var netEntity) ||
            !entities.TryGetEntity(netEntity, out var resolved) ||
            resolved is not { } entity ||
            !entities.EntityExists(entity))
        {
            shell.WriteError("Invalid network entity uid.");
            return false;
        }

        target = entity;
        return true;
    }

    public static CompletionResult GetCompletion(string[] args, IEntityManager entities)
    {
        if (args.Length != 1)
            return CompletionResult.Empty;

        return CompletionResult.FromOptions(CompletionHelper.NetEntities(args[0], entManager: entities));
    }
}
