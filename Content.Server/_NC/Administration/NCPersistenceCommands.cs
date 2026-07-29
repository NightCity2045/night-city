// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server.Administration;
using Content.Server.Preferences.Managers;
using Content.Server._NC.Identity;
using Content.Server._NC.Persistence;
using Content.Shared._NC.CCVar;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Legal;
using Content.Shared._NC.Organizations;
using Content.Shared.Administration;
using Robust.Shared.Configuration;
using Robust.Shared.Console;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Administration;

[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCRegisterPropertyCommand : IConsoleCommand
{
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IEntitySystemManager _systems = default!;

    public string Command => "nc_register_property";
    public string Description => "Registers persistent property ownership.";
    public string Help => $"{Command} <profileId> <prototypeId> <propertyType> [shareBasisPoints]";

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 3 || !int.TryParse(args[0], out var profileId))
        {
            shell.WriteError(Help);
            return;
        }

        var share = args.Length >= 4 && int.TryParse(args[3], out var parsed) ? parsed : 10_000;
        var result = await _database.RegisterNCPropertyAsync(
            args[1], args[2], profileId, share,
            shell.Player?.UserId.UserId ?? Guid.Empty,
            _systems.GetEntitySystem<GameTicker>().RoundId, Guid.NewGuid());
        shell.WriteLine(result.Success
            ? $"Property registered: {result.EntityId}"
            : $"Failed: {result.Error}");
    }
}

[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCRegisterBusinessCommand : IConsoleCommand
{
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private IEntitySystemManager _systems = default!;

    public string Command => "nc_register_business";
    public string Description => "Registers a persistent business and its bank account.";
    public string Help => $"{Command} <profileId> <name> <businessType> [shareBasisPoints]";

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 3 || !int.TryParse(args[0], out var profileId))
        {
            shell.WriteError(Help);
            return;
        }

        var share = args.Length >= 4 && int.TryParse(args[3], out var parsed) ? parsed : 10_000;
        var result = await _database.RegisterNCBusinessAsync(
            args[1], args[2], profileId, share,
            _configuration.GetCVar(NCCVars.BankCurrency),
            shell.Player?.UserId.UserId ?? Guid.Empty,
            _systems.GetEntitySystem<GameTicker>().RoundId, Guid.NewGuid());
        shell.WriteLine(result.Success
            ? $"Business registered: {result.EntityId}"
            : $"Failed: {result.Error}");
    }
}

[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCPersistenceAuditCommand : IConsoleCommand
{
    [Dependency] private IServerDbManager _database = default!;

    public string Command => "nc_persistence_audit";
    public string Description => "Shows recent persistent-character audit records.";
    public string Help => $"{Command} <profileId> [limit]";

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1 || !int.TryParse(args[0], out var profileId))
        {
            shell.WriteError(Help);
            return;
        }

        var limit = args.Length >= 2 && int.TryParse(args[1], out var parsed) ? parsed : 20;
        var rows = await _database.GetNCPersistenceAuditAsync(profileId, limit);
        foreach (var row in rows)
            shell.WriteLine($"{row.Timestamp:u} {row.Action}: {row.Reason}");
        if (rows.Count == 0)
            shell.WriteLine("No audit records.");
    }
}

[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCEmploymentCommand : IConsoleCommand
{
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IEntitySystemManager _systems = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public string Command => "nc_employment";
    public string Description => "Applies an audited administrative employment action.";
    public string Help =>
        $"{Command} <profileId> <action> <organizationPrototype> [positionPrototype] [reason]";

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 3 ||
            !int.TryParse(args[0], out var profileId) ||
            !Enum.TryParse<NCEmploymentAction>(args[1], true, out var action) ||
            !_prototypes.TryIndex<NCOrganizationPrototype>(args[2], out var organization))
        {
            shell.WriteError(Help);
            return;
        }

        NCPositionPrototype? position = null;
        if (args.Length >= 4 && args[3] != "-" &&
            !_prototypes.TryIndex(args[3], out position))
        {
            shell.WriteError($"Unknown position prototype: {args[3]}");
            return;
        }

        var result = await _database.ApplyNCEmploymentActionAsync(new NCEmploymentMutation(
            profileId,
            null,
            shell.Player?.UserId.UserId ?? Guid.Empty,
            action,
            organization.OrganizationId,
            position?.PositionId,
            args.Length >= 5 ? string.Join(' ', args[4..]) : "administrative-change",
            _systems.GetEntitySystem<GameTicker>().RoundId,
            false,
            null,
            Guid.NewGuid()));
        shell.WriteLine(result.Success ? "Employment updated." : $"Failed: {result.Error}");
    }
}

