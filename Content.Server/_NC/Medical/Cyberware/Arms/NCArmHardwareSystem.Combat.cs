// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Abilities;
using Content.Shared._NC.Medical.Cyberware.Arms;
using Content.Shared.Body;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.Containers;
using Content.Shared.Weapons.Melee.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Player;

namespace Content.Server._NC.Medical.Cyberware.Arms;

public sealed partial class NCArmHardwareSystem
{
    private void InitializeCombat()
    {
        SubscribeLocalEvent<Abilities.NCIntegralCyberwareItemComponent, AttemptShootEvent>(OnShootAttempt);
        SubscribeLocalEvent<Abilities.NCIntegralCyberwareItemComponent, GunShotEvent>(OnShot);
        SubscribeLocalEvent<Abilities.NCIntegralCyberwareItemComponent, MeleeHitEvent>(OnMeleeHit);
        SubscribeLocalEvent<Abilities.NCIntegralCyberwareItemComponent, EntInsertedIntoContainerMessage>(OnReload);
    }

    public bool IsDeployedReady(EntityUid module) => !TryComp<NCArmModuleComponent>(module, out var arm) || _timing.CurTime >= arm.ReadyAt;

    public void SetDeployed(EntityUid body, EntityUid module, bool deployed)
    {
        if (!TryComp<NCArmModuleComponent>(module, out var hardware)) return;
        hardware.ReadyAt = _timing.CurTime + TimeSpan.FromSeconds(hardware.DeploySeconds);
        SetVisual(body, module, deployed ? NCArmPhase.Deploy : NCArmPhase.Retract, hardware.DeploySeconds);
    }

    private void OnShootAttempt(Entity<Abilities.NCIntegralCyberwareItemComponent> item, ref AttemptShootEvent args)
    {
        if (item.Comp.Module is not { } module || !_abilities.CanUseIntegral(item, args.User) || !IsDeployedReady(module)) args.Cancelled = true;
    }

    private void OnShot(Entity<Abilities.NCIntegralCyberwareItemComponent> item, ref GunShotEvent args)
    {
        if (item.Comp.Module is not { } module) return;
        SetVisual(args.User, module, NCArmPhase.Fire, .25f);
        _medical.TryMarkDirty(args.User, NCMedicalDirtyDomains.Persistence);
        // Ordinary gun systems own ammunition, recoil, projectile travel and impacts.
    }

    private void OnReload(Entity<Abilities.NCIntegralCyberwareItemComponent> item, ref EntInsertedIntoContainerMessage args)
    {
        if (args.Container.ID is not ("gun_magazine" or "ballistic-ammo") || item.Comp.Module is not { } module || !TryComp<NCCyberwareAbilityComponent>(module, out var ability) ||
            !ability.Active || ability.Body is not { } body || !TryComp<NCArmModuleComponent>(module, out var arm) ||
            arm.Kind is not (NCArmModuleKind.Pistol or NCArmModuleKind.Launcher)) return;
        arm.ReadyAt = _timing.CurTime + TimeSpan.FromSeconds(.6);
        SetVisual(body, module, NCArmPhase.Reload, .6f);
        _medical.TryMarkDirty(body, NCMedicalDirtyDomains.Persistence);
    }

    private NCArmPhase IdlePhase(EntityUid body, string? visualState, string side)
    {
        if (TryComp<NCCyberwareAbilityBodyComponent>(body, out var controls))
            foreach (var uid in controls.Modules)
                if (TryComp<NCArmModuleComponent>(uid, out var arm) && arm.VisualState == visualState &&
                    TryComp<ChildOrganComponent>(uid, out var child) && child.Parent is { } host &&
                    TryComp<OrganComponent>(host, out var organ) && organ.Category?.Id == side &&
                    TryComp<NCCyberwareAbilityComponent>(uid, out var ability) && ability.Active &&
                    ability.IntegralItem is { } item && arm.Kind is NCArmModuleKind.Pistol or NCArmModuleKind.Launcher)
                {
                    var count = new GetAmmoCountEvent();
                    RaiseLocalEvent(item, ref count);
                    return count.Count == 0 ? NCArmPhase.Empty : NCArmPhase.Ready;
                }
        return NCArmPhase.Ready;
    }

    private void OnMeleeHit(Entity<Abilities.NCIntegralCyberwareItemComponent> item, ref MeleeHitEvent args)
    {
        if (!args.IsHit || item.Comp.Module is not { } module || !TryComp<NCArmModuleComponent>(module, out var arm)) return;
        SetVisual(args.User, module, NCArmPhase.Fire, .25f);
        if (arm.Kind == NCArmModuleKind.Impact && TryComp<ChildOrganComponent>(module, out var child) && child.Parent is { } host)
            args.BonusDamage += args.BaseDamage * (_cyberware.ArmStrength(host) - 1);
        if (arm.Kind != NCArmModuleKind.Monowire) return;
        var target = args.HitEntities.FirstOrDefault();
        if (target == default || !TryComp(target, out TransformComponent? transform)) return;
        RaiseNetworkEvent(new NCMonowireArcEvent(GetNetEntity(args.User), GetNetCoordinates(transform.Coordinates)), Filter.Pvs(args.User));
    }

    private void SetVisual(EntityUid body, EntityUid module, NCArmPhase phase, float seconds)
    {
        if (!TryComp<NCArmModuleComponent>(module, out var arm) || !TryComp<ChildOrganComponent>(module, out var child) ||
            child.Parent is not { } host || !TryComp<OrganComponent>(host, out var organ)) return;
        var visual = EnsureComp<NCArmVisualComponent>(body);
        if (organ.Category == "ArmLeft") { visual.Left = arm.VisualState; visual.LeftPhase = phase; visual.LeftUntil = _timing.CurTime + TimeSpan.FromSeconds(seconds); }
        else if (organ.Category == "ArmRight") { visual.Right = arm.VisualState; visual.RightPhase = phase; visual.RightUntil = _timing.CurTime + TimeSpan.FromSeconds(seconds); }
        Dirty(body, visual);
    }

    private void UpdateVisuals()
    {
        var query = EntityQueryEnumerator<NCArmVisualComponent>();
        while (query.MoveNext(out var body, out var visual))
        {
            if (visual.LeftUntil != TimeSpan.Zero && _timing.CurTime >= visual.LeftUntil)
            {
                visual.LeftPhase = visual.LeftPhase == NCArmPhase.Retract ? NCArmPhase.Folded : IdlePhase(body, visual.Left, "ArmLeft");
                visual.LeftUntil = TimeSpan.Zero; Dirty(body, visual);
            }
            if (visual.RightUntil != TimeSpan.Zero && _timing.CurTime >= visual.RightUntil)
            {
                visual.RightPhase = visual.RightPhase == NCArmPhase.Retract ? NCArmPhase.Folded : IdlePhase(body, visual.Right, "ArmRight");
                visual.RightUntil = TimeSpan.Zero; Dirty(body, visual);
            }
        }
    }
}
