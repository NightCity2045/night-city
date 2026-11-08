// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Client.Viewport;
using Content.Shared._NC.Medical.Cyberware.Optics;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.State;
using Robust.Shared.Timing;
using Robust.Client.Utility;

namespace Content.Client._NC.Medical.Cyberware.Optics;

public sealed partial class NCCyberOpticsSystem : EntitySystem
{
    [Dependency] private Robust.Client.ResourceManagement.IResourceCache _resources = default!;
    [Dependency] private IOverlayManager _overlays = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IStateManager _states = default!;
    public NCOpticViewEvent? View { get; private set; }
    private TimeSpan _expires;
    private NCOpticAlbumWindow? _album;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCOpticViewEvent>(OnView);
        SubscribeNetworkEvent<NCOpticSnapshotEvent>(OnCapture);
        SubscribeNetworkEvent<NCOpticAlbumEvent>(OnAlbum);
        _overlays.AddOverlay(new NCOpticOverlay(this, EntityManager, _players));
        _overlays.AddOverlay(new NCOpticHudOverlay(this, _resources));
    }
    public override void Shutdown()
    {
        _overlays.RemoveOverlay<NCOpticOverlay>();
        _overlays.RemoveOverlay<NCOpticHudOverlay>();
        _album?.Dispose();
        base.Shutdown();
    }
    private void OnView(NCOpticViewEvent message)
    {
        if (_players.LocalEntity is not { } body || GetNetEntity(body) != message.Viewer || message.Map != Transform(body).MapID) return;
        View = message; _expires = _timing.CurTime + TimeSpan.FromSeconds(1.5);
    }
    public override void FrameUpdate(float frameTime)
    {
        if (_expires <= _timing.CurTime || _players.LocalEntity is not { } body || View?.Viewer != GetNetEntity(body) || View.Map != Transform(body).MapID)
            View = null;
    }
    private void OnCapture(NCOpticSnapshotEvent message)
    {
        if (_players.LocalEntity is not { } body || GetNetEntity(body) != message.Viewer || _states.CurrentState is not IMainViewportState game) return;
        game.Viewport.Viewport.Screenshot(source =>
        {
            using (source)
            {
                // Use the engine's sandbox-safe image access, preserving the viewport aspect ratio.
                var pixels = new byte[NCOpticMemoryComponent.Bytes];
                var scale = Math.Min((float) NCOpticMemoryComponent.Width / source.Width,
                    (float) NCOpticMemoryComponent.Height / source.Height);
                var width = Math.Max(1, (int) (source.Width * scale));
                var height = Math.Max(1, (int) (source.Height * scale));
                var offsetX = (NCOpticMemoryComponent.Width - width) / 2;
                var offsetY = (NCOpticMemoryComponent.Height - height) / 2;
                var sourcePixels = source.GetPixelSpan();
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var pixel = sourcePixels[(y * source.Height / height) * source.Width + x * source.Width / width];
                    var index = ((y + offsetY) * NCOpticMemoryComponent.Width + x + offsetX) * 3;
                    pixels[index] = pixel.R;
                    pixels[index + 1] = pixel.G;
                    pixels[index + 2] = pixel.B;
                }
                RaiseNetworkEvent(new NCOpticPhotoUploadEvent { Token = message.MemoryId, Pixels = pixels });
            }
        });
    }
    private void OnAlbum(NCOpticAlbumEvent message)
    {
        _album ??= new NCOpticAlbumWindow();
        _album.Requested = (index, delete) => RaiseNetworkEvent(new NCOpticAlbumRequestEvent { Module = message.Module, Index = index, Delete = delete });
        _album.UpdatePhoto(message);
        if (!_album.IsOpen) _album.OpenCentered();
    }
}
