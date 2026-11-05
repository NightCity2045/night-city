// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Medical.Cyberware.Optics;
using Content.Shared.Verbs;
using Robust.Shared.Player;

namespace Content.Server._NC.Medical.Cyberware.Optics;

public sealed partial class NCCyberOpticsSystem
{
    [Dependency] private Content.Shared._NC.Medical.Anatomy.NCMedicalBodySystem _medicalState = default!;
    private sealed record Capture(EntityUid Eye, EntityUid Memory, EntityUid Body, Guid Token, TimeSpan Expires, float Quality);
    private readonly Dictionary<EntityUid, Capture> _captures = [];
    private readonly Dictionary<EntityUid, TimeSpan> _albumRates = [];

    private void InitializeRecording()
    {
        SubscribeNetworkEvent<NCOpticPhotoUploadEvent>(OnPhoto);
        SubscribeNetworkEvent<NCOpticAlbumRequestEvent>(OnAlbum);
        SubscribeLocalEvent<NCOpticMemoryComponent, GetVerbsEvent<AlternativeVerb>>(OnMemoryVerbs);
    }

    private bool TryCapture(EntityUid eye, EntityUid body)
    {
        if (!TryModule(eye, NCOpticMode.Recording, out var module) || !TryComp<NCOpticMemoryComponent>(module, out var memory) ||
            memory.NextCapture > _timing.CurTime || memory.Photos.Count >= NCOpticMemoryComponent.Capacity ||
            !TryComp<ActorComponent>(body, out var actor)) return false;
        foreach (var (owner, pending) in _captures.ToArray()) if (pending.Expires <= _timing.CurTime) _captures.Remove(owner);
        if (_captures.Count >= 64 || _captures.ContainsKey(body)) return false;
        memory.NextCapture = _timing.CurTime + TimeSpan.FromSeconds(5);
        var token = Guid.NewGuid();
        _captures[body] = new(eye, module, body, token, _timing.CurTime + TimeSpan.FromSeconds(3), FunctionQuality(eye, NCOpticMode.Recording));
        RaiseNetworkEvent(new NCOpticSnapshotEvent { Viewer = GetNetEntity(body), MemoryId = token }, actor.PlayerSession.Channel);
        return true;
    }

    private void OnPhoto(NCOpticPhotoUploadEvent message, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { } body || !_captures.TryGetValue(body, out var pending) || pending.Token != message.Token) return;
        _captures.Remove(body);
        // Fixed-size raw RGB: no compressed image parser or attacker-controlled allocation on the server.
        if (message.Pixels.Length != NCOpticMemoryComponent.Bytes || pending.Expires <= _timing.CurTime ||
            !IsReady(pending.Eye, out var patient) || patient != body ||
            !TryModule(pending.Eye, NCOpticMode.Recording, out var module) || module != pending.Memory ||
            !TryComp<NCOpticMemoryComponent>(module, out var memory) || memory.Photos.Count >= NCOpticMemoryComponent.Capacity) return;
        var pixels = message.Pixels.ToArray();
        if (pending.Quality < .85f)
        {
            for (var y = 0; y < NCOpticMemoryComponent.Height; y += 2)
            for (var x = 0; x < NCOpticMemoryComponent.Width; x += 2)
            for (var row = 0; row < 2; row++)
            for (var column = 0; column < 2; column++)
                Array.Copy(pixels, (y * NCOpticMemoryComponent.Width + x) * 3, pixels, ((y + row) * NCOpticMemoryComponent.Width + x + column) * 3, 3);
        }
        memory.Photos.Add(pixels);
        MarkMemoryChanged(module);
        SendAlbum(module, body, memory.Photos.Count - 1);
    }

    private bool CanReadMemory(EntityUid module, EntityUid user) => HasComp<NCOpticMemoryComponent>(module) &&
        _blocker.CanInteract(user, module) && (_containers.TryGetContainingContainer((module, null, null), out var container) &&
            HasComp<NCCyberEyeComponent>(container.Owner) && _cyberware.TryGetOwningBody(container.Owner, out var body) ? body == user :
            _interaction.InRangeUnobstructed(user, module, 1.5f));

    private void OnMemoryVerbs(Entity<NCOpticMemoryComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess || !CanReadMemory(ent, args.User)) return;
        var user = args.User;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-optics-album"), Act = () => SendAlbum(ent, user, 0) });
    }

    private void OnAlbum(NCOpticAlbumRequestEvent message, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { } user || !TryGetEntity(message.Module, out var module) || module is not { } uid || !CanReadMemory(uid, user)) return;
        if (_albumRates.TryGetValue(user, out var next) && next > _timing.CurTime) return;
        foreach (var pair in _albumRates.ToArray()) if (pair.Value <= _timing.CurTime) _albumRates.Remove(pair.Key);
        if (_albumRates.Count >= 128) return;
        _albumRates[user] = _timing.CurTime + TimeSpan.FromMilliseconds(250);
        var memory = Comp<NCOpticMemoryComponent>(uid);
        if (message.Delete && message.Index >= 0 && message.Index < memory.Photos.Count)
        { memory.Photos.RemoveAt(message.Index); MarkMemoryChanged(uid); }
        SendAlbum(uid, user, message.Index);
    }

    private void SendAlbum(EntityUid module, EntityUid user, int index)
    {
        if (!CanReadMemory(module, user) || !TryComp<ActorComponent>(user, out var actor)) return;
        var memory = Comp<NCOpticMemoryComponent>(module);
        index = Math.Clamp(index, 0, Math.Max(0, memory.Photos.Count - 1));
        RaiseNetworkEvent(new NCOpticAlbumEvent { Module = GetNetEntity(module), Index = index, Count = memory.Photos.Count,
            Pixels = memory.Photos.Count == 0 ? [] : memory.Photos[index] }, actor.PlayerSession.Channel);
    }
    private void MarkMemoryChanged(EntityUid module)
    {
        if (_containers.TryGetContainingContainer((module, null, null), out var container) &&
            _cyberware.TryGetOwningBody(container.Owner, out var body))
            _medicalState.TryMarkDirty(body, Content.Shared._NC.Medical.Anatomy.NCMedicalDirtyDomains.Persistence | Content.Shared._NC.Medical.Anatomy.NCMedicalDirtyDomains.Diagnostics);
    }

}