[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCProfileIdCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "nc_profile_id";
    public string Description => "Shows the stable profile ID bound to an online entity.";
    public string Help => $"{Command} <netEntity>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 ||
            !NetEntity.TryParse(args[0], out var netEntity) ||
            !_entities.TryGetEntity(netEntity, out var entity) ||
            !_entities.System<CharacterIdentitySystem>()
                .TryGetIdentity(entity.Value, out var profileId, out var accountId))
        {
            shell.WriteError(Help);
            return;
        }

        shell.WriteLine($"ProfileId: {profileId.Value}; account: {accountId}");
    }
}

[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCConfirmPermadeathCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IEntitySystemManager _systems = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;

    public string Command => "nc_confirm_permadeath";
    public string Description => "Explicitly confirms a dead character for deferred permanent deletion.";
    public string Help => $"{Command} <netEntity> <reason>";

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2 ||
            !NetEntity.TryParse(args[0], out var netEntity) ||
            !_entities.TryGetEntity(netEntity, out var target))
        {
            shell.WriteError(Help);
            return;
        }

        var actorAccountId = shell.Player?.UserId.UserId ?? Guid.Empty;
        ProfileId? actorProfileId = null;
        if (shell.Player != null &&
            _preferences.TryGetSelectedProfileId(shell.Player.UserId, out var selectedProfileId))
        {
            actorProfileId = selectedProfileId;
        }

        // Deletion remains deferred; revival before lobby/round cleanup cancels this declaration.
        var result = await _systems.GetEntitySystem<PermadeathSystem>().ConfirmPermadeathAsync(
            target.Value,
            actorAccountId,
            actorProfileId,
            string.Join(' ', args[1..]));
        shell.WriteLine(result.Success
            ? "Permadeath confirmed; final deletion is deferred."
            : $"Failed: {result.Error}");
    }
}

[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCLicenseCommand : IConsoleCommand
{
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private IEntitySystemManager _systems = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public string Command => "nc_license";
    public string Description => "Issues or revokes a persistent character license.";
    public string Help => $"{Command} <profileId> <issue|revoke> <licensePrototype> <reason>";

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 4 ||
            !int.TryParse(args[0], out var profileId) ||
            !_prototypes.TryIndex<NCLicensePrototype>(args[2], out var prototype) ||
            args[1] is not ("issue" or "revoke"))
        {
            shell.WriteError(Help);
            return;
        }

        var actorProfileId = GetActorProfileId(shell.Player, _preferences);
        var result = await _database.SetNCLicenseAsync(
            profileId,
            prototype.ID,
            args[1] == "issue",
            prototype.ValidityDays,
            actorProfileId,
            shell.Player?.UserId.UserId,
            shell.Player?.UserId.UserId ?? Guid.Empty,
            string.Join(' ', args[3..]),
            _systems.GetEntitySystem<GameTicker>().RoundId,
            Guid.NewGuid());
        shell.WriteLine(result.Success ? "License updated." : $"Failed: {result.Error}");
    }

    private static int? GetActorProfileId(
        ICommonSession? session,
        IServerPreferencesManager preferences)
    {
        return session != null &&
               preferences.TryGetSelectedProfileId(session.UserId, out var profileId)
            ? profileId.Value
            : null;
    }
}

