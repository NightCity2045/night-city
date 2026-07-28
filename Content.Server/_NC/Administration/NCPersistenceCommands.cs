using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server.Administration;
using Content.Server._NC.Identity;
using Content.Shared._NC.CCVar;
using Content.Shared._NC.Organizations;
using Content.Shared.Administration;
using Robust.Shared.Configuration;
using Robust.Shared.Console;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Administration;

[AdminCommand(AdminFlags.Admin)]
public sealed class NCRegisterPropertyCommand : IConsoleCommand
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
public sealed class NCRegisterBusinessCommand : IConsoleCommand
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
public sealed class NCPersistenceAuditCommand : IConsoleCommand
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
public sealed class NCEmploymentCommand : IConsoleCommand
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
            Guid.NewGuid()));
        shell.WriteLine(result.Success ? "Employment updated." : $"Failed: {result.Error}");
    }
}

[AdminCommand(AdminFlags.Admin)]
public sealed class NCProfileIdCommand : IConsoleCommand
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
