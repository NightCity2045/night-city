// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.CityServices;
using Content.Server._NC.LivingCity.Employment;
using Content.Server._NC.LivingCity.Groups;
using Content.Server._NC.LivingCity.Neural;
using Content.Server._NC.LivingCity.Social;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Roles;
using Robust.Shared.Asynchronous;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors;

/// <summary>
/// Turns a neural interpretation of spoken words into ordinary persistent commitments. The model never grants
/// authority: hierarchy, group leadership, destination identity and actor versions are revalidated here.
/// </summary>
public sealed partial class NCHumanDirectiveSystem : EntitySystem
{
    private const string Capability = "NCCognitionAcceptDirective";
    private const string AcceptAction = "human-accept-authorized-directive";
    private static readonly ProtoId<NCHumanDirectivePolicyPrototype> DefaultPolicy = "NCDefaultHumanDirectivePolicy";
    private static readonly ProtoId<NCMemoryEventPrototype> DirectiveMemory = "MemoryDirectiveAccepted";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private ITaskManager _tasks = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCCityLocalPerceptionSystem _perception = default!;
    [Dependency] private NCOrganizationWorkforceSystem _workforce = default!;
    [Dependency] private NCCityGroupRegistrySystem _groups = default!;
    [Dependency] private NCFactionMembershipRegistrySystem _factions = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCMemoryRegistrySystem _memories = default!;
    [Dependency] private NCPhysicalActorBrainSystem _brain = default!;
    [Dependency] private NCNeuralSuggestionSystem _suggestions = default!;

