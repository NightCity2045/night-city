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
    private const int Segments = 40;
    public override OverlaySpace Space => OverlaySpace.WorldSpace;
    protected override void Draw(in OverlayDrawArgs args)
    {
        foreach (var arc in system.Arcs)
        {
            if (arc.Map != args.MapId) continue;
            var fade = Math.Clamp((float)(arc.Expires - system.Now).TotalSeconds / .25f, 0, 1);
            var delta = arc.End - arc.Start;
            var length = delta.Length();
            if (length < 0.001f) continue;

            // A travelling ripple along a bowed filament, in the plane of the strike.
            // The envelope pins both ends and lets the wire straighten as it loses energy.
            var normal = new Vector2(-delta.Y, delta.X) / length;
            var age = 1f - fade;
            var bend = Math.Min(length * 0.24f, 0.65f) * fade;
            var color = new Color(0.7f, 0.95f, 1f, fade);
            var previous = arc.Start;
            for (var i = 1; i <= Segments; i++)
            {
                var t = i / (float) Segments;
                var envelope = MathF.Sin(MathF.PI * t);
                var ripple = MathF.Sin(MathF.Tau * (t - age * 1.4f));
                var offset = envelope * bend * (0.7f + 0.3f * ripple);
                var point = i == Segments ? arc.End : arc.Start + delta * t + normal * offset;
                args.WorldHandle.DrawLine(previous, point, color);
                previous = point;
            }
        }
    }
}
