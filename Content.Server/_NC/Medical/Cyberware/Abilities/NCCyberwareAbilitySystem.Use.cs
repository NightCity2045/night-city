// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Abilities;
using Content.Shared._NC.Medical.Function;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared.Body;
using Content.Shared.Hands;
using Content.Shared.Hands.Components;
using Content.Shared.Interaction.Components;
using Content.Shared.Popups;
using Content.Shared.Tools.Components;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.Containers;

namespace Content.Server._NC.Medical.Cyberware.Abilities;

public sealed partial class NCCyberwareAbilitySystem
{
    private void OnActivate(Entity<NCCyberwareAbilityComponent> module, ref NCActivateCyberwareAbilityEvent args)
    {
        if (!args.Handled)
        {
            NotifyManualControl(module.Owner);
            args.Handled = TryToggle(args.Performer, (module.Owner, module.Comp));
        }
    }

    private bool TryToggleCore(EntityUid body, Entity<NCCyberwareAbilityComponent?> module, Func<bool>? guard)
    {
        if (!Resolve(module, ref module.Comp) || module.Comp.Body != body || guard != null && !guard()) return false;
        if (module.Comp.Active)
        { Stop((module, module.Comp)); RefreshBody(body); return true; }
        if (!CanOperate(body, module)) return Fail(body, "nc-cyberware-ability-unavailable");
        var profile = GetProfile(module.Comp);
        var remaining = NCCyberwareAbilityMath.Remaining(module.Comp.CooldownRemainingSeconds, module.Comp.CooldownObservedAt, _timing.CurTime);
        if (!NCCyberwareAbilityMath.ValidCooldown(remaining)) return Fail(body, "nc-cyberware-ability-unavailable");
        if (remaining > 0f) return Fail(body, "nc-cyberware-ability-cooldown");
        if (profile.Kind is NCCyberwareAbilityKind.MotorBurst or NCCyberwareAbilityKind.Stabilization &&
            TryComp<NCCyberwareAbilityBodyComponent>(body, out var controls) &&
            controls.Modules.Any(uid => uid != module.Owner && TryComp<NCCyberwareAbilityComponent>(uid, out var other) &&
                other.Active && GetProfile(other).Kind is NCCyberwareAbilityKind.MotorBurst or NCCyberwareAbilityKind.Stabilization))
            return Fail(body, "nc-cyberware-ability-motor-busy");
        if (guard != null && !guard()) return false;
        if (profile.IntegralItem.HasValue && !TryDeploy(body, (module, module.Comp), profile))
            return Fail(body, "nc-cyberware-ability-hand-busy");
        if (guard != null && !guard()) { Retract((module, module.Comp)); return false; }
        if (!_cyberware.TryBeginAbilityLoad(module, profile.StartupEnergy))
        { Retract((module, module.Comp)); return Fail(body, "nc-cyberware-ability-power"); }
        _audio.PlayPvs(profile.ActivationSound, body);

        if (profile.Kind == NCCyberwareAbilityKind.Biomonitor)
        {
            _cyberware.TrySetLoad(module, 0f);
            if (!TryComp<NCPhysiologyComponent>(body, out var physiology) || !TryComp<NCCirculationComponent>(body, out var circulation))
                return false;
            _popup.PopupEntity(Loc.GetString("nc-cyberware-biomonitor-reading",
                ("oxygen", MathF.Round(physiology.Oxygenation * 100f)),
                ("perfusion", MathF.Round(circulation.Perfusion * 100f)),
                ("pain", MathF.Round(physiology.TotalPain)),
                ("condition", Loc.GetString(physiology.Oxygenation < 0.8f || circulation.Perfusion < 0.7f
                    ? "nc-cyberware-biomonitor-alarm" : "nc-cyberware-biomonitor-stable"))), body, body, PopupType.Large);
            return true;
        }
        module.Comp.Active = true;
        module.Comp.ActiveUntil = profile.Kind == NCCyberwareAbilityKind.MotorBurst
            ? _timing.CurTime + TimeSpan.FromSeconds(profile.DurationSeconds) : null;
        Comp<NCCyberwareComponent>(module).ActiveLoadUntil = module.Comp.ActiveUntil;
        module.Comp.CooldownRemainingSeconds = profile.Kind == NCCyberwareAbilityKind.MotorBurst
            ? profile.DurationSeconds + profile.CooldownSeconds : profile.CooldownSeconds;
        module.Comp.CooldownObservedAt = _timing.CurTime;
        module.Comp.NextStressAt = _timing.CurTime + TimeSpan.FromSeconds(1);
        _active.Add(module);
        _actions.SetToggled(module.Comp.Action, true);
        var message = profile.Kind == NCCyberwareAbilityKind.MotorBurst ? "nc-cyberware-ability-burst-visible" :
            profile.Kind == NCCyberwareAbilityKind.Blade ? "nc-cyberware-ability-blade-visible" : "nc-cyberware-ability-active-visible";
        _popup.PopupEntity(Loc.GetString(message, ("user", body)), body, PopupType.MediumCaution);
        _medical.TryMarkDirty(body, NCMedicalDirtyDomains.Persistence | NCMedicalDirtyDomains.Diagnostics);
        RefreshBody(body);
        return true;
    }

