// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Numerics;
using Content.Shared._NC.Medical.Humanity;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client._NC.Medical.Humanity;

public sealed partial class NCCyberEpisodeClientSystem : EntitySystem
{
    [Dependency] private Robust.Shared.Configuration.IConfigurationManager _config = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IOverlayManager _overlays = default!;
    [Dependency] private IResourceCache _resources = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    public NCCyberEpisodeViewEvent? View { get; private set; }
    public NCCyberSymptomPrototype? Symptom { get; private set; }
    public bool ReducedEffects => _config.GetCVar(NCCyberEpisodeCVars.ReducedEffects);
    public bool QuietSounds => _config.GetCVar(NCCyberEpisodeCVars.QuietSounds);
    public float SymptomRemaining => Math.Max(0f, (float) (_symptomUntil - _timing.CurTime).TotalSeconds);
    private TimeSpan _expires;
    private TimeSpan _symptomUntil;
    private EntityUid? _sound;
    private NCCyberEpisodeWindow? _window;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCCyberEpisodeViewEvent>(OnView);
        SubscribeNetworkEvent<NCCyberEpisodeOpenEvent>(_ => OpenWindow());
        _overlays.AddOverlay(new NCCyberEpisodeOverlay(this, _resources));
    }
    public override void Shutdown()
    {
        Clear();
        _window?.Dispose();
        _overlays.RemoveOverlay<NCCyberEpisodeOverlay>();
        base.Shutdown();
    }
    private void OnView(NCCyberEpisodeViewEvent message)
    {
        if (_players.LocalEntity is not { } body || GetNetEntity(body) != message.Body) return;
        var changed = View == null || View.Body != message.Body || View.Sequence != message.Sequence;
        View = message;
        _expires = _timing.CurTime + TimeSpan.FromSeconds(2.5);
        if (changed)
        {
            ClearDolls();
            _sound = _audio.Stop(_sound);
            Symptom = message.Symptom is { } id && _prototypes.TryIndex(id, out var definition) ? definition : null;
            _symptomUntil = _timing.CurTime + TimeSpan.FromSeconds(Symptom?.Duration ?? 0f);
            CreateDoll(body);
            if (!QuietSounds && Symptom?.Sound is { } sound)
            {
                // Synthetic local source: its position never comes from a hidden player.
                var offset = new Vector2(message.Sequence % 2 == 0 ? -2 : 2, 1);
                _sound = _audio.PlayStatic(sound, body, Transform(body).Coordinates.Offset(offset))?.Entity;
            }
        }
        _window?.SetView(message);
    }
    public override void FrameUpdate(float frameTime)
    {
        if (View != null && (_expires <= _timing.CurTime || _players.LocalEntity is not { } body || View.Body != GetNetEntity(body))) Clear();
        if (SymptomRemaining <= 0f) { Symptom = null; ClearDolls(); }
        UpdateDoll();
    }
    private void Clear()
    {
        ClearDolls();
        View = null;
        Symptom = null;
        _sound = _audio.Stop(_sound);
        _window?.Close();
    }
    private void OpenWindow()
    {
        if (View == null) return;
        _window ??= new NCCyberEpisodeWindow
        {
            Requested = message => RaiseNetworkEvent(message),
            ReduceChanged = value => _config.SetCVar(NCCyberEpisodeCVars.ReducedEffects, value),
            QuietChanged = value => { _config.SetCVar(NCCyberEpisodeCVars.QuietSounds, value); if (value) { _sound = _audio.Stop(_sound); _shotAudio = _audio.Stop(_shotAudio); } },
        };
        _window.SetPreferences(ReducedEffects, QuietSounds);
        _window.SetView(View);
        if (!_window.IsOpen) _window.OpenCentered();
    }
    public bool TryGetPerceivedPosition(out Vector2 position)
    {
        position = default;
        if (View?.PerceivedTarget is not { } target || !TryGetEntity(target, out var uid) ||
            !TryComp<TransformComponent>(uid, out var transform) || _players.LocalEntity is not { } body ||
            transform.MapID != Transform(body).MapID || !TryComp<Robust.Client.GameObjects.SpriteComponent>(uid, out var sprite) || !sprite.Visible)
            return false;
        position = _transform.GetWorldPosition(transform);
        return true;
    }
    public static string StageKey(NCCyberCrisisStage stage) => stage switch
    {
        NCCyberCrisisStage.Warning => "nc-psychosis-stage-warning",
        NCCyberCrisisStage.Crisis => "nc-psychosis-stage-crisis",
        NCCyberCrisisStage.Acute => "nc-psychosis-stage-acute",
        NCCyberCrisisStage.Recovery => "nc-psychosis-stage-recovery",
        _ => "nc-psychosis-stage-stable",
    };
}
