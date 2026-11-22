// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Power.EntitySystems;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Pharmacology.Lab;

/// <summary>A laboratory machine that is operated through a window: its slots, its readings and a start button.</summary>
[RegisterComponent, Access(typeof(NCLabMachineUiSystem))]
public sealed partial class NCLabMachineUiComponent : Component
{
    /// <summary>The researcher whose procedure the machine is running, so the window can follow it.</summary>
    [ViewVariables]
    public EntityUid? Runner;
}

/// <summary>Asks a machine to start its procedure. The machine's own system answers it, as it did a touch before.</summary>
[ByRefEvent]
public record struct NCLabMachineStartEvent(EntityUid User)
{
    public bool Handled;
}

/// <summary>Asks a machine what it has to report: readings, a different button or a running state of its own.</summary>
[ByRefEvent]
public record struct NCLabMachineStatusEvent
{
    public List<NCLabMachineReadout> Readouts;

    /// <summary>Set by machines that run without a timed procedure, such as an incubator.</summary>
    public NCLabMachinePhase? Phase;

    public float Progress;
    public int Remaining;
    public string? StartKey;
    public bool? CanStart;

    public NCLabMachineStatusEvent()
    {
        Readouts = [];
    }
}

/// <summary>
/// One window for every laboratory machine. Touching the machine opens it; the start button runs the same
/// procedure a touch used to, and the machine's own system still decides whether it may run.
/// </summary>
public sealed partial class NCLabMachineUiSystem : EntitySystem
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(500);

    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private ItemSlotsSystem _slots = default!;
    [Dependency] private SharedPowerReceiverSystem _power = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private NCPharmaWasteSystem _waste = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;

    private TimeSpan _nextRefresh;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCLabMachineUiComponent, ActivateInWorldEvent>(OnActivate);
        SubscribeLocalEvent<NCLabMachineUiComponent, BoundUIOpenedEvent>(OnOpened);
        SubscribeLocalEvent<NCLabMachineUiComponent, NCLabMachineStartMessage>(OnStart);
        SubscribeLocalEvent<NCLabMachineUiComponent, NCLabMachineCancelMessage>(OnCancel);
        SubscribeLocalEvent<NCLabMachineUiComponent, NCLabMachineEjectMessage>(OnEject);
        SubscribeLocalEvent<NCLabMachineUiComponent, EntInsertedIntoContainerMessage>(OnSlotChanged);
        SubscribeLocalEvent<NCLabMachineUiComponent, EntRemovedFromContainerMessage>(OnSlotChanged);
    }

    private void OnActivate(Entity<NCLabMachineUiComponent> machine, ref ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex)
            return;

        args.Handled = true;
        _ui.OpenUi(machine.Owner, NCLabMachineUiKey.Key, args.User);
    }

    private void OnOpened(Entity<NCLabMachineUiComponent> machine, ref BoundUIOpenedEvent args)
    {
        UpdateUi(machine);
    }

    private void OnSlotChanged(Entity<NCLabMachineUiComponent> machine, ref EntInsertedIntoContainerMessage args)
    {
        UpdateUi(machine);
    }

    private void OnSlotChanged(Entity<NCLabMachineUiComponent> machine, ref EntRemovedFromContainerMessage args)
    {
        UpdateUi(machine);
    }

    private void OnStart(Entity<NCLabMachineUiComponent> machine, ref NCLabMachineStartMessage args)
    {
        if (!_power.IsPowered(machine.Owner))
        {
            _popup.PopupEntity(Loc.GetString("nc-lab-machine-unpowered"), machine, args.Actor);
            return;
        }

        var start = new NCLabMachineStartEvent(args.Actor);
        RaiseLocalEvent(machine, ref start);
        if (!start.Handled)
            _popup.PopupEntity(Loc.GetString("nc-lab-machine-cannot-start"), machine, args.Actor);
        else
            machine.Comp.Runner = args.Actor;

        UpdateUi(machine);
    }

    private void OnCancel(Entity<NCLabMachineUiComponent> machine, ref NCLabMachineCancelMessage args)
    {
        if (FindRun(machine, out var runner, out var id))
            _doAfter.Cancel(runner, id);
        UpdateUi(machine);
    }

    private void OnEject(Entity<NCLabMachineUiComponent> machine, ref NCLabMachineEjectMessage args)
    {
        // A machine at work keeps what is in it.
        if (FindRun(machine, out _, out _))
        {
            _popup.PopupEntity(Loc.GetString("nc-lab-machine-busy"), machine, args.Actor);
            return;
        }

        _slots.TryEject(machine.Owner, args.Slot, args.Actor, out _);
        UpdateUi(machine);
    }

    /// <summary>The procedure the machine is running for its researcher, if any.</summary>
    private bool FindRun(Entity<NCLabMachineUiComponent> machine, out EntityUid runner, out ushort id)
        => FindRun(machine, out runner, out id, out _);

    private bool FindRun(Entity<NCLabMachineUiComponent> machine, out EntityUid runner, out ushort id, out Content.Shared.DoAfter.DoAfter run)
    {
        runner = default;
        id = default;
        run = default!;
        if (machine.Comp.Runner is not { } user || !TryComp<DoAfterComponent>(user, out var doAfters))
            return false;

        foreach (var (key, doAfter) in doAfters.DoAfters)
        {
            if (doAfter.Args.Target != machine.Owner || doAfter.Completed || doAfter.Cancelled)
                continue;

            runner = user;
            id = key;
            run = doAfter;
            return true;
        }

        return false;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.CurTime < _nextRefresh)
            return;

        _nextRefresh = _timing.CurTime + RefreshInterval;
        var query = EntityQueryEnumerator<NCLabMachineUiComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (_ui.IsUiOpen(uid, NCLabMachineUiKey.Key))
                UpdateUi((uid, comp));
            else
                SetWorking(uid, IsWorking((uid, comp)));
        }

        // The synthesizers and the thermoreactor run on their own run components.
        var synthesizers = EntityQueryEnumerator<NCPharmaSynthesizerComponent>();
        while (synthesizers.MoveNext(out var uid, out _))
            SetWorking(uid, HasComp<NCPharmaSynthesisRunComponent>(uid));
        var reactors = EntityQueryEnumerator<NCPharmaReactorComponent>();
        while (reactors.MoveNext(out var uid, out _))
            SetWorking(uid, HasComp<NCPharmaReactorRunComponent>(uid));
    }

    private bool IsWorking(Entity<NCLabMachineUiComponent> machine)
    {
        if (!_power.IsPowered(machine.Owner))
            return false;

        var status = new NCLabMachineStatusEvent();
        RaiseLocalEvent(machine, ref status);
        return status.Phase is { } own
            ? own == NCLabMachinePhase.Working
            : FindRun(machine, out _, out _);
    }

    private void SetWorking(EntityUid uid, bool working)
    {
        _appearance.SetData(uid, NCLabMachineVisuals.Working, working);
    }

    private void UpdateUi(Entity<NCLabMachineUiComponent> machine)
    {
        var status = new NCLabMachineStatusEvent();
        RaiseLocalEvent(machine, ref status);

        var phase = NCLabMachinePhase.Idle;
        var progress = 0f;
        var remaining = 0;
        if (FindRun(machine, out _, out _, out var run))
        {
            phase = NCLabMachinePhase.Working;
            var delay = run.Args.Delay.TotalSeconds;
            var elapsed = (_timing.CurTime - run.StartTime).TotalSeconds;
            progress = delay > 0 ? (float) Math.Clamp(elapsed / delay, 0d, 1d) : 1f;
            remaining = (int) Math.Max(0d, Math.Ceiling(delay - elapsed));
        }
        else
        {
            machine.Comp.Runner = null;
        }

        if (status.Phase is { } own)
        {
            phase = own;
            progress = status.Progress;
            remaining = status.Remaining;
        }

        var slots = new List<NCLabMachineSlot>();
        if (TryComp<ItemSlotsComponent>(machine, out var itemSlots))
        {
            foreach (var (slotId, slot) in itemSlots.Slots.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                slots.Add(new NCLabMachineSlot(slotId, slot.Name, slot.Item is { } item ? GetNetEntity(item) : null));
        }

        var readouts = status.Readouts;
        if (_waste.TryGetFill(machine.Owner, out var used, out var capacity))
        {
            readouts.Add(new NCLabMachineReadout("nc-lab-machine-ui-waste",
                $"{used:0.#} / {capacity:0.#}"));
        }

        var powered = _power.IsPowered(machine.Owner);
        SetWorking(machine.Owner, powered && phase == NCLabMachinePhase.Working);
        _ui.SetUiState(
            machine.Owner,
            NCLabMachineUiKey.Key,
            new NCLabMachineState(
                powered,
                phase,
                progress,
                remaining,
                powered && phase != NCLabMachinePhase.Working && (status.CanStart ?? true),
                status.StartKey ?? "nc-lab-machine-ui-start",
                slots.ToArray(),
                readouts.ToArray()));
    }
}
