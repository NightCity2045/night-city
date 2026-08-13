// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.Medical.Treatment;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Emergency;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Medical;
using Content.Shared.Popups;
using Content.Shared.Standing;
using Content.Shared.Stacks;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Emergency;

/// <summary>
/// Production interaction boundary for CPR and physical emergency-care devices. Every completion is
/// revalidated from live server hand/range/capability state before the authoritative resolver is called.
/// </summary>
public sealed partial class NCEmergencyCareInteractionSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private StandingStateSystem _standing = default!;
    [Dependency] private NCEmergencyCareSystem _emergency = default!;
    [Dependency] private NCTreatmentInteractionSystem _treatment = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCEmergencyCareItemComponent, AfterInteractEvent>(OnItemAfterInteract);
        SubscribeLocalEvent<NCEmergencyCareItemComponent, NCEmergencyCareDoAfterEvent>(OnItemDoAfter);
        SubscribeLocalEvent<NCMedicalBodyComponent, InteractHandEvent>(
            OnPatientInteractHand,
            before: [typeof(InteractionPopupSystem)]);
        SubscribeLocalEvent<NCMedicalBodyComponent, NCCprDoAfterEvent>(OnCprDoAfter);
    }

    private void OnItemAfterInteract(Entity<NCEmergencyCareItemComponent> item, ref AfterInteractEvent args)
    {
        // Vanilla defibrillators own their toggle, battery, DoAfter and effects; their server hook calls the
        // same NC resolver after the physical discharge succeeds.
        if (HasComp<DefibrillatorComponent>(item.Owner))
            return;

        if (args.Handled || args.Target is not { } patient || !HasComp<NCMedicalBodyComponent>(patient))
            return;

        args.Handled = true;
        if (!TryValidateItem(item, args.User, patient, item.Comp.Capability, out var capability))
        {
            _popup.PopupEntity(Loc.GetString("nc-medical-interaction-invalidated"), patient, args.User);
            return;
        }

        var doAfter = new DoAfterArgs(
            EntityManager,
            args.User,
            capability.ApplicationDuration,
            new NCEmergencyCareDoAfterEvent(NCActionResolutionId.New(), item.Comp.Capability),
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
            _popup.PopupEntity(Loc.GetString("nc-medical-emergency-started"), patient, args.User);
    }

    private void OnItemDoAfter(Entity<NCEmergencyCareItemComponent> item, ref NCEmergencyCareDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || args.Target is not { } patient || args.Used != item.Owner)
            return;

        args.Handled = true;
        if (!TryValidateItem(item, args.User, patient, args.Capability, out var capability))
        {
            _popup.PopupEntity(Loc.GetString("nc-medical-interaction-invalidated"), patient, args.User);
            return;
        }

        var context = new NCEmergencyCareActionContext(
            args.ResolutionId,
            _timing.CurTime,
            GetPrototypeId(args.User),
            GetPrototypeId(item.Owner),
            item.Comp.Capability);
        var result = capability.Kind switch
        {
            NCEmergencyCareKind.Oxygen => _emergency.ApplyOxygen(patient, item.Owner, context),
            NCEmergencyCareKind.Ventilation => _emergency.ApplyVentilation(patient, item.Owner, context),
            NCEmergencyCareKind.Crystalloid => _emergency.InfuseCrystalloid(patient, item.Owner, context),
            NCEmergencyCareKind.BloodTransfusion => _emergency.TransfuseBlood(patient, item.Owner, context),
            NCEmergencyCareKind.Defibrillation => _emergency.Defibrillate(patient, item.Owner, context),
            _ => new NCEmergencyCareResult(
                args.ResolutionId,
                NCEmergencyCareOutcome.UnsupportedItem,
                0f,
                0f,
                0),
        };

        PopupResult(patient, args.User, capability.Kind, result);
    }

    private void OnPatientInteractHand(Entity<NCMedicalBodyComponent> patient, ref InteractHandEvent args)
    {
        if (args.Handled || args.User == patient.Owner || _hands.TryGetActiveItem(args.User, out _))
        {
            return;
        }

        if (_treatment.TryStartManualPressure(args.User, patient.Owner))
        {
            args.Handled = true;
            return;
        }

        if (!_standing.IsDown(patient.Owner))
            return;

        args.Handled = true;
        var profile = _prototypes.EnumeratePrototypes<NCEmergencyCareProfilePrototype>()
            .Where(candidate => candidate.DefaultForHumans)
            .OrderBy(candidate => candidate.ID, StringComparer.Ordinal)
            .FirstOrDefault();
        if (profile == null || profile.CprApplicationDuration <= TimeSpan.Zero ||
            !_interaction.InRangeUnobstructed(args.User, patient.Owner, profile.CprInteractionRange))
        {
            _popup.PopupEntity(Loc.GetString("nc-medical-interaction-invalidated"), patient.Owner, args.User);
            return;
        }

        var doAfter = new DoAfterArgs(
            EntityManager,
            args.User,
            profile.CprApplicationDuration,
            new NCCprDoAfterEvent(NCActionResolutionId.New()),
            patient.Owner,
            target: patient.Owner)
        {
            NeedHand = true,
            NeedFreeHand = true,
            BreakOnMove = true,
            BreakOnDamage = false,
            DistanceThreshold = profile.CprInteractionRange,
        };

        if (_doAfter.TryStartDoAfter(doAfter))
            _popup.PopupEntity(Loc.GetString("nc-medical-cpr-started"), patient.Owner, args.User);
    }

    private void OnCprDoAfter(Entity<NCMedicalBodyComponent> patient, ref NCCprDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || args.Target != patient.Owner)
            return;

        args.Handled = true;
        var profile = _prototypes.EnumeratePrototypes<NCEmergencyCareProfilePrototype>()
            .Where(candidate => candidate.DefaultForHumans)
            .OrderBy(candidate => candidate.ID, StringComparer.Ordinal)
            .FirstOrDefault();
        if (profile == null ||
            !_standing.IsDown(patient.Owner) ||
            _hands.TryGetActiveItem(args.User, out _) ||
            !_interaction.InRangeUnobstructed(args.User, patient.Owner, profile.CprInteractionRange))
        {
            _popup.PopupEntity(Loc.GetString("nc-medical-interaction-invalidated"), patient.Owner, args.User);
            return;
        }

        var context = new NCEmergencyCareActionContext(
            args.ResolutionId,
            _timing.CurTime,
            GetPrototypeId(args.User));
        PopupResult(
            patient.Owner,
            args.User,
            NCEmergencyCareKind.Cpr,
            _emergency.PerformCpr(patient.Owner, context));
    }

    private bool TryValidateItem(
        Entity<NCEmergencyCareItemComponent> item,
        EntityUid user,
        EntityUid patient,
        ProtoId<NCEmergencyCareCapabilityPrototype> expectedCapability,
        out NCEmergencyCareCapabilityPrototype capability)
    {
        capability = default!;
        return !Deleted(user) &&
               !Deleted(patient) &&
               item.Comp.Capability == expectedCapability &&
               HasComp<NCMedicalBodyComponent>(patient) &&
               _prototypes.TryIndex(item.Comp.Capability, out capability!) &&
               capability.Kind != NCEmergencyCareKind.Cpr &&
               capability.ApplicationDuration > TimeSpan.Zero &&
               capability.InteractionRange > 0f &&
               capability.ChargeCost > 0 &&
               HasResource(item, capability) &&
               _hands.IsHolding(user, item.Owner) &&
               _interaction.InRangeUnobstructed(user, patient, capability.InteractionRange);
    }

    private bool HasResource(
        Entity<NCEmergencyCareItemComponent> item,
        NCEmergencyCareCapabilityPrototype capability)
    {
        return capability.ResourceKind switch
        {
            NCEmergencyCareResourceKind.ComponentCharges =>
                item.Comp.ChargesRemaining >= capability.ChargeCost,
            NCEmergencyCareResourceKind.Stack =>
                TryComp<StackComponent>(item.Owner, out var stack) && stack.Count >= capability.ChargeCost,
            _ => false,
        };
    }

    private void PopupResult(
        EntityUid patient,
        EntityUid user,
        NCEmergencyCareKind kind,
        NCEmergencyCareResult result)
    {
        if (result.Succeeded && kind == NCEmergencyCareKind.BloodTransfusion &&
            TryComp<NCCirculationComponent>(patient, out var circulation) &&
            _prototypes.TryIndex(circulation.Profile, out NCPhysiologyProfilePrototype? profile) &&
            profile.NominalBloodVolume > 0f)
        {
            var restoredPercent = (int) MathF.Round(MathF.Max(
                0f,
                result.CurrentValue - result.PreviousValue) / profile.NominalBloodVolume * 100f);
            _popup.PopupEntity(
                Loc.GetString("nc-medical-transfusion-complete", ("percent", restoredPercent)),
                patient,
                user);
            return;
        }

        var key = result.Succeeded ? "nc-medical-emergency-complete" : "nc-medical-emergency-failed";
        _popup.PopupEntity(Loc.GetString(key, ("reason", result.Outcome)), patient, user);
    }

    private EntProtoId? GetPrototypeId(EntityUid entity)
    {
        return TryComp(entity, out MetaDataComponent? metadata) && metadata.EntityPrototype?.ID is { } id
            ? id
            : null;
    }
}
