// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Linq;
using Content.Server.Administration;
using Content.Server.Preferences.Managers;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Dungeons;
using Content.Shared._NC.Dungeons.Prototypes;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Dungeons.Commands;

/// <summary>
/// Admin-only command surface for validating reservation, deployment, completion and cleanup of dungeon runs.
/// </summary>
[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCDungeonRunCommand : IConsoleCommand
{
    [Dependency] private IEntitySystemManager _systems = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public string Command => "nc-dungeon-run";
    public string Description => "Start, inspect or finish a Night City physical dungeon run.";
    public string Help =>
        "nc-dungeon-run start <definition> [slotId] [seed] | " +
        "nc-dungeon-run status <runId> | nc-dungeon-run finish <runId> <Success|Failure|Aborted>";

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2)
        {
            shell.WriteError(Help);
            return;
        }

        var dungeons = _systems.GetEntitySystem<NCDungeonSystem>();
        switch (args[0].ToLowerInvariant())
        {
            case "start":
                if (args.Length > 4 || shell.Player is not { } player ||
                    !_preferences.TryGetSelectedNCCharacterId(player.UserId, out var characterId))
                {
                    shell.WriteError(Help);
                    return;
                }

                NCDungeonSlotId? slot = null;
                if (args.Length >= 3)
                {
                    if (!NCDungeonSlotId.TryParse(args[2], out var parsedSlot))
                    {
                        shell.WriteError("slotId must be a non-empty dungeon-zone GUID.");
                        return;
                    }

                    slot = parsedSlot;
                }

                int? seed = null;
                if (args.Length == 4)
                {
                    if (!int.TryParse(args[3], out var parsedSeed))
                    {
                        shell.WriteError("seed must be an integer.");
                        return;
                    }

                    seed = parsedSeed;
                }

                var start = await dungeons.TryStartRunAsync(
                    new ProtoId<NCDungeonDefinitionPrototype>(args[1]),
                    [characterId],
                    slot,
                    seed);
                if (!start.Success)
                {
                    shell.WriteError(start.Error);
                    return;
                }

                shell.WriteLine($"Dungeon run queued: {start.RunId}.");
                return;

            case "status":
                if (args.Length != 2 || !TryParseRunId(args[1], out var statusId) ||
                    !dungeons.TryGetRun(statusId, out var status))
                {
                    shell.WriteError("Dungeon run does not exist. Usage: nc-dungeon-run status <runId>");
                    return;
                }

                shell.WriteLine(
                    $"Run={status.RunId}, State={status.State}, Slot={status.SlotId}, " +
                    $"Deployment={status.DeploymentId}, Result={status.Result}, Seed={status.Seed}, " +
                    $"Error={status.Error ?? "none"}");
                return;

            case "finish":
                if (args.Length != 3 || !TryParseRunId(args[1], out var finishId) ||
                    !Enum.TryParse<NCDungeonRunResult>(args[2], true, out var result) ||
                    result == NCDungeonRunResult.None)
                {
                    shell.WriteError("Usage: nc-dungeon-run finish <runId> <Success|Failure|Aborted>");
                    return;
                }

                if (!dungeons.TryFinishRun(finishId, result, out var error))
                {
                    shell.WriteError(error);
                    return;
                }

                shell.WriteLine($"Dungeon run {finishId} is closing with result {result}.");
                return;

            default:
                shell.WriteError(Help);
                return;
        }
    }

    private static bool TryParseRunId(string value, out NCDungeonRunId runId)
    {
        if (Guid.TryParse(value, out var guid) && guid != Guid.Empty)
        {
            runId = new NCDungeonRunId(guid);
            return true;
        }

        runId = default;
        return false;
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            1 => CompletionResult.FromHintOptions(["start", "status", "finish"], "<operation>"),
            2 when args[0].Equals("start", StringComparison.OrdinalIgnoreCase) =>
                CompletionResult.FromHintOptions(
                    _prototypes.EnumeratePrototypes<NCDungeonDefinitionPrototype>().Select(definition => definition.ID),
                    "<dungeon definition>"),
            2 => CompletionResult.FromHint("<run id>"),
            3 when args[0].Equals("start", StringComparison.OrdinalIgnoreCase) =>
                CompletionResult.FromHint("<optional dungeon slot id>"),
            3 when args[0].Equals("finish", StringComparison.OrdinalIgnoreCase) =>
                CompletionResult.FromHintOptions(
                    [nameof(NCDungeonRunResult.Success), nameof(NCDungeonRunResult.Failure), nameof(NCDungeonRunResult.Aborted)],
                    "<result>"),
            4 when args[0].Equals("start", StringComparison.OrdinalIgnoreCase) =>
                CompletionResult.FromHint("<optional seed>"),
            _ => CompletionResult.Empty,
        };
    }
}
