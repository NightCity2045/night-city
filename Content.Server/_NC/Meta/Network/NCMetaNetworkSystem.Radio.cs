// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.Meta.Devices;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.Network;
using Content.Shared.PowerCell.Components;
using Content.Shared.Verbs;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Meta.Network;

public sealed partial class NCMetaNetworkSystem
{
    [Dependency] private IPrototypeManager _radioPrototypes = default!;
    private readonly HashSet<EntityUid> _interferenceSources = [];
    private long _nextRadioDraw;
    private void InitializeRadio()
    {
        SubscribeLocalEvent<NCMetaRadioInterferenceComponent, MapInitEvent>(OnInterferenceInit, after: [typeof(NCMetaDeviceRegistrySystem)]);
        SubscribeLocalEvent<NCMetaRadioInterferenceComponent, ComponentShutdown>(OnInterferenceShutdown);
        SubscribeLocalEvent<NCMetaRadioInterferenceComponent, GetVerbsEvent<AlternativeVerb>>(OnInterferenceVerbs);
    }
    private void OnInterferenceInit(Entity<NCMetaRadioInterferenceComponent> ent, ref MapInitEvent args)
    { if (_interferenceSources.Count < 64) _interferenceSources.Add(ent.Owner); RoutingChanged?.Invoke(); }
    private void OnInterferenceShutdown(Entity<NCMetaRadioInterferenceComponent> ent, ref ComponentShutdown args)
    { _interferenceSources.Remove(ent.Owner); RoutingChanged?.Invoke(); }
    private void OnInterferenceVerbs(Entity<NCMetaRadioInterferenceComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !_devices.CanManage(ent.Owner, args.User)) return;
        var actor = args.User;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-meta-network-toggle-interference"), Act = () => TryToggleInterference(ent.Owner, actor) });
    }
    public bool TryToggleInterference(EntityUid device, EntityUid actor)
    {
        if (!_devices.CanManage(device, actor) || !TryComp<NCMetaRadioInterferenceComponent>(device, out var source)) return false;
        source.Enabled = !source.Enabled; _devices.TryInvalidate(device); return true;
    }
    private bool CanRadio(EntityUid from, EntityUid to, NCMetaPortComponent source, NCMetaPortComponent target)
    {
        if (!source.Wireless || !target.Wireless || !_radioPrototypes.TryIndex(source.RadioProfile, out var first) || !_radioPrototypes.TryIndex(target.RadioProfile, out var second) || first.Channel != second.Channel) return false;
        var range = Math.Min(Math.Min(source.Range, target.Range), Math.Min(first.MaximumRange, second.MaximumRange));
        if (!float.IsFinite(range) || range <= 0 || !_interaction.InRangeUnobstructed(from, to, range: Math.Min(range, 32))) return false;
        var interference = 0;
        foreach (var uid in _interferenceSources)
            if (TryComp<NCMetaRadioInterferenceComponent>(uid, out var emitter) && emitter.Enabled && emitter.Channel == first.Channel && float.IsFinite(emitter.Radius) && Online(uid) &&
                (_interaction.InRangeUnobstructed(uid, from, range: Math.Clamp(emitter.Radius, 0, 16)) || _interaction.InRangeUnobstructed(uid, to, range: Math.Clamp(emitter.Radius, 0, 16))))
            {
                interference += Math.Clamp(emitter.Strength, 1, 100);
                if (interference > Math.Clamp(Math.Min(first.MaximumInterference, second.MaximumInterference), 0, 100)) return false;
            }
        return true;
    }
    private void UpdateRadio()
    {
        if (_now < _nextRadioDraw) return;
        _nextRadioDraw = _now + 1000;
        foreach (var uid in _interferenceSources)
            if (TryComp<NCMetaRadioInterferenceComponent>(uid, out var emitter) && emitter.Enabled && HasComp<PowerCellSlotComponent>(uid) && Online(uid) && !_cells.TryUseCharge(uid, 5))
            { emitter.Enabled = false; _devices.TryInvalidate(uid); }
    }
}
