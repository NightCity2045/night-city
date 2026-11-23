// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.Meta.Persistence;
using Robust.Shared.Asynchronous;
using Content.Shared._NC.Meta;
using Content.Shared.Interaction;
using Content.Shared.Power;
using Content.Shared.Power.EntitySystems;

namespace Content.Server._NC.Meta.Devices;

public sealed class NCMetaDeviceEntry(EntityUid uid, string id, string owner)
{
    public EntityUid Entity { get; } = uid;
    public string Id { get; } = id;
    public string Owner { get; internal set; } = owner;
    public ulong DeviceEpoch { get; internal set; } = (BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0) & (ulong) long.MaxValue) | 1UL;
    public ulong AuthorityEpoch { get; internal set; } = 1;
    public bool Available { get; internal set; } = true;
    public bool Ready { get; internal set; }
}

/// <summary>Runtime index only. Persistent IDs address devices; callers must separately authorize each use.</summary>
public sealed partial class NCMetaDeviceRegistrySystem : EntitySystem
{
    public Content.Shared._NC.Persistence.Identity.NCItemId? GetEconomicItemId(EntityUid uid) =>
        TryComp<NCCityPersistentItemAdapterComponent>(uid, out var item) && item.ItemId.IsValid ? item.ItemId : null;
    public event Action<NCMetaDeviceEntry>? Changed;
    [Dependency] private NCEntityPartyResolverSystem _parties = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedPowerReceiverSystem _power = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private ITaskManager _tasks = default!;
    private readonly Dictionary<string, NCMetaDeviceEntry> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<EntityUid, string> _entities = [];
    private readonly Dictionary<string, HashSet<string>> _owners = new(StringComparer.Ordinal);
    private readonly Dictionary<EntityUid, string> _reserved = [];
    private readonly HashSet<string> _reservedCodes = new(StringComparer.Ordinal);
    private readonly List<Func<EntityUid, bool?>> _powerPolicies = [];
    private Func<EntityUid, string, bool>? _managementAuthority;
    public void RegisterManagementAuthority(Func<EntityUid, string, bool> authority)
    {
        if (_managementAuthority != null) throw new InvalidOperationException("META management authority is already registered.");
        _managementAuthority = authority;
    }
    public void RegisterPowerPolicy(Func<EntityUid, bool?> policy)
    {
        if (_powerPolicies.Count >= 16) throw new InvalidOperationException("META native power policy limit.");
        _powerPolicies.Add(policy);
    }
    public bool IsPowered(EntityUid uid)
    {
        if (!Exists(uid) || !_power.IsPowered(uid)) return false;
        foreach (var policy in _powerPolicies) if (policy(uid) == false) return false;
        return true;
    }

    public override void Initialize()
    {
        base.Initialize();
        InitializeInteractions();
        SubscribeLocalEvent<NCMetaHostComponent, ComponentInit>(OnHostComponentInit);
        SubscribeLocalEvent<NCMetaDeviceComponent, ComponentInit>(OnDeviceComponentInit);
        SubscribeLocalEvent<NCMetaHostComponent, MapInitEvent>(OnHostInit);
        SubscribeLocalEvent<NCMetaDeviceComponent, MapInitEvent>(OnDeviceInit);
        SubscribeLocalEvent<NCMetaHostComponent, ComponentShutdown>(OnHostShutdown);
        SubscribeLocalEvent<NCMetaDeviceComponent, ComponentShutdown>(OnDeviceShutdown);
        SubscribeLocalEvent<NCMetaHostComponent, PowerChangedEvent>(OnHostPower);
        SubscribeLocalEvent<NCMetaDeviceComponent, PowerChangedEvent>(OnDevicePower);
    }

    // A device gets its code when it is created, not when the round starts, so the code is saved with a mapped entity.
    private void OnHostComponentInit(Entity<NCMetaHostComponent> ent, ref ComponentInit args) => Reserve(ent.Owner, ref ent.Comp.DeviceId);
    private void OnDeviceComponentInit(Entity<NCMetaDeviceComponent> ent, ref ComponentInit args) => Reserve(ent.Owner, ref ent.Comp.DeviceId);

    private void Reserve(EntityUid uid, ref string id)
    {
        if (id.Length != 0 || HasComp<NCCityPersistentItemAdapterComponent>(uid))
            return;
        if (!_reserved.TryGetValue(uid, out var code))
        {
            code = NewCode();
            _reserved.Add(uid, code);
            _reservedCodes.Add(code);
        }

        id = code;
    }

