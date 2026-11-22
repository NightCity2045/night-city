// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Runtime;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Meta.Devices;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Robust.Shared.Timing;

namespace Content.Server._NC.Meta.Devices;

public enum NCMetaDoorAnswerResult : byte { Applied, NotFound, Expired, Interlock }

/// <summary>
/// Server authority for META-controlled doors. A person's attempt is cancelled in the vanilla door pipeline and turned
/// into an <c>accessRequested</c> event. The program answers with <c>answerAccess</c>; silence is a denial.
/// With no listening program the door is open to everyone.
/// </summary>
public sealed class NCMetaDoorAccessSystem : SharedNCMetaDoorAccessSystem, INCMetaDeviceAdapter
{
    private const string RequestedEvent = "meta.doors.Door.accessRequested";
    private const string ResolvedEvent = "meta.doors.Door.accessResolved";
    private const string AnswerMember = "meta.doors.Door.answerAccess";
    private const int MaxPending = 256;
    private const int MaxJournal = 32;
    private const int MaxNameLength = 128;

    private sealed record Subject(string Name, string Key);
    private sealed record Pending(string Id, EntityUid Door, EntityUid User, Subject Subject, TimeSpan Deadline);

    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCEntityPartyResolverSystem _parties = default!;
    [Dependency] private SharedDoorSystem _doors = default!;
    [Dependency] private IGameTiming _timing = default!;

    private readonly Dictionary<(EntityUid Door, EntityUid User), Pending> _byAttempt = [];
    private readonly Dictionary<string, Pending> _byId = new(StringComparer.Ordinal);
    private readonly PriorityQueue<string, TimeSpan> _deadlines = new();
    private (EntityUid Door, EntityUid User)? _authorized;

    public IEnumerable<string> Members => [AnswerMember];

    public override void Initialize()
    {
        base.Initialize();
        _runtime.RegisterAdapter(this);
    }

    /// <summary>Opens a door on behalf of a person whose access was already decided by a program or a native fallback.</summary>
    public bool TryOpenAuthorized(EntityUid door, DoorComponent? component, EntityUid? user, bool quiet = true)
    {
        var previous = _authorized;
        _authorized = user is { } person ? (door, person) : null;
        try
        {
            var opened = _doors.TryOpen(door, component, user, quiet: quiet);
            // A program may open the door directly instead of answering; that settles its own pending request.
            if (opened && user is { } person2 && _byAttempt.TryGetValue((door, person2), out var pending))
            {
                Remove(pending);
                Resolve(pending, NCMetaDoorAccessOutcome.Allowed);
            }
            return opened;
        }
        finally { _authorized = previous; }
    }

    protected override bool TryHandleOpenAttempt(Entity<NCMetaDoorAccessComponent> door, EntityUid user)
    {
        if (_authorized == (door.Owner, user))
            return true;
        if (_byAttempt.ContainsKey((door.Owner, user)) || _byId.Count >= MaxPending)
            return false;

        var subject = CreateSubject(user);
        var id = Guid.NewGuid().ToString("D");
        var accepted = _runtime.TryPublishEvent(door.Owner, RequestedEvent, process => process.CreateRecord(
            process.GetTypeId("meta.doors.AccessRequest"),
            [process.CreateString(id), SubjectValue(process, subject), MetaValue.Bool(true), _runtime.CreateStamp(process)]), user);
        if (accepted == 0)
        {
            Record(door.Owner, subject, NCMetaDoorAccessOutcome.Unavailable);
            return true;
        }

        var deadline = _timing.CurTime + TimeSpan.FromSeconds(Math.Clamp(door.Comp.ResponseSeconds, 0.1f, 30f));
        var pending = new Pending(id, door.Owner, user, subject, deadline);
        _byAttempt.Add((door.Owner, user), pending);
        _byId.Add(id, pending);
        _deadlines.Enqueue(id, deadline);
        return false;
    }

