// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Projects;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.Network;
using Content.Shared._NC.Meta.UI;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.UserInterface;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Meta.Network;

/// <summary>
/// Breaking into a closed chip that sits in the slot of the hacker's own laptop or deck. The engine only meters time:
/// a stronger chip resists longer, a faster tool and a running netrun helper program shorten it. There is no engine
/// ICE here; protection that fights back is whatever programs players write. The chip remembers which device tried.
/// </summary>
public sealed class NCMetaChipHackSystem : EntitySystem
{
    /// <summary>A program that asks for this scope is a netrun helper; one has to be running for a break-in to start.</summary>
    public const string HelperScope = "netrun.probe";
    private const int Stages = 3;
    private const int HelperBonus = 1;
    private static readonly TimeSpan BaseTime = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CrackedFor = TimeSpan.FromMinutes(10);

    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private ItemSlotsSystem _slots = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;

    private sealed class Attempt
    {
        public required EntityUid Terminal;
        public required EntityUid User;
        public required EntityUid Chip;
        public int Stage;
        public TimeSpan Due;
    }

    private readonly Dictionary<EntityUid, Attempt> _attempts = [];
    private readonly Dictionary<(EntityUid Chip, EntityUid User), TimeSpan> _cracked = [];

    public bool IsCracked(EntityUid chip, EntityUid user) => _cracked.TryGetValue((chip, user), out var until) && until > _timing.CurTime;

    /// <summary>Whether the device can try a break-in at all: a laptop or a deck with a tool profile.</summary>
    public bool CanAttempt(EntityUid terminal) => HasComp<NCMetaCyberdeckComponent>(terminal);

    /// <summary>Starts a break-in; the answer is the localization id of what the player sees.</summary>
    public string TryStart(EntityUid terminal, EntityUid user)
    {
        if (!_devices.CanManage(terminal, user) || !_devices.TryGetEntry(terminal, out var device) || !_devices.IsPowered(terminal)) return "nc-meta-error-access";
        if (!TryComp<NCMetaCyberdeckComponent>(terminal, out _)) return "nc-meta-hack-no-tool";
        if (!_slots.TryGetSlot(terminal, NCMetaChipSlot.Id, out var slot) || slot.Item is not { } chip ||
            !TryComp<NCMetaRemovableMediaComponent>(chip, out var media)) return "nc-meta-hack-no-chip";
        if (media.Open || _devices.CanManage(chip, user, requireRange: false) || IsCracked(chip, user)) return "nc-meta-hack-not-needed";
        if (!_runtime.HasRunningScope(terminal, device.Owner, HelperScope)) return "nc-meta-hack-no-helper";
        if (_attempts.ContainsKey(terminal)) return "nc-meta-hack-busy";
        media.Attempts++;
        media.LastIntruder = device.Id;
        var attempt = new Attempt { Terminal = terminal, User = user, Chip = chip };
        attempt.Due = _timing.CurTime + StageTime(terminal, media);
        _attempts[terminal] = attempt;
        return "nc-meta-hack-started";
    }

    /// <summary>The whole break-in takes base time times the chip's strength, divided by the tool speed; split in equal stages.</summary>
    private TimeSpan StageTime(EntityUid terminal, NCMetaRemovableMediaComponent media)
    {
        var speed = Math.Max(1, (ToolOf(terminal)?.Rating ?? 1) + HelperBonus);
        var total = BaseTime * Math.Max(1, media.Strength) / speed;
        return TimeSpan.FromMilliseconds(Math.Max(1000, total.TotalMilliseconds / Stages));
    }

    private NCMetaNetToolProfilePrototype? ToolOf(EntityUid terminal) =>
        TryComp<NCMetaCyberdeckComponent>(terminal, out var deck) && _prototypes.TryIndex(deck.ToolProfile, out var tool) ? tool : null;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_attempts.Count == 0) return;
        var now = _timing.CurTime;
        foreach (var attempt in _attempts.Values.Where(value => value.Due <= now).ToArray()) Advance(attempt, now);
    }

    private void Advance(Attempt attempt, TimeSpan now)
    {
        if (!StillValid(attempt, out var media))
        {
            Finish(attempt, "nc-meta-hack-aborted");
            return;
        }
        attempt.Stage++;
        if (attempt.Stage >= Stages)
        {
            _cracked[(attempt.Chip, attempt.User)] = now + CrackedFor;
            Finish(attempt, "nc-meta-hack-success");
            return;
        }
        attempt.Due = now + StageTime(attempt.Terminal, media);
        Notify(attempt, "nc-meta-hack-stage-ok", attempt.Stage, Stages);
    }

    private bool StillValid(Attempt attempt, out NCMetaRemovableMediaComponent media)
    {
        media = default!;
        if (!Exists(attempt.Terminal) || !Exists(attempt.User) || !Exists(attempt.Chip) || !_devices.IsPowered(attempt.Terminal) ||
            !_devices.CanManage(attempt.Terminal, attempt.User) || !_devices.TryGetEntry(attempt.Terminal, out var device) ||
            !_slots.TryGetSlot(attempt.Terminal, NCMetaChipSlot.Id, out var slot) || slot.Item != attempt.Chip ||
            !_runtime.HasRunningScope(attempt.Terminal, device.Owner, HelperScope) ||
            !TryComp<NCMetaRemovableMediaComponent>(attempt.Chip, out var found) || found.Open) return false;
        media = found;
        return true;
    }

    private void Finish(Attempt attempt, string key)
    {
        _attempts.Remove(attempt.Terminal);
        Notify(attempt, key);
    }

    private void Notify(Attempt attempt, string key, int stage = 0, int stages = 0)
    {
        if (!Exists(attempt.User) || !_ui.IsUiOpen(attempt.Terminal, NCMetaUiKey.Workspace, attempt.User)) return;
        var notice = Loc.GetString(key, ("stage", stage), ("stages", stages));
        _ui.ServerSendUiMessage(attempt.Terminal, NCMetaUiKey.Workspace,
            new NCMetaWorkspaceUpdateMessage(new NCMetaWorkspaceState { Partial = true, Notice = notice }), attempt.User);
    }
}