[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCDocumentCommand : IConsoleCommand
{
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private IEntitySystemManager _systems = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public string Command => "nc_document";
    public string Description => "Issues or revokes a persistent character document.";
    public string Help => $"{Command} <profileId> <issue|revoke> <documentPrototype> <reason>";

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 4 ||
            !int.TryParse(args[0], out var profileId) ||
            !_prototypes.TryIndex<NCDocumentPrototype>(args[2], out var prototype) ||
            args[1] is not ("issue" or "revoke"))
        {
            shell.WriteError(Help);
            return;
        }

        int? actorProfileId = null;
        if (shell.Player != null &&
            _preferences.TryGetSelectedProfileId(shell.Player.UserId, out var selectedProfileId))
        {
            actorProfileId = selectedProfileId.Value;
        }

        var result = await _database.SetNCDocumentAsync(
            profileId,
            prototype.ID,
            args[1] == "issue",
            prototype.ValidityDays,
            string.Empty,
            actorProfileId,
            shell.Player?.UserId.UserId,
            shell.Player?.UserId.UserId ?? Guid.Empty,
            string.Join(' ', args[3..]),
            _systems.GetEntitySystem<GameTicker>().RoundId,
            Guid.NewGuid());
        shell.WriteLine(result.Success ? "Document updated." : $"Failed: {result.Error}");
    }
}

[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCTransferOwnershipCommand : IConsoleCommand
{
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IEntitySystemManager _systems = default!;

    public string Command => "nc_transfer_share";
    public string Description => "Transfers a registered property or business ownership share.";
    public string Help =>
        $"{Command} <property|business> <assetGuid> <sourceProfileId> <targetProfileId> <basisPoints> <reason>";

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 6 ||
            !Guid.TryParse(args[1], out var assetId) ||
            !int.TryParse(args[2], out var sourceProfileId) ||
            !int.TryParse(args[3], out var targetProfileId) ||
            !int.TryParse(args[4], out var shareBasisPoints) ||
            args[0] is not ("property" or "business"))
        {
            shell.WriteError(Help);
            return;
        }

        var actor = shell.Player?.UserId.UserId ?? Guid.Empty;
        var reason = string.Join(' ', args[5..]);
        var roundId = _systems.GetEntitySystem<GameTicker>().RoundId;
        var requestId = Guid.NewGuid();
        var result = args[0] == "property"
            ? await _database.TransferNCPropertyShareAsync(
                assetId,
                NCOwnerType.Character,
                sourceProfileId.ToString(),
                NCOwnerType.Character,
                targetProfileId.ToString(),
                shareBasisPoints,
                actor,
                reason,
                roundId,
                requestId)
            : await _database.TransferNCBusinessShareAsync(
                assetId,
                sourceProfileId,
                targetProfileId,
                shareBasisPoints,
                actor,
                reason,
                roundId,
                requestId);
        shell.WriteLine(result.Success ? "Ownership share transferred." : $"Failed: {result.Error}");
    }
}

[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCResolveInheritanceCommand : IConsoleCommand
{
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IEntitySystemManager _systems = default!;

    public string Command => "nc_resolve_inheritance";
    public string Description => "Assigns a pending estate share to a new owner.";
    public string Help =>
        $"{Command} <caseGuid> <character|organization|business|system> <ownerId> <reason>";

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 4 ||
            !Guid.TryParse(args[0], out var caseId) ||
            !Enum.TryParse<NCOwnerType>(args[1], true, out var ownerType))
        {
            shell.WriteError(Help);
            return;
        }

        var result = await _database.ResolveNCInheritanceAsync(
            caseId,
            ownerType,
            args[2],
            shell.Player?.UserId.UserId ?? Guid.Empty,
            string.Join(' ', args[3..]),
            _systems.GetEntitySystem<GameTicker>().RoundId,
            Guid.NewGuid());
        shell.WriteLine(result.Success ? "Inheritance resolved." : $"Failed: {result.Error}");
    }
}
