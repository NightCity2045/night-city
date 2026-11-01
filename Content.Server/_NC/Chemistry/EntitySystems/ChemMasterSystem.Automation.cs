// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server.Chemistry.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared.ActionBlocker;
using Content.Shared.Chemistry;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction;

namespace Content.Server.Chemistry.EntitySystems;

public sealed record NCChemPackagingOutcome(int Completed, bool Consumed);

public sealed partial class ChemMasterSystem
{
    [Dependency] private SharedInteractionSystem _automationInteraction = default!;
    [Dependency] private ActionBlockerSystem _automationBlocker = default!;
    [Dependency] private PowerReceiverSystem _automationPower = default!;
    private readonly HashSet<EntityUid> _automationPackaging = [];

    private bool CanAutomatePackaging(EntityUid uid, EntityUid actor, string label) => _automationPackaging.Count < 64 &&
        !_automationPackaging.Contains(uid) && Exists(actor) && _automationBlocker.CanConsciouslyPerformAction(actor) &&
        _automationInteraction.InRangeUnobstructed(actor, uid) && _automationPower.IsPowered(uid) && label.Length <= SharedChemMaster.LabelMaxLength;

    public NCChemPackagingOutcome TryCreatePills(Entity<ChemMasterComponent?> ent, EntityUid actor, int number, int dosage, string label, Func<bool> guard)
    {
        if (!Resolve(ent, ref ent.Comp) || number is < 1 or > 16 || dosage is < 1 or > 1000 || !CanAutomatePackaging(ent.Owner, actor, label) || !guard()) return new(0, false);
        _automationPackaging.Add(ent.Owner);
        try
        {
            var result = CreatePillsCore((ent.Owner, ent.Comp), actor, (uint) number, (uint) dosage, label, guard);
            return new(result.Completed, result.Consumed);
        }
        finally { _automationPackaging.Remove(ent.Owner); }
    }
    public NCChemPackagingOutcome TryBottle(Entity<ChemMasterComponent?> ent, EntityUid actor, int dosage, string label, Func<bool> guard)
    {
        if (!Resolve(ent, ref ent.Comp) || dosage is < 1 or > 1000 || !CanAutomatePackaging(ent.Owner, actor, label) || !guard()) return new(0, false);
        _automationPackaging.Add(ent.Owner);
        try
        {
            var result = BottleCore((ent.Owner, ent.Comp), actor, FixedPoint2.New(dosage), label, guard);
            return new(result.Completed, result.Consumed);
        }
        finally { _automationPackaging.Remove(ent.Owner); }
    }
}
