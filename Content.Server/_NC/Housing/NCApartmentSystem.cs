// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._NC.Bank;
using Content.Server._NC.City.ZoneTemplates;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server.Gibbing.Systems;
using Content.Server.Preferences.Managers;
using Content.Shared._NC.City.ZoneTemplates;
using Content.Shared._NC.City.ZoneTemplates.Prototypes;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Housing;
using Content.Shared._NC.Housing.Components;
using Content.Shared._NC.Housing.Prototypes;
using Content.Shared._NC.Identity;
using Content.Shared.GameTicking;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Housing;

/// <summary>
/// Server authority for apartment tenancy, recurring rent, access credentials and interiors.
/// Mutable state is persisted by CharacterId; this runtime cache only avoids database reads on door swipes.
/// </summary>
public sealed partial class NCApartmentSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCBankSystem _bank = default!;
    [Dependency] private City.Zones.NCZoneSystem _zones = default!;
    [Dependency] private NCZoneTemplateSystem _templates = default!;
    [Dependency] private NCZoneOccupancySystem _occupancy = default!;
    [Dependency] private NCPropertySnapshotSystem _snapshots = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;

    private readonly Dictionary<NCApartmentId, NCPropertyData> _properties = [];
    private readonly Dictionary<NCApartmentId, HashSet<EntityUid>> _locks = [];
    private readonly Dictionary<NCZoneDeploymentId, PendingInterior> _pendingInteriors = [];
    private readonly Dictionary<NCApartmentId, NCZoneDeploymentId> _runtimeInteriors = [];
    private readonly HashSet<NCApartmentId> _rentProcessing = [];
    private bool _roundSnapshotsCaptured;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PostGameMapLoad>(OnPostGameMapLoad);
        SubscribeLocalEvent<RoundEndMessageEvent>(OnRoundEnd, before: [typeof(GibOnRoundEndSystem)]);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart, before: [typeof(NCZoneTemplateSystem)]);
        SubscribeLocalEvent<PlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<NCPropertyLockComponent, ComponentStartup>(OnLockStartup);
        SubscribeLocalEvent<NCPropertyLockComponent, ComponentShutdown>(OnLockShutdown);
        SubscribeLocalEvent<NCZoneDeploymentStateChangedEvent>(OnDeploymentStateChanged);
    }

    private async void OnPostGameMapLoad(PostGameMapLoad ev)
    {
        _roundSnapshotsCaptured = false;
        try
        {
            foreach (var listing in _prototypes.EnumeratePrototypes<NCPropertyListingPrototype>())
            {
                if (!_zones.TryGetApartment(listing.ApartmentId, out var apartment) ||
                    !_zones.TryGetBuilding(new NCBuildingId(apartment.Parent), out _))
                {
                    Log.Error($"Property listing {listing.ID} does not reference an apartment nested in a building.");
                    continue;
                }

                var property = await _database.EnsureNCPropertyAsync(new NCPropertySeedData(
                    listing.ApartmentId,
                    new NCBuildingId(apartment.Parent),
                    [listing.ApartmentId.ZoneId],
                    0,
                    string.Empty,
                    listing.AccessPolicy.Id));
                if (property == null)
                    Log.Error($"Property listing {listing.ID} could not be seeded into persistent storage.");
            }

            var now = DateTime.UtcNow;
            var loaded = await _database.GetNCPropertiesAsync();
            var properties = new List<NCPropertyData>(loaded.Count);
            foreach (var loadedProperty in loaded)
            {
                var property = await ConvertLegacyOwnerAsync(loadedProperty, now);
                property = await ProcessRentLifecycleAsync(property, now, liveRound: false);
                properties.Add(property);
                Cache(property);
            }

            await _snapshots.LoadPreparedSnapshotsAsync(properties);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to initialize persistent properties: {exception}");
        }
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        // A direct restart may skip the normal round-end message, so cleanup remains a guarded fallback.
        CaptureActiveRoundSnapshots();
        _snapshots.BeginRoundLoad();
        _properties.Clear();
        _locks.Clear();
        _pendingInteriors.Clear();
        _runtimeInteriors.Clear();
        _rentProcessing.Clear();
    }

    private void OnPlayerAttached(PlayerAttachedEvent ev)
    {
        if (_preferences.TryGetSelectedNCCharacterId(ev.Player.UserId, out var characterId))
            _ = ProcessCharacterRentAsync(characterId);
    }

    private async Task<NCPropertyData> ConvertLegacyOwnerAsync(NCPropertyData property, DateTime now)
    {
        if (property.OwnerCharacterId is not { } owner ||
            !TryGetListing(property.PropertyId, out var listing))
            return property;

        var offer = listing.Interiors.FirstOrDefault(value => value.Template.Id == property.CurrentTemplateId) ??
                    listing.Interiors.FirstOrDefault();
        if (offer == null ||
            !_prototypes.TryIndex(offer.Template, out NCZoneTemplatePrototype? template) ||
            !_prototypes.TryIndex(offer.RentPolicy, out NCRentPolicyPrototype? rentPolicy))
        {
            Log.Error($"Legacy property {property.PropertyId} cannot be converted because its interior offer is invalid.");
            return property;
        }

        var result = await _database.TryConvertNCPropertyOwnerToRentalAsync(
            property.PropertyId,
            owner,
            offer.Template.Id,
            template.Revision,
            offer.RentPolicy.Id,
            now + rentPolicy.BillingInterval,
            property.Version);
        if (result.Result != NCPropertyMutationResult.Success || result.Property == null)
        {
            Log.Error($"Legacy property {property.PropertyId} could not be converted to tenancy: {result.Result}.");
            return result.Property ?? property;
        }

        return result.Property;
    }

    /// <summary>
    /// Processes only this character's due leases. Called on attach and terminal open, never from Update().
    /// </summary>
    public async Task ProcessCharacterRentAsync(NCCharacterId characterId)
    {
        var properties = _properties.Values
            .Where(property => IsActiveTenant(property, characterId))
            .ToArray();
        foreach (var property in properties)
        {
            if (!_rentProcessing.Add(property.PropertyId))
                continue;

            try
            {
                var updated = await ProcessRentLifecycleAsync(property, DateTime.UtcNow, liveRound: true);
                Cache(updated);
            }
            catch (Exception exception)
            {
                Log.Error($"Failed to process rent for property {property.PropertyId}: {exception}");
            }
            finally
            {
                _rentProcessing.Remove(property.PropertyId);
            }
        }
    }

    private async Task<NCPropertyData> ProcessRentLifecycleAsync(
        NCPropertyData property,
        DateTime now,
        bool liveRound)
    {
        if (property.Status is not (NCPropertyStatus.Rented or NCPropertyStatus.Delinquent) ||
            property.NextRentDueAt is not { } dueAt || now < dueAt ||
            !_prototypes.TryIndex<NCRentPolicyPrototype>(property.RentPolicyId, out var rentPolicy))
            return property;

        var tenant = property.Tenants
            .Where(value => value.RevokedAt == null)
            .OrderBy(value => value.StartedAt)
            .FirstOrDefault();
        if (tenant == null)
            return property;

        if (now > dueAt + rentPolicy.GracePeriod)
        {
            var eviction = await _database.TryEvictNCPropertyForNonPaymentAsync(
                property.PropertyId,
                property.Version);
            if (eviction.Result != NCPropertyMutationResult.Success || eviction.Property == null)
                return eviction.Property ?? property;

            if (liveRound)
            {
                _snapshots.ConfiscateLiveContents(property);
                if (_runtimeInteriors.TryGetValue(property.PropertyId, out var deploymentId) &&
                    !_templates.TryCloseDeployment(deploymentId, out var error))
                {
                    Log.Error($"Evicted property {property.PropertyId} could not close its interior: {error}");
                }
                _runtimeInteriors.Remove(property.PropertyId);
                _snapshots.ForgetSnapshot(property);
            }

            return eviction.Property;
        }

        var renewal = await _bank.RunSerializedBalanceMutationAsync(() =>
            _database.TryRenewNCPropertyRentAsync(
                property.PropertyId,
                tenant.CharacterId,
                rentPolicy.RentAmount,
                now + rentPolicy.BillingInterval,
                property.Version));
        if (renewal.Result == NCPropertyMutationResult.Success && renewal.Property != null)
            return renewal.Property;
        if (renewal.Result is not (NCPropertyMutationResult.InsufficientFunds or
            NCPropertyMutationResult.BankAccountNotFound))
            return renewal.Property ?? property;

        var delinquent = await _database.TryMarkNCPropertyRentDelinquentAsync(
            property.PropertyId,
            property.Version);
        return delinquent.Property ?? property;
    }

    private void OnRoundEnd(RoundEndMessageEvent ev)
    {
        // Capture before round-end systems can gib or otherwise mutate entities that belong in the snapshot.
        CaptureActiveRoundSnapshots();
    }

    private void CaptureActiveRoundSnapshots()
    {
        if (_roundSnapshotsCaptured)
            return;

        var active = new List<NCPropertySnapshotCapture>();
        foreach (var (propertyId, deploymentId) in _runtimeInteriors)
        {
            if (_properties.TryGetValue(propertyId, out var property))
                active.Add(new NCPropertySnapshotCapture(property, deploymentId));
        }

        _snapshots.CaptureRoundSnapshots(active);
        _roundSnapshotsCaptured = true;
    }

    public bool TryGetProperty(NCApartmentId propertyId, out NCPropertyData property) =>
        _properties.TryGetValue(propertyId, out property!);

    public async Task<NCPropertyMutationData> TryRentAsync(
        NCApartmentId propertyId,
        NCCharacterId characterId,
        ProtoId<NCZoneTemplatePrototype> templateId,
        EntityUid cardRecipient)
    {
        if (!TryGetListing(propertyId, out var listing) ||
            !TryGetInteriorOffer(listing, templateId, out var offer, out var template, out var rentPolicy) ||
            !_prototypes.TryIndex(listing.AccessPolicy, out var accessPolicy))
            return new NCPropertyMutationData(NCPropertyMutationResult.InvalidRequest, null, null, null);

        var result = await _bank.RunSerializedBalanceMutationAsync(() =>
            _database.TryRentNCPropertyAsync(
                propertyId,
                characterId,
                offer.Template.Id,
                template.Revision,
                offer.RentPolicy.Id,
                rentPolicy.RentAmount,
                DateTime.UtcNow + rentPolicy.BillingInterval,
                accessPolicy.TenantPermissions));
        ApplyMutation(result, accessPolicy, cardRecipient);
        return result;
    }

    /// <summary>
    /// Applies starting housing through the same paid rental transaction as the public terminal.
    /// Character creation can expose an explicit design choice later; the first configured offer is the safe default.
    /// </summary>
    public async Task<NCPropertyMutationData> TryAssignStartingHousingAsync(
        NCCharacterId characterId,
        EntityUid cardRecipient)
    {
        if (_properties.Values.Any(property => IsActiveTenant(property, characterId)))
            return new NCPropertyMutationData(NCPropertyMutationResult.Conflict, null, null, null);

        foreach (var listing in _prototypes.EnumeratePrototypes<NCPropertyListingPrototype>())
        {
            if (!listing.StartingHousingEligible ||
                !_properties.TryGetValue(listing.ApartmentId, out var property) ||
                property.Status != NCPropertyStatus.Available ||
                listing.Interiors.FirstOrDefault() is not { } offer)
                continue;

            var result = await TryRentAsync(listing.ApartmentId, characterId, offer.Template, cardRecipient);
            if (result.Result == NCPropertyMutationResult.Success)
                return result;
        }

        return new NCPropertyMutationData(NCPropertyMutationResult.NotAvailable, null, null, null);
    }

    public async Task<NCPropertyMutationData> TryReleaseAsync(
        NCApartmentId propertyId,
        NCCharacterId characterId,
        NCPropertyReleaseReason reason)
    {
        if (!TryGetListing(propertyId, out _) || reason == NCPropertyReleaseReason.Sale)
            return new NCPropertyMutationData(NCPropertyMutationResult.InvalidRequest, null, null, null);
        if (reason == NCPropertyReleaseReason.VoluntaryMoveOut &&
            _properties.TryGetValue(propertyId, out var rental) &&
            _snapshots.HasResidentItems(rental))
        {
            return new NCPropertyMutationData(
                NCPropertyMutationResult.Conflict,
                rental,
                null,
                null);
        }
        if (reason == NCPropertyReleaseReason.VoluntaryMoveOut &&
            _occupancy.IsOccupied(propertyId.ZoneId, out _))
            return new NCPropertyMutationData(
                NCPropertyMutationResult.Conflict,
                _properties.GetValueOrDefault(propertyId),
                null,
                null);
        var current = _properties.GetValueOrDefault(propertyId);
        var deploymentToClose = _runtimeInteriors.GetValueOrDefault(propertyId);
        if (!deploymentToClose.IsValid && current != null)
            deploymentToClose = current.ActiveDeploymentId;

        var result = await _bank.RunSerializedBalanceMutationAsync(() =>
            _database.TryReleaseNCPropertyAsync(propertyId, characterId, reason, 0));
        ApplyMutation(result, null, null);
        if (result.Result == NCPropertyMutationResult.Success && deploymentToClose.IsValid)
        {
            if (!_templates.TryCloseDeployment(deploymentToClose, out var error))
                Log.Error($"Property {propertyId} was released but interior cleanup could not start: {error}");
        }
        if (result.Result == NCPropertyMutationResult.Success && current != null)
            _snapshots.ForgetSnapshot(current);
        return result;
    }

    public async Task<NCPropertyMutationData> TryIssueKeyCardAsync(
        NCApartmentId propertyId,
        NCCharacterId actorCharacterId,
        NCCharacterId subjectCharacterId,
        NCPropertyPermission permissions,
        DateTime? validUntil,
        EntityUid cardRecipient)
    {
        if (!TryGetListing(propertyId, out var listing) ||
            !_prototypes.TryIndex(listing.AccessPolicy, out var accessPolicy))
            return new NCPropertyMutationData(NCPropertyMutationResult.InvalidRequest, null, null, null);

        var result = await _bank.RunSerializedBalanceMutationAsync(() =>
            _database.GrantNCPropertyAccessAsync(
                propertyId,
                actorCharacterId,
                subjectCharacterId,
                permissions,
                validUntil,
                accessPolicy.MaximumActiveCards));
        ApplyMutation(result, accessPolicy, cardRecipient);
        return result;
    }

    public bool TryInvokeInterior(
        NCApartmentId propertyId,
        NCCharacterId actorCharacterId,
        out NCZoneDeploymentId deploymentId,
        out string error)
    {
        deploymentId = default;
        error = string.Empty;
        if (!_properties.TryGetValue(propertyId, out var property) || !IsActiveTenant(property, actorCharacterId))
        {
            error = "Only the active renter may invoke an interior.";
            return false;
        }
        if (property.CurrentTemplateId is not { } selected ||
            property.CurrentTemplateRevision is not { } selectedRevision)
        {
            error = "This property does not have a selected interior.";
            return false;
        }

        var templateId = new ProtoId<NCZoneTemplatePrototype>(selected);
        if (!TryGetListing(propertyId, out var listing) ||
            !listing.Interiors.Any(value => value.Template == templateId) ||
            !_prototypes.TryIndex(templateId, out NCZoneTemplatePrototype? template) ||
            template.TargetZoneId != propertyId.ZoneId || template.Revision != selectedRevision)
        {
            error = "The selected interior revision is unavailable.";
            return false;
        }

        return TryDeployInterior(property, actorCharacterId, templateId, out deploymentId, out error);
    }

    public bool IsInteriorActiveOrPending(NCApartmentId propertyId)
    {
        if (_runtimeInteriors.TryGetValue(propertyId, out var runtime) &&
            _templates.TryGetDeployment(runtime, out var deployment) &&
            deployment.State is not NCZoneDeploymentState.Completed and not NCZoneDeploymentState.FailedClean)
            return true;

        return _pendingInteriors.Values.Any(value => value.PropertyId == propertyId);
    }

    public bool AreInteriorSnapshotsReady => _snapshots.IsReady;

    private bool TryDeployInterior(
        NCPropertyData property,
        NCCharacterId actorCharacterId,
        ProtoId<NCZoneTemplatePrototype> templateId,
        out NCZoneDeploymentId deploymentId,
        out string error)
    {
        deploymentId = default;
        error = string.Empty;
        if (property.CurrentTemplateId != null && !_snapshots.IsReady)
        {
            error = "Persistent apartment snapshots are still loading.";
            return false;
        }
        if (IsInteriorActiveOrPending(property.PropertyId))
        {
            error = "This property's interior is already active or pending.";
            return false;
        }

        var request = new NCZoneDeploymentRequest(
            NCZoneDeploymentId.New(),
            property.PropertyId.ZoneId,
            templateId,
            NCZoneDeploymentPurpose.ApartmentInterior,
            actorCharacterId,
            null);
        if (!_templates.TryRequestDeployment(request, out deploymentId, out error))
            return false;

        _pendingInteriors[deploymentId] = new PendingInterior(property.PropertyId, templateId.Id, property.Version);
        return true;
    }

    private void OnDeploymentStateChanged(ref NCZoneDeploymentStateChangedEvent ev)
    {
        var propertyId = new NCApartmentId(ev.ZoneId);
        if (ev.NewState == NCZoneDeploymentState.Active)
            _runtimeInteriors[propertyId] = ev.DeploymentId;
        else if (ev.NewState is NCZoneDeploymentState.Completed or NCZoneDeploymentState.FailedClean)
            _runtimeInteriors.Remove(propertyId);

        if (!_pendingInteriors.TryGetValue(ev.DeploymentId, out var pending))
            return;
        if (ev.NewState is NCZoneDeploymentState.FailedClean or NCZoneDeploymentState.Completed)
        {
            _pendingInteriors.Remove(ev.DeploymentId);
            return;
        }
        if (ev.NewState != NCZoneDeploymentState.Active ||
            !_templates.TryGetDeployment(ev.DeploymentId, out var deployment))
            return;

        _pendingInteriors.Remove(ev.DeploymentId);
        _ = PersistInteriorAsync(ev.DeploymentId, pending, deployment.TemplateRevision);
    }

    private async Task PersistInteriorAsync(
        NCZoneDeploymentId deploymentId,
        PendingInterior pending,
        int templateRevision)
    {
        try
        {
            var property = await _database.SetNCPropertyInteriorAsync(
                pending.PropertyId,
                deploymentId,
                pending.TemplateId,
                templateRevision,
                "active",
                pending.ExpectedVersion);
            if (property != null)
                Cache(property);
            else
                Log.Error($"Interior deployment {deploymentId} became active but its property version conflicted.");
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to persist interior deployment {deploymentId}: {exception}");
        }
    }

    private void ApplyMutation(
        NCPropertyMutationData result,
        NCPropertyAccessPolicyPrototype? accessPolicy,
        EntityUid? cardRecipient)
    {
        if (result.Property != null)
            Cache(result.Property);
        if (result.Result != NCPropertyMutationResult.Success || result.Grant == null ||
            accessPolicy == null || cardRecipient is not { } recipient || !Exists(recipient))
            return;

        var card = Spawn(accessPolicy.KeyCardPrototype, Transform(recipient).Coordinates);
        var credential = EnsureComp<NCPropertyKeyCardComponent>(card);
        credential.GrantId = result.Grant.GrantId;
        credential.PropertyId = result.Grant.PropertyId;
        credential.AccessRevision = result.Grant.AccessRevision;
        credential.Permissions = result.Grant.Permissions;
        credential.ValidUntil = result.Grant.ValidUntil;
        Dirty(card, credential);
        _metadata.SetEntityName(card, $"Property key-card {result.Grant.PropertyId}");
    }

    private void Cache(NCPropertyData property)
    {
        _properties[property.PropertyId] = property;
        if (_locks.TryGetValue(property.PropertyId, out var locks))
        {
            foreach (var uid in locks.ToArray())
            {
                if (!TryComp(uid, out NCPropertyLockComponent? propertyLock))
                    continue;
                propertyLock.AccessRevision = property.AccessRevision;
                propertyLock.Enabled = property.Status != NCPropertyStatus.Unavailable;
                Dirty(uid, propertyLock);
            }
        }

        var ev = new NCPropertyChangedEvent(property.PropertyId);
        RaiseLocalEvent(ref ev);
    }

    private void OnLockStartup(Entity<NCPropertyLockComponent> entity, ref ComponentStartup args)
    {
        IndexPropertyLock(entity);
        ApplyPropertyLockState(entity);
    }

    private void OnLockShutdown(Entity<NCPropertyLockComponent> entity, ref ComponentShutdown args)
    {
        RemovePropertyLock(entity.Owner);
    }

    private void IndexPropertyLock(Entity<NCPropertyLockComponent> entity)
    {
        // Invalid serialized IDs are ignored defensively; valid layouts bind directly to their apartment zone.
        if (!entity.Comp.PropertyId.IsValid)
            return;

        if (!_locks.TryGetValue(entity.Comp.PropertyId, out var locks))
            _locks[entity.Comp.PropertyId] = locks = [];
        locks.Add(entity.Owner);
    }

    private void RemovePropertyLock(EntityUid uid)
    {
        foreach (var (propertyId, locks) in _locks.ToArray())
        {
            locks.Remove(uid);
            if (locks.Count == 0)
                _locks.Remove(propertyId);
        }
    }

    private void ApplyPropertyLockState(Entity<NCPropertyLockComponent> entity)
    {
        if (!_properties.TryGetValue(entity.Comp.PropertyId, out var property))
            return;

        entity.Comp.AccessRevision = property.AccessRevision;
        entity.Comp.Enabled = property.Status != NCPropertyStatus.Unavailable;
        Dirty(entity, entity.Comp);
    }

    private bool TryGetListing(NCApartmentId id, out NCPropertyListingPrototype listing)
    {
        foreach (var candidate in _prototypes.EnumeratePrototypes<NCPropertyListingPrototype>())
        {
            if (candidate.ApartmentId != id)
                continue;
            listing = candidate;
            return true;
        }

        listing = default!;
        return false;
    }

    private bool TryGetInteriorOffer(
        NCPropertyListingPrototype listing,
        ProtoId<NCZoneTemplatePrototype> templateId,
        out NCPropertyInteriorOffer offer,
        out NCZoneTemplatePrototype template,
        out NCRentPolicyPrototype rentPolicy)
    {
        offer = listing.Interiors.FirstOrDefault(value => value.Template == templateId)!;
        template = default!;
        rentPolicy = default!;
        if (offer == null ||
            !_prototypes.TryIndex(offer.Template, out NCZoneTemplatePrototype? resolvedTemplate) ||
            resolvedTemplate.TargetZoneId != listing.ApartmentId.ZoneId ||
            !_prototypes.TryIndex(offer.RentPolicy, out NCRentPolicyPrototype? resolvedPolicy) ||
            resolvedPolicy.RentAmount < 0 ||
            resolvedPolicy.BillingInterval <= TimeSpan.Zero ||
            resolvedPolicy.GracePeriod < TimeSpan.Zero)
            return false;

        template = resolvedTemplate;
        rentPolicy = resolvedPolicy;
        return true;
    }

    private static bool IsActiveTenant(NCPropertyData property, NCCharacterId characterId)
    {
        return property.Tenants.Any(tenant => tenant.CharacterId == characterId &&
            tenant.RevokedAt == null &&
            (tenant.ValidUntil is not { } expiry || expiry > DateTime.UtcNow));
    }

    private readonly record struct PendingInterior(NCApartmentId PropertyId, string TemplateId, int ExpectedVersion);
}

/// <summary>Raised whenever the authoritative property cache changes.</summary>
[ByRefEvent]
public readonly record struct NCPropertyChangedEvent(NCApartmentId PropertyId);
