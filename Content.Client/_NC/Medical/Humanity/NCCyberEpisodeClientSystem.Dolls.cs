// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using System.Numerics;
using Content.Shared.Hands.Components;
using Content.Shared.Interaction;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Client._NC.Medical.Humanity;

public sealed partial class NCCyberEpisodeClientSystem
{
    [Dependency] private Content.Shared.Hands.EntitySystems.SharedHandsSystem _hands = default!;
    [Dependency] private Content.Shared.Tag.TagSystem _tags = default!;
    [Dependency] private IEyeManager _eyes = default!;
    [Dependency] private SpriteSystem _sprites = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private IRobustRandom _random = default!;
    private EntityUid? _doll;
    private EntityUid? _weaponWearer;
    private EntityUid? _shotAudio;
    private TimeSpan _nextShot;
    private TimeSpan _nextDollVisibility;
    private int _shots;

    private bool CanCopyBody(EntityUid body, EntityUid observer) =>
        TryComp<SpriteComponent>(body, out var sprite) && sprite.Visible &&
        TryComp<TransformComponent>(body, out var transform) && transform.MapID == Transform(observer).MapID &&
        _interaction.InRangeUnobstructed(observer, body, 7f);

    private void CreateDoll(EntityUid observer)
    {
        if (ReducedEffects || Symptom is not { } symptom || (!symptom.Phantom && !symptom.BodyEcho)) return;
        EntityUid model = observer;
        var coordinates = Transform(observer).Coordinates;
        var weaponOnly = false;
        if (symptom.Threat && View?.PerceivedTarget is { } net && TryGetEntity(net, out var target) &&
            target is { } wearer && CanCopyBody(wearer, observer) && HasFreeRightHand(wearer))
        {
            model = wearer;
            coordinates = new EntityCoordinates(wearer, Vector2.Zero);
            _weaponWearer = wearer;
            weaponOnly = true;
        }
        else if (symptom.Phantom)
        {
            // Only already received, visible player appearances. Never request hidden players or their positions.
            var candidates = _players.Sessions.Select(s => s.AttachedEntity)
                .Where(uid => uid != null && uid != observer && HasComp<HandsComponent>(uid.Value) && CanCopyBody(uid.Value, observer)).ToArray();
            if (candidates.Length > 0) model = _random.Pick(candidates)!.Value;
            var placed = false;
            for (var i = 0; i < 8; i++)
            {
                var angle = _random.NextFloat() * MathF.Tau;
                var offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (2f + _random.NextFloat());
                var candidate = Transform(observer).Coordinates.Offset(offset);
                if (!_interaction.InRangeUnobstructed(observer, candidate, 4f)) continue;
                coordinates = candidate;
                placed = true;
                break;
            }
            if (!placed) return;
        }
        else coordinates = coordinates.Offset(new Vector2(.18f, .08f));
        if (!TryComp<SpriteComponent>(model, out var source)) return;
        var doll = Spawn("clientsideclone", coordinates);
        _doll = doll;
        _tags.AddTag(doll, "HideContextMenu");
        var sprite = Comp<SpriteComponent>(doll);
        _sprites.CopySprite((model, source), (doll, sprite));
        _transform.SetWorldRotation(doll, !weaponOnly && symptom.Threat
            ? (_transform.GetWorldPosition(observer) - _transform.GetWorldPosition(doll)).ToWorldAngle()
            : _transform.GetWorldRotation(model));
        // The weapon is a native directional in-hand layer, never a floating inventory icon.
        if (weaponOnly)
        {
            var layerCount = sprite.AllLayers.Count();
            for (var i = 0; i < layerCount; i++) _sprites.LayerSetVisible((doll, sprite), i, false);
            _sprites.SetDrawDepth((doll, sprite), source.DrawDepth + 1);
        }
        else if (TryComp<HandsComponent>(model, out var hands))
        {
            foreach (var layers in hands.RevealedLayers.Values)
                foreach (var key in layers)
                    if (_sprites.LayerMapTryGet((doll, sprite), key, out var layer, false)) _sprites.LayerSetVisible((doll, sprite), layer, false);
        }
        if (symptom.Threat)
        {
            var layer = _sprites.LayerMapReserve((doll, sprite), "nc-psychosis-inhand");
            _sprites.LayerSetData((doll, sprite), layer, new PrototypeLayerData
            { RsiPath = symptom.InhandRsi, State = "inhand-right", Visible = true });
        }
        if (!symptom.Phantom) _sprites.SetColor((doll, sprite), source.Color.WithAlpha(.3f));
        _shots = 0;
        _nextShot = _timing.CurTime + TimeSpan.FromSeconds(.8);
    }

