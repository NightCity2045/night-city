// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Numerics;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Enums;

namespace Content.Client._NC.Medical.Cyberware.Optics;

/// <summary>Anonymous native sensor returns, never sprites or entities outside the player's PVS.</summary>
public sealed class NCOpticOverlay(NCCyberOpticsSystem optics, IEntityManager entities, IPlayerManager players) : Overlay
{
    public override OverlaySpace Space => OverlaySpace.WorldSpace;
    protected override void Draw(in OverlayDrawArgs args)
    {
        if (optics.View is not { } view || view.Map != args.MapId ||
            !entities.TryGetComponent<EyeComponent>(players.LocalEntity, out var eye) || args.Viewport.Eye != eye.Eye) return;
        foreach (var contact in view.Contacts)
        {
            var color = (contact.Thermal ? Color.OrangeRed : Color.Cyan).WithAlpha(Math.Clamp(1 - contact.Age / 1.5f, .15f, .85f));
            var position = contact.Position;
            var handle = args.WorldHandle;
            handle.DrawCircle(position + new Vector2(0, .32f), .1f, color);
            handle.DrawLine(position + new Vector2(0, .2f), position + new Vector2(0, -.12f), color);
            handle.DrawLine(position + new Vector2(-.18f, .03f), position + new Vector2(.18f, .03f), color);
            handle.DrawLine(position + new Vector2(0, -.12f), position + new Vector2(-.12f, -.35f), color);
            handle.DrawLine(position + new Vector2(0, -.12f), position + new Vector2(.12f, -.35f), color);
        }
    }
}
