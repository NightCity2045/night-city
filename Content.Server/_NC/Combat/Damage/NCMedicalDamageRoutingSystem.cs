// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.Medical.Systemic;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Damage;
using Content.Shared._NC.Combat.Environment;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Combat.Damage;

/// <summary>
/// Stable server routing contract shared by projectile, hitscan, melee, explosion and compatibility adapters.
/// It owns no damage state: it resolves the data-defined semantic map and delegates whole-body changes to the
/// systemic authority exactly once for the supplied action correlation.
/// </summary>
public sealed partial class NCMedicalDamageRoutingSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCSystemicSystem _systemic = default!;

    private ProtoId<NCEnvironmentDamageProfileMapPrototype>? _defaultProfileMap;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        CacheDefaultProfileMap();
    }

    /// <summary>
    /// Produces a pure semantic partition. Local trauma contains only positive locally mapped entries;
    /// systemic entries and explicitly rejected physical healing remain separate.
    /// </summary>
    public bool TryPartitionDamage(DamageSpecifier damage, out NCMedicalDamagePartition partition)
    {
        if (!TryGetDefaultMap(out var map))
        {
            partition = default;
            return false;
        }

        partition = NCMedicalDamagePartitionRules.Partition(
            damage,
            map.Profiles,
            map.SystemicProfiles,
            map.RejectedLocalHealing);
        return true;
    }

    /// <summary>
    /// Compatibility API for sources with at most one contact per sequence. Only its systemic subset is consumed,
    /// and repeating the same body/action/source/sequence is a successful no-op rather than a second exposure.
    /// </summary>
    public NCSystemicPayloadResult ApplySystemicSubset(
        EntityUid body,
        NCActionResolutionId resolutionId,
        NCSystemicDamageSourceKind source,
        uint sequence,
        DamageSpecifier damage)
    {
        return ApplySystemicSubset(body, resolutionId, source, sequence, 0, damage);
    }

    /// <summary>
    /// Rich impact overload used by sources that can contact the same body more than once. Replaying the same
    /// correlation is a no-op; a later physical contact from the same source remains a distinct exposure.
    /// </summary>
    public NCSystemicPayloadResult ApplySystemicSubset(
        EntityUid body,
        NCActionResolutionId resolutionId,
        NCSystemicDamageSourceKind source,
        uint sequence,
        uint impactSequence,
        DamageSpecifier damage)
    {
        if (!TryGetDefaultMap(out var map))
            return default;

        return _systemic.ApplyDamagePayload(
            body,
            resolutionId,
            source,
            sequence,
            impactSequence,
            damage,
            map.SystemicProfiles);
    }

    public bool TryResolveTraumaProfile(
        DamageSpecifier damage,
        out ProtoId<NCCombatDamageProfilePrototype> profile)
    {
        if (!TryGetDefaultMap(out var map))
        {
            profile = default;
            return false;
        }

        return NCEnvironmentDamageProfileRules.TryResolve(map, damage, out profile) &&
               _prototypes.HasIndex(profile);
    }

    public bool TryResolveSystemicProfile(
        ProtoId<DamageTypePrototype> damageType,
        out ProtoId<NCSystemicDamageProfilePrototype> profile)
    {
        if (!TryGetDefaultMap(out var map))
        {
            profile = default;
            return false;
        }

        return NCEnvironmentDamageProfileRules.TryResolveSystemic(map, damageType, out profile) &&
               _prototypes.HasIndex(profile);
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        CacheDefaultProfileMap();
    }

    private void CacheDefaultProfileMap()
    {
        var defaults = _prototypes.EnumeratePrototypes<NCEnvironmentDamageProfileMapPrototype>()
            .Where(candidate => candidate.DefaultForHumans)
            .OrderBy(candidate => candidate.ID, StringComparer.Ordinal)
            .ToArray();

        if (defaults.Length != 1)
        {
            _defaultProfileMap = null;
            if (defaults.Length > 1)
                Log.Error("More than one default human ncEnvironmentDamageProfileMap is defined.");
            return;
        }

        _defaultProfileMap = defaults[0].ID;
    }

    private bool TryGetDefaultMap(out NCEnvironmentDamageProfileMapPrototype map)
    {
        if (_defaultProfileMap is { } mapId &&
            _prototypes.TryIndex(mapId, out var indexedMap))
        {
            map = indexedMap;
            return true;
        }

        CacheDefaultProfileMap();
        if (_defaultProfileMap is { } refreshedId &&
            _prototypes.TryIndex(refreshedId, out var refreshedMap))
        {
            map = refreshedMap;
            return true;
        }

        map = default!;
        return false;
    }
}
