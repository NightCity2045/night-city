// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Numerics;
using Content.Shared._NC.Medical.Cyberware.Neural;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Client._NC.Medical.Cyberware.Neural;

public sealed partial class NCNeuralVisualSystem : EntitySystem
{
    [Dependency] private IOverlayManager _overlays = default!;
    [Dependency] private IResourceCache _resources = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    public readonly Queue<Trail> Trails = new();
    public sealed record Trail(MapId Map, Vector2 Position, TimeSpan Expires, Color Color);
    public NCNeuralStatusEvent? Status;
    private TimeSpan _nextSample;
    private TimeSpan _statusUntil;
    private int _color;
    public TimeSpan Now => _timing.CurTime;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCNeuralStatusEvent>(OnStatus);
        _overlays.AddOverlay(new NCNeuralTrailOverlay(this, _resources));
        _overlays.AddOverlay(new NCNeuralHudOverlay(this, _resources));
    }
    public override void Shutdown()
    {
        _overlays.RemoveOverlay<NCNeuralTrailOverlay>();
        _overlays.RemoveOverlay<NCNeuralHudOverlay>();
        base.Shutdown();
    }
    private void OnStatus(NCNeuralStatusEvent message)
    {
        if (_players.LocalEntity is not { } body || message.Body != GetNetEntity(body)) return;
        Status = message;
        _statusUntil = Now + TimeSpan.FromSeconds(1.5);
    }
    public override void FrameUpdate(float frameTime)
    {
        if (Now >= _statusUntil || _players.LocalEntity is not { } owner || Status?.Body != GetNetEntity(owner)) Status = null;
        while (Trails.TryPeek(out var old) && old.Expires <= Now) Trails.Dequeue();
        if (Now < _nextSample) return;
        _nextSample = Now + TimeSpan.FromSeconds(.1);
        var query = EntityQueryEnumerator<NCNeuralVisualComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var visual, out var transform))
        {
            if (!visual.Burst || IsPaused(uid) || transform.MapID == MapId.Nullspace ||
                !TryComp<Robust.Client.GameObjects.SpriteComponent>(uid, out var sprite) || !sprite.Visible || sprite.Color.A <= .01f) continue;
            while (Trails.Count >= 128) Trails.Dequeue();
            Trails.Enqueue(new(transform.MapID, _transform.GetWorldPosition(uid), Now + TimeSpan.FromSeconds(.4),
                (++_color % 3) switch { 0 => Color.Cyan, 1 => Color.Lime, _ => Color.Magenta }));
        }
    }
}

public sealed class NCNeuralTrailOverlay : Overlay
{
    private readonly NCNeuralVisualSystem _system;
    private readonly Texture _texture;
    public override OverlaySpace Space => OverlaySpace.WorldSpaceBelowFOV;
    public NCNeuralTrailOverlay(NCNeuralVisualSystem system, IResourceCache resources)
    {
        _system = system;
        _texture = resources.GetResource<RSIResource>("/Textures/_NC/Objects/Cyberware/neural.rsi").RSI["trail"].GetFrame(Robust.Shared.Graphics.RSI.RsiDirection.South, 0);
    }
    protected override void Draw(in OverlayDrawArgs args)
    {
        foreach (var trail in _system.Trails)
        {
            if (trail.Map != args.MapId) continue;
            var alpha = Math.Clamp((float) (trail.Expires - _system.Now).TotalSeconds, 0, .4f);
            args.WorldHandle.DrawTextureRect(_texture, Box2.CenteredAround(trail.Position, new Vector2(.8f, 1)), trail.Color.WithAlpha(alpha));
        }
    }
}

public sealed class NCNeuralHudOverlay(NCNeuralVisualSystem system, IResourceCache resources) : Overlay
{
    private readonly Font _font = new VectorFont(resources.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 12);
    public override OverlaySpace Space => OverlaySpace.ScreenSpace;
    protected override void Draw(in OverlayDrawArgs args)
    {
        if (system.Status is not { } status) return;
        var text = Loc.GetString("nc-neural-hud", ("heat", MathF.Round(status.Heat * 100)), ("cooldown", status.CooldownSeconds),
            ("state", Loc.GetString(status.Active ? "nc-neural-active" : "nc-neural-idle")));
        args.ScreenHandle.DrawString(_font, new Vector2(24, 108), text, status.Heat >= .7f ? Color.OrangeRed : Color.Cyan);
    }
}
