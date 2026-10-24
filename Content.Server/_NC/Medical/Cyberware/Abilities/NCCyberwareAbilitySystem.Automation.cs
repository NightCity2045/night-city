// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Medical.Cyberware.Abilities;

namespace Content.Server._NC.Medical.Cyberware.Abilities;

public sealed record NCCyberwareFunctionState(NCCyberwareAbilityKind Kind, bool Active, bool Operational, long CooldownMs, long ActiveRemainingMs);

public sealed partial class NCCyberwareAbilitySystem
{
    private readonly HashSet<EntityUid> _toggling = [];
    public bool TryToggle(EntityUid body, Entity<NCCyberwareAbilityComponent?> module, Func<bool>? guard = null)
    {
        if (_toggling.Count >= 64 || !_toggling.Add(module.Owner)) return false;
        try { return TryToggleCore(body, module, guard); }
        finally { _toggling.Remove(module.Owner); }
    }
    /// <summary>Physical controls notify automation before their original native toggle, without changing that toggle's meaning.</summary>
    public event Action<EntityUid>? ManualControlRequested;
    private void NotifyManualControl(EntityUid module) => ManualControlRequested?.Invoke(module);

    public bool TryGetFunctionState(EntityUid uid, out NCCyberwareFunctionState state)
    {
        state = default!;
        if (!TryComp<NCCyberwareAbilityComponent>(uid, out var module) || module.Body is not { } body) return false;
        var profile = GetProfile(module);
        var remaining = NCCyberwareAbilityMath.Remaining(module.CooldownRemainingSeconds, module.CooldownObservedAt, _timing.CurTime);
        if (!NCCyberwareAbilityMath.ValidCooldown(remaining)) return false;
        var active = module.ActiveUntil is { } until ? Math.Max(0, (until - _timing.CurTime).TotalMilliseconds) : 0;
        state = new(profile.Kind, module.Active, CanOperate(body, uid), (long) Math.Ceiling(remaining * 1000), (long) Math.Ceiling(active));
        return true;
    }

    /// <summary>Explicit desired state preserves timed duration, native startup energy, cooldown and hand constraints.</summary>
    public bool TrySetEnabled(EntityUid body, Entity<NCCyberwareAbilityComponent?> module, bool enabled, Func<bool>? guard = null)
    {
        if (!Resolve(module, ref module.Comp) || module.Comp.Body != body || guard != null && !guard() ||
            GetProfile(module.Comp).Kind == NCCyberwareAbilityKind.Biomonitor) return false;
        if (module.Comp.Active == enabled) return !enabled || CanOperate(body, module);
        return TryToggle(body, module, guard);
    }

    /// <summary>The biomonitor remains a native one-shot reading, with its normal physical power debit and wearer feedback.</summary>
    public bool TryPulse(EntityUid body, Entity<NCCyberwareAbilityComponent?> module, Func<bool>? guard = null)
    {
        return Resolve(module, ref module.Comp) && module.Comp.Body == body &&
            GetProfile(module.Comp).Kind == NCCyberwareAbilityKind.Biomonitor && (guard == null || guard()) && TryToggle(body, module, guard);
    }
}
