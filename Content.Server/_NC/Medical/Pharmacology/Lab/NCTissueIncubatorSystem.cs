// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Containers.ItemSlots;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Pharmacology.Lab;

/// <summary>A culture seed: what grows from it and how long it takes.</summary>
[RegisterComponent]
public sealed partial class NCTissueSeedComponent : Component
{
    [DataField(required: true)]
    public EntProtoId Output;

    [DataField]
    public TimeSpan Duration = TimeSpan.FromMinutes(10);
}

public enum NCTissuePhase : byte
{
    Idle,
    Growing,
    Ready,
}

/// <summary>
/// Cultivation takes real time but needs a person at both ends: someone must load the seed, and the finished
/// tissue stays in the incubator until someone collects it. A power cut pauses the growth.
/// </summary>
[RegisterComponent, Access(typeof(NCTissueIncubatorSystem))]
public sealed partial class NCTissueIncubatorComponent : Component
{
    [DataField]
    public string Slot = "seed";

    public NCTissuePhase Phase;
    public TimeSpan FinishAt;
    public EntProtoId? Output;
}

public sealed class NCTissueIncubatorSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ItemSlotsSystem _slots = default!;
    [Dependency] private SharedPowerReceiverSystem _power = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedHandsSystem _hands = default!;

    private TimeSpan _lastUpdate;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCTissueIncubatorComponent, ActivateInWorldEvent>(OnActivate);
    }

    private void OnActivate(Entity<NCTissueIncubatorComponent> incubator, ref ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex)
            return;

        args.Handled = true;
        switch (incubator.Comp.Phase)
        {
            case NCTissuePhase.Ready when incubator.Comp.Output is { } output:
                var tissue = Spawn(output, Transform(incubator).Coordinates);
                _hands.TryPickupAnyHand(args.User, tissue);
                incubator.Comp.Phase = NCTissuePhase.Idle;
                incubator.Comp.Output = null;
                _popup.PopupEntity(Loc.GetString("nc-lab-incubator-collected"), incubator, args.User);
                break;
            case NCTissuePhase.Growing:
                var remaining = Math.Max(0, (int) (incubator.Comp.FinishAt - _timing.CurTime).TotalSeconds);
                _popup.PopupEntity(Loc.GetString("nc-lab-incubator-growing", ("seconds", remaining)), incubator, args.User);
                break;
            default:
                TryStart(incubator, args.User);
                break;
        }
    }

    private void TryStart(Entity<NCTissueIncubatorComponent> incubator, EntityUid user)
    {
        if (!_power.IsPowered(incubator.Owner))
        {
            _popup.PopupEntity(Loc.GetString("nc-lab-machine-unpowered"), incubator, user);
            return;
        }

        if (!_slots.TryGetSlot(incubator.Owner, incubator.Comp.Slot, out var slot) ||
            slot.Item is not { } seed || !TryComp<NCTissueSeedComponent>(seed, out var seedData))
        {
            _popup.PopupEntity(Loc.GetString("nc-lab-incubator-no-seed"), incubator, user);
            return;
        }

        incubator.Comp.Phase = NCTissuePhase.Growing;
        incubator.Comp.FinishAt = _timing.CurTime + seedData.Duration;
        incubator.Comp.Output = seedData.Output;
        QueueDel(seed);
        _popup.PopupEntity(Loc.GetString("nc-lab-incubator-started"), incubator, user);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var elapsed = now - _lastUpdate;
        _lastUpdate = now;
        var query = EntityQueryEnumerator<NCTissueIncubatorComponent>();
        while (query.MoveNext(out var uid, out var incubator))
        {
            if (incubator.Phase != NCTissuePhase.Growing)
                continue;

            if (!_power.IsPowered(uid))
            {
                // No power, no growth: the clock stands still until the machine works again.
                incubator.FinishAt += elapsed;
                continue;
            }

            if (now >= incubator.FinishAt)
                incubator.Phase = NCTissuePhase.Ready;
        }
    }
}
