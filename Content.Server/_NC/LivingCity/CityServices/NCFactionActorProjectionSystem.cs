// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.CityServices.Components;
using Content.Server._NC.LivingCity.Identity;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.CityServices;

/// <summary>
/// Rehydrates gang/community identity on the same physical human used by all other residents. It deliberately does
/// not force vanilla hostile factions: strategic relations and witnessed actions decide hostility at runtime.
/// </summary>
public sealed partial class NCFactionActorProjectionSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _tasks = default!;
    [Dependency] private NCFactionMembershipRegistrySystem _memberships = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;

    private readonly Dictionary<NCOrganizationId, ProtoId<NCFactionProfilePrototype>> _profiles = [];
    public int ProjectedActors { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCityActorRehydratedEvent>(OnRehydrated);
        SubscribeLocalEvent<NCFactionActorComponent, ComponentShutdown>(OnProjectionShutdown);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        RebuildProfiles();
    }

    private void OnRehydrated(NCCityActorRehydratedEvent args) =>
        _ = ProjectAsync(args.ActorId, args.EntityUid);

    private void OnProjectionShutdown(Entity<NCFactionActorComponent> entity, ref ComponentShutdown args) =>
        ProjectedActors = Math.Max(0, ProjectedActors - 1);

    private async Task ProjectAsync(NCActorId actorId, EntityUid entityUid)
    {
        var record = await _memberships.GetAsync(actorId);
        var active = record?.Snapshot.Memberships.FirstOrDefault(value =>
            value.Status == NCFactionMembershipStatus.Active && _profiles.ContainsKey(value.FactionId));
        _tasks.RunOnMainThread(() => Apply(actorId, entityUid, active));
    }

    private void Apply(NCActorId actorId, EntityUid entityUid, NCFactionMembership? membership)
    {
        if (!Exists(entityUid) || !_runtime.TryGetActor(entityUid, out var bound) || bound != actorId)
            return;
        if (membership == null || !_profiles.TryGetValue(membership.FactionId, out var profile))
        {
            RemComp<NCFactionActorComponent>(entityUid);
            return;
        }

        var existed = HasComp<NCFactionActorComponent>(entityUid);
        var component = EnsureComp<NCFactionActorComponent>(entityUid);
        component.ActorId = actorId;
        component.FactionId = membership.FactionId;
        component.Profile = profile;
        component.Role = membership.Role;
        if (!existed)
            ProjectedActors++;
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<NCFactionProfilePrototype>())
            RebuildProfiles();
    }

    private void RebuildProfiles()
    {
        _profiles.Clear();
        foreach (var profile in _prototypes.EnumeratePrototypes<NCFactionProfilePrototype>())
        {
            var identity = NCPersistentIdentity.Organization(profile.StableKey);
            _profiles[new NCOrganizationId(identity.PersistentId)] = profile.ID;
        }
    }
}
