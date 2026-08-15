// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.Medical.Persistence;
using Content.Server._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Persistence;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Transplantation;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Transplantation;

/// <summary>
/// Advances only graft roots inside one scheduler-selected body. It never scans the world and never rolls;
/// donor identity, elapsed time, organ state and measured immunosuppression fully determine the outcome.
/// </summary>
public sealed partial class NCTransplantRejectionSystem : EntitySystem
{
    private const int OrganTraversalLimit = 64;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCPharmacologySystem _pharmacology = default!;
    [Dependency] private NCMedicalBodySystem _medicalBodies = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCMedicalBodyComponent, NCOrganAttachedEvent>(OnOrganAttached);
        SubscribeLocalEvent<NCMedicalBodyComponent, NCMedicalScheduledUpdateEvent>(
            OnScheduledUpdate,
            after: [typeof(NCPharmacologySystem)]);
        SubscribeLocalEvent<NCMedicalBodyComponent, NCMedicalNextUpdateEvent>(OnNextUpdateRequested);
    }

    private void OnOrganAttached(Entity<NCMedicalBodyComponent> body, ref NCOrganAttachedEvent args)
    {
        ReconcileGraft(body.Owner, args.OrganRoot);
    }

    private void OnScheduledUpdate(Entity<NCMedicalBodyComponent> body, ref NCMedicalScheduledUpdateEvent args)
    {
        AdvanceBody(body.Owner, args.Elapsed);
    }

    private void OnNextUpdateRequested(Entity<NCMedicalBodyComponent> body, ref NCMedicalNextUpdateEvent args)
    {
        foreach (var organ in CollectGrafts(body.Owner))
        {
            var rejection = Comp<NCTransplantRejectionComponent>(organ);
            if (!_prototypes.TryIndex(rejection.Profile, out NCTransplantRejectionProfilePrototype? profile) ||
                !ValidateProfile(profile))
            {
                continue;
            }

            if (args.Delay == null || profile.ActiveUpdateInterval < args.Delay)
                args.Delay = profile.ActiveUpdateInterval;
            return;
        }
    }

    public bool ReconcileGraft(EntityUid body, EntityUid organRoot)
    {
        if (!HasComp<NCMedicalBodyComponent>(body) ||
            !TryComp<OrganComponent>(organRoot, out var organ) || organ.Body != body ||
            !TryComp<NCOrganIntegrityComponent>(organRoot, out var integrity) ||
            integrity.Material != NCAnatomicalMaterialKind.Organic ||
            !TryComp<NCOrganPersistenceIdentityComponent>(organRoot, out var identity) ||
            !TryComp<NCMedicalPersistenceOwnerComponent>(body, out var recipient))
        {
            return false;
        }

        if (identity.DonorKind == NCMedicalStableOwnerKind.Unassigned ||
            recipient.Kind == NCMedicalStableOwnerKind.Unassigned)
        {
            return false;
        }

        if (identity.DonorKind == recipient.Kind && identity.DonorKey == recipient.Key)
        {
            RemComp<NCTransplantRejectionComponent>(organRoot);
            return true;
        }

        var defaults = _prototypes.EnumeratePrototypes<NCTransplantRejectionProfilePrototype>()
            .Where(profile => profile.DefaultForHumans)
            .OrderBy(profile => profile.ID, StringComparer.Ordinal)
            .ToList();
        if (defaults.Count != 1 || !ValidateProfile(defaults[0]))
            return false;

        var state = EnsureComp<NCTransplantRejectionComponent>(organRoot);
        state.Profile = defaults[0].ID;
        state.StateInitialized = true;
        state.DonorKind = identity.DonorKind;
        state.DonorKey = identity.DonorKey;
        state.RecipientKind = recipient.Kind;
        state.RecipientKey = recipient.Key;
        state.Burden = Math.Clamp(state.Burden, 0f, defaults[0].MaximumBurden);
        state.Stage = ResolveStage(state.Burden, defaults[0]);
        return true;
    }

    public bool AdvanceBody(EntityUid body, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero || !HasComp<NCMedicalBodyComponent>(body))
            return false;

        var seconds = (float) elapsed.TotalSeconds;
        if (!float.IsFinite(seconds) || seconds <= 0f)
            return false;

        var immunosuppression = _pharmacology.GetModifiers(body).Immunosuppression;
        var changed = false;
        foreach (var organ in CollectGrafts(body))
        {
            var state = Comp<NCTransplantRejectionComponent>(organ);
            if (!state.StateInitialized && !ReconcileGraft(body, organ) ||
                !_prototypes.TryIndex(state.Profile, out NCTransplantRejectionProfilePrototype? profile) ||
                !ValidateProfile(profile))
            {
                continue;
            }

            var previousBurden = state.Burden;
            var previousStage = state.Stage;
            var growth = profile.BaseGrowthPerSecond *
                         (1f - immunosuppression * profile.GrowthSuppressionAtMaximumImmunosuppression);
            var recovery = immunosuppression * profile.RecoveryPerImmunosuppressionPerSecond;
            state.Burden = Math.Clamp(
                previousBurden + (growth - recovery) * seconds,
                0f,
                profile.MaximumBurden);
            state.Stage = ResolveStage(state.Burden, profile);

            var integrityDamage = profile.IntegrityDamagePerSecond[state.Stage] * seconds;
            var functionDamage = profile.FunctionDamagePerSecond[state.Stage] * seconds;
            var necrosis = profile.NecrosisPerSecond[state.Stage] * seconds;
            if (TryComp<NCOrganIntegrityComponent>(organ, out var integrity))
            {
                integrity.CurrentIntegrity = Math.Clamp(
                    integrity.CurrentIntegrity - integrityDamage,
                    0f,
                    MathF.Max(0f, integrity.MaxIntegrity));
                integrity.FunctionalEfficiency = integrity.MaxIntegrity > 0f
                    ? integrity.CurrentIntegrity / integrity.MaxIntegrity
                    : 0f;
                integrity.Necrosis = Math.Clamp(integrity.Necrosis + necrosis, 0f, 1f);
            }
            if (TryComp<NCOrganFunctionComponent>(organ, out var function))
            {
                function.CurrentCapacity = Math.Clamp(
                    function.CurrentCapacity - functionDamage,
                    0f,
                    MathF.Max(0f, function.BaseCapacity));
                function.Necrosis = Math.Clamp(function.Necrosis + necrosis, 0f, 1f);
            }

            var organChanged = !MathHelper.CloseTo(previousBurden, state.Burden) ||
                               previousStage != state.Stage ||
                               integrityDamage > 0f || functionDamage > 0f || necrosis > 0f;
            changed |= organChanged;
            if (previousStage != state.Stage)
            {
                var raised = new NCTransplantRejectionChangedEvent(
                    organ,
                    previousStage,
                    state.Stage,
                    state.Burden);
                RaiseLocalEvent(body, ref raised);
            }
        }

        if (changed)
        {
            _medicalBodies.TryMarkDirty(
                body,
                NCMedicalDirtyDomains.OrganFunction |
                NCMedicalDirtyDomains.Consciousness |
                NCMedicalDirtyDomains.Diagnostics |
                NCMedicalDirtyDomains.Persistence);
        }
        return changed;
    }

    private List<EntityUid> CollectGrafts(EntityUid body)
    {
        var result = new List<EntityUid>();
        if (!TryComp<BodyComponent>(body, out var bodyComponent) || bodyComponent.Organs == null)
            return result;

        foreach (var organ in bodyComponent.Organs.ContainedEntities.OrderBy(uid => uid.Id))
        {
            if (result.Count >= OrganTraversalLimit)
                break;
            if (HasComp<NCTransplantRejectionComponent>(organ))
                result.Add(organ);
        }
        return result;
    }

    private static NCTransplantRejectionStage ResolveStage(
        float burden,
        NCTransplantRejectionProfilePrototype profile)
    {
        if (burden < profile.StageThresholds[0])
            return NCTransplantRejectionStage.None;
        if (burden < profile.StageThresholds[1])
            return NCTransplantRejectionStage.Early;
        if (burden < profile.StageThresholds[2])
            return NCTransplantRejectionStage.Active;
        return burden < profile.StageThresholds[3]
            ? NCTransplantRejectionStage.Severe
            : NCTransplantRejectionStage.Critical;
    }

    private static bool ValidateProfile(NCTransplantRejectionProfilePrototype profile)
    {
        if (profile.ActiveUpdateInterval <= TimeSpan.Zero ||
            !float.IsFinite(profile.MaximumBurden) || profile.MaximumBurden <= 0f ||
            !float.IsFinite(profile.BaseGrowthPerSecond) || profile.BaseGrowthPerSecond <= 0f ||
            !FiniteUnit(profile.GrowthSuppressionAtMaximumImmunosuppression) ||
            !float.IsFinite(profile.RecoveryPerImmunosuppressionPerSecond) ||
            profile.RecoveryPerImmunosuppressionPerSecond < 0f ||
            profile.StageThresholds.Count != 4)
        {
            return false;
        }

        var previous = 0f;
        foreach (var threshold in profile.StageThresholds)
        {
            if (!float.IsFinite(threshold) || threshold <= previous || threshold > profile.MaximumBurden)
                return false;
            previous = threshold;
        }

        foreach (var stage in Enum.GetValues<NCTransplantRejectionStage>())
        {
            if (!FiniteRate(profile.IntegrityDamagePerSecond, stage) ||
                !FiniteRate(profile.FunctionDamagePerSecond, stage) ||
                !FiniteRate(profile.NecrosisPerSecond, stage))
            {
                return false;
            }
        }
        return true;
    }

    private static bool FiniteRate(
        IReadOnlyDictionary<NCTransplantRejectionStage, float> values,
        NCTransplantRejectionStage stage)
    {
        return values.TryGetValue(stage, out var value) && float.IsFinite(value) && value >= 0f;
    }

    private static bool FiniteUnit(float value)
    {
        return float.IsFinite(value) && value >= 0f && value <= 1f;
    }
}
