// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Medical.Cyberware.Arms;
using Robust.Client.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Utility;

namespace Content.Client._NC.Medical.Cyberware.Arms;

/// <summary>Directional sprite layers follow the server's actual deployment phase.</summary>
public sealed partial class NCArmVisualSystem : EntitySystem
{
    [Dependency] private SpriteSystem _sprites = default!;
    private static readonly ResPath Rsi = new("_NC/Mobs/Cyberware/arms.rsi");
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCArmVisualComponent, AfterAutoHandleStateEvent>(OnState);
        SubscribeLocalEvent<NCArmVisualComponent, ComponentShutdown>(OnShutdown);
    }
    private void OnState(Entity<NCArmVisualComponent> body, ref AfterAutoHandleStateEvent args)
    {
        if (!TryComp<SpriteComponent>(body, out var sprite)) return;
        Set(body, sprite, "nc-arm-left", body.Comp.Left, body.Comp.LeftPhase, "left");
        Set(body, sprite, "nc-arm-right", body.Comp.Right, body.Comp.RightPhase, "right");
    }
    private void Set(EntityUid uid, SpriteComponent sprite, string key, string? state, NCArmPhase phase, string side)
    {
        var layer = _sprites.LayerMapTryGet((uid, sprite), key, out var existing, false) ? existing : _sprites.LayerMapReserve((uid, sprite), key);
        var visible = state != null && phase != NCArmPhase.Folded;
        _sprites.LayerSetVisible((uid, sprite), layer, visible);
        if (visible) _sprites.LayerSetRsi((uid, sprite), layer, Rsi, state + "_" + side + "_" + phase.ToString().ToLowerInvariant());
    }
    private void OnShutdown(Entity<NCArmVisualComponent> body, ref ComponentShutdown args)
    {
        if (!TryComp<SpriteComponent>(body, out var sprite)) return;
        foreach (var key in new[] { "nc-arm-left", "nc-arm-right" })
            if (_sprites.LayerMapTryGet((body.Owner, sprite), key, out var layer, false)) _sprites.LayerSetVisible((body.Owner, sprite), layer, false);
    }
}
