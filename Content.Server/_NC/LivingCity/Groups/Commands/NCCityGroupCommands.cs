// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Linq;
using Content.Server.Administration;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Groups;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Groups.Commands;

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityGroupInfoCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityGroupRegistrySystem _groups = default!;

    public override string Command => "alife_group_info";
    public override string Description => "Inspect one persistent group without materializing its members.";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!NCCityGroupCommandHelpers.TryParseGroup(args, out var groupId))
        {
            shell.WriteError($"Usage: {Command} <groupId>");
            return;
        }

        var group = await _groups.GetAsync(groupId, true);
        if (group == null)
        {
            shell.WriteError($"Persistent group {groupId} was not found or has an unreadable snapshot.");
            return;
        }

        var snapshot = group.Snapshot;
        shell.WriteLine(
            $"{snapshot.DisplayName} ({groupId}), profile={snapshot.ProfileId}, " +
            $"version={group.AggregateVersion}/{snapshot.DataVersion}, status={snapshot.Status}.");
        shell.WriteLine(
            $"Leader={snapshot.LeaderActorId}, faction={snapshot.FactionId}, members={snapshot.Members.Length}, " +
            $"alert={snapshot.AlertState}, morale={snapshot.Resources.Morale}.");
        shell.WriteLine(
            $"Location={snapshot.Route.CurrentZone}, destination={snapshot.Route.DestinationZone}, " +
            $"mode={snapshot.Route.TravelMode}, arrival={snapshot.Route.ExpectedArrivalAt?.Elapsed.ToString() ?? "none"}.");
        shell.WriteLine(
            $"Purpose={snapshot.Directive.Purpose}, order={snapshot.Directive.CurrentOrder}, " +
            $"mission={snapshot.Directive.MissionReference}, incident={snapshot.Directive.IncidentId?.ToString("D") ?? "none"}.");
        foreach (var member in snapshot.Members)
            shell.WriteLine($"  {member.ActorId}: role={member.Role}, joined={member.JoinedAt.Elapsed}.");
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityGroupCreateCommand : LocalizedEntityCommands
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCCityGroupRegistrySystem _groups = default!;

    public override string Command => "alife_group_create";
    public override string Description => "Create an audited deterministic persistent group.";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 7 || !Guid.TryParse(args[0], out var scopeId) || scopeId == Guid.Empty ||
            !_prototypes.HasIndex(new ProtoId<NCCityGroupProfilePrototype>(args[2])) ||
            !NCCityGroupCommandHelpers.TryParseActor(args[3], out var leaderActorId) ||
            !NCZoneId.TryParse(args[4], out var currentZone))
        {
            shell.WriteError(
                $"Usage: {Command} <scopeGuid> <stableLocalId> <profile> <leaderActorId> " +
                "<currentZoneId> <leaderRole> <display name>");
            return;
        }

        var result = await _groups.CreateAsync(new NCCityGroupCreateRequest(
            Guid.NewGuid(),
            scopeId,
            args[1],
            string.Join(' ', args.Skip(6)),
            new ProtoId<NCCityGroupProfilePrototype>(args[2]),
            default,
            leaderActorId,
            args[5],
            currentZone,
            string.Empty,
            Guid.NewGuid(),
            AuditSource: NCCityGroupCommandHelpers.AuditSource(shell)));
        NCCityGroupCommandHelpers.WriteMutation(shell, result);
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityGroupAddMemberCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityGroupRegistrySystem _groups = default!;

    public override string Command => "alife_group_add";
    public override string Description => "Add one persistent actor to a group with an atomic membership update.";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 3 || !NCCityGroupCommandHelpers.TryParseGroup(args[0], out var groupId) ||
            !NCCityGroupCommandHelpers.TryParseActor(args[1], out var actorId))
        {
            shell.WriteError($"Usage: {Command} <groupId> <actorId> <role>");
            return;
        }

        var group = await _groups.GetAsync(groupId, true);
        if (group == null)
        {
            shell.WriteError($"Persistent group {groupId} was not found.");
            return;
        }

        NCCityGroupCommandHelpers.WriteMutation(shell, await _groups.AddMemberAsync(new NCCityGroupAddMemberRequest(
            Guid.NewGuid(), groupId, group.AggregateVersion, actorId, args[2], Guid.NewGuid(),
            AuditSource: NCCityGroupCommandHelpers.AuditSource(shell))));
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityGroupRemoveMemberCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityGroupRegistrySystem _groups = default!;

    public override string Command => "alife_group_remove";
    public override string Description => "Remove one actor from a group and optionally appoint a successor leader.";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is < 2 or > 3 || !NCCityGroupCommandHelpers.TryParseGroup(args[0], out var groupId) ||
            !NCCityGroupCommandHelpers.TryParseActor(args[1], out var actorId) ||
            !NCCityGroupCommandHelpers.TryParseOptionalActor(args.Length == 3 ? args[2] : "none", out var successor))
        {
            shell.WriteError($"Usage: {Command} <groupId> <actorId> [successorActorId|none]");
            return;
        }

        var group = await _groups.GetAsync(groupId, true);
        if (group == null)
        {
            shell.WriteError($"Persistent group {groupId} was not found.");
            return;
        }

        NCCityGroupCommandHelpers.WriteMutation(shell, await _groups.RemoveMemberAsync(
            new NCCityGroupRemoveMemberRequest(
                Guid.NewGuid(), groupId, group.AggregateVersion, actorId, successor, Guid.NewGuid(),
                AuditSource: NCCityGroupCommandHelpers.AuditSource(shell))));
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityGroupTransferMemberCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityGroupRegistrySystem _groups = default!;

    public override string Command => "alife_group_transfer";
    public override string Description => "Atomically transfer one actor between two persistent groups.";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is < 4 or > 5 || !NCCityGroupCommandHelpers.TryParseActor(args[0], out var actorId) ||
            !NCCityGroupCommandHelpers.TryParseGroup(args[1], out var sourceGroupId) ||
            !NCCityGroupCommandHelpers.TryParseGroup(args[2], out var destinationGroupId) ||
            !NCCityGroupCommandHelpers.TryParseOptionalActor(args.Length == 5 ? args[4] : "none", out var successor))
        {
            shell.WriteError(
                $"Usage: {Command} <actorId> <sourceGroupId> <destinationGroupId> <destinationRole> " +
                "[sourceSuccessorActorId|none]");
            return;
        }

        var source = await _groups.GetAsync(sourceGroupId, true);
        var destination = await _groups.GetAsync(destinationGroupId, true);
        if (source == null || destination == null)
        {
            shell.WriteError("The source or destination group was not found.");
            return;
        }

        NCCityGroupCommandHelpers.WriteMutation(shell, await _groups.TransferMemberAsync(
            new NCCityGroupTransferMemberRequest(
                Guid.NewGuid(), sourceGroupId, source.AggregateVersion, destinationGroupId,
                destination.AggregateVersion, actorId, args[3], successor, Guid.NewGuid(),
                AuditSource: NCCityGroupCommandHelpers.AuditSource(shell))));
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityGroupSetLeaderCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityGroupRegistrySystem _groups = default!;

    public override string Command => "alife_group_leader";
    public override string Description => "Assign an existing group member as leader.";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2 || !NCCityGroupCommandHelpers.TryParseGroup(args[0], out var groupId) ||
            !NCCityGroupCommandHelpers.TryParseActor(args[1], out var leaderActorId))
        {
            shell.WriteError($"Usage: {Command} <groupId> <leaderActorId>");
            return;
        }

        var group = await _groups.GetAsync(groupId, true);
        if (group == null)
        {
            shell.WriteError($"Persistent group {groupId} was not found.");
            return;
        }

        NCCityGroupCommandHelpers.WriteMutation(shell, await _groups.SetLeaderAsync(new NCCityGroupSetLeaderRequest(
            Guid.NewGuid(), groupId, group.AggregateVersion, leaderActorId, Guid.NewGuid(),
            AuditSource: NCCityGroupCommandHelpers.AuditSource(shell))));
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityGroupOrderCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityGroupRegistrySystem _groups = default!;

    public override string Command => "alife_group_order";
    public override string Description => "Set an audited semantic order on a persistent group.";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 4 || !NCCityGroupCommandHelpers.TryParseGroup(args[0], out var groupId) ||
            !NCCityGroupCommandHelpers.TryParseOptionalGuid(args[2], out var incidentId))
        {
            shell.WriteError($"Usage: {Command} <groupId> <missionRef|none> <incidentId|none> <order>");
            return;
        }

        var group = await _groups.GetAsync(groupId, true);
        if (group == null)
        {
            shell.WriteError($"Persistent group {groupId} was not found.");
            return;
        }

        var mission = args[1].Equals("none", StringComparison.OrdinalIgnoreCase) ? string.Empty : args[1];
        NCCityGroupCommandHelpers.WriteMutation(shell, await _groups.SetDirectiveAsync(
            new NCCityGroupSetDirectiveRequest(
                Guid.NewGuid(),
                groupId,
                group.AggregateVersion,
                group.Snapshot.Directive.Purpose,
                string.Join(' ', args.Skip(3)),
                mission,
                incidentId,
                Guid.NewGuid(),
                AuditSource: NCCityGroupCommandHelpers.AuditSource(shell))));
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityGroupMaterializeCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityGroupPhysicalizationSystem _physicalization = default!;

    public override string Command => "alife_group_materialize";
    public override string Description => "Queue coordinated materialization and formation assignment for one group.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is < 1 or > 2 || !NCCityGroupCommandHelpers.TryParseGroup(args[0], out var groupId) ||
            !TryPriority(args, out var priority))
        {
            shell.WriteError($"Usage: {Command} <groupId> [priority]");
            return;
        }

        var state = _physicalization.Request(new NCCityGroupPhysicalizationRequest(
            Guid.NewGuid(),
            groupId,
            NCCityGroupPhysicalizationDirection.Materialize,
            NCCityGroupPhysicalizationReason.Admin,
            priority,
            unchecked((uint) groupId.Value.GetHashCode()),
            null,
            "admin.group-materialize"));
        NCCityGroupCommandHelpers.WritePhysicalization(shell, state);
    }

    private static bool TryPriority(string[] args, out int priority)
    {
        if (args.Length == 1)
        {
            priority = 900;
            return true;
        }
        return int.TryParse(args[1], out priority);
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityGroupDematerializeCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityGroupPhysicalizationSystem _physicalization = default!;

    public override string Command => "alife_group_dematerialize";
    public override string Description => "Queue coordinated physical-to-strategic hand-off for one group.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is < 1 or > 2 || !NCCityGroupCommandHelpers.TryParseGroup(args[0], out var groupId) ||
            !TryPriority(args, out var priority))
        {
            shell.WriteError($"Usage: {Command} <groupId> [priority]");
            return;
        }

        var state = _physicalization.Request(new NCCityGroupPhysicalizationRequest(
            Guid.NewGuid(),
            groupId,
            NCCityGroupPhysicalizationDirection.Dematerialize,
            NCCityGroupPhysicalizationReason.Admin,
            priority,
            unchecked((uint) groupId.Value.GetHashCode()),
            null,
            "admin.group-dematerialize"));
        NCCityGroupCommandHelpers.WritePhysicalization(shell, state);
    }

    private static bool TryPriority(string[] args, out int priority)
    {
        if (args.Length == 1)
        {
            priority = 900;
            return true;
        }
        return int.TryParse(args[1], out priority);
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityGroupPhysicalizationInfoCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityGroupPhysicalizationSystem _physicalization = default!;

    public override string Command => "alife_group_physicalization_info";
    public override string Description => "Inspect one retained group physicalization request.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || !Guid.TryParse(args[0], out var requestId) || requestId == Guid.Empty)
        {
            shell.WriteError($"Usage: {Command} <requestId>");
            return;
        }
        if (!_physicalization.TryGetRequest(requestId, out var state))
        {
            shell.WriteError($"Group physicalization request {requestId:D} was not retained.");
            return;
        }
        NCCityGroupCommandHelpers.WritePhysicalization(shell, state);
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityGroupFormationInfoCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityGroupRuntimeFormationSystem _formations = default!;

    public override string Command => "alife_group_formation_info";
    public override string Description => "Inspect one complete round-local group formation.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!NCCityGroupCommandHelpers.TryParseGroup(args, out var groupId))
        {
            shell.WriteError($"Usage: {Command} <groupId>");
            return;
        }
        if (!_formations.TryGetGroup(groupId, out var formation))
        {
            shell.WriteError($"Group {groupId} has no complete physical formation.");
            return;
        }
        shell.WriteLine(
            $"Group={groupId}, version={formation.SourceGroupVersion}, policy={formation.Policy}, " +
            $"leader={formation.LeaderActorId}/{formation.LeaderEntity}, members={formation.Members.Count}.");
        foreach (var member in formation.Members)
        {
            shell.WriteLine(
                $"  slot={member.SlotIndex}, actor={member.ActorId}, entity={member.EntityUid}, " +
                $"role={member.Role}, leader={member.IsLeader}, offset={member.DesiredOffset}.");
        }
    }
}

