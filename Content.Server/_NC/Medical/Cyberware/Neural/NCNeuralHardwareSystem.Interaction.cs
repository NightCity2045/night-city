// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.Meta.Network;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Neural;
using Content.Shared._NC.Meta.Network;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Verbs;

namespace Content.Server._NC.Medical.Cyberware.Neural;

public sealed partial class NCNeuralHardwareSystem
{
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private NCMetaNetworkSystem _network = default!;

    public bool IsPortOnline(EntityUid board) => !HasComp<NCNeuralControllerComponent>(board) ||
        TryResolveController(board, out var module, out _) &&
        TryComp<NCNeuralHardwareComponent>(module, out var neural) &&
        neural.Kind == NCNeuralKind.Interface && neural.Enabled && IsReady(module);

    private void OnWearerVerbs(Entity<NCNeuralWearerComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || args.User != ent.Owner) return;
        var user = args.User;
        foreach (var uid in _cyberware.GetInstalledModuleEntities(user, 32))
        {
            if (!TryComp<NCNeuralHardwareComponent>(uid, out var neural)) continue;
            var module = uid;
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-neural-status", ("name", Name(module))), Act = () => ShowStatus(module, user) });
            if (neural.Kind is NCNeuralKind.Kerenzikov or NCNeuralKind.Interface)
                args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString(neural.Enabled ? "nc-neural-disable" : "nc-neural-enable", ("name", Name(module))),
                    Act = () =>
                    {
                        if (!_blocker.CanInteract(user, user) || !_cyberware.TryGetOwningBody(module, out var body) || body != user) return;
                        _abilities.NotifyManualControl(module);
                        TrySetEnabled(module, !neural.Enabled);
                    } });
            if (!TryGetController(module, out var board)) continue;
            if (_devices.CanClaim(board, user)) args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-neural-claim", ("module", Name(module))), Act = () => _devices.TryClaim(board, user) });
            AddControllerVerbs(module, board, user, args.Verbs);
            if (neural.Kind == NCNeuralKind.Interface)
                args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-neural-connect"), Act = () => TryConnect(user, module, board) });
            if (_devices.CanManage(board, user) && neural.Kind == NCNeuralKind.Sandevistan)
                args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-neural-program-request"), Act = () =>
                {
                    if (_blocker.CanInteract(user, user) && _cyberware.TryGetOwningBody(module, out var body) && body == user && _devices.CanManage(board, user))
                        _runtime.TryPublishEvent(board, "meta.implants.NeuralController.commanded", process =>
                            process.CreateRecord(process.GetTypeId("meta.implants.NeuralCommand"),
                                [Content.Meta.Runtime.MetaValue.Bool(true), _runtime.CreateStamp(process)]), user);
                } });
        }
    }

    private void TryConnect(EntityUid user, EntityUid module, EntityUid board)
    {
        if (!_blocker.CanInteract(user, user) || !_cyberware.TryGetOwningBody(module, out var body) || body != user ||
            !IsPortOnline(board) || !_devices.CanManage(board, user) ||
            _hands.GetActiveItem(user) is not { } cable || !TryComp<NCMetaNetworkCableComponent>(cable, out var physical)) return;
        var success = _network.TryConnectCable((cable, physical), user, board);
        _popup.PopupEntity(Loc.GetString(success ? "nc-meta-network-cable-connected" : "nc-meta-network-cable-rejected"), user, user);
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
