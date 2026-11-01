// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.Medical.Cyberware.Abilities;
using Content.Shared.CCVar;
using Robust.Client.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Spawners;
using Robust.Shared.Timing;

namespace Content.Client._NC.Medical.Cyberware;

/// <summary>Short native sprite afterimages of visible moving bodies. Reduced motion changes only presentation.</summary>
public sealed partial class NCCyberwareMotionSystem : EntitySystem
{
    private static readonly EntProtoId ClonePrototype = "clientsideclone";
    [Dependency] private SpriteSystem _sprites = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private NCCyberwareAbilityModifiersSystem _modifiers = default!;
    private readonly HashSet<EntityUid> _active = [];
    private readonly Dictionary<EntityUid, MapCoordinates> _previous = [];
    private readonly Queue<(EntityUid Uid, TimeSpan Until)> _clones = [];
    private TimeSpan _nextFrame;

    public override void Initialize()
    {
        base.Initialize();
        _modifiers.BodyStateApplied += OnState;
        SubscribeLocalEvent<NCCyberwareAbilityBodyComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnState(EntityUid uid)
    {
        if (TryComp<NCCyberwareAbilityBodyComponent>(uid, out var body) && body.MotorBonus > 0f) _active.Add(uid);
        else { _active.Remove(uid); _previous.Remove(uid); }
    }

    private void OnShutdown(Entity<NCCyberwareAbilityBodyComponent> body, ref ComponentShutdown args)
    { _active.Remove(body); _previous.Remove(body); }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = _timing.CurTime;
        while (_clones.TryPeek(out var old) && old.Until <= now) _clones.Dequeue();
        if (_cfg.GetCVar(CCVars.ReducedMotion))
        {
            while (_clones.TryDequeue(out var clone)) if (Exists(clone.Uid)) QueueDel(clone.Uid);
            _previous.Clear();
            return;
        }
        if (_active.Count == 0 || now < _nextFrame) return;
        _nextFrame = now + TimeSpan.FromSeconds(0.1);
        foreach (var uid in _active.ToArray())
        {
            if (!TryComp<NCCyberwareAbilityBodyComponent>(uid, out var body) || body.MotorBonus <= 0f)
            { _active.Remove(uid); _previous.Remove(uid); continue; }
            if (!TryComp<SpriteComponent>(uid, out var sprite) || !sprite.Visible ||
                !TryComp<PhysicsComponent>(uid, out var physics) || physics.LinearVelocity.LengthSquared() <= 0.01f ||
                !TryComp(uid, out TransformComponent? xform) || xform.MapID == MapId.Nullspace ||
                _containers.IsEntityOrParentInContainer(uid))
            { _previous.Remove(uid); continue; }
            var position = _transform.GetMapCoordinates(uid);
            var hadPrevious = _previous.TryGetValue(uid, out var previous);
            _previous[uid] = position;
            if (!hadPrevious || previous.MapId != position.MapId ||
                !previous.InRange(position, 2f) || previous.InRange(position, 0.04f) || _clones.Count >= 64) continue;
            var clone = Spawn(ClonePrototype, previous);
            var cloneSprite = Comp<SpriteComponent>(clone);
            _sprites.CopySprite((uid, sprite), (clone, cloneSprite));
            _sprites.SetColor((clone, cloneSprite), sprite.Color * new Color(0.25f, 0.85f, 1f, 0.22f));
            _transform.SetWorldRotation(clone, _transform.GetWorldRotation(uid));
            EnsureComp<TimedDespawnComponent>(clone).Lifetime = 0.2f;
            _clones.Enqueue((clone, now + TimeSpan.FromSeconds(0.2)));
        }
    }

    public override void Shutdown()
    {
        _modifiers.BodyStateApplied -= OnState;
        while (_clones.TryDequeue(out var clone)) if (Exists(clone.Uid)) QueueDel(clone.Uid);
        _active.Clear();
        _previous.Clear();
        base.Shutdown();
    }
}