    private string NewCode()
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var code = NCMetaDeviceIds.Random();
            if (!_devices.ContainsKey(code) && !_reservedCodes.Contains(code))
                return code;
        }

        return Guid.NewGuid().ToString("D");
    }

    private void OnHostInit(Entity<NCMetaHostComponent> ent, ref MapInitEvent args) =>
        Register(ent.Owner, ref ent.Comp.DeviceId, ent.Comp.OwnerKey);
    private void OnDeviceInit(Entity<NCMetaDeviceComponent> ent, ref MapInitEvent args) =>
        Register(ent.Owner, ref ent.Comp.DeviceId, ent.Comp.OwnerKey);
    private void OnHostShutdown(Entity<NCMetaHostComponent> ent, ref ComponentShutdown args) => Remove(ent.Owner);
    private void OnDeviceShutdown(Entity<NCMetaDeviceComponent> ent, ref ComponentShutdown args) => Remove(ent.Owner);
    private void OnHostPower(Entity<NCMetaHostComponent> ent, ref PowerChangedEvent args) => PowerChanged(ent.Owner);
    private void OnDevicePower(Entity<NCMetaDeviceComponent> ent, ref PowerChangedEvent args) => PowerChanged(ent.Owner);

    private void Register(EntityUid uid, ref string id, string owner)
    {
        if (_entities.TryGetValue(uid, out var previous))
        {
            id = previous;
            return;
        }
        if (_reserved.Remove(uid, out var reserved))
            _reservedCodes.Remove(reserved);
        var itemBacked = TryComp<NCCityPersistentItemAdapterComponent>(uid, out var item) && item.ItemId.IsValid;
        if (id.Length == 0)
            id = itemBacked ? NCMetaDeviceIds.FromKey("meta-device:item:" + item!.ItemId) : NewCode();
        else if (!itemBacked && NCMetaDeviceIds.IsValid(id) && _devices.ContainsKey(id))
            id = NewCode(); // a copy of a mapped entity keeps the code of its original; the copy is a new device
        if (!NCMetaDeviceIds.IsValid(id) || _devices.ContainsKey(id))
        {
            Log.Error($"Rejected duplicate or invalid META device identity for {ToPrettyString(uid)}.");
            return;
        }
        var created = new NCMetaDeviceEntry(uid, id, owner);
        _devices.Add(id, created);
        _entities.Add(uid, id);
        if (owner.Length > 0) IndexOwner(owner, id);
        // Ordinary electronics have no durable record to restore: they are ready at once, which keeps a map full of them cheap.
        if (TryComp<NCMetaDeviceComponent>(uid, out var plain) && plain.Automatic)
        {
            created.Ready = true;
            return;
        }

        _ = RestoreOwnerAsync(uid, id);
    }

    private async Task RestoreOwnerAsync(EntityUid uid, string id)
    {
        try
        {
            var record = await _persistence.ReadDeviceAsync(id);
            _tasks.RunOnMainThread(() =>
            {
                if (!TryGetEntry(uid, out var entry) || entry.Id != id) return;
                entry.Ready = true;
                if (record == null) { Changed?.Invoke(entry); return; }
                if (entry.Owner.Length == 0) TryApplyOwner(uid, id, string.Empty, record.Owner, record.AuthorityEpoch);
                else if (entry.Owner != record.Owner)
                {
                    entry.Available = false;
                    Changed?.Invoke(entry);
                    Log.Error($"META device {id} has a conflicting durable owner; maintenance is required.");
                }
                else { entry.AuthorityEpoch = record.AuthorityEpoch; Changed?.Invoke(entry); }
            });
        }
        catch (Exception exception)
        {
            Log.Error($"Could not restore META device {id}: {exception}");
            _tasks.RunOnMainThread(() =>
            {
                if (!TryGetEntry(uid, out var entry) || entry.Id != id) return;
                entry.Available = false;
                Changed?.Invoke(entry);
            });
        }
    }

    private void Remove(EntityUid uid)
    {
        if (_reserved.Remove(uid, out var reserved))
            _reservedCodes.Remove(reserved);
        if (!_entities.Remove(uid, out var id) || !_devices.Remove(id, out var entry))
            return;
        entry.Available = false;
        if (_owners.TryGetValue(entry.Owner, out var owned) && owned.Remove(id) && owned.Count == 0) _owners.Remove(entry.Owner);
        entry.DeviceEpoch = entry.DeviceEpoch == ulong.MaxValue ? ulong.MaxValue : entry.DeviceEpoch + 1;
        Changed?.Invoke(entry);
    }

    private void PowerChanged(EntityUid uid)
    {
        if (_entities.TryGetValue(uid, out var id) && _devices.TryGetValue(id, out var entry))
        {
            entry.DeviceEpoch = entry.DeviceEpoch == ulong.MaxValue ? ulong.MaxValue : entry.DeviceEpoch + 1;
            Changed?.Invoke(entry);
        }
    }

    public bool TryInvalidate(EntityUid uid)
    {
        if (!TryGetEntry(uid, out var entry)) return false;
        entry.DeviceEpoch = entry.DeviceEpoch == ulong.MaxValue ? ulong.MaxValue : entry.DeviceEpoch + 1;
        Changed?.Invoke(entry);
        return true;
    }

    public bool TryResolve(string id, out NCMetaDeviceEntry entry) =>
        _devices.TryGetValue(id, out entry!) && entry.Available && Exists(entry.Entity);

    public bool TryGetEntry(EntityUid uid, out NCMetaDeviceEntry entry)
    {
        entry = default!;
        return _entities.TryGetValue(uid, out var id) && TryResolve(id, out entry);
    }

    public bool CanManage(EntityUid uid, EntityUid user, bool requireRange = true)
    {
        if (!TryGetEntry(uid, out var entry) || !entry.Ready || !_parties.TryResolve(user, out var party) ||
            entry.Owner != party.ToString() && _managementAuthority?.Invoke(user, entry.Owner) != true || !IsPowered(uid))
            return false;
        return !requireRange || _interaction.InRangeUnobstructed(user, uid, range: 2);
    }

    public bool CanClaim(EntityUid uid, EntityUid user, bool requireRange = true)
    {
        if (!CanClaimNative(uid, user) || !TryGetEntry(uid, out var entry) || !entry.Ready || entry.Owner.Length != 0 ||
            !_parties.TryResolve(user, out _) || !IsPowered(uid) ||
            requireRange && !_interaction.InRangeUnobstructed(user, uid, range: 2))
            return false;
        return TryComp<NCMetaHostComponent>(uid, out var host) && host.AllowClaim ||
               TryComp<NCMetaDeviceComponent>(uid, out var device) && device.AllowClaim;
    }

    /// <summary>Called only after a successful durable owner claim, on the server thread.</summary>
    public bool TryApplyOwner(EntityUid uid, string id, string expectedOwner, string newOwner, ulong durableAuthorityEpoch = 0)
    {
        if (!TryResolve(id, out var entry) || entry.Entity != uid || entry.Owner != expectedOwner ||
            !Content.Shared._NC.Persistence.Identity.NCPartyRef.TryParse(newOwner, out _))
            return false;
        if (entry.AuthorityEpoch == ulong.MaxValue || durableAuthorityEpoch == ulong.MaxValue ||
            durableAuthorityEpoch != 0 && durableAuthorityEpoch < 2) return false;
        if (_owners.TryGetValue(entry.Owner, out var previous) && previous.Remove(id) && previous.Count == 0) _owners.Remove(entry.Owner);
        entry.Owner = newOwner;
        IndexOwner(newOwner, id);
        entry.AuthorityEpoch = durableAuthorityEpoch == 0 ? entry.AuthorityEpoch + 1 : durableAuthorityEpoch;
        if (TryComp<NCMetaHostComponent>(uid, out var host))
            host.OwnerKey = newOwner;
        if (TryComp<NCMetaDeviceComponent>(uid, out var device))
            device.OwnerKey = newOwner;
        Changed?.Invoke(entry);
        return true;
    }

    public IEnumerable<NCMetaDeviceEntry> GetOwned(string owner) =>
        _owners.TryGetValue(owner, out var owned)
            ? owned.Order(StringComparer.Ordinal).Where(_devices.ContainsKey).Select(id => _devices[id])
                .Where(entry => entry.Available && entry.Ready && entry.Owner == owner)
            : [];
    public IEnumerable<NCMetaDeviceEntry> GetRegistered() => _devices.Values;
    private void IndexOwner(string owner, string id)
    {
        if (!_owners.TryGetValue(owner, out var owned)) _owners.Add(owner, owned = []);
        owned.Add(id);
    }
}
