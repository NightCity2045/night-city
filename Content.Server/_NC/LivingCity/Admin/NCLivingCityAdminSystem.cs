// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Administration.Logs;
using Content.Server._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Admin;
using Content.Shared.Administration;
using Content.Shared.Administration.Managers;
using Content.Shared.Database;
using Robust.Shared.Network;
using Robust.Shared.Timing;

namespace Content.Server._NC.LivingCity.Admin;

/// <summary>Minimal permission-checked F7 boundary for creating one persistent physical GPT resident.</summary>
public sealed partial class NCLivingCityAdminSystem : EntitySystem
{
    private static readonly TimeSpan RequestInterval = TimeSpan.FromMilliseconds(300);

    [Dependency] private ISharedAdminManager _admins = default!;
    [Dependency] private IAdminLogManager _adminLog = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private NCRandomResidentFactorySystem _randomResidents = default!;

    private readonly Dictionary<NetUserId, TimeSpan> _lastRequestAt = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCLivingCityAdminRandomResidentRequest>(OnRandomResidentRequest);
    }

    public override void Shutdown()
    {
        _lastRequestAt.Clear();
        base.Shutdown();
    }

    private async void OnRandomResidentRequest(
        NCLivingCityAdminRandomResidentRequest request,
        EntitySessionEventArgs args)
    {
        if (request.RequestId == Guid.Empty)
            return;
        if (!CanMutate(args))
        {
            Send(args, request.RequestId, NCLivingCityAdminMutationStatus.Unauthorized, [], "not-authorized");
            return;
        }
        if (!AcceptRate(args.SenderSession.UserId))
        {
            Send(args, request.RequestId, NCLivingCityAdminMutationStatus.RateLimited, [], "rate-limited");
            return;
        }
        if (args.SenderSession.AttachedEntity is not { } administrator || !Exists(administrator))
        {
            Send(args, request.RequestId, NCLivingCityAdminMutationStatus.Rejected, [],
                "administrator-has-no-attached-entity");
            return;
        }

        try
        {
            var result = await _randomResidents.CreateAtAsync(administrator, $"admin-f7:{args.SenderSession.UserId}");
            var fields = new[]
            {
                new NCLivingCityAdminField("actor-id", result.ActorId.ToString()),
                new NCLivingCityAdminField("full-name", result.FullName),
                new NCLivingCityAdminField("persona-profile", result.PersonaProfileId),
                new NCLivingCityAdminField("persistence-status", result.PersistenceStatus.ToString()),
                new NCLivingCityAdminField("spawn-status", result.Spawn?.Status.ToString() ?? "none"),
            };
            var status = result.Success
                ? NCLivingCityAdminMutationStatus.Applied
                : NCLivingCityAdminMutationStatus.Rejected;
            AddAudit(args, result.ActorId.ToString(), status, result.Error);
            Send(args, request.RequestId, status, fields, result.Error);
        }
        catch (Exception exception)
        {
            Log.Error($"F7 random resident creation failed: {exception}");
            AddAudit(args, "random-resident", NCLivingCityAdminMutationStatus.Failed,
                "storage-or-validation-failure");
            Send(args, request.RequestId, NCLivingCityAdminMutationStatus.Failed, [],
                "storage-or-validation-failure");
        }
    }

    private bool CanMutate(EntitySessionEventArgs args) =>
        _admins.HasAdminFlag(args.SenderSession, AdminFlags.Server) ||
        _admins.HasAdminFlag(args.SenderSession, AdminFlags.Mapping);

    private bool AcceptRate(NetUserId userId)
    {
        var now = _timing.RealTime;
        if (_lastRequestAt.TryGetValue(userId, out var last) && now - last < RequestInterval)
            return false;
        if (_lastRequestAt.Count >= 256 && !_lastRequestAt.ContainsKey(userId))
            _lastRequestAt.Clear();
        _lastRequestAt[userId] = now;
        return true;
    }

    private void AddAudit(
        EntitySessionEventArgs args,
        string target,
        NCLivingCityAdminMutationStatus status,
        string? error)
    {
        _adminLog.Add(
            LogType.Action,
            LogImpact.Medium,
            $"{args.SenderSession.Name} ({args.SenderSession.UserId}) used F7 to create persistent NPC " +
            $"{target}: result={status}; error={error ?? "none"}");
    }

    private void Send(
        EntitySessionEventArgs args,
        Guid requestId,
        NCLivingCityAdminMutationStatus status,
        NCLivingCityAdminField[] fields,
        string? error)
    {
        NCLivingCityAdminTelemetry.Record(status == NCLivingCityAdminMutationStatus.Applied, error, fields);
        RaiseNetworkEvent(new NCLivingCityAdminRandomResidentResult(requestId, status, fields, error),
            args.SenderSession);
    }
}
