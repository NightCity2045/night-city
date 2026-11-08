// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Optics;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Verbs;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Cyberware.Optics;

public sealed partial class NCCyberOpticsSystem
{
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;

    private void InitializeSurgery() => SubscribeLocalEvent<NCOpticWearerComponent, NCOpticModuleDoAfterEvent>(OnModuleSwap);

    private bool CanServiceEye(EntityUid eye, EntityUid user, EntityUid body) =>
        user != body && _blocker.CanInteract(user, body) && _interaction.InRangeUnobstructed(user, body, 1.5f) &&
        _cyberware.TryGetOwningBody(eye, out var owner) && owner == body &&
        TryComp<NCSurgicalSiteComponent>(eye, out var site) && site.Contamination <= .1f && site.BleedingControlled &&
        site.Incisions.Exists(incision => !incision.Sutured && incision.Depth >= .44f && incision.EdgeSeparation >= .2f && incision.Contamination <= .1f) &&
        _hands.EnumerateHeld(user).Any(item => TryComp<NCSurgeryToolComponent>(item, out var tool) && tool.Capabilities.ContainsKey("NCSurgeryToolCyberware"));

    private void AddSurgicalVerbs(EntityUid body, EntityUid user, SortedSet<AlternativeVerb> verbs)
    {
        var query = EntityQueryEnumerator<NCCyberEyeComponent>();
        while (query.MoveNext(out var eye, out _))
        {
            if (!HasComp<NCCyberEyeComponent>(eye) || !CanServiceEye(eye, user, body) || !TryComp<NCCyberwareAssemblyComponent>(eye, out var assembly)) continue;
            foreach (var slot in assembly.OptionalSlots)
            {
                if (!assembly.Parts.TryGetValue(slot, out var socket)) continue;
                if (socket.ContainedEntity is { } installed)
                {
                    var target = installed;
                    verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-optics-remove-option", ("eye", Name(eye)), ("part", Name(target))),
                        Act = () => TryBeginModuleSwap(eye, user, body, target, slot, true) });
                }
                else foreach (var held in _hands.EnumerateHeld(user))
                {
                    if (!TryComp<NCCyberwarePartComponent>(held, out var part) || !HasComp<NCOpticModuleComponent>(held)) continue;
                    var option = Comp<NCOpticModuleComponent>(held);
                    if (HasOption(assembly, option.Mode)) continue;
                    var profile = _prototypes.Index(part.Profile);
                    if (profile.Slot != slot && !profile.CompatibleSlots.Contains(slot)) continue;
                    var target = held;
                    verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-optics-insert-option", ("eye", Name(eye)), ("part", Name(target)), ("slot", Loc.GetString(_prototypes.Index(slot).Name))),
                        Act = () => TryBeginModuleSwap(eye, user, body, target, slot, false) });
                }
            }
        }
    }

    private bool TryBeginModuleSwap(EntityUid eye, EntityUid user, EntityUid body, EntityUid part, ProtoId<NCCyberwarePartSlotPrototype> slot, bool remove)
    {
        if (!CanServiceEye(eye, user, body) || !TryComp<NCCyberwareAssemblyComponent>(eye, out var assembly) || !assembly.OptionalSlots.Contains(slot)) return false;
        return _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user, TimeSpan.FromSeconds(5),
            new NCOpticModuleDoAfterEvent { Eye = GetNetEntity(eye), Part = GetNetEntity(part), Slot = slot.Id, Revision = assembly.ConfigurationRevision, Remove = remove }, body, target: body)
        { NeedHand = true, BreakOnMove = true, BreakOnDamage = true });
    }

    private void OnModuleSwap(Entity<NCOpticWearerComponent> body, ref NCOpticModuleDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled) return;
        args.Handled = true;
        var eye = GetEntity(args.Eye); var part = GetEntity(args.Part);
        if (!CanServiceEye(eye, args.User, body) || !TryComp<NCCyberwareAssemblyComponent>(eye, out var assembly) ||
            assembly.ConfigurationRevision != args.Revision || assembly.ConfigurationRevision == uint.MaxValue || !assembly.OptionalSlots.Contains(args.Slot) ||
            !assembly.Parts.TryGetValue(args.Slot, out var container)) return;
        if (args.Remove)
        {
            if (container.ContainedEntity != part || !_containers.Remove(part, container, destination: Transform(body).Coordinates)) return;
            _hands.TryPickupAnyHand(args.User, part);
        }
        else
        {
            if (container.ContainedEntity != null || !_hands.IsHolding(args.User, part) || !HasComp<NCOpticModuleComponent>(part) ||
                !TryComp<NCCyberwarePartComponent>(part, out var hardware)) return;
            var option = Comp<NCOpticModuleComponent>(part);
            if (HasOption(assembly, option.Mode)) return;
            var profile = _prototypes.Index(hardware.Profile);
            if (profile.Slot.Id != args.Slot && !profile.CompatibleSlots.Contains(args.Slot)) return;
            _hands.TryDropIntoContainer(args.User, part, container);
        }
    }
    private bool HasOption(NCCyberwareAssemblyComponent assembly, NCOpticMode mode) =>
        assembly.Parts.Values.Any(socket => socket.ContainedEntity is { } existing &&
            TryComp<NCOpticModuleComponent>(existing, out var other) && other.Mode == mode);

}