    public NCMetaDoorAnswerResult TryAnswer(EntityUid door, string requestId, bool allow)
    {
        if (!_byId.TryGetValue(requestId, out var pending) || pending.Door != door || !Exists(pending.User))
            return NCMetaDoorAnswerResult.NotFound;
        if (pending.Deadline <= _timing.CurTime)
            return NCMetaDoorAnswerResult.Expired;

        Remove(pending);
        if (!allow)
        {
            Resolve(pending, NCMetaDoorAccessOutcome.Denied);
            _doors.Deny(door, user: pending.User);
            return NCMetaDoorAnswerResult.Applied;
        }

        Resolve(pending, NCMetaDoorAccessOutcome.Allowed);
        return TryOpenAuthorized(door, null, pending.User) ? NCMetaDoorAnswerResult.Applied : NCMetaDoorAnswerResult.Interlock;
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
            Resolve(pending, NCMetaDoorAccessOutcome.TimedOut);
            if (Exists(pending.Door))
                _doors.Deny(pending.Door, user: pending.User);
        }
    }

    private void Remove(Pending pending)
    {
        _byAttempt.Remove((pending.Door, pending.User));
        _byId.Remove(pending.Id);
    }

    private void Resolve(Pending pending, NCMetaDoorAccessOutcome outcome)
    {
        Record(pending.Door, pending.Subject, outcome);
        if (!Exists(pending.Door))
            return;
        _runtime.TryPublishEvent(pending.Door, ResolvedEvent, process => process.CreateRecord(
            process.GetTypeId("meta.doors.AccessResolution"),
            [process.CreateString(pending.Id), SubjectValue(process, pending.Subject),
                process.CreateEnum(process.GetTypeId("meta.doors.AccessOutcome"), (int) outcome), _runtime.CreateStamp(process)]));
    }

    private void Record(EntityUid door, Subject subject, NCMetaDoorAccessOutcome outcome)
    {
        if (!TryComp<NCMetaDoorAccessComponent>(door, out var component))
            return;
        component.Journal.Add(new NCMetaDoorAccessRecord(_timing.CurTime, subject.Name, outcome));
        if (component.Journal.Count > MaxJournal)
            component.Journal.RemoveAt(0);
    }

    private Subject CreateSubject(EntityUid user)
    {
        var name = Name(user);
        if (name.Length > MaxNameLength)
            name = name[..MaxNameLength];
        return new Subject(name, _parties.TryResolve(user, out var party) ? party.ToString() : string.Empty);
    }

    private static MetaValue SubjectValue(MetaProcess process, Subject subject) => process.CreateRecord(
        process.GetTypeId("meta.access.Subject"), [process.CreateString(subject.Name), process.CreateString(subject.Key)]);

    public MetaValue Read(NCMetaDeviceInvocation invocation) => Error(invocation, "Unsupported");

    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        var process = invocation.Runtime.Process;
        var requestId = process.GetString(invocation.Request.Arguments[0]);
        var allow = invocation.Request.Arguments[1].AsBool();
        var result = TryAnswer(invocation.Device, requestId, allow);
        if (result != NCMetaDoorAnswerResult.Applied)
            return MetaHostResult.Completed(Error(invocation, result switch
            {
                NCMetaDoorAnswerResult.Expired => "Timeout",
                NCMetaDoorAnswerResult.Interlock => "Interlock",
                _ => "NotFound",
            }));
        if (!TryComp<DoorComponent>(invocation.Device, out var door))
            return MetaHostResult.Completed(Error(invocation, "NotFound"));
        var phase = door.State switch
        {
            DoorState.Closed => 0, DoorState.Opening => 1, DoorState.Open => 2, DoorState.Closing => 3, _ => 4,
        };
        return MetaHostResult.Completed(process.CreateResult(process.GetImport(invocation.Request.ImportId).ReturnType!.Value,
            process.CreateRecord(process.GetTypeId("meta.doors.DoorState"),
                [MetaValue.Bool(door.State is DoorState.Open or DoorState.Opening),
                    process.CreateEnum(process.GetTypeId("meta.doors.DoorPhase"), phase), _runtime.CreateStamp(process)])));
    }

    private static MetaValue Error(NCMetaDeviceInvocation invocation, string code) => invocation.Runtime.Process.CreateErrorResult(
        invocation.Runtime.Process.GetImport(invocation.Request.ImportId).ReturnType!.Value, code);
}
