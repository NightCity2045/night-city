// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.Devices;
using Content.Shared.Verbs;
using Content.Shared.Examine;
using Content.Server.Popups;

namespace Content.Server._NC.Meta.Devices;

public sealed partial class NCMetaDeviceRegistrySystem
{
    [Dependency] private PopupSystem _popup = default!;
    private readonly HashSet<EntityUid> _claiming = [];

    private void InitializeInteractions()
    {
        SubscribeLocalEvent<NCMetaDeviceComponent, GetVerbsEvent<AlternativeVerb>>(OnDeviceVerbs);
        SubscribeLocalEvent<NCMetaHostComponent, GetVerbsEvent<AlternativeVerb>>(OnHostVerbs);
        SubscribeLocalEvent<NCMetaDeviceComponent, ExaminedEvent>(OnDeviceExamined);
        SubscribeLocalEvent<NCMetaHostComponent, ExaminedEvent>(OnHostExamined);
    }
    private void OnDeviceVerbs(Entity<NCMetaDeviceComponent> ent, ref GetVerbsEvent<AlternativeVerb> args) => AddClaimVerb(ent.Owner, ref args);
    private void OnHostVerbs(Entity<NCMetaHostComponent> ent, ref GetVerbsEvent<AlternativeVerb> args) => AddClaimVerb(ent.Owner, ref args);
    private void AddClaimVerb(EntityUid uid, ref GetVerbsEvent<AlternativeVerb> args)
    {
        // Terminals, servers and doors are claimed by use or by being cabled to something the user owns, not by a verb.
        if (!args.CanAccess || !args.CanInteract || IsNetworkDevice(uid) || !CanClaim(uid, args.User)) return;
        var user = args.User;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-meta-claim-device"), Act = () => TryClaim(uid, user) });
    }
    /// <summary>Devices that join the META network: they are claimed by use or by a cable, never by a menu verb.</summary>
    public bool IsNetworkDevice(EntityUid uid) => HasComp<NCMetaTerminalComponent>(uid) || HasComp<NCMetaDoorAccessComponent>(uid);

    public bool TryClaim(EntityUid uid, EntityUid user, Action<bool>? done = null, bool requireRange = true)
    {
        if (!CanClaim(uid, user, requireRange) || !TryGetEntry(uid, out var device) || !_parties.TryResolve(user, out var owner) ||
            !_persistence.TryCaptureTime(out var epoch, out var time) || _claiming.Count >= 32 || !_claiming.Add(uid)) return false;
        _ = ClaimAsync(uid, user, device.Id, owner.ToString(), epoch, time, GetEconomicItemId(uid), done, requireRange);
        return true;
    }
    private async Task ClaimAsync(EntityUid uid, EntityUid user, string id, string owner,
        Content.Shared._NC.Persistence.Time.NCSessionEpoch epoch, Content.Shared._NC.Persistence.Time.NCActiveWorldTime time,
        Content.Shared._NC.Persistence.Identity.NCItemId? itemId, Action<bool>? done, bool requireRange)
    {
        var success = false;
        try
        {
            success = await _persistence.ClaimDeviceAsync(Guid.NewGuid(), id, owner, Loc.GetString("nc-meta-default-project"), epoch, time, itemId) != null;
        }
        catch (Exception exception) { Log.Error($"META device claim failed: {exception}"); }
        _tasks.RunOnMainThread(() =>
        {
            _claiming.Remove(uid);
            if (!Exists(uid) || !Exists(user) || !TryGetEntry(uid, out var current) || current.Id != id) { done?.Invoke(false); return; }
            var applied = success && CanClaimNative(uid, user) &&
                (current.Owner == owner || CanClaim(uid, user, requireRange) && TryApplyOwner(uid, id, string.Empty, owner));
            _popup.PopupEntity(Loc.GetString(applied ? "nc-meta-device-claimed" : "nc-meta-error-access"), uid, user);
            done?.Invoke(applied);
        });
    }
    private void OnDeviceExamined(Entity<NCMetaDeviceComponent> ent, ref ExaminedEvent args) => ExamineIdentity(ent.Owner, ref args);
    private void OnHostExamined(Entity<NCMetaHostComponent> ent, ref ExaminedEvent args) => ExamineIdentity(ent.Owner, ref args);
    private void ExamineIdentity(EntityUid uid, ref ExaminedEvent args)
    {
        var user = args.Examiner;
        if (!CanManage(uid, user) || !TryGetEntry(uid, out var entry)) return;
        args.PushMarkup(Loc.GetString("nc-meta-device-id", ("id", NCMetaDeviceIds.Format(entry.Id))));
    }
}
