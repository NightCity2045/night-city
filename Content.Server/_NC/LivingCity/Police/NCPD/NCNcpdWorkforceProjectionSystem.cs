// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Employment;
using Content.Shared._NC.LivingCity.Police.NCPD;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Roles;
using Content.Shared.SSDIndicator;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Police.NCPD;

/// <summary>
/// Projects canonical NCPD employment onto materialized actors without consuming player job slots.
/// </summary>
public sealed partial class NCNcpdWorkforceProjectionSystem : EntitySystem
{
    private static readonly ProtoId<DepartmentPrototype> NcpdOrganization = "NCPD";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCOrganizationWorkforceSystem _workforce = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;

    private readonly Dictionary<ProtoId<JobPrototype>, ProtoId<NCNcpdRoleProfilePrototype>> _profileByJob = [];
    private readonly Dictionary<NCActorId, ProtoId<NCNcpdRoleProfilePrototype>> _profileByActor = [];
    private readonly List<NCWorkforceActorProjection> _workforceBuffer = [];

    public int OfficerCount => _profileByActor.Count;
    public int RoleProfileCount => _profileByJob.Count;
    public string? ConfigurationError { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCWorkforceAssignmentChangedEvent>(OnWorkforceAssignmentChanged);
        SubscribeLocalEvent<NCCityActorMaterializationChangedEvent>(OnMaterializationChanged);
        SubscribeLocalEvent<NCCityActorRehydratedEvent>(OnActorRehydrated);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        RebuildProfiles();
        ReconcileWorkforce();
    }

    public bool TryGetRoleProfile(
        NCActorId actorId,
        out ProtoId<NCNcpdRoleProfilePrototype> profileId,
        out NCNcpdRoleProfilePrototype profile)
    {
        if (_profileByActor.TryGetValue(actorId, out profileId) &&
            _prototypes.TryIndex(profileId, out profile!))
        {
            return true;
        }

        profileId = default;
        profile = default!;
        return false;
    }

    public void GetOfficerProjections(List<(NCActorId ActorId, ProtoId<NCNcpdRoleProfilePrototype> Profile)> result)
    {
        result.Clear();
        foreach (var (actorId, profile) in _profileByActor.OrderBy(static value => value.Key.Value))
            result.Add((actorId, profile));
    }

    private void OnWorkforceAssignmentChanged(NCWorkforceAssignmentChangedEvent args)
    {
        if (!args.Active || args.Organization != NcpdOrganization ||
            !_profileByJob.TryGetValue(args.Job, out var profile))
        {
            RemoveProjection(args.ActorId);
            return;
        }

        Project(args.ActorId, profile);
    }

    private void OnMaterializationChanged(NCCityActorMaterializationChangedEvent args)
    {
        if (args.Materialized && _profileByActor.TryGetValue(args.ActorId, out var profile))
            ApplyPhysicalProjection(args.ActorId, args.EntityUid, profile);
    }

    private void OnActorRehydrated(NCCityActorRehydratedEvent args)
    {
        if (_profileByActor.TryGetValue(args.ActorId, out var profile))
            ApplyPhysicalProjection(args.ActorId, args.EntityUid, profile);
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (!args.WasModified<NCNcpdRoleProfilePrototype>() &&
            !args.WasModified<DepartmentPrototype>() &&
            !args.WasModified<JobPrototype>())
        {
            return;
        }

        RebuildProfiles();
        ReconcileWorkforce();
    }

    private void RebuildProfiles()
    {
        _profileByJob.Clear();
        ConfigurationError = null;
        if (!_prototypes.TryIndex(NcpdOrganization, out DepartmentPrototype? department) || department == null ||
            !department.NCSelectable)
        {
            ConfigurationError = "Department NCPD is missing or is not selectable.";
            Log.Error(ConfigurationError);
            return;
        }

        foreach (var profile in _prototypes.EnumeratePrototypes<NCNcpdRoleProfilePrototype>()
                     .OrderBy(static value => value.ID, StringComparer.Ordinal))
        {
            if (!_prototypes.TryIndex(profile.Job, out JobPrototype? job) || job == null ||
                !department.Roles.Contains(profile.Job) || !job.NCNpcAllowed)
            {
                ConfigurationError = $"NCPD role profile {profile.ID} references a missing, player-only, or foreign job.";
                break;
            }

            if (job.ID == "NCPDChief")
            {
                ConfigurationError = "NCPDChief is player-only and cannot have an NPC role profile.";
                break;
            }

            if (!profile.WorkZone.IsValid ||
                profile.MinimumGroupSize < 1 || profile.MaximumGroupSize < profile.MinimumGroupSize ||
                !_profileByJob.TryAdd(profile.Job, profile.ID))
            {
                ConfigurationError = $"NCPD role profile {profile.ID} has an invalid work zone, group bounds, or duplicate job.";
                break;
            }
        }

        if (ConfigurationError != null)
            Log.Error(ConfigurationError);
    }

    private void ReconcileWorkforce()
    {
        _workforce.GetActorProjections(_workforceBuffer);
        var retained = new HashSet<NCActorId>();
        if (ConfigurationError == null)
        {
            foreach (var assignment in _workforceBuffer)
            {
                if (assignment.Organization != NcpdOrganization ||
                    !_profileByJob.TryGetValue(assignment.Job, out var profile))
                {
                    continue;
                }

                retained.Add(assignment.ActorId);
                Project(assignment.ActorId, profile);
            }
        }

        foreach (var actorId in _profileByActor.Keys.Where(actor => !retained.Contains(actor)).ToArray())
            RemoveProjection(actorId);
    }

    private void Project(NCActorId actorId, ProtoId<NCNcpdRoleProfilePrototype> profile)
    {
        var changed = !_profileByActor.TryGetValue(actorId, out var current) || current != profile;
        _profileByActor[actorId] = profile;
        if (_runtime.TryGetEntity(actorId, out var entityUid))
            ApplyPhysicalProjection(actorId, entityUid, profile);
        if (changed)
            RaiseLocalEvent(new NCNcpdOfficerProjectedEvent(actorId, profile));
    }

    private void RemoveProjection(NCActorId actorId)
    {
        if (!_profileByActor.Remove(actorId) || !_runtime.TryGetEntity(actorId, out var entityUid))
            return;

        RemComp<NCNcpdPoliceClaimComponent>(entityUid);
        RemComp<NCNcpdAssignmentComponent>(entityUid);
        RemComp<NCNcpdUnitMemberComponent>(entityUid);
        RemComp<NCNcpdDutyComponent>(entityUid);
        RemComp<NCNcpdOfficerComponent>(entityUid);
    }

    private void ApplyPhysicalProjection(
        NCActorId actorId,
        EntityUid entityUid,
        ProtoId<NCNcpdRoleProfilePrototype> profileId)
    {
        if (!Exists(entityUid) || !_runtime.TryGetActor(entityUid, out var boundActor) || boundActor != actorId ||
            !_prototypes.TryIndex(profileId, out NCNcpdRoleProfilePrototype? profile) || profile == null)
        {
            return;
        }

        var officer = EnsureComp<NCNcpdOfficerComponent>(entityUid);
        officer.ActorId = actorId;
        officer.RoleProfile = profileId;
        officer.StationZone = profile.WorkZone;
        EnsureComp<NCNcpdPoliceClaimComponent>(entityUid);

        // SSD is a player-session indicator. Server-owned employees retain their universal HTN instead.
        if (HasComp<SSDIndicatorComponent>(entityUid))
            RemCompDeferred<SSDIndicatorComponent>(entityUid);
    }
}
