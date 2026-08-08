// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Administration;
using Content.Server.Preferences.Managers;
using Content.Shared.Administration;
using Content.Shared.Roles;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Prototypes;
using System.Linq;

namespace Content.Server._NC.Jobs.Commands;

/// <summary>
/// Minimal administrative bridge for recording IC hiring decisions until an organization HR UI exists.
/// </summary>
[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCSetJobCommand : IConsoleCommand
{
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;

    public string Command => "ncsetjob";
    public string Description => "Set the selected character's persistent Night City job.";
    public string Help => "ncsetjob <online player> <job prototype|resident>";

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteError(Help);
            return;
        }

        if (!_players.TryGetSessionByUsername(args[0], out var target))
        {
            shell.WriteError("The target player must be online.");
            return;
        }

        ProtoId<JobPrototype>? job = null;
        if (!args[1].Equals("resident", StringComparison.OrdinalIgnoreCase))
        {
            var jobId = new ProtoId<JobPrototype>(args[1]);
            if (!_prototypes.HasIndex(jobId) || !IsNCJob(jobId))
            {
                shell.WriteError("Unknown or non-Night City job prototype.");
                return;
            }

            job = jobId;
        }

        if (!await _preferences.SetSelectedNCEmploymentAsync(target.UserId, job))
        {
            shell.WriteError("The selected character has no persistent identity.");
            return;
        }

        shell.WriteLine(job is { } assigned
            ? $"Assigned {assigned.Id} to {target.Name}'s selected character."
            : $"Terminated employment for {target.Name}'s selected character; they are now a Resident.");
    }

    private bool IsNCJob(ProtoId<JobPrototype> job)
    {
        return _prototypes.EnumeratePrototypes<DepartmentPrototype>()
            .Any(department => department.NCSelectable && department.Roles.Contains(job));
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            1 => CompletionResult.FromHintOptions(CompletionHelper.SessionNames(players: _players), "<online player>"),
            2 => CompletionResult.FromHintOptions(
                _prototypes.EnumeratePrototypes<DepartmentPrototype>()
                    .Where(department => department.NCSelectable)
                    .SelectMany(department => department.Roles)
                    .Select(job => job.Id)
                    .Append("resident")
                    .Distinct(),
                "<job prototype|resident>"),
            _ => CompletionResult.Empty,
        };
    }
}
