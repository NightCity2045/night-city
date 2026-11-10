// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text;
using Content.Server._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Power.EntitySystems;

namespace Content.Server._NC.Medical.Pharmacology.Lab;

/// <summary>
/// The vivarium: test subjects for experimental pharmacology and toxicology. It is where an unknown compound, a
/// new modifier or a suspect batch is studied for what it really does to a living body, at the price of
/// subjects. It is a way to learn, not a gate: a known formula never has to pass through it.
/// </summary>
[RegisterComponent, Access(typeof(NCVivariumSystem))]
public sealed partial class NCVivariumComponent : Component
{
    [DataField]
    public string Slot = "pack";

    /// <summary>Test subjects available. They are used up by studies that harm them.</summary>
    [DataField]
    public int Subjects = 4;

    [DataField]
    public int Capacity = 8;

    [DataField]
    public TimeSpan Duration = TimeSpan.FromSeconds(20);

    [DataField]
    public float WastePerStudy = 1f;

    /// <summary>How many studies this vivarium has run; it numbers the subjects.</summary>
    [DataField]
    public int Studies;
}

/// <summary>A crate of test subjects for a vivarium.</summary>
[RegisterComponent]
public sealed partial class NCVivariumSubjectCrateComponent : Component
{
    [DataField]
    public int Subjects = 4;
}

public sealed class NCVivariumSystem : EntitySystem
{
    private const float InteractionRange = 1.5f;

    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private ItemSlotsSystem _slots = default!;
    [Dependency] private SharedPowerReceiverSystem _power = default!;
    [Dependency] private NCPharmaBatchSystem _batches = default!;
    [Dependency] private NCLabSystem _lab = default!;
    [Dependency] private NCPharmaWasteSystem _waste = default!;
    [Dependency] private NCBiotechActionSystem _actions = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCVivariumComponent, ActivateInWorldEvent>(OnActivate);
        SubscribeLocalEvent<NCVivariumComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<NCVivariumComponent, NCVivariumDoAfterEvent>(OnDone);
    }

    private void OnInteractUsing(Entity<NCVivariumComponent> vivarium, ref InteractUsingEvent args)
    {
        if (args.Handled || !TryComp<NCVivariumSubjectCrateComponent>(args.Used, out var crate))
            return;

        args.Handled = true;
        if (vivarium.Comp.Subjects + crate.Subjects > vivarium.Comp.Capacity)
        {
            _popup.PopupEntity(Loc.GetString("nc-lab-vivarium-full"), vivarium, args.User);
            return;
        }

        vivarium.Comp.Subjects += crate.Subjects;
        QueueDel(args.Used);
        _popup.PopupEntity(Loc.GetString("nc-lab-vivarium-restocked", ("count", vivarium.Comp.Subjects)), vivarium, args.User);
    }

    private EntityUid? GetPack(Entity<NCVivariumComponent> vivarium)
    {
        return _slots.TryGetSlot(vivarium.Owner, vivarium.Comp.Slot, out var slot) &&
               slot.Item is { } item && HasComp<NCPharmaContainerComponent>(item)
            ? item
            : null;
    }

    private void OnActivate(Entity<NCVivariumComponent> vivarium, ref ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex)
            return;

        args.Handled = true;
        if (!_power.IsPowered(vivarium.Owner))
        {
            _popup.PopupEntity(Loc.GetString("nc-lab-machine-unpowered"), vivarium, args.User);
            return;
        }

        if (vivarium.Comp.Subjects <= 0)
        {
            _popup.PopupEntity(Loc.GetString("nc-lab-vivarium-no-subjects"), vivarium, args.User);
            return;
        }

        if (GetPack(vivarium) is not { } pack || !TryComp<NCPharmaContainerComponent>(pack, out var container) ||
            container.Doses < 1)
        {
            _popup.PopupEntity(Loc.GetString("nc-lab-machine-empty"), vivarium, args.User);
            return;
        }

        if (!_waste.CanTake(vivarium.Owner, vivarium.Comp.WastePerStudy))
        {
            _popup.PopupEntity(Loc.GetString("nc-pharma-waste-full"), vivarium, args.User);
            return;
        }

        var doAfter = new DoAfterArgs(EntityManager, args.User, vivarium.Comp.Duration,
            new NCVivariumDoAfterEvent(), vivarium.Owner, target: vivarium.Owner)
        {
            BreakOnMove = true,
            DistanceThreshold = InteractionRange,
        };
        if (_doAfter.TryStartDoAfter(doAfter))
            _popup.PopupEntity(Loc.GetString("nc-lab-machine-started"), vivarium, args.User);
    }

    private void OnDone(Entity<NCVivariumComponent> vivarium, ref NCVivariumDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled)
            return;

        args.Handled = true;
        if (!_power.IsPowered(vivarium.Owner) || vivarium.Comp.Subjects <= 0 ||
            GetPack(vivarium) is not { } pack || !TryComp<NCPharmaContainerComponent>(pack, out var container) ||
            !_waste.CanTake(vivarium.Owner, vivarium.Comp.WastePerStudy))
        {
            return;
        }

        _actions.Resolve(args.User, "NCBiotechStudy", vivarium.Owner, [container.BatchId]);
        var number = ++vivarium.Comp.Studies;
        var subject = Loc.GetString("nc-lab-vivarium-subject-name", ("number", number));
        if (!_batches.TryGetPackCompound(container, out var compound) ||
            !_batches.TryStudyInVivo(container, pack, Name(args.User), subject, out var result))
        {
            _popup.PopupEntity(Loc.GetString("nc-lab-machine-wrong-sample"), vivarium, args.User);
            return;
        }

        _waste.Add(vivarium.Owner, vivarium.Comp.WastePerStudy);
        if (result.Outcome == NCPharmaStudyOutcome.Lost)
            vivarium.Comp.Subjects--;

        var report = new StringBuilder();
        report.AppendLine(Loc.GetString("nc-lab-vivarium-subject", ("subject", subject)));
        report.AppendLine(Loc.GetString("nc-lab-vivarium-outcome-" + NCPharmaBatchSystem.OutcomeId(result.Outcome)));
        report.AppendLine(Loc.GetString("nc-lab-vivarium-peak", ("peak", result.Peak.ToString("0.##"))));
        report.AppendLine(Loc.GetString("nc-lab-vivarium-ratio", ("ratio", result.ToxicRatio.ToString("P0"))));
        report.AppendLine(Loc.GetString("nc-lab-vivarium-toxin", ("toxin", result.Toxin.ToString("0.##"))));
        report.AppendLine(Loc.GetString("nc-lab-vivarium-sepsis", ("sepsis", result.Sepsis.ToString("0.##"))));
        _lab.AppendDossier(report, compound);
        _lab.Print(vivarium.Owner, args.User, Loc.GetString("nc-lab-report-vivarium"), report.ToString());
    }
}
