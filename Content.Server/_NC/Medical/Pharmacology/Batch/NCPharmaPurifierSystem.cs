// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Power.EntitySystems;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// Purification of raw material. Every pass raises purity by a share of the gap to the ceiling, so the last
/// percents are the dearest, and every pass costs material, filter capacity and time.
/// </summary>
[RegisterComponent, Access(typeof(NCPharmaPurifierSystem))]
public sealed partial class NCPharmaPurifierComponent : Component
{
    [DataField]
    public string Slot = "source";

    [DataField]
    public int FilterCharges = 10;

    [DataField]
    public TimeSpan PassDuration = TimeSpan.FromSeconds(20);
}

/// <summary>A replacement filter for a purifier.</summary>
[RegisterComponent]
public sealed partial class NCPharmaFilterCartridgeComponent : Component
{
    [DataField]
    public int Charges = 10;
}

public sealed class NCPharmaPurifierSystem : EntitySystem
{
    private const float InteractionRange = 1.5f;

    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private ItemSlotsSystem _slots = default!;
    [Dependency] private SharedPowerReceiverSystem _power = default!;
    [Dependency] private NCPharmaBatchSystem _batches = default!;
    [Dependency] private Content.Server._NC.Medical.Pharmacology.Lab.NCBiotechActionSystem _actions = default!;
    [Dependency] private Content.Server._NC.Medical.Pharmacology.Lab.NCPharmaWasteSystem _waste = default!;

    private const float WastePerPass = 2f;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCPharmaPurifierComponent, ActivateInWorldEvent>(OnActivate);
        SubscribeLocalEvent<NCPharmaPurifierComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<NCPharmaPurifierComponent, NCPharmaPurifyDoAfterEvent>(OnDone);
    }

    private void OnInteractUsing(Entity<NCPharmaPurifierComponent> purifier, ref InteractUsingEvent args)
    {
        if (args.Handled || !TryComp<NCPharmaFilterCartridgeComponent>(args.Used, out var cartridge))
            return;

        args.Handled = true;
        purifier.Comp.FilterCharges += cartridge.Charges;
        QueueDel(args.Used);
        _popup.PopupEntity(Loc.GetString("nc-pharma-purifier-refilled"), purifier, args.User);
    }

    private void OnActivate(Entity<NCPharmaPurifierComponent> purifier, ref ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex)
            return;

        args.Handled = true;
        if (!_power.IsPowered(purifier.Owner))
        {
            _popup.PopupEntity(Loc.GetString("nc-lab-machine-unpowered"), purifier, args.User);
            return;
        }

        if (purifier.Comp.FilterCharges <= 0)
        {
            _popup.PopupEntity(Loc.GetString("nc-pharma-purifier-no-filter"), purifier, args.User);
            return;
        }

        if (!_waste.CanTake(purifier.Owner, WastePerPass))
        {
            _popup.PopupEntity(Loc.GetString("nc-pharma-waste-full"), purifier, args.User);
            return;
        }

        if (GetSource(purifier) == null)
        {
            _popup.PopupEntity(Loc.GetString("nc-lab-machine-empty"), purifier, args.User);
            return;
        }

        var doAfter = new DoAfterArgs(EntityManager, args.User, purifier.Comp.PassDuration,
            new NCPharmaPurifyDoAfterEvent(), purifier.Owner, target: purifier.Owner)
        {
            BreakOnMove = true,
            DistanceThreshold = InteractionRange,
        };
        if (_doAfter.TryStartDoAfter(doAfter))
            _popup.PopupEntity(Loc.GetString("nc-lab-machine-started"), purifier, args.User);
    }

    private EntityUid? GetSource(Entity<NCPharmaPurifierComponent> purifier)
    {
        return _slots.TryGetSlot(purifier.Owner, purifier.Comp.Slot, out var slot) &&
               slot.Item is { } item && HasComp<NCPharmaSourceComponent>(item)
            ? item
            : null;
    }

    private void OnDone(Entity<NCPharmaPurifierComponent> purifier, ref NCPharmaPurifyDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled)
            return;

        args.Handled = true;
        if (!_power.IsPowered(purifier.Owner) || purifier.Comp.FilterCharges <= 0 ||
            GetSource(purifier) is not { } item || !TryComp<NCPharmaSourceComponent>(item, out var source) ||
            !_batches.TryGetTuning(out var tuning))
        {
            return;
        }

        purifier.Comp.FilterCharges--;
        _waste.Add(purifier.Owner, WastePerPass);
        var (gained, units) = NCPharmaBatchMath.Purify(tuning, source.Purity, source.Units);
        // The common action-resolution layer decides how well the pass went; a plain pass has quality 1.
        var quality = _actions.Resolve(args.User, "NCBiotechPurify", purifier.Owner).Quality;
        var purity = source.Purity + (gained - source.Purity) * quality;
        source.Purity = purity;
        source.Units = units;
        Dirty(item, source);
        _popup.PopupEntity(Loc.GetString("nc-pharma-purifier-done",
            ("purity", purity.ToString("P1")), ("units", units.ToString("0.#"))), purifier, args.User);
    }
}
