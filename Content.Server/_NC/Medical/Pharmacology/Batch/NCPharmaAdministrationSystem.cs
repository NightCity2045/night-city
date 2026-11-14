// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// Connects a physical dose pack to the batch registry. The pack is used on a patient, a cancellable DoAfter
/// runs for the length of the delivery form, and the dose is resolved only when it completes.
/// </summary>
public sealed partial class NCPharmaAdministrationSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private NCPharmaBatchSystem _batches = default!;

    private const float InteractionRange = 1.5f;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCPharmaContainerComponent, AfterInteractEvent>(OnAfterInteract);
        SubscribeLocalEvent<NCPharmaContainerComponent, NCPharmaAdministerDoAfterEvent>(OnDoAfter);
    }

    private void OnAfterInteract(Entity<NCPharmaContainerComponent> container, ref AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || args.Target is not { } patient)
            return;

        args.Handled = TryStartAdminister(args.User, container, patient);
    }

    /// <summary>Starts administering one dose. Returns true when a DoAfter was started.</summary>
    public bool TryStartAdminister(EntityUid user, Entity<NCPharmaContainerComponent> container, EntityUid patient)
    {
        if (!CanStartAdminister(user, container, patient, out var form))
            return false;

        var doAfter = new DoAfterArgs(
            EntityManager,
            user,
            form.AdministrationDelay,
            new NCPharmaAdministerDoAfterEvent(),
            container.Owner,
            target: patient,
            used: container.Owner)
        {
            NeedHand = true,
            BreakOnMove = true,
            BreakOnDamage = true,
            DistanceThreshold = InteractionRange,
        };

        if (!_doAfter.TryStartDoAfter(doAfter))
            return false;

        _popup.PopupEntity(Loc.GetString("nc-pharma-administer-started"), patient, user);
        return true;
    }

    public bool CanStartAdminister(
        EntityUid user,
        Entity<NCPharmaContainerComponent> container,
        EntityUid patient,
        out NCPharmaDeliveryFormPrototype form)
    {
        form = default!;
        if (!HasComp<NCMedicalBodyComponent>(patient) ||
            !_interaction.InRangeUnobstructed(user, patient, InteractionRange) ||
            !_batches.CanAdminister(patient, container, out _, out _, out var resolvedForm, out _))
        {
            return false;
        }

        form = resolvedForm;
        return true;
    }

    private void OnDoAfter(Entity<NCPharmaContainerComponent> container, ref NCPharmaAdministerDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || args.Target is not { } patient)
            return;

        args.Handled = true;
        if (!_interaction.InRangeUnobstructed(args.User, patient, InteractionRange) ||
            !_batches.TryAdministerDose(args.User, patient, container, out _))
        {
            _popup.PopupEntity(Loc.GetString("nc-pharma-administer-failed"), patient, args.User);
            return;
        }

        _popup.PopupEntity(Loc.GetString("nc-pharma-administer-complete"), patient, args.User);
    }
}
