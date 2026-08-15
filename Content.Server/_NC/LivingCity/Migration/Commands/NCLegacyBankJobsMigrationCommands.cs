// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._NC.LivingCity.Migration.Commands;

/// <summary>
/// Reports migration progress without starting work or changing either legacy or common-domain state.
/// </summary>
[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCLegacyBankJobsMigrationStatusCommand : LocalizedEntityCommands
{
    [Dependency] private NCLegacyBankJobsMigrationSystem _migration = default!;

    public override string Command => "alife_legacy_bank_jobs_status";
    public override string Description => "Show the explicit legacy Bank/Jobs rehearsal state.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError($"Usage: {Command}");
            return;
        }

        NCLegacyBankJobsMigrationCommandHelpers.WriteProgress(shell, _migration.Progress);
    }
}

/// <summary>
/// Runs either a read-only dual-read verification or an idempotent common-domain backfill. The command is the only
/// activation boundary: the migration system deliberately has no round-start hook or background schedule.
/// </summary>
[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCLegacyBankJobsMigrationRehearseCommand : LocalizedEntityCommands
{
    private const int MaximumPrintedIssues = 50;

    [Dependency] private NCLegacyBankJobsMigrationSystem _migration = default!;

    public override string Command => "alife_legacy_bank_jobs_rehearse";
    public override string Description => "Verify legacy Bank/Jobs projections or idempotently backfill common domains.";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || !TryParseMode(args[0], out var mode))
        {
            shell.WriteError($"Usage: {Command} <verify|apply>");
            return;
        }

        var auditSource = shell.Player is { } player
            ? $"admin:{player.UserId}:legacy-bank-jobs-{args[0].ToLowerInvariant()}"
            : $"admin:server-console:legacy-bank-jobs-{args[0].ToLowerInvariant()}";
        var report = await _migration.RehearseAsync(mode, auditSource);
        var summary =
            $"Legacy Bank/Jobs {report.Mode}: status={report.Status}, processed={report.Processed}, " +
            $"aligned={report.Aligned}, changed={report.Changed}, conflicts={report.Conflicts}, failed={report.Failed}.";

        if (report.Status == NCLegacyMigrationRunStatus.Completed && report.Conflicts == 0 && report.Failed == 0)
            shell.WriteLine(summary);
        else
            shell.WriteError(summary);

        foreach (var issue in report.Issues.Take(MaximumPrintedIssues))
        {
            shell.WriteError(
                $"{issue.SourceKind} {issue.SourceKey}: {issue.Kind}: {issue.Message}");
        }

        if (report.Issues.Count > MaximumPrintedIssues)
            shell.WriteError($"... {report.Issues.Count - MaximumPrintedIssues} additional issues omitted.");
    }

    private static bool TryParseMode(string value, out NCLegacyMigrationRunMode mode)
    {
        if (value.Equals("verify", StringComparison.OrdinalIgnoreCase))
        {
            mode = NCLegacyMigrationRunMode.Verify;
            return true;
        }

        if (value.Equals("apply", StringComparison.OrdinalIgnoreCase))
        {
            mode = NCLegacyMigrationRunMode.Apply;
            return true;
        }

        mode = default;
        return false;
    }
}

internal static class NCLegacyBankJobsMigrationCommandHelpers
{
    public static void WriteProgress(IConsoleShell shell, NCLegacyBankJobsMigrationProgress progress)
    {
        shell.WriteLine(
            $"Legacy Bank/Jobs: running={progress.Running}, mode={progress.Mode}, phase={progress.Phase}, " +
            $"processed={progress.Processed}, aligned={progress.Aligned}, changed={progress.Changed}, " +
            $"conflicts={progress.Conflicts}, failed={progress.Failed}.");
        if (progress.LastError != null)
            shell.WriteError($"Last error: {progress.LastError}");
    }
}
