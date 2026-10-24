// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Abilities;

namespace Content.Server._NC.Medical.Cyberware;

public sealed partial class NCCyberwareSystem
{
    public bool TryGetOwningBody(EntityUid module, out EntityUid body) => TryResolveOwningBody(module, out body);

    /// <summary>Settle old demand, admit active demand and debit the actual main battery once. Enhancements never use life-support reserve.</summary>
    public bool TryBeginAbilityLoad(EntityUid uid, float startupEnergy)
    {
        if (!float.IsFinite(startupEnergy) || startupEnergy < 0f ||
            !TryComp<NCCyberwareComponent>(uid, out var module) || !module.Operational || module.CrisisBlocksActiveModes ||
            !TryResolveOwningBody(uid, out var body)) return false;
        TryAdvanceBodyPower(body, _timing.CurTime);
        var previous = module.Load;
        module.Load = 1f;
        RefreshBodyPower(body, _timing.CurTime);
        if (!module.Operational || FindPowerBus(CollectCyberware(body), _timing.CurTime) is not { } holder ||
            GetBusCell(holder, NCCyberwarePowerBusComponent.MainSlot) is not { } cell ||
            _battery.GetCharge(cell.AsNullable()) <= startupEnergy ||
            !_battery.TryUseCharge(cell.AsNullable(), startupEnergy))
        {
            module.Load = previous;
            RefreshBodyPower(body, _timing.CurTime);
            return false;
        }
        MarkBodyChanged(body);
        return true;
    }

    public void PrepareLegacyAbilityCooldowns(EntityUid body, Content.Shared._NC.Medical.Persistence.NCMedicalStateSnapshot snapshot)
    {
        foreach (var module in snapshot.CyberwareModules)
            Prepare(module);
        foreach (var organ in snapshot.Organs.Values)
            if (organ.Cyberware is { } module) Prepare(module);
        return;

        void Prepare(NCCyberwareStateSnapshot module)
        {
            if (module.AbilityCooldownSeconds == null &&
                _prototypes.TryIndex<Robust.Shared.Prototypes.EntityPrototype>(module.EntityPrototype, out var prototype) &&
                prototype.TryComp<NCCyberwareAbilityComponent>(out _, _componentFactory))
            {
                module.AbilityCooldownSeconds = 0f;
                if (TryGetInstalledModuleEntity(body, module.StableInstanceId, out var live) &&
                    TryComp<NCCyberwareAbilityComponent>(live, out var state))
                    module.AbilityCooldownSeconds = NCCyberwareAbilityMath.Remaining(
                        state.CooldownRemainingSeconds, state.CooldownObservedAt, _timing.CurTime);
            }
        }
    }
}