    private bool Fail(EntityUid body, string key)
    { _popup.PopupEntity(Loc.GetString(key), body, body); return false; }

    private void Stop(Entity<NCCyberwareAbilityComponent> module)
    {
        if (!module.Comp.Active) return;
        module.Comp.Active = false;
        if (module.Comp.Body is { } owner && !TerminatingOrDeleted(owner))
            _audio.PlayPvs(GetProfile(module.Comp).RetractionSound, owner);
        _active.Remove(module);
        // Settle the old timed demand before clearing its boundary.
        if (!_cyberware.TrySetLoad(module, 0f) && TryComp<NCCyberwareComponent>(module, out var detached))
            detached.Load = 0f;
        if (TryComp<NCCyberwareComponent>(module, out var hardware)) hardware.ActiveLoadUntil = null;
        module.Comp.ActiveUntil = null;
        Retract(module);
        _actions.SetToggled(module.Comp.Action, false);
        if (module.Comp.Body is { } body && !TerminatingOrDeleted(body))
            _medical.TryMarkDirty(body, NCMedicalDirtyDomains.Persistence | NCMedicalDirtyDomains.Diagnostics);
    }

    private bool TryFindHand(EntityUid body, EntityUid module, out string handId)
    {
        handId = string.Empty;
        if (!TryComp<ChildOrganComponent>(module, out var child) || child.Parent is not { } host ||
            !TryComp<OrganComponent>(host, out var organ) || organ.Category is not { } category ||
            !TryComp<HandsComponent>(body, out var hands) || !TryComp<NCBodyFunctionComponent>(body, out var functions)) return false;
        var location = category.Id switch { "ArmLeft" => HandLocation.Left, "ArmRight" => HandLocation.Right, _ => HandLocation.Middle };
        if (location == HandLocation.Middle || (location == HandLocation.Left ? functions.LeftGrip : functions.RightGrip) <= 0f)
            return false;
        foreach (var (id, hand) in hands.Hands)
            if (hand.Location == location) { handId = id; return true; }
        return false;
    }

    private bool TryDeploy(EntityUid body, Entity<NCCyberwareAbilityComponent> module, NCCyberwareAbilityPrototype profile)
    {
        if (!TryFindHand(body, module, out var hand) || _hands.GetHeldItem(body, hand) != null) return false;
        module.Comp.ItemContainer ??= _containers.EnsureContainer<ContainerSlot>(module, NCCyberwareAbilityComponent.ContainerId);
        if (module.Comp.IntegralItem == null)
        {
            var item = Spawn(profile.IntegralItem!.Value, Transform(module).Coordinates);
            module.Comp.IntegralItem = item;
            EnsureComp<NCIntegralCyberwareItemComponent>(item).Module = module;
            if (!_containers.Insert(item, module.Comp.ItemContainer)) { QueueDel(item); module.Comp.IntegralItem = null; return false; }
        }
        if (module.Comp.IntegralItem is not { } integral || TerminatingOrDeleted(integral) ||
            !_hands.TryPickup(body, integral, hand)) return false;
        EnsureComp<UnremoveableComponent>(integral);
        return true;
    }

    private void Retract(Entity<NCCyberwareAbilityComponent> module)
    {
        if (module.Comp.IntegralItem is not { } item || TerminatingOrDeleted(item)) return;
        RemComp<UnremoveableComponent>(item);
        module.Comp.ItemContainer ??= _containers.EnsureContainer<ContainerSlot>(module, NCCyberwareAbilityComponent.ContainerId);
        _containers.Insert(item, module.Comp.ItemContainer);
    }

    private bool CanUseIntegralItem(EntityUid item, NCIntegralCyberwareItemComponent bound, EntityUid user)
    {
        return bound.Module is { } uid && TryComp<NCCyberwareAbilityComponent>(uid, out var module) &&
               module.Body == user && module.Active && module.IntegralItem == item && CanOperate(user, uid) &&
               TryFindHand(user, uid, out var hand) && _hands.GetHeldItem(user, hand) == item;
    }
    private void OnMeleeAttempt(Entity<NCIntegralCyberwareItemComponent> item, ref AttemptMeleeEvent args)
    { if (!CanUseIntegralItem(item, item.Comp, args.User)) args.Cancelled = true; }
    private void OnToolAttempt(Entity<NCIntegralCyberwareItemComponent> item, ref ToolUseAttemptEvent args)
    { if (!CanUseIntegralItem(item, item.Comp, args.User)) args.Cancel(); }
}
