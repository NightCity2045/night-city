// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Neural;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared.Verbs;

namespace Content.Server._NC.Medical.Cyberware.Neural;

public sealed partial class NCNeuralHardwareSystem
{
    private void OnWearerVerbs(Entity<NCNeuralWearerComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || args.User != ent.Owner) return;
        var user = args.User;
        foreach (var uid in _cyberware.GetInstalledModuleEntities(user, 32))
        {
            if (!TryComp<NCNeuralHardwareComponent>(uid, out var neural)) continue;
            var module = uid;
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-neural-status", ("name", Name(module))), Act = () => ShowStatus(module, user) });
            if (neural.Kind == NCNeuralKind.Kerenzikov)
                args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString(neural.Enabled ? "nc-neural-disable" : "nc-neural-enable", ("name", Name(module))),
                    Act = () =>
                    {
                        if (!_blocker.CanInteract(user, user) || !_cyberware.TryGetOwningBody(module, out var body) || body != user) return;
                        _abilities.NotifyManualControl(module);
                        TrySetEnabled(module, !neural.Enabled);
                    } });
        }
    }

    private void ShowStatus(EntityUid module, EntityUid user)
    {
        if (!_cyberware.TryGetOwningBody(module, out var body) || body != user || !TryComp<NCCyberwareComponent>(module, out var hardware)) return;
        var definition = _prototypes.Index(hardware.Profile);
        var heat = definition.HeatShutdownThreshold > 0 ? hardware.Heat / definition.HeatShutdownThreshold * 100 : 0;
        var cooldown = _abilities.TryGetFunctionState(module, out var state) ? Math.Ceiling(state.CooldownMs / 1000d) : 0;
        _popup.PopupEntity(Loc.GetString("nc-neural-reading", ("name", Name(module)), ("heat", MathF.Round(heat)), ("cooldown", cooldown)), user, user);
        if (Comp<NCNeuralHardwareComponent>(module).Kind == NCNeuralKind.ChipSocket)
            _popup.PopupEntity(Loc.GetString("nc-neural-socket-empty"), user, user);
    }
}
