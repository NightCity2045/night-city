// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Numerics;
using Content.Shared._NC.Medical.Cyberware.Arms;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Client._NC.Medical.Cyberware.Arms;

public sealed partial class NCMonowireVisualSystem : EntitySystem
{
    [Dependency] private IOverlayManager _overlays = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    public readonly Queue<Arc> Arcs = new();
    public sealed record Arc(MapId Map, Vector2 Start, Vector2 End, TimeSpan Expires);
    public TimeSpan Now => _timing.CurTime;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCMonowireArcEvent>(OnArc);
        _overlays.AddOverlay(new NCMonowireOverlay(this));
    }
    public override void Shutdown()
    {
        _overlays.RemoveOverlay<NCMonowireOverlay>();
        base.Shutdown();
    }
    private void OnArc(NCMonowireArcEvent message)
    {
        if (!TryGetEntity(message.Source, out var source) || !TryComp(source, out TransformComponent? transform)) return;
        var target = GetCoordinates(message.Target);
        if (!target.IsValid(EntityManager)) return;
        var mapTarget = _transform.ToMapCoordinates(target);
        if (mapTarget.MapId != transform.MapID) return;
        while (Arcs.Count >= 64) Arcs.Dequeue();
        Arcs.Enqueue(new(transform.MapID, _transform.GetWorldPosition(source.Value), mapTarget.Position, Now + TimeSpan.FromSeconds(.25)));
    }
    public override void FrameUpdate(float frameTime)
    {
        while (Arcs.TryPeek(out var arc) && arc.Expires <= Now) Arcs.Dequeue();
    }
}

public sealed class NCMonowireOverlay(NCMonowireVisualSystem system) : Overlay
{
    public override OverlaySpace Space => OverlaySpace.WorldSpace;
    protected override void Draw(in OverlayDrawArgs args)
    {
        foreach (var arc in system.Arcs)
        {
            if (arc.Map != args.MapId) continue;
            var fade = Math.Clamp((float)(arc.Expires - system.Now).TotalSeconds / .25f, 0, 1);
            var middle = (arc.Start + arc.End) / 2 + new Vector2(0, .25f * fade);
            args.WorldHandle.DrawLine(arc.Start, middle, Color.Cyan.WithAlpha(fade));
            args.WorldHandle.DrawLine(middle, arc.End, Color.White.WithAlpha(fade));
        }
    }
}