    private bool HasFreeRightHand(EntityUid body) => TryComp<HandsComponent>(body, out var hands) &&
        hands.Hands.Any(hand => hand.Value.Location == HandLocation.Right && _hands.HandIsEmpty((body, hands), hand.Key));

    private void UpdateDoll()
    {
        UpdateShotVisuals();
        if (_doll is not { } doll) return;
        if (ReducedEffects || !Exists(doll) || Symptom == null || _players.LocalEntity is not { } observer)
        { ClearDolls(); return; }
        if (Transform(doll).MapID != Transform(observer).MapID) { ClearDolls(); return; }
        if (_timing.CurTime >= _nextDollVisibility)
        {
            _nextDollVisibility = _timing.CurTime + TimeSpan.FromSeconds(.1);
            if (!_interaction.InRangeUnobstructed(observer, doll, 7f)) { ClearDolls(); return; }
        }
        if (_weaponWearer is { } wearer)
        {
            if (!Exists(wearer) || !Comp<SpriteComponent>(wearer).Visible || !HasFreeRightHand(wearer)) { ClearDolls(); return; }
            // Preserve walking offsets, scale and rotation without changing any real entity's sprite or inventory.
            var source = Comp<SpriteComponent>(wearer);
            var sprite = Comp<SpriteComponent>(doll);
            _sprites.SetOffset((doll, sprite), source.Offset);
            _sprites.SetRotation((doll, sprite), source.Rotation);
            _sprites.SetScale((doll, sprite), source.Scale);
        }
        if (!Symptom.ShootAtViewer || _shots >= 3 || _timing.CurTime < _nextShot) return;
        _shots++;
        _nextShot = _timing.CurTime + TimeSpan.FromSeconds(.75);
        var weaponSprite = Comp<SpriteComponent>(doll);
        var direction = SpriteComponent.Layer.GetDirection(RsiDirectionType.Dir4,
            _transform.GetWorldRotation(doll) + _eyes.CurrentEye.Rotation);
        // Barrel pixels of the native Mk58 right-hand frames, relative to their 32px origin.
        var barrel = direction switch
        {
            RsiDirection.North => new Vector2(12, -5),
            RsiDirection.East => new Vector2(4, -5),
            RsiDirection.West => new Vector2(-9, -5),
            _ => new Vector2(-14, -5),
        } / 32f;
        var offset = Vector2.Transform(barrel * weaponSprite.Scale,
            Matrix3x2.CreateRotation((float) weaponSprite.Rotation.Theta)) + weaponSprite.Offset;
        var shotOrigin = _transform.GetWorldPosition(doll) + Vector2.Transform(offset,
            Matrix3x2.CreateRotation(-(float) _eyes.CurrentEye.Rotation.Theta));
        var shotEnd = _transform.GetWorldPosition(observer) + new Vector2((_random.NextFloat() - .5f) * .5f, .1f);
        ShowNativeShot(observer, shotOrigin, shotEnd);
        _shotAudio = _audio.Stop(_shotAudio);
        if (!QuietSounds && Symptom.ShotSound is { } sound)
            _shotAudio = _audio.PlayStatic(sound, observer, Transform(doll).Coordinates)?.Entity;
    }

    private void ClearDolls()
    {
        if (_doll is { } doll && Exists(doll)) QueueDel(doll);
        _doll = null;
        _weaponWearer = null;
        ClearShotVisuals();
        _shotAudio = _audio.Stop(_shotAudio);
    }
}
