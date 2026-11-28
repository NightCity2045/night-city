// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using Content.Server._NC.Bank;
using Content.Server._NC.Identity;
using Content.Shared._NC.Dispatch;
using Content.Shared.GameTicking;
using Content.Shared.Mobs;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Dispatch;

/// <summary>
/// Trauma Team subscriptions. A subscription is paid when it starts and on every payday; a subscriber's biomonitor
/// calls Trauma by itself when they fall critical or die, and a crew arriving after the plan's response time
/// refunds one payment. Without a subscription a call to Trauma is a paid one-off.
/// </summary>
public sealed partial class NCDispatchSystem
{
    private readonly Dictionary<int, ProtoId<NCTraumaPlanPrototype>> _plans = new();

    private void InitializeTrauma()
    {
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
        SubscribeLocalEvent<NCBankPaydayEvent>(OnPayday);
        SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged);
    }

    private async void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        if (!_preferences.TryGetSelectedNCCharacterId(args.Player.UserId, out var characterId) || !characterId.IsValid)
            return;

        var profileId = characterId.Value;
        try
        {
            var plan = await _db.GetNCTraumaSubscriptionAsync(profileId);
            if (plan != null && _prototypes.HasIndex<NCTraumaPlanPrototype>(plan))
                _plans.TryAdd(profileId, plan);
        }
        catch (Exception exception)
        {
            Log.Error($"Unable to load the Trauma subscription of profile {profileId}: {exception}");
        }
    }

    private bool TryGetProfile(EntityUid user, out int profileId)
    {
        profileId = 0;
        if (!_minds.TryGetMind(user, out var mindId, out _) ||
            !TryComp<NCCharacterIdentityComponent>(mindId, out var identity) ||
            !identity.CharacterId.IsValid)
        {
            return false;
        }

        profileId = identity.CharacterId.Value;
        return true;
    }

    /// <summary>The Trauma plan of the character a person plays, if subscribed.</summary>
    public NCTraumaPlanPrototype? GetPlan(EntityUid user)
    {
        return TryGetProfile(user, out var profileId) ? GetPlan(profileId) : null;
    }

    public NCTraumaPlanPrototype? GetPlan(int profileId)
    {
        return _plans.TryGetValue(profileId, out var plan) && _prototypes.TryIndex(plan, out var prototype)
            ? prototype
            : null;
    }

    /// <summary>Plans offered to residents, best first.</summary>
    public IEnumerable<NCTraumaPlanPrototype> GetPlans()
    {
        return _prototypes.EnumeratePrototypes<NCTraumaPlanPrototype>().OrderByDescending(plan => plan.Rank);
    }

    /// <summary>Starts or changes a subscription; the first payment is charged now.</summary>
    public bool TrySubscribe(EntityUid user, ProtoId<NCTraumaPlanPrototype> planId, bool quiet = false)
    {
        if (!CanSubscribe(user, planId, quiet) ||
            !TryGetProfile(user, out var profileId) ||
            !_bank.TryGetCharacterAccount(user, out var account))
        {
            return false;
        }

        var plan = _prototypes.Index(planId);
        var service = _prototypes.Index(TraumaService);
        if (!_bank.TryPayService(account, service.Account!.Value, plan.Price, Loc.GetString(plan.Name), user))
            return false;

        SetPlan(profileId, plan.ID);
        if (!quiet)
            _popup.PopupEntity(Loc.GetString("nc-trauma-subscribed", ("plan", Loc.GetString(plan.Name))), user, user);
        return true;
    }

    public bool CanSubscribe(EntityUid user, ProtoId<NCTraumaPlanPrototype> planId, bool quiet = false)
    {
        string? error = null;
        if (!_prototypes.TryIndex(planId, out var plan) ||
            !_prototypes.TryIndex(TraumaService, out var service) || service.Account == null)
        {
            error = "nc-dispatch-call-unavailable";
        }
        else if (!TryGetProfile(user, out _) || !_bank.TryGetCharacterAccount(user, out var account))
            error = "nc-dispatch-call-no-account";
        else if (GetPlan(user)?.ID == plan.ID)
            error = "nc-trauma-already-subscribed";
        else if (account.Balance < plan.Price)
            error = "nc-dispatch-call-no-money";

        if (error == null)
            return true;

        if (!quiet)
            _popup.PopupEntity(Loc.GetString(error), user, user);
        return false;
    }

    /// <summary>Ends the subscription. Nothing is paid back.</summary>
    public bool TryUnsubscribe(EntityUid user, bool quiet = false)
    {
        if (!TryGetProfile(user, out var profileId) || !_plans.ContainsKey(profileId))
            return false;

        SetPlan(profileId, null);
        if (!quiet)
            _popup.PopupEntity(Loc.GetString("nc-trauma-unsubscribed"), user, user);
        return true;
    }

    /// <summary>Sets a character's plan without any payment. Used by subscriptions, admins and tests.</summary>
    public void SetPlan(int profileId, ProtoId<NCTraumaPlanPrototype>? plan)
    {
        if (plan is { } value)
            _plans[profileId] = value;
        else
            _plans.Remove(profileId);

        _dirty = true;
        PersistPlan(profileId, plan?.Id);
    }

    private async void PersistPlan(int profileId, string? plan)
    {
        try
        {
            await _db.SetNCTraumaSubscriptionAsync(profileId, plan);
        }
        catch (Exception exception)
        {
            Log.Error($"Unable to save the Trauma subscription of profile {profileId}: {exception}");
        }
    }

    /// <summary>Every payday each subscriber in the city pays their plan; one who cannot pay loses it.</summary>
    private void OnPayday(ref NCBankPaydayEvent args)
    {
        if (!_prototypes.TryIndex(TraumaService, out var service) || service.Account is not { } department)
            return;

        var query = EntityQueryEnumerator<ActorComponent>();
        while (query.MoveNext(out var uid, out var actor))
        {
            if (_mobState.IsDead(uid) || !TryGetProfile(uid, out var profileId) || GetPlan(profileId) is not { } plan ||
                !_bank.TryGetCharacterAccount(uid, out var account))
            {
                continue;
            }

            if (_bank.TryPayService(account, department, plan.Price, Loc.GetString(plan.Name), uid))
            {
                _chat.DispatchServerMessage(actor.PlayerSession, Loc.GetString("nc-trauma-billed",
                    ("plan", Loc.GetString(plan.Name)),
                    ("amount", plan.Price)));
                continue;
            }

            SetPlan(profileId, null);
            _chat.DispatchServerMessage(actor.PlayerSession, Loc.GetString("nc-trauma-lapsed", ("plan", Loc.GetString(plan.Name))));
        }
    }

    /// <summary>The biomonitor of a subscriber calls Trauma when they fall critical or die.</summary>
    private void OnMobStateChanged(MobStateChangedEvent args)
    {
        if (args.NewMobState is not (MobState.Critical or MobState.Dead) ||
            GetPlan(args.Target) is not { } plan ||
            _incidents.Any(incident => incident.Service == TraumaService && incident.Subject == args.Target) ||
            !_mapCoordinates.TryGetCoordinates(args.Target, out var location))
        {
            return;
        }

        var message = Loc.GetString(args.NewMobState == MobState.Dead
            ? "nc-dispatch-biomonitor-dead"
            : "nc-dispatch-biomonitor-critical");
        var incident = CreateIncident(TraumaService, NCDispatchIncidentKind.Biomonitor, args.Target, location,
            Name(args.Target), message);
        incident.Subject = args.Target;
        incident.Caller = args.Target;
        incident.Client = _bank.TryGetCharacterAccount(args.Target, out var account) ? account : null;
        incident.Plan = plan.ID;
        incident.Deadline = incident.Created + plan.ResponseTime;
        Announce(incident);
    }

    /// <summary>What a late crew pays back: one payment of the plan, or the one-off price.</summary>
    private long LateRefund(NCDispatchIncident incident)
    {
        if (incident.Plan is { } planId && _prototypes.TryIndex(planId, out var plan))
            return plan.Price;

        return incident.Paid;
    }
}