internal static class NCCityGroupCommandHelpers
{
    public static bool TryParseGroup(string[] args, out NCGroupId groupId)
    {
        groupId = default;
        return args.Length == 1 && TryParseGroup(args[0], out groupId);
    }

    public static bool TryParseGroup(string value, out NCGroupId groupId)
    {
        groupId = default;
        if (!Guid.TryParse(value, out var raw) || raw == Guid.Empty)
            return false;
        groupId = new NCGroupId(raw);
        return true;
    }

    public static bool TryParseActor(string value, out NCActorId actorId)
    {
        actorId = default;
        if (!Guid.TryParse(value, out var raw) || raw == Guid.Empty)
            return false;
        actorId = new NCActorId(raw);
        return true;
    }

    public static bool TryParseOptionalActor(string value, out NCActorId actorId)
    {
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            actorId = default;
            return true;
        }

        return TryParseActor(value, out actorId);
    }

    public static bool TryParseOptionalGuid(string value, out Guid? id)
    {
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            id = null;
            return true;
        }

        if (!Guid.TryParse(value, out var raw) || raw == Guid.Empty)
        {
            id = null;
            return false;
        }

        id = raw;
        return true;
    }

    public static string AuditSource(IConsoleShell shell) =>
        shell.Player is { } player ? $"admin:{player.UserId}" : "admin:server-console";

    public static void WriteMutation(IConsoleShell shell, NCCityGroupMutationResult result)
    {
        if (result.Status is not (NCCityGroupMutationStatus.Applied or NCCityGroupMutationStatus.Duplicate))
        {
            shell.WriteError($"{result.Status}: {result.Error}");
            return;
        }

        var versions = string.Join(", ", result.Groups.Select(group =>
            $"{group.Snapshot.GroupId}@{group.AggregateVersion}"));
        shell.WriteLine($"{result.Status}: groups=[{versions}].");
    }

    public static void WritePhysicalization(
        IConsoleShell shell,
        NCCityGroupPhysicalizationRequestState state)
    {
        var text =
            $"{state.Status}: request={state.RequestId:D}, group={state.GroupId}, " +
            $"direction={state.Direction}, members={state.CompletedMembers}/{state.TotalMembers}, " +
            $"attempts={state.Attempts}, expires={state.ExpiresAt.Elapsed}.";
        if (state.Status is NCCityGroupPhysicalizationStatus.Rejected or
            NCCityGroupPhysicalizationStatus.Expired or
            NCCityGroupPhysicalizationStatus.PartialFailure)
        {
            shell.WriteError($"{text} {state.Error}");
        }
        else
        {
            shell.WriteLine(state.Error == null ? text : $"{text} {state.Error}");
        }
    }
}
