// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Linq;
using Content.Server.Administration;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Shared._NC.City.ZoneTemplates.Prototypes;
using Content.Shared._NC.Housing;
using Content.Shared._NC.Housing.Prototypes;
using Content.Shared._NC.Identity;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Housing.Commands;

/// <summary>
/// Admin-only diagnostic surface for exercising persistent property lifecycle operations without the player terminal.
/// </summary>
[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCPropertyCommand : IConsoleCommand
{
    [Dependency] private IEntitySystemManager _systems = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public string Command => "nc-property";
    public string Description => "Exercise Night City apartment rent, access, invocation and release flows.";
    public string Help =>
        "nc-property <status|rent|release|card|invoke> <listing> " +
        "[interiorPrototype|releaseReason|permissions]";

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2 || !_prototypes.TryIndex<NCPropertyListingPrototype>(args[1], out var listing))
        {
            shell.WriteError(Help);
            return;
        }

        if (!TryGetActor(shell, out var actor, out var recipient))
            return;

        var apartments = _systems.GetEntitySystem<NCApartmentSystem>();

        switch (args[0].ToLowerInvariant())
        {
            case "status":
                if (!apartments.TryGetProperty(listing.ApartmentId, out var property))
                {
                    shell.WriteError("Property has not been seeded into persistent storage yet.");
                    return;
                }

                shell.WriteLine(
                    $"Property={property.PropertyId}, Status={property.Status}, " +
                    $"Tenants={property.Tenants.Count}, Revision={property.AccessRevision}, " +
                    $"Interior={property.CurrentTemplateId ?? "none"}, Deployment={property.ActiveDeploymentId}");
                return;

            case "rent":
                if (args.Length != 3)
                {
                    shell.WriteError("Usage: nc-property rent <listing> <interiorPrototype>");
                    return;
                }

                WriteMutation(shell, await apartments.TryRentAsync(
                    listing.ApartmentId,
                    actor,
                    new ProtoId<NCZoneTemplatePrototype>(args[2]),
                    recipient));
                return;

            case "release":
                if (args.Length > 3 || args.Length == 3 &&
                    !Enum.TryParse<NCPropertyReleaseReason>(args[2], true, out _))
                {
                    shell.WriteError("Usage: nc-property release <listing> [VoluntaryMoveOut|Administrative]");
                    return;
                }

                var reason = args.Length == 3
                    ? Enum.Parse<NCPropertyReleaseReason>(args[2], true)
                    : NCPropertyReleaseReason.VoluntaryMoveOut;
                WriteMutation(shell, await apartments.TryReleaseAsync(listing.ApartmentId, actor, reason));
                return;

            case "card":
                if (args.Length > 3 || args.Length == 3 &&
                    !Enum.TryParse<NCPropertyPermission>(args[2], true, out _))
                {
                    shell.WriteError("Usage: nc-property card <listing> [Enter|All]");
                    return;
                }

                var permissions = args.Length == 3
                    ? Enum.Parse<NCPropertyPermission>(args[2], true)
                    : NCPropertyPermission.Enter;
                WriteMutation(shell, await apartments.TryIssueKeyCardAsync(
                    listing.ApartmentId,
                    actor,
                    actor,
                    permissions,
                    null,
                    recipient));
                return;

            case "invoke":
                if (args.Length != 2)
                {
                    shell.WriteError("Usage: nc-property invoke <listing>");
                    return;
                }
                if (!apartments.TryInvokeInterior(
                        listing.ApartmentId,
                        actor,
                        out var deployment,
                        out var error))
                {
                    shell.WriteError(error);
                    return;
                }

                shell.WriteLine($"Interior deployment queued: {deployment}.");
                return;

            default:
                shell.WriteError(Help);
                return;
        }
    }

    private bool TryGetActor(IConsoleShell shell, out NCCharacterId actor, out EntityUid recipient)
    {
        actor = default;
        recipient = default;
        if (shell.Player is not { } player || player.AttachedEntity is not { } attached)
        {
            shell.WriteError("Run this command as an in-game player with an attached entity.");
            return false;
        }

        if (!_preferences.TryGetSelectedNCCharacterId(player.UserId, out actor))
        {
            shell.WriteError("The current player has no selected persistent Night City character.");
            return false;
        }

        recipient = attached;
        return true;
    }

    private static void WriteMutation(IConsoleShell shell, NCPropertyMutationData result)
    {
        var balance = result.Balance is { } current ? $", Balance={current}" : string.Empty;
        var revision = result.Property is { } property ? $", Revision={property.AccessRevision}" : string.Empty;
        shell.WriteLine($"Result={result.Result}{balance}{revision}");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            1 => CompletionResult.FromHintOptions(
                ["status", "rent", "release", "card", "invoke"],
                "<operation>"),
            2 => CompletionResult.FromHintOptions(
                _prototypes.EnumeratePrototypes<NCPropertyListingPrototype>().Select(listing => listing.ID),
                "<property listing>"),
            3 when args[0].Equals("release", StringComparison.OrdinalIgnoreCase) =>
                CompletionResult.FromHintOptions(
                    [nameof(NCPropertyReleaseReason.VoluntaryMoveOut), nameof(NCPropertyReleaseReason.Administrative)],
                    "<release reason>"),
            3 when args[0].Equals("card", StringComparison.OrdinalIgnoreCase) =>
                CompletionResult.FromHintOptions(Enum.GetNames<NCPropertyPermission>(), "<permissions>"),
            3 when args[0].Equals("rent", StringComparison.OrdinalIgnoreCase) =>
                CompletionResult.FromHintOptions(
                    _prototypes.EnumeratePrototypes<NCZoneTemplatePrototype>()
                        .Where(template => template.Kind == NCZoneTemplateKind.ApartmentInterior)
                        .Select(template => template.ID),
                    "<interior prototype>"),
            _ => CompletionResult.Empty,
        };
    }
}
