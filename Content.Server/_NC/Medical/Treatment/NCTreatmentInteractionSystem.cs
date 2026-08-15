// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.Combat.Targeting;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Treatment;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Charges.Components;
using Content.Shared.Charges.Systems;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Treatment;

/// <summary>
/// Connects physical first-aid items to the authoritative treatment resolver. Selection is bounded to
/// one patient's wound/source collections and never performs a world scan or medical check/roll.
/// </summary>
public sealed partial class NCTreatmentInteractionSystem : EntitySystem
{
    private static readonly ProtoId<NCTreatmentCapabilityPrototype> ManualPressureCapability =
        "NCManualPressureCapability";

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedStackSystem _stacks = default!;
    [Dependency] private SharedChargesSystem _charges = default!;
    [Dependency] private NCTargetingSelectionSystem _targeting = default!;
    [Dependency] private NCTreatmentSystem _treatment = default!;
    [Dependency] private NCCityActorDematerializationSystem _dematerialization = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCTreatmentItemComponent, AfterInteractEvent>(OnAfterInteract);
        SubscribeLocalEvent<NCTreatmentItemComponent, NCTreatmentDoAfterEvent>(OnDoAfter);
        SubscribeLocalEvent<NCMedicalBodyComponent, NCTreatmentDoAfterEvent>(OnManualPressureDoAfter);
    }

    /// <summary>
    /// Gives empty-hand first aid priority over CPR on the same patient interaction. The emergency
    /// interaction system owns the single InteractHand subscription and delegates here first.
    /// </summary>
    public bool TryStartManualPressure(EntityUid user, EntityUid patient)
    {
        if (user == patient || !HasComp<NCMedicalBodyComponent>(patient) ||
            _hands.TryGetActiveItem(user, out _) ||
            !_prototypes.TryIndex(ManualPressureCapability, out var capability) ||
            !_interaction.InRangeUnobstructed(user, patient, capability.InteractionRange) ||
            !TrySelectWound(user, patient, NCTreatmentKind.Pressure, out var woundId))
        {
            return false;
        }

        var doAfter = new DoAfterArgs(
            EntityManager,
            user,
            capability.ApplicationDuration,
            new NCTreatmentDoAfterEvent(NCActionResolutionId.New(), woundId, capability.ID),
            patient,
            target: patient)
        {
            NeedHand = true,
            NeedFreeHand = true,
            BreakOnMove = true,
            BreakOnDamage = false,
            DistanceThreshold = capability.InteractionRange,
        };

        if (_doAfter.TryStartDoAfter(doAfter))
        {
            _dematerialization.BeginBlocker(
                patient,
                NCCityDematerializationBlockerFlags.PlayerTreatment);
            _popup.PopupEntity(Loc.GetString("nc-medical-treatment-started"), patient, user);
            return true;
        }

        return false;
    }

    private void OnManualPressureDoAfter(Entity<NCMedicalBodyComponent> patient, ref NCTreatmentDoAfterEvent args)
    {
        _dematerialization.EndBlocker(
            patient.Owner,
            NCCityDematerializationBlockerFlags.PlayerTreatment);
        if (args.Handled || args.Cancelled || args.Target != patient.Owner ||
            !_prototypes.TryIndex(args.Capability, out NCTreatmentCapabilityPrototype? capability) ||
            capability.Kind != NCTreatmentKind.Pressure)
        {
            return;
        }

        args.Handled = true;
        if (_hands.TryGetActiveItem(args.User, out _) ||
            !_interaction.InRangeUnobstructed(args.User, patient.Owner, capability.InteractionRange) ||
            !TryComp<NCWoundContainerComponent>(patient.Owner, out var wounds) ||
            !wounds.Wounds.ContainsKey(args.WoundId))
        {
            _popup.PopupEntity(Loc.GetString("nc-medical-interaction-invalidated"), patient.Owner, args.User);
            return;
        }

        var context = new NCTreatmentActionContext(
            args.ResolutionId,
            _timing.CurTime,
            GetPrototypeId(args.User),
            null,
            args.Capability);
        var result = _treatment.ApplyPressure(patient.Owner, args.WoundId, context, capability.Profile);
        _popup.PopupEntity(
            Loc.GetString(result.Succeeded ? "nc-medical-treatment-complete" : "nc-medical-treatment-failed",
                ("reason", result.Outcome)),
            patient.Owner,
            args.User);
    }

    private void OnAfterInteract(Entity<NCTreatmentItemComponent> item, ref AfterInteractEvent args)
    {
        if (args.Handled || args.Target is not { } patient || !HasComp<NCMedicalBodyComponent>(patient))
            return;

        args.Handled = true;
        if (!TryValidate(item, args.User, patient, item.Comp.Capability, out var capability, true) ||
            !TrySelectWound(args.User, patient, capability.Kind, out var woundId))
        {
            _popup.PopupEntity(Loc.GetString("nc-medical-treatment-no-target"), patient, args.User);
            return;
        }

        var resolutionId = NCActionResolutionId.New();
        var doAfter = new DoAfterArgs(
            EntityManager,
            args.User,
            capability.ApplicationDuration,
            new NCTreatmentDoAfterEvent(resolutionId, woundId, item.Comp.Capability),
            item.Owner,
            target: patient,
            used: item.Owner)
        {
            NeedHand = true,
            BreakOnMove = true,
            BreakOnDamage = false,
            DistanceThreshold = capability.InteractionRange,
        };

        if (_doAfter.TryStartDoAfter(doAfter))
        {
            _dematerialization.BeginBlocker(
                patient,
                NCCityDematerializationBlockerFlags.PlayerTreatment);
            _popup.PopupEntity(Loc.GetString("nc-medical-treatment-started"), patient, args.User);
        }
    }

    private void OnDoAfter(Entity<NCTreatmentItemComponent> item, ref NCTreatmentDoAfterEvent args)
    {
        if (args.Target is { } blockerTarget)
        {
            _dematerialization.EndBlocker(
                blockerTarget,
                NCCityDematerializationBlockerFlags.PlayerTreatment);
        }
        if (args.Handled || args.Cancelled || args.Target is not { } patient || args.Used != item.Owner)
            return;

        args.Handled = true;
        if (!TryValidate(item, args.User, patient, args.Capability, out var capability, true))
        {
            _popup.PopupEntity(Loc.GetString("nc-medical-interaction-invalidated"), patient, args.User);
            return;
        }

        var context = new NCTreatmentActionContext(
            args.ResolutionId,
            _timing.CurTime,
            GetPrototypeId(args.User),
            GetPrototypeId(item.Owner),
            item.Comp.Capability);
        var result = _treatment.ApplyFromItem(item.Owner, patient, args.WoundId, context);
        if (!result.Succeeded)
        {
            _popup.PopupEntity(
                Loc.GetString("nc-medical-treatment-failed", ("reason", result.Outcome)),
                patient,
                args.User);
            return;
        }

        // Resources are consumed only after the medical mutation is accepted. The resolver's action ID
        // makes a duplicate completion harmless, and a duplicate result never reaches this branch.
        if (!ConsumeResource(item.Owner, capability))
            return;

        var message = capability.Kind is NCTreatmentKind.Pressure or
            NCTreatmentKind.Bandage or
            NCTreatmentKind.Hemostatic or
            NCTreatmentKind.Tourniquet
            ? TreatmentBleedingMessage(result)
            : Loc.GetString("nc-medical-treatment-complete");
        _popup.PopupEntity(message, patient, args.User);
    }

    /// <summary>
    /// Selects the worst visible external source, preferring the user's explicit doll zone. Stable tie
    /// breakers make selection reproducible and UI-independent.
    /// </summary>
    public bool TrySelectWound(
        EntityUid user,
        EntityUid patient,
        NCTreatmentKind kind,
        out Guid woundId)
    {
        woundId = default;
        if (!TryComp<NCWoundContainerComponent>(patient, out var wounds))
            return false;

        TryComp<NCBleedingComponent>(patient, out var bleeding);

        var selected = _targeting.GetSelection(user);
        var preferredZone = selected.Mode == NCTargetingMode.SelectedZone ? selected.SelectedZone : null;
        var candidates = new List<(NCWoundRecord Wound, float Rate, int ChannelRank)>();
        foreach (var wound in wounds.Wounds.Values)
        {
            if (!CanReceive(wound, kind))
                continue;

            if (kind == NCTreatmentKind.Splint)
            {
                if (_treatment.CanSplintZone(patient, wound.TargetZone))
                    candidates.Add((wound, wound.IntegrityDamage.Float(), 0));
                continue;
            }

            if (bleeding == null)
                continue;

            var rate = 0f;
            var channelRank = 0;
            foreach (var source in bleeding.Sources)
            {
                if (source.WoundId != wound.WoundId ||
                    source.Channel == NCBleedingChannel.Internal ||
                    source.CurrentRate <= 0f)
                {
                    continue;
                }

                rate += source.CurrentRate;
                channelRank = Math.Max(channelRank, source.Channel switch
                {
                    NCBleedingChannel.ExternalArterial => 3,
                    NCBleedingChannel.ExternalVenous => 2,
                    _ => 1,
                });
            }

            if (rate > 0f)
                candidates.Add((wound, rate, channelRank));
        }

        if (candidates.Count == 0)
            return false;

        var selectedCandidate = candidates
            .Where(candidate => preferredZone == null || candidate.Wound.TargetZone == preferredZone)
            .OrderByDescending(candidate => candidate.ChannelRank)
            .ThenByDescending(candidate => candidate.Rate)
            .ThenByDescending(candidate => candidate.Wound.Severity)
            .ThenBy(candidate => candidate.Wound.CreatedAt)
            .ThenBy(candidate => candidate.Wound.WoundId)
            .FirstOrDefault();
        if (selectedCandidate.Wound == null)
        {
            selectedCandidate = candidates
                .OrderByDescending(candidate => candidate.ChannelRank)
                .ThenByDescending(candidate => candidate.Rate)
                .ThenByDescending(candidate => candidate.Wound.Severity)
                .ThenBy(candidate => candidate.Wound.CreatedAt)
                .ThenBy(candidate => candidate.Wound.WoundId)
                .First();
        }

        woundId = selectedCandidate.Wound.WoundId;
        return true;
    }

    private bool TryValidate(
        Entity<NCTreatmentItemComponent> item,
        EntityUid user,
        EntityUid patient,
        ProtoId<NCTreatmentCapabilityPrototype> expectedCapability,
        out NCTreatmentCapabilityPrototype capability,
        bool requireResource)
    {
        capability = default!;
        return !Deleted(user) &&
               !Deleted(patient) &&
               item.Comp.Capability == expectedCapability &&
               HasComp<NCMedicalBodyComponent>(patient) &&
               _prototypes.TryIndex(item.Comp.Capability, out capability!) &&
               capability.ApplicationDuration > TimeSpan.Zero &&
               capability.InteractionRange > 0f &&
               _hands.IsHolding(user, item.Owner) &&
               _interaction.InRangeUnobstructed(user, patient, capability.InteractionRange) &&
               (!requireResource || HasResource(item.Owner, capability));
    }

    private bool HasResource(EntityUid item, NCTreatmentCapabilityPrototype capability)
    {
        if (capability.ResourceCost <= 0)
            return capability.ResourceKind == NCTreatmentResourceKind.None;

        return capability.ResourceKind switch
        {
            NCTreatmentResourceKind.None => true,
            NCTreatmentResourceKind.Stack => TryComp<StackComponent>(item, out var stack) &&
                                             stack.Count >= capability.ResourceCost,
            NCTreatmentResourceKind.Charges => TryComp<LimitedChargesComponent>(item, out var charges) &&
                                               _charges.HasCharges((item, charges), capability.ResourceCost),
            _ => false,
        };
    }

    private bool ConsumeResource(EntityUid item, NCTreatmentCapabilityPrototype capability)
    {
        switch (capability.ResourceKind)
        {
            case NCTreatmentResourceKind.None:
                return true;
            case NCTreatmentResourceKind.Stack:
                return TryComp<StackComponent>(item, out var stack) &&
                       _stacks.TryUse((item, stack), capability.ResourceCost);
            case NCTreatmentResourceKind.Charges:
                return TryComp<LimitedChargesComponent>(item, out var charges) &&
                       _charges.TryUseCharges((item, charges), capability.ResourceCost);
            default:
                return false;
        }
    }

    private static bool CanReceive(NCWoundRecord wound, NCTreatmentKind kind)
    {
        return kind switch
        {
            NCTreatmentKind.Pressure => !wound.TreatmentFlags.HasFlag(NCWoundTreatmentFlags.Pressure),
            NCTreatmentKind.Bandage => !wound.TreatmentFlags.HasFlag(NCWoundTreatmentFlags.Bandaged),
            NCTreatmentKind.Hemostatic => !wound.TreatmentFlags.HasFlag(NCWoundTreatmentFlags.Hemostatic),
            NCTreatmentKind.Tourniquet => !wound.TreatmentFlags.HasFlag(NCWoundTreatmentFlags.Tourniqueted),
            NCTreatmentKind.Splint => true,
            _ => false,
        };
    }

    private string TreatmentBleedingMessage(in NCTreatmentResult result)
    {
        if (result.CurrentBleedRate <= 0f)
            return Loc.GetString("nc-medical-treatment-bleeding-controlled");

        var reduction = result.PreviousBleedRate > 0f
            ? (int) MathF.Round(Math.Clamp(
                1f - result.CurrentBleedRate / result.PreviousBleedRate,
                0f,
                1f) * 100f)
            : 0;
        return Loc.GetString("nc-medical-treatment-bleeding-reduced", ("percent", reduction));
    }

    private EntProtoId? GetPrototypeId(EntityUid entity)
    {
        return TryComp(entity, out MetaDataComponent? metadata) && metadata.EntityPrototype?.ID is { } id
            ? id
            : null;
    }
}
