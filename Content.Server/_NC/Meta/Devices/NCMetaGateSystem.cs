// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Runtime;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.Devices;
using Content.Shared.Popups;
using Content.Shared.UserInterface;
using Robust.Shared.Timing;

namespace Content.Server._NC.Meta.Devices;

public enum NCMetaGateAnswerResult : byte { Applied, NotFound, Expired }

/// <summary>
/// Server authority for gated devices. A person's attempt to open the interface is cancelled in the ordinary activation
/// path and turned into a <c>useRequested</c> event. The program answers with <c>answer</c>; silence turns the person away.
/// With no listening program the device works as it always did.
/// </summary>
public sealed class NCMetaGateSystem : SharedNCMetaGateSystem, INCMetaDeviceAdapter
{
    private const string RequestedEvent = "meta.devices.Gate.useRequested";
    private const string AnswerMember = "meta.devices.Gate.answer";
    private const int MaxPending = 256;
    private const int MaxNameLength = 128;

    private sealed record Pending(string Id, EntityUid Device, EntityUid User, TimeSpan Deadline);

    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCEntityPartyResolverSystem _parties = default!;
    [Dependency] private ActivatableUISystem _activatable = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private IGameTiming _timing = default!;

    private readonly Dictionary<(EntityUid Device, EntityUid User), Pending> _byAttempt = [];
    private readonly Dictionary<string, Pending> _byId = new(StringComparer.Ordinal);
    private readonly PriorityQueue<string, TimeSpan> _deadlines = new();
    private (EntityUid Device, EntityUid User)? _authorized;

    public IEnumerable<string> Members => [AnswerMember];

    public bool CanBind(EntityUid device, string capability) =>
        capability == "meta.devices.Gate" && HasComp<NCMetaGateComponent>(device) && HasComp<NCMetaHostComponent>(device);

    public override void Initialize()
    {
        base.Initialize();
        _runtime.RegisterAdapter(this);
    }

    protected override bool TryHandleOpenAttempt(Entity<NCMetaGateComponent> device, EntityUid user)
    {
        if (_authorized == (device.Owner, user))
            return true;
        if (_byAttempt.ContainsKey((device.Owner, user)) || _byId.Count >= MaxPending)
            return false;

        var id = Guid.NewGuid().ToString("D");
        var accepted = _runtime.TryPublishEvent(device.Owner, RequestedEvent, process => process.CreateRecord(
            process.GetTypeId("meta.devices.UseRequest"),
            [process.CreateString(id), SubjectValue(process, user), _runtime.CreateStamp(process)]), user);
        if (accepted == 0)
            return true;

        var deadline = _timing.CurTime + TimeSpan.FromSeconds(Math.Clamp(device.Comp.ResponseSeconds, 0.1f, 30f));
        var pending = new Pending(id, device.Owner, user, deadline);
        _byAttempt.Add((device.Owner, user), pending);
        _byId.Add(id, pending);
        _deadlines.Enqueue(id, deadline);
        return false;
    }

    public NCMetaGateAnswerResult TryAnswer(EntityUid device, string requestId, bool allow)
    {
        if (!_byId.TryGetValue(requestId, out var pending) || pending.Device != device || !Exists(pending.User))
            return NCMetaGateAnswerResult.NotFound;
        if (pending.Deadline <= _timing.CurTime)
            return NCMetaGateAnswerResult.Expired;

        Remove(pending);
        if (!allow)
        {
            _popup.PopupEntity(Loc.GetString("nc-meta-gate-denied"), device, pending.User);
            return NCMetaGateAnswerResult.Applied;
        }

        // The person's attempt is repeated as it was, and this time the gate lets it through.
        var previous = _authorized;
        _authorized = (device, pending.User);
        try
        {
            if (TryComp<ActivatableUIComponent>(device, out var activatable))
                _activatable.InteractUI(pending.User, device, activatable);
        }
        finally { _authorized = previous; }
        return NCMetaGateAnswerResult.Applied;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        for (var work = 0; work < 32 && _deadlines.TryPeek(out var id, out var deadline) && deadline <= _timing.CurTime; work++)
        {
            _deadlines.Dequeue();
            if (!_byId.TryGetValue(id, out var pending) || pending.Deadline != deadline)
                continue;
            Remove(pending);
            if (Exists(pending.Device) && Exists(pending.User))
                _popup.PopupEntity(Loc.GetString("nc-meta-gate-silent"), pending.Device, pending.User);
        }
    }

    private void Remove(Pending pending)
    {
        _byAttempt.Remove((pending.Device, pending.User));
        _byId.Remove(pending.Id);
    }

    private MetaValue SubjectValue(MetaProcess process, EntityUid user)
    {
        var name = Name(user);
        if (name.Length > MaxNameLength)
            name = name[..MaxNameLength];
        return process.CreateRecord(process.GetTypeId("meta.access.Subject"),
            [process.CreateString(name), process.CreateString(_parties.TryResolve(user, out var party) ? party.ToString() : string.Empty)]);
    }

    public MetaValue Read(NCMetaDeviceInvocation invocation) => Error(invocation, "Unsupported");

    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        var process = invocation.Runtime.Process;
        var requestId = process.GetString(invocation.Request.Arguments[0]);
        var allow = invocation.Request.Arguments[1].AsBool();
        var result = TryAnswer(invocation.Device, requestId, allow);
        if (result != NCMetaGateAnswerResult.Applied)
            return MetaHostResult.Completed(Error(invocation, result == NCMetaGateAnswerResult.Expired ? "Timeout" : "NotFound"));
        return MetaHostResult.Completed(process.CreateResult(process.GetImport(invocation.Request.ImportId).ReturnType!.Value, MetaValue.Unit));
    }

    private static MetaValue Error(NCMetaDeviceInvocation invocation, string code) => invocation.Runtime.Process.CreateErrorResult(
        invocation.Runtime.Process.GetImport(invocation.Request.ImportId).ReturnType!.Value, code, code);
}