    private readonly HashSet<Guid> _pending = [];
    private readonly Dictionary<NCActorId, ActiveDirective> _directives = [];
    private readonly PriorityQueue<DirectiveExpiry, long> _expiries = new();
    private readonly Dictionary<Guid, PendingDirectiveDialogue> _pendingDialogues = [];
    public long Accepted { get; private set; }
    public long Rejected { get; private set; }
    public int InFlight => _pending.Count;
    public string LastResult { get; private set; } = "No natural directives processed.";

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCNeuralDialogueDirectiveProposedEvent>(OnIntent);
        SubscribeLocalEvent<NCHumanActionExecutionEvent>(OnExecuteStep);
        SubscribeLocalEvent<NCCognitionCollectFactsEvent>(OnCollectFacts);
        SubscribeLocalEvent<NCCityActorBindingChangedEvent>(OnBindingChanged);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Reset());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Reset());
    }

    private void OnExecuteStep(NCHumanActionExecutionEvent args)
    {
        if (args.Step.ActionId is "human-execute-zone-directive" or "human-execute-person-directive")
        {
            ExecuteDirective(args);
            return;
        }
        if (args.Step.ActionId != AcceptAction)
            return;

        args.Handled = true;
        if (args.EntityUid is not { } entity || !Exists(entity) ||
            !TryComp<NCHumanDirectiveComponent>(entity, out var directive) ||
            directive.IssuedTo != args.ActorId)
        {
            args.Failure = "human-directive-not-authorized";
            return;
        }

        // The durable commitment was already produced at the neural/server authorization boundary. This step only
        // acknowledges that projection so BDI can continue with ordinary schedule travel and role-specific work.
        args.Completed = true;
    }

    private void OnIntent(NCNeuralDialogueDirectiveProposedEvent args)
    {
        var intent = args.Response.Commands.FirstOrDefault(command => command.CapabilityId == Capability);
        if (intent == null ||
            !_clock.TryGetCurrentTime(out _, out var now) ||
            !_prototypes.TryIndex(DefaultPolicy, out NCHumanDirectivePolicyPrototype? policy) || policy == null ||
            !_runtime.TryGetEntity(args.ActorId, out var targetEntity) || !Exists(targetEntity) ||
            !_perception.TryResolveSubjectHandle(args.ActorId, intent.TargetHandle, now, out var speakerEntity) ||
            speakerEntity == targetEntity || !TryResolveParty(speakerEntity, out var speakerParty) ||
            !_pending.Add(args.Request.RequestId))
        {
            return;
        }
        args.Handled = true;

        var arguments = intent.Arguments
            .GroupBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value.Trim(), StringComparer.OrdinalIgnoreCase);
        var action = Normalize(arguments.GetValueOrDefault("action", intent.GoalKey), 64);
        var order = Normalize(arguments.GetValueOrDefault("order", args.Request.DialogueInput?.Utterance ?? action),
            policy.MaximumOrderLength);
        if (action.Length == 0 || order.Length == 0 ||
            args.Request.AllowedDirectiveActionIds is not { } allowedDirectiveActions ||
            !allowedDirectiveActions.Contains(action, StringComparer.Ordinal) ||
            !_prototypes.TryIndex(new ProtoId<NCHumanDirectiveActionPrototype>(action),
                out NCHumanDirectiveActionPrototype? actionDefinition) || actionDefinition == null ||
            actionDefinition.RequiresKnownLocation && string.IsNullOrWhiteSpace(arguments.GetValueOrDefault("location")) ||
            !TryResolveDirectiveZone(actionDefinition, arguments.GetValueOrDefault("location"),
                args.ActorId, out var targetZone))
        {
            Reject(args, "The spoken directive has no valid action or known destination.");
            return;
        }

        var targetSubject = actionDefinition.Execution is NCHumanDirectiveExecutionKind.ApproachIssuer or
            NCHumanDirectiveExecutionKind.FollowIssuer
                ? speakerEntity
                : (EntityUid?) null;
        var targetHandle = targetSubject != null ? intent.TargetHandle : string.Empty;
        if (actionDefinition.RequiresSubject)
        {
            if (!arguments.TryGetValue("subject", out var subjectHandle) ||
                !_perception.TryResolveSubjectHandle(args.ActorId, subjectHandle, now, out var subjectEntity) ||
                subjectEntity == targetEntity)
            {
                Reject(args, "The directive subject is not a distinct person currently known to the recipient.");
                return;
            }
            targetSubject = subjectEntity;
            targetHandle = subjectHandle;
        }

        NCActorId? companionActor = null;
        if (arguments.TryGetValue("companion", out var companionHandle) &&
            !string.IsNullOrWhiteSpace(companionHandle))
        {
            if (!actionDefinition.AllowsCompanion ||
                !_perception.TryResolveSubjectHandle(args.ActorId, companionHandle, now, out var companionEntity) ||
                !_runtime.TryGetActor(companionEntity, out var resolvedCompanion) ||
                resolvedCompanion == args.ActorId)
            {
                Reject(args,
                    "The named companion is not a distinct actor currently known to the recipient.");
                return;
            }

            companionActor = resolvedCompanion;
        }

        _ = AuthorizeAndApplyAsync(args, speakerEntity, speakerParty, actionDefinition, order,
            targetZone, targetSubject, targetHandle, companionActor,
            Math.Min(intent.Priority, policy.MaximumPriority), policy, now);
    }

    private async Task AuthorizeAndApplyAsync(NCNeuralDialogueDirectiveProposedEvent proposal,
        EntityUid speakerEntity,
        NCPartyRef speakerParty,
        NCHumanDirectiveActionPrototype actionDefinition,
        string order,
        NCZoneId targetZone,
        EntityUid? targetSubject,
        string targetHandle,
        NCActorId? companionActor,
        int priority,
        NCHumanDirectivePolicyPrototype policy,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime now)
    {
        var requestId = proposal.Request.RequestId;
        var targetActor = proposal.ActorId;
        var recipients = new List<NCActorId> { targetActor };
        if (companionActor is { } companion)
            recipients.Add(companion);
        // Voluntary consent belongs only to the resident who spoke. Group propagation remains an authority-only path.
        if (policy.ApplyToCurrentGroup && actionDefinition.RequiresAuthority)
        {
            var membership = await _groups.GetMembershipAsync(targetActor);
            if (membership?.Snapshot.GroupId.IsValid == true)
            {
                var group = await _groups.GetAsync(membership.Snapshot.GroupId);
                if (group != null)
                    recipients.AddRange(group.Snapshot.Members.Select(value => value.ActorId));
            }
        }
        recipients = recipients.Distinct().ToList();

        // A spoken group order is atomic: one unauthorized recipient rejects the whole interpretation. This keeps
        // Luna from smuggling unrelated citizens into an otherwise valid organizational command.
        foreach (var recipient in recipients)
        {
            if (IsActionAllowed(recipient, actionDefinition) &&
                (!actionDefinition.RequiresAuthority || await IsAuthorizedAsync(speakerEntity, recipient, policy)))
                continue;
            _tasks.RunOnMainThread(() => Reject(proposal,
                "The speaker has no authority for this action over every named recipient."));
            return;
        }

        var applied = new List<AppliedDirective>();
        foreach (var recipient in recipients)
        {
            var actor = await _actors.GetAsync(recipient, true);
            if (actor == null)
                continue;
            var commitmentId = new Content.Shared._NC.LivingCity.Actors.NCCityCommitmentId(
                DeterministicGuid(requestId, recipient.Value));
            var result = await _actors.AddCommitmentAsync(new NCCityActorAddCommitmentRequest(
                DeterministicGuid(requestId, recipient.Value, 1),
                recipient,
                actor.AggregateVersion,
                new NCCityActorCommitment(commitmentId, actionDefinition.ID, order, targetZone, now,
                    now.Advance(policy.DefaultDuration), NCCityActorAvailability.Busy, priority),
                requestId,
                requestId,
                "human-natural-directive"));
            if (result.Status is NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate)
            {
                applied.Add(new AppliedDirective(recipient, commitmentId));
                var worldEvent = DeterministicGuid(requestId, recipient.Value, 2);
                await _memories.RememberAsync(new NCRememberWorldEventRequest(
                    DeterministicGuid(requestId, recipient.Value, 3),
                    NCPartyRef.ForActor(recipient),
                    DirectiveMemory,
                    worldEvent,
                    [],
                    [speakerParty],
                    new NCMemorySource(NCMemorySourceKind.ParticipantReport, speakerParty),
                    [],
                    null,
                    null,
                    null,
                    speakerParty,
                    requestId,
                    requestId,
                    "human-natural-directive"));
            }
        }

        _tasks.RunOnMainThread(() =>
        {
            if (applied.All(value => value.ActorId != targetActor))
            {
                foreach (var partial in applied)
                    _ = RemoveCommitmentAsync(partial.ActorId, partial.CommitmentId, requestId);
                Reject(proposal, "The primary recipient could not commit the directive.");
                return;
            }

            foreach (var projection in applied)
            {
                if (_directives.TryGetValue(projection.ActorId, out var previous))
                    FinishDirective(previous.ActorId, previous.RequestId, "nc-human-directive-replaced");
                var directive = new ActiveDirective(
                    requestId,
                    projection.ActorId,
                    projection.CommitmentId,
                    actionDefinition.ID,
                    actionDefinition.Execution,
                    order,
                    targetZone,
                    targetSubject,
                    targetHandle,
                    now,
                    now.Advance(policy.DefaultDuration),
                    NCHumanDirectiveExecutionPhase.Accepted,
                    Guid.Empty,
                    string.Empty);
                _directives[projection.ActorId] = directive;
                _expiries.Enqueue(new DirectiveExpiry(projection.ActorId, requestId), directive.ExpiresAt.Ticks);
                MirrorDirective(directive);
                _brain.RequestDecision(projection.ActorId);
            }

            _pendingDialogues[requestId] = new PendingDirectiveDialogue(
                targetActor,
                proposal.SessionId,
                proposal.ConversationVersion,
                proposal.Request,
                proposal.Response,
                applied.Count,
                recipients.Count);
            _suggestions.TryApplyDeferredSuggestion(proposal.Request, proposal.Response);
            Finish(requestId, true,
                applied.Count == recipients.Count
                    ? $"Authorized directive committed for {applied.Count} actor(s)."
                    : $"Authorized directive partially committed for {applied.Count} of {recipients.Count} actor(s).");
        });
    }

    private async Task<bool> IsAuthorizedAsync(EntityUid speakerEntity, NCActorId targetActor,
        NCHumanDirectivePolicyPrototype policy)
    {
        if (_runtime.TryGetActor(speakerEntity, out var speakerActor))
        {
            var membership = await _groups.GetMembershipAsync(targetActor);
            if (membership?.Snapshot.GroupId.IsValid == true)
            {
                var group = await _groups.GetAsync(membership.Snapshot.GroupId);
                if (group?.Snapshot.LeaderActorId == speakerActor)
                    return true;
            }

            return TryCompareWorkforce(speakerActor, targetActor, policy.AllowEqualRank) ||
                   await HasFactionAuthorityAsync(speakerActor, targetActor, policy);
        }

        if (!_players.TryGetSessionByEntity(speakerEntity, out var session) ||
            !_preferences.TryGetSelectedNCCharacterId(session.UserId, out var characterId) ||
            !_workforce.TryGetAssignment(targetActor, out var organization, out var targetJob) ||
            !_prototypes.TryIndex(organization, out DepartmentPrototype? department) || department == null)
            return false;
        var activeJobId = await _database.GetNCActiveJobAsync(characterId);
        if (activeJobId == null)
            return false;
        var speakerJob = new ProtoId<JobPrototype>(activeJobId);
        return department.Roles.Contains(speakerJob) &&
               HigherRank(department, speakerJob, targetJob, policy.AllowEqualRank);
    }

    private bool IsActionAllowed(NCActorId recipient, NCHumanDirectiveActionPrototype action)
    {
        if (action.AllowedOrganizations.Count == 0)
            return true;
        return _workforce.TryGetAssignment(recipient, out var organization, out _) &&
               action.AllowedOrganizations.Contains(organization);
    }

    private bool TryCompareWorkforce(NCActorId speaker, NCActorId target, bool allowEqual)
    {
        if (!_workforce.TryGetAssignment(speaker, out var speakerOrganization, out var speakerJob) ||
            !_workforce.TryGetAssignment(target, out var targetOrganization, out var targetJob) ||
            speakerOrganization != targetOrganization ||
            !_prototypes.TryIndex(speakerOrganization, out DepartmentPrototype? department) || department == null)
            return false;
        return HigherRank(department, speakerJob, targetJob, allowEqual);
    }

    private async Task<bool> HasFactionAuthorityAsync(NCActorId speaker, NCActorId target,
        NCHumanDirectivePolicyPrototype policy)
    {
        var speakerRecord = await _factions.GetAsync(speaker);
        var targetRecord = await _factions.GetAsync(target);
        if (speakerRecord == null || targetRecord == null)
            return false;

        foreach (var speakerMembership in speakerRecord.Snapshot.Memberships.Where(value =>
                     value.Status == NCFactionMembershipStatus.Active))
        {
            var targetMembership = targetRecord.Snapshot.Memberships.FirstOrDefault(value =>
                value.FactionId == speakerMembership.FactionId && value.Status == NCFactionMembershipStatus.Active);
            if (targetMembership == null ||
                !policy.FactionRoleRanks.TryGetValue(speakerMembership.Role, out var speakerRank) ||
                !policy.FactionRoleRanks.TryGetValue(targetMembership.Role, out var targetRank))
                continue;
            if (speakerRank > targetRank || policy.AllowEqualRank && speakerRank == targetRank)
                return true;
        }
        return false;
    }

    private static bool HigherRank(DepartmentPrototype department, ProtoId<JobPrototype> speaker,
        ProtoId<JobPrototype> target, bool allowEqual) =>
        department.NCPositionRanks.TryGetValue(speaker, out var speakerRank) &&
        department.NCPositionRanks.TryGetValue(target, out var targetRank) &&
        (speakerRank > targetRank || allowEqual && speakerRank == targetRank);

    private bool TryResolveDirectiveZone(
        NCHumanDirectiveActionPrototype action,
        string? handle,
        NCActorId actorId,
        out NCZoneId zoneId)
    {
        if (!string.IsNullOrWhiteSpace(handle) && handle.StartsWith("zone:", StringComparison.Ordinal) &&
            Guid.TryParse(handle[5..], out var raw) && _zones.TryGetZone(new NCZoneId(raw), out _))
        {
            zoneId = new NCZoneId(raw);
            return true;
        }
        if (action.RequiresKnownLocation)
        {
            zoneId = default;
            return false;
        }
        if (_actors.TryGetCached(actorId, out var actor) &&
            _zones.TryGetZone(actor.Snapshot.Location.CurrentZone, out _))
        {
            zoneId = actor.Snapshot.Location.CurrentZone;
            return true;
        }
        zoneId = default;
        return false;
    }

    private void Reject(NCNeuralDialogueDirectiveProposedEvent proposal, string result)
    {
        Finish(proposal.Request.RequestId, false, result);
        var refusal = proposal.Response with
        {
            SpeechActKey = "refusal",
            RenderedText = Loc.GetString("nc-human-directive-refused"),
            Commands = [],
        };
        _suggestions.TryDeliverDeferredDialogue(
            proposal.ActorId,
            proposal.Request.RequestId,
            proposal.SessionId,
            proposal.ConversationVersion,
            refusal);
    }

    private bool TryResolveParty(EntityUid entity, out NCPartyRef party)
    {
        if (_runtime.TryGetActor(entity, out var actorId))
        {
            party = NCPartyRef.ForActor(actorId);
            return true;
        }
        if (_players.TryGetSessionByEntity(entity, out var session) &&
            _preferences.TryGetSelectedNCCharacterId(session.UserId, out var characterId))
        {
            party = NCPartyRef.ForCharacter(characterId);
            return true;
        }
        party = default;
        return false;
    }

    private void Finish(Guid requestId, bool accepted, string result)
    {
        _pending.Remove(requestId);
        if (accepted) Accepted++; else Rejected++;
        LastResult = result;
    }

    private void Reset()
    {
        foreach (var directive in _directives.Values)
            StopPhysicalDirective(directive);
        _pending.Clear();
        _directives.Clear();
        _expiries.Clear();
        _pendingDialogues.Clear();
        Accepted = 0;
        Rejected = 0;
        LastResult = "No natural directives processed.";
    }

    private static string Normalize(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || maximum < 1)
            return string.Empty;
        var normalized = string.Join(' ', value.Split((char[]?) null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized[..Math.Min(maximum, normalized.Length)];
    }

    private static Guid DeterministicGuid(Guid requestId, Guid actorId, byte discriminator = 0)
    {
        Span<byte> bytes = stackalloc byte[16];
        requestId.TryWriteBytes(bytes);
        Span<byte> actor = stackalloc byte[16];
        actorId.TryWriteBytes(actor);
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] ^= actor[i];
        bytes[0] ^= discriminator;
        return new Guid(bytes);
    }

    private sealed record ActiveDirective(
        Guid RequestId,
        NCActorId ActorId,
        NCCityCommitmentId CommitmentId,
        string ActionId,
        NCHumanDirectiveExecutionKind Execution,
        string Order,
        NCZoneId TargetZone,
        EntityUid? TargetEntity,
        string TargetHandle,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime AcceptedAt,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime ExpiresAt,
        NCHumanDirectiveExecutionPhase Phase,
        Guid ChildInstanceId,
        string ChildActionId);

    private sealed record PendingDirectiveDialogue(
        NCActorId ActorId,
        NCConversationSessionId SessionId,
        int ConversationVersion,
        NCNeuralRequestEnvelope Request,
        NCNeuralResponseEnvelope Response,
        int AppliedRecipients,
        int RequestedRecipients);

    private readonly record struct DirectiveExpiry(NCActorId ActorId, Guid RequestId);
    private readonly record struct AppliedDirective(
        NCActorId ActorId,
        NCCityCommitmentId CommitmentId);
}
