// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.Client.Clickable;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Medical.Wounds;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.Utility;
using Robust.Shared.Input;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Medical.Surgery;

/// <summary>
/// NC sprite-only surgical body. The outer silhouette, incisions, retracted skin, tissue, bones and organs are
/// composed in one coordinate space. Client clicks provide only a normalized contact point; the server resolves the
/// real region, structure and medical mutation.
/// </summary>
public sealed partial class NCFreeformSurgeryFieldControl : Container
{
    private const float CanvasPadding = 12f;

    private static readonly Color Canvas = Color.FromHex("#030B0F");
    private static readonly Color Grid = Color.FromHex("#16404458");
    private static readonly Color Border = Color.FromHex("#36D7C8C0");

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IResourceCache _resources = default!;
    [Dependency] private IClickMapManager _clickMaps = default!;

    public event Action<ProtoId<NCTargetZonePrototype>, NCWoundSurface, Guid?, Vector2>? InteractionRequested;
    public event Action<ProtoId<NCTargetZonePrototype>, NCWoundSurface, Vector2>? AttachRequested;
    public event Action<ProtoId<NCTargetZonePrototype>, NCWoundSurface, Guid?>? SiteRequested;
    public event Action<float>? DepthAdjusted;

    private readonly SchematicBodyField _body;
    private ProtoId<NCAnatomyProfilePrototype>? _profile;

    public NCFreeformSurgeryFieldControl()
    {
        IoCManager.InjectDependencies(this);
        MinSize = new Vector2(520f, 460f);
        HorizontalExpand = true;
        VerticalExpand = true;
        RectClipContent = true;
        MouseFilter = MouseFilterMode.Ignore;

        _body = new SchematicBodyField(_clickMaps);
        _body.InteractionRequested +=
            (zone, surface, wound, position) => InteractionRequested?.Invoke(zone, surface, wound, position);
        _body.AttachRequested +=
            (zone, surface, point) => AttachRequested?.Invoke(zone, surface, point);
        _body.SiteRequested +=
            (zone, surface, incision) => SiteRequested?.Invoke(zone, surface, incision);
        _body.DepthAdjusted += delta => DepthAdjusted?.Invoke(delta);
        AddChild(_body);
    }

    public void SetState(NCFreeformSurgeryState state, float predictedDepth)
    {
        if (_profile != state.AnatomyProfile)
        {
            _profile = state.AnatomyProfile;
            RebuildAtlas(state.AnatomyProfile);
        }

        _body.SetState(state, predictedDepth);
    }

    public void SetPredictedDepth(float depth)
    {
        _body.SetPredictedDepth(depth);
    }

    public void ResolvePendingInteraction()
    {
        _body.ResolvePendingInteraction();
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        _body.Measure(availableSize);
        return MinSize;
    }

    protected override Vector2 ArrangeOverride(Vector2 finalSize)
    {
        // NC: Preserve the authored pixel aspect ratio. Stretching the silhouette would also distort the
        // alpha hit map and make an organ appear somewhere other than its authoritative normalized position.
        var available = Vector2.Max(finalSize - new Vector2(CanvasPadding * 2f), Vector2.One);
        var canvas = Vector2.Max(_body.CanvasSize, Vector2.One);
        var scale = MathF.Min(available.X / canvas.X, available.Y / canvas.Y);
        var size = canvas * MathF.Max(0.01f, scale);
        var position = (finalSize - size) * 0.5f;
        _body.Arrange(UIBox2.FromDimensions(position, size));
        return finalSize;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        var bounds = PixelSizeBox;
        handle.DrawRect(bounds, Canvas);
        for (var step = 1; step < 10; step++)
        {
            var x = PixelWidth * step / 10f;
            var y = PixelHeight * step / 10f;
            handle.DrawLine(new Vector2(x, 0f), new Vector2(x, PixelHeight), Grid);
            handle.DrawLine(new Vector2(0f, y), new Vector2(PixelWidth, y), Grid);
        }
        handle.DrawRect(bounds, Border, filled: false);
    }

    private void RebuildAtlas(ProtoId<NCAnatomyProfilePrototype> profileId)
    {
        if (string.IsNullOrEmpty(profileId.Id) ||
            !_prototypes.TryIndex(profileId, out var anatomy) ||
            !_prototypes.TryIndex(anatomy.SurgicalField, out NCFreeformSurgeryProfilePrototype? surgery) ||
            !_resources.TryGetResource<TextureResource>(surgery.BodySilhouette, out var silhouette) ||
            !_resources.TryGetResource<TextureResource>(surgery.BodyLayerAtlas, out var bodyLayers) ||
            !_resources.TryGetResource<TextureResource>(surgery.AnatomyAtlas, out var anatomyTexture) ||
            !_resources.TryGetResource<TextureResource>(surgery.WoundAtlas, out var woundTexture) ||
            !_resources.TryGetResource<TextureResource>(surgery.ForeignBodySprite, out var foreignBodyTexture))
        {
            _body.Configure(new Vector2(128f, 192f), [], null);
            return;
        }

        var zones = new List<ZoneProjection>(surgery.BodyZones.Count);
        foreach (var (zoneId, definition) in surgery.BodyZones)
        {
            if (!anatomy.TargetZones.Contains(zoneId) ||
                !_prototypes.TryIndex(zoneId, out NCTargetZonePrototype? zone))
            {
                continue;
            }

            zones.Add(new ZoneProjection(
                zoneId,
                UIBox2.FromDimensions(definition.Position, definition.Size),
                definition.OpeningSize,
                definition.InteractionAnchor,
                zone.HitPriority));
        }

        var visuals = new SurgeryVisuals(
            surgery,
            silhouette.Texture,
            bodyLayers.Texture,
            anatomyTexture.Texture,
            woundTexture.Texture,
            foreignBodyTexture.Texture);
        _body.Configure(
            surgery.BodyCanvasSize,
            zones.OrderByDescending(zone => zone.Priority)
                .ThenBy(zone => zone.Bounds.Size.LengthSquared())
                .ToArray(),
            visuals);
    }

    private readonly record struct ZoneProjection(
        ProtoId<NCTargetZonePrototype> Zone,
        UIBox2 Bounds,
        Vector2 OpeningSize,
        Vector2 InteractionAnchor,
        int Priority);

    private sealed record SurgeryVisuals(
        NCFreeformSurgeryProfilePrototype Profile,
        Texture BodySilhouette,
        Texture BodyLayerAtlas,
        Texture AnatomyAtlas,
        Texture WoundAtlas,
        Texture ForeignBodySprite);

    /// <summary>
    /// One persistent interactive composition. A scalpel click creates a canonical data-driven incision; every
    /// later tool click operates at the exact displayed zone or exposed structure coordinate.
    /// </summary>
    private sealed class SchematicBodyField : Control
    {
        public event Action<ProtoId<NCTargetZonePrototype>, NCWoundSurface, Guid?, Vector2>? InteractionRequested;
        public event Action<ProtoId<NCTargetZonePrototype>, NCWoundSurface, Vector2>? AttachRequested;
        public event Action<ProtoId<NCTargetZonePrototype>, NCWoundSurface, Guid?>? SiteRequested;
        public event Action<float>? DepthAdjusted;

        private readonly IClickMapManager _clickMaps;
        private NCFreeformSurgeryState? _state;
        private SurgeryVisuals? _visuals;
        private ZoneProjection[] _zones = [];
        private Vector2 _canvasSize = new(128f, 192f);
        private float _predictedDepth;
        private bool _awaitingInteractionResult;
        private ProtoId<NCTargetZonePrototype> _pendingZone;
        private NCWoundSurface _pendingSurface;
        private Vector2? _pendingPosition;
        private NCFreeformSurgeryAction? _pendingAction;

        public Vector2 CanvasSize => _canvasSize;

        public SchematicBodyField(IClickMapManager clickMaps)
        {
            _clickMaps = clickMaps;
            MouseFilter = MouseFilterMode.Stop;
            RectClipContent = true;
            HorizontalExpand = true;
            VerticalExpand = true;
        }

        public void Configure(Vector2 canvasSize, ZoneProjection[] zones, SurgeryVisuals? visuals)
        {
            _canvasSize = Vector2.Max(canvasSize, Vector2.One);
            _zones = zones;
            _visuals = visuals;
        }

        public void SetState(NCFreeformSurgeryState state, float predictedDepth)
        {
            _state = state;
            _predictedDepth = Math.Clamp(predictedDepth, 0f, 1f);
        }

        public void SetPredictedDepth(float depth)
        {
            _predictedDepth = Math.Clamp(depth, 0f, 1f);
        }

        public void ResolvePendingInteraction()
        {
            _awaitingInteractionResult = false;
            _pendingPosition = null;
            _pendingAction = null;
        }

        protected override void KeyBindDown(GUIBoundKeyEventArgs args)
        {
            base.KeyBindDown(args);
            if (_state == null || _visuals == null ||
                (args.Function != EngineKeyFunctions.Use && args.Function != EngineKeyFunctions.UseSecondary))
            {
                return;
            }

            // NC: Wounds and retained projectiles are resolved before their surrounding aperture. Their stable
            // ids make overlapping trauma deterministic and automatically select the original impact surface.
            var hitWound = TryResolveWoundAtScreen(
                args.RelativePixelPosition,
                out var screenWound,
                out var local);
            var zone = hitWound ? screenWound.Zone : default;
            var surface = hitWound ? screenWound.Surface : _state.Surface;
            Guid? woundId = hitWound ? screenWound.WoundId : null;
            var screenIncision = default(NCFreeformSurgeryIncisionSummary);
            var hitIncision = !hitWound && TryResolveIncisionAtScreen(
                args.RelativePixelPosition,
                out zone,
                out local,
                out screenIncision);
            if (hitIncision)
                surface = screenIncision.Surface;
            if (!hitWound && !hitIncision &&
                !TryResolvePoint(args.RelativePixelPosition, out zone, out local))
            {
                return;
            }

            if (_awaitingInteractionResult)
            {
                args.Handle();
                return;
            }

            if (args.Function == EngineKeyFunctions.UseSecondary)
            {
                AttachRequested?.Invoke(zone, surface, local);
                args.Handle();
                return;
            }

            var action = ResolveSelectedAction();
            if (action == null)
            {
                if (hitIncision)
                    SiteRequested?.Invoke(zone, surface, screenIncision.IncisionId);
                else if (TryResolveIncision(zone, surface, local, out var incision))
                    SiteRequested?.Invoke(zone, surface, incision.IncisionId);
                args.Handle();
                return;
            }
            if (!TryGetZone(zone, out var projection))
                return;
            // NC: The server authors the static cut geometry. Cutting actions still transmit the real cursor
            // point so a repeated scalpel click can damage the exposed sprite under it.
            var contact = action == NCFreeformSurgeryAction.Retract ||
                          action == NCFreeformSurgeryAction.Suture && hitIncision
                ? Vector2.Clamp(projection.InteractionAnchor, Vector2.Zero, Vector2.One)
                : local;

            _awaitingInteractionResult = true;
            _pendingZone = zone;
            _pendingSurface = surface;
            _pendingPosition = contact;
            _pendingAction = action;
            InteractionRequested?.Invoke(zone, surface, woundId, contact);
            args.Handle();
        }

        protected override void MouseMove(GUIMouseMoveEventArgs args)
        {
            base.MouseMove(args);
            ToolTip = ResolveStructureTooltip(args.RelativePixelPosition);
        }

        protected override void MouseWheel(GUIMouseWheelEventArgs args)
        {
            base.MouseWheel(args);
            if (MathHelper.CloseTo(args.Delta.Y, 0f))
                return;
            DepthAdjusted?.Invoke(args.Delta.Y * 0.04f);
            args.Handle();
        }

        protected override void Draw(DrawingHandleScreen handle)
        {
            base.Draw(handle);
            if (_state == null || _visuals == null)
                return;

            DrawCell(handle,
                _visuals.BodyLayerAtlas,
                _visuals.Profile.BodyLayerAtlasGrid,
                _visuals.Profile.BodySkinSprite,
                PixelSizeBox,
                Color.White);

            // NC: Open sites replace only their own skin pixels. All other wounds and incisions remain in the
            // same persistent body composition while the operator changes region, tool or working depth.
            foreach (var incision in _state.Incisions)
            {
                if (!incision.Sutured && incision.EdgeSeparation >= _visuals.Profile.MinimumOpenRetraction)
                    DrawOpening(handle, incision);
            }

            foreach (var wound in _state.Wounds)
                DrawWound(handle, wound);

            foreach (var incision in _state.Incisions)
            {
                if (!incision.Sutured && incision.EdgeSeparation >= _visuals.Profile.MinimumOpenRetraction)
                    continue;
                DrawClosedSite(handle, incision);
            }

            foreach (var attachment in _state.Attachments)
            {
                var point = LocalToScreen(_state.SelectedZone, _state.Surface, attachment.Position);
                DrawCell(handle, _visuals.AnatomyAtlas, _visuals.Profile.AnatomyAtlasGrid,
                    _visuals.Profile.RetractedSiteSprite,
                    UIBox2.FromDimensions(point - new Vector2(CanvasScale * 2f), new Vector2(CanvasScale * 4f)),
                    Color.White);
            }

            if (_pendingPosition is { } pending)
                DrawInteractionPrediction(handle, _pendingZone, _pendingSurface, pending);
        }

        private void DrawClosedSite(DrawingHandleScreen handle, NCFreeformSurgeryIncisionSummary incision)
        {
            if (_visuals == null || !TryClosedSiteRect(incision, out var destination))
                return;
            var cell = incision.Sutured
                ? _visuals.Profile.SutureSprite
                : _visuals.Profile.IncisionSprite;
            DrawCell(handle, _visuals.WoundAtlas, _visuals.Profile.WoundAtlasGrid, cell,
                destination, Color.White);
        }

        /// <summary>
        /// NC: Prediction mirrors the next discrete site state without drawing any client-authored incision
        /// line. The authoritative server reply replaces this stamp or rolls it back.
        /// </summary>
        private void DrawInteractionPrediction(
            DrawingHandleScreen handle,
            ProtoId<NCTargetZonePrototype> zoneId,
            NCWoundSurface surface,
            Vector2 position)
        {
            if (_visuals == null || _pendingAction == null || !TryGetZone(zoneId, out var zone))
                return;

            var displayPosition = _pendingAction is NCFreeformSurgeryAction.Incise or
                NCFreeformSurgeryAction.Autopsy or NCFreeformSurgeryAction.Retract
                ? zone.InteractionAnchor
                : position;
            var screenCenter = LocalToScreen(zoneId, surface, displayPosition);
            var authoredSize = zone.OpeningSize / _canvasSize * PixelSize;
            var tint = _awaitingInteractionResult
                ? Color.FromHex("#D8FFFAB8")
                : Color.White;

            if (_pendingAction == NCFreeformSurgeryAction.Retract &&
                TryResolveIncision(zoneId, surface, position, out var incision))
            {
                DrawOpening(handle, incision);
                return;
            }

            if (_pendingAction is not (NCFreeformSurgeryAction.Incise or
                NCFreeformSurgeryAction.Autopsy or NCFreeformSurgeryAction.Suture))
            {
                return;
            }

            var cell = _pendingAction == NCFreeformSurgeryAction.Suture
                ? _visuals.Profile.SutureSprite
                : _visuals.Profile.IncisionSprite;
            var size = Vector2.Max(authoredSize * 0.58f, new Vector2(CanvasScale * 6f));
            DrawCell(handle, _visuals.WoundAtlas, _visuals.Profile.WoundAtlasGrid, cell,
                UIBox2.FromDimensions(screenCenter - size * 0.5f, size), tint);
        }

        private void DrawOpening(DrawingHandleScreen handle, NCFreeformSurgeryIncisionSummary incision)
        {
            if (_state == null || _visuals == null ||
                !TryOpeningRects(incision, out var site, out var aperture))
            {
                return;
            }

            // NC: The atlas retractor contains opaque wound tissue in its centre. Draw it first, then replace
            // only the inset aperture with the currently exposed layer and authoritative structures.
            DrawCell(handle, _visuals.AnatomyAtlas, _visuals.Profile.AnatomyAtlasGrid,
                _visuals.Profile.RetractedSiteSprite, site, Color.White);

            if (incision.Stage == NCInteractiveSurgerySiteStage.Retracted)
            {
                // NC: Retracting skin reveals tissue, not organs. A surface-depth incision uses the first actual
                // soft-tissue cell so an opened site never looks like intact blue skin or an impossible cavity.
                var tissue = incision.VisibleLayer == NCTissueLayerKind.Surface
                    ? NCTissueLayerKind.SoftTissue
                    : incision.VisibleLayer;
                if (_visuals.Profile.LayerSprites.TryGetValue(tissue, out var tissueCell))
                {
                    DrawCell(handle, _visuals.AnatomyAtlas, _visuals.Profile.AnatomyAtlasGrid,
                        tissueCell, aperture, Color.White);
                }
                return;
            }

            if (incision.Stage == NCInteractiveSurgerySiteStage.StructuralExposed)
            {
                // NC: Use the co-registered full-body skeleton crop that matched the old presentation. Correct
                // source normalization keeps ribs, skull and long bones pixel-locked to the silhouette.
                DrawBodyLayerRegion(handle, _visuals.Profile.BodySkeletonSprite, aperture, Color.White);
                DrawAuthoritativeStructures(handle, incision, aperture, drawOrgans: false);
                return;
            }

            if (_visuals.Profile.LayerSprites.TryGetValue(NCTissueLayerKind.Cavity, out var cavityCell))
            {
                DrawCell(handle, _visuals.AnatomyAtlas, _visuals.Profile.AnatomyAtlasGrid,
                    cavityCell, aperture, Color.White);
            }

            DrawAuthoritativeStructures(handle, incision, aperture, drawOrgans: true);
            if (incision.BoneCasingOpen)
                DrawOpenedBoneLayerRegion(handle, aperture);
        }

        private void DrawAuthoritativeStructures(
            DrawingHandleScreen handle,
            NCFreeformSurgeryIncisionSummary incision,
            UIBox2 aperture,
            bool drawOrgans)
        {
            if (_state == null || _visuals == null || incision.Zone != _state.SelectedZone ||
                incision.Surface != _state.Surface)
                return;

            foreach (var structure in _state.Structures
                         .Where(value => value.Exposed && value.Kind != NCFreeformSurgeryStructureKind.Region)
                         .Where(value => drawOrgans || value.Kind is
                             NCFreeformSurgeryStructureKind.Bone or
                             NCFreeformSurgeryStructureKind.Tendon or
                             NCFreeformSurgeryStructureKind.Nerve)
                         // NC: Structural supports sit behind deeper organ sprites. Higher authored depth is
                         // drawn last, matching the same ordering used by authoritative cursor hit-testing.
                         .OrderBy(value => value.Kind == NCFreeformSurgeryStructureKind.Bone ? 0 : 1)
                         .ThenBy(value => value.MinimumDepth)
                         .ThenBy(value => value.Handle))
            {
                // Registered skeleton crops own intact/protective bone presentation. Dynamic bone sprites are
                // retained only for damage markers and hit-testing, avoiding the compact shifted rib cage.
                if (structure.Kind == NCFreeformSurgeryStructureKind.Bone)
                {
                    DrawStructureDamage(handle, structure, aperture);
                    continue;
                }
                if (!_visuals.Profile.StructureSprites.TryGetValue(structure.Visual, out var cell))
                    continue;

                var center = aperture.TopLeft + Vector2.Clamp(structure.Position, Vector2.Zero, Vector2.One) *
                    aperture.Size;
                var size = Vector2.Clamp(structure.Size, new Vector2(0.04f), Vector2.One) * aperture.Size;
                var destination = UIBox2.FromDimensions(center - size * 0.5f, size);
                DrawCell(handle, _visuals.AnatomyAtlas, _visuals.Profile.AnatomyAtlasGrid,
                    cell, destination, ResolveStructureTint(structure.DamageVisual));
                DrawStructureDamage(handle, structure, aperture);
            }
        }

        private void DrawStructureDamage(
            DrawingHandleScreen handle,
            NCFreeformSurgeryStructureSummary structure,
            UIBox2 aperture)
        {
            if (_visuals == null || structure.DamageVisual == NCFreeformSurgeryDamageVisualStage.Healthy)
                return;

            var cell = structure.DamageVisual switch
            {
                NCFreeformSurgeryDamageVisualStage.Fixed or NCFreeformSurgeryDamageVisualStage.Repaired =>
                    _visuals.Profile.SutureSprite,
                NCFreeformSurgeryDamageVisualStage.Critical or NCFreeformSurgeryDamageVisualStage.Ruptured or
                    NCFreeformSurgeryDamageVisualStage.Shattered =>
                    _visuals.Profile.WoundSprites.GetValueOrDefault(NCWoundType.Blast),
                _ => _visuals.Profile.WoundSprites.GetValueOrDefault(NCWoundType.Slash),
            };
            var center = aperture.TopLeft + Vector2.Clamp(structure.Position, Vector2.Zero, Vector2.One) *
                aperture.Size;
            var markerSize = Vector2.Max(
                Vector2.Clamp(structure.Size, new Vector2(0.04f), Vector2.One) * aperture.Size * 0.38f,
                new Vector2(CanvasScale * 4f));
            DrawCell(handle, _visuals.WoundAtlas, _visuals.Profile.WoundAtlasGrid, cell,
                UIBox2.FromDimensions(center - markerSize * 0.5f, markerSize), Color.White);
        }

        private static Color ResolveStructureTint(NCFreeformSurgeryDamageVisualStage stage)
        {
            return stage switch
            {
                NCFreeformSurgeryDamageVisualStage.Damaged or NCFreeformSurgeryDamageVisualStage.Bruised =>
                    Color.FromHex("#FFD0C8"),
                NCFreeformSurgeryDamageVisualStage.Critical or NCFreeformSurgeryDamageVisualStage.Ruptured or
                    NCFreeformSurgeryDamageVisualStage.Shattered => Color.FromHex("#D94A4A"),
                NCFreeformSurgeryDamageVisualStage.Fixed or NCFreeformSurgeryDamageVisualStage.Repaired =>
                    Color.FromHex("#B7F4E8"),
                _ => Color.White,
            };
        }

        private void DrawOpenedBoneLayerRegion(DrawingHandleScreen handle, UIBox2 aperture)
        {
            if (_visuals == null)
                return;
            var gap = aperture.Width * 0.13f;
            var halfWidth = aperture.Width * 0.5f;
            var leftSource = UIBox2.FromDimensions(aperture.TopLeft, new Vector2(halfWidth, aperture.Height));
            var rightSource = UIBox2.FromDimensions(
                new Vector2(aperture.Left + halfWidth, aperture.Top),
                new Vector2(halfWidth, aperture.Height));
            DrawBodyLayerRegion(handle, _visuals.Profile.BodySkeletonSprite,
                new UIBox2(leftSource.Left - gap, leftSource.Top, leftSource.Right - gap, leftSource.Bottom),
                Color.White, leftSource);
            DrawBodyLayerRegion(handle, _visuals.Profile.BodySkeletonSprite,
                new UIBox2(rightSource.Left + gap, rightSource.Top, rightSource.Right + gap, rightSource.Bottom),
                Color.White, rightSource);
        }

        private void DrawBodyLayerRegion(
            DrawingHandleScreen handle,
            Vector2 cell,
            UIBox2 destination,
            Color color,
            UIBox2? registeredSource = null)
        {
            if (_visuals == null || PixelWidth <= 0f || PixelHeight <= 0f)
                return;

            var columns = Math.Max(1f, _visuals.Profile.BodyLayerAtlasGrid.X);
            var rows = Math.Max(1f, _visuals.Profile.BodyLayerAtlasGrid.Y);
            var cellSize = new Vector2(
                _visuals.BodyLayerAtlas.Width / columns,
                _visuals.BodyLayerAtlas.Height / rows);
            var projection = registeredSource ?? destination;
            // NC: projection is already measured in this control's screen pixels. Dividing by PixelSize yields
            // the atlas-cell UV directly; dividing by the authored canvas again sampled a tiny transparent corner
            // and left only the generic retractor visible where ribs or long bones should have been drawn.
            var normalizedTopLeft = projection.TopLeft / PixelSize;
            var normalizedSize = projection.Size / PixelSize;
            var source = UIBox2.FromDimensions(
                cell * cellSize + normalizedTopLeft * cellSize,
                normalizedSize * cellSize);
            handle.DrawTextureRectRegion(_visuals.BodyLayerAtlas, destination, source, color);
        }

        private void DrawWound(DrawingHandleScreen handle, NCFreeformSurgeryWoundSummary wound)
        {
            if (_visuals == null)
                return;
            var cell = wound.ClosureState == NCWoundClosureState.Sutured
                ? _visuals.Profile.SutureSprite
                : _visuals.Profile.WoundSprites.GetValueOrDefault(wound.Type);
            if (wound.ClosureState != NCWoundClosureState.Sutured &&
                !_visuals.Profile.WoundSprites.TryGetValue(wound.Type, out cell))
                cell = _visuals.Profile.WoundSprites.GetValueOrDefault(NCWoundType.Ballistic);
            // NC: The entry trauma is immutable and always remains at the original surface coordinate. Opening a
            // static operative site may reveal the internal channel, but must never relocate or replace this mark.
            var point = LocalToScreen(wound.Zone, wound.Surface, wound.Position);
            var size = CanvasScale * (2.4f + (float) wound.Severity * 0.8f);
            DrawCell(handle, _visuals.WoundAtlas, _visuals.Profile.WoundAtlasGrid, cell,
                UIBox2.FromDimensions(point - new Vector2(size * 0.5f), new Vector2(size)), Color.White);
            if (!wound.ForeignBodyExposed ||
                !TryResolveOpenIncision(wound.Zone, wound.Surface, out var incision) ||
                !TryOpeningRects(incision, out _, out var aperture))
            {
                return;
            }

            // NC: The retained projectile is a second sprite inside the opened cavity, positioned from the same
            // persistent wound coordinates and addressed by WoundId. It no longer inherits the incision centre.
            var internalPoint = aperture.TopLeft + Vector2.Clamp(wound.Position, Vector2.Zero, Vector2.One) *
                aperture.Size;
            var channelSize = MathF.Max(size * 0.72f, CanvasScale * 2.5f);
            DrawCell(handle, _visuals.WoundAtlas, _visuals.Profile.WoundAtlasGrid, cell,
                UIBox2.FromDimensions(internalPoint - new Vector2(channelSize * 0.5f),
                    new Vector2(channelSize)), Color.White);
            var markerSize = MathF.Max(CanvasScale * 4f, 6f);
            handle.DrawTextureRect(_visuals.ForeignBodySprite,
                UIBox2.FromDimensions(internalPoint - new Vector2(markerSize * 0.5f), new Vector2(markerSize)),
                Color.FromHex("#FFF1A8"));
        }

        private NCFreeformSurgeryAction? ResolveSelectedAction()
        {
            if (_state == null)
                return null;
            foreach (var capability in _state.Capabilities)
            {
                if (capability.Selected)
                    return capability.Action;
            }
            return null;
        }

        private bool TryResolveIncision(
            ProtoId<NCTargetZonePrototype> zone,
            NCWoundSurface surface,
            Vector2 point,
            out NCFreeformSurgeryIncisionSummary incision)
        {
            incision = default;
            if (_state == null || _visuals == null)
                return false;

            var best = _visuals.Profile.IncisionSelectionRadius *
                       _visuals.Profile.IncisionSelectionRadius;
            foreach (var candidate in _state.Incisions)
            {
                if (candidate.Zone != zone || candidate.Surface != surface ||
                    !TryGetZone(candidate.Zone, out var projection))
                    continue;
                // NC: An operative site is an interactive sprite, not a drawable line. Selection therefore
                // resolves against the prototype-owned anchor and can never follow stale stored geometry.
                var distance = Vector2.DistanceSquared(point, projection.InteractionAnchor);
                if (distance > best)
                    continue;
                best = distance;
                incision = candidate;
            }
            return incision.IncisionId != Guid.Empty;
        }

        private bool TryResolveOpenIncision(
            ProtoId<NCTargetZonePrototype> zone,
            NCWoundSurface surface,
            out NCFreeformSurgeryIncisionSummary incision)
        {
            incision = default;
            if (_state == null || _visuals == null)
                return false;
            incision = _state.Incisions
                .Where(value => value.Zone == zone && value.Surface == surface && !value.Sutured &&
                                value.EdgeSeparation >= _visuals.Profile.MinimumOpenRetraction)
                .OrderByDescending(value => value.IncisionId == _state.ActiveIncisionId)
                .ThenBy(value => value.IncisionId)
                .FirstOrDefault();
            return incision.IncisionId != Guid.Empty;
        }

        private string? ResolveStructureTooltip(Vector2 screenPoint)
        {
            if (_state == null || !TryResolveOperativePoint(screenPoint, out var zone, out var local) ||
                zone != _state.SelectedZone)
            {
                return null;
            }

            return _state.Structures
                .Where(structure => structure.Exposed && Contains(structure.Position, structure.Size, local))
                .OrderByDescending(structure => structure.MinimumDepth)
                .Select(structure => structure.Name)
                .FirstOrDefault();
        }

        private bool TryResolvePoint(
            Vector2 screenPoint,
            out ProtoId<NCTargetZonePrototype> zone,
            out Vector2 local)
        {
            if (TryResolveOperativePoint(screenPoint, out zone, out local))
                return true;
            return TryResolveSkinPoint(screenPoint, out zone, out local);
        }

        /// <summary>
        /// NC: Resolves both the permanent entry mark and an exposed retained-projectile sprite to the same
        /// authoritative wound. This runs before aperture hit-testing so the surrounding incision cannot steal
        /// a click intended for a small foreign body.
        /// </summary>
        private bool TryResolveWoundAtScreen(
            Vector2 screenPoint,
            out NCFreeformSurgeryWoundSummary wound,
            out Vector2 local)
        {
            wound = default;
            local = default;
            if (_state == null || _visuals == null)
                return false;

            var bestDistance = float.MaxValue;
            var bestWound = default(NCFreeformSurgeryWoundSummary);
            var bestLocal = Vector2.Zero;
            foreach (var candidate in _state.Wounds)
            {
                var exterior = LocalToScreen(candidate.Zone, candidate.Surface, candidate.Position);
                var exteriorSize = CanvasScale * (2.4f + (float) candidate.Severity * 0.8f);
                TrySelect(candidate, exterior, MathF.Max(exteriorSize * 0.75f, 5f));

                if (!candidate.ForeignBodyExposed ||
                    !TryResolveOpenIncision(candidate.Zone, candidate.Surface, out var incision) ||
                    !TryOpeningRects(incision, out _, out var aperture))
                {
                    continue;
                }

                var internalPoint = aperture.TopLeft +
                    Vector2.Clamp(candidate.Position, Vector2.Zero, Vector2.One) * aperture.Size;
                var markerSize = MathF.Max(CanvasScale * 4f, 6f);
                TrySelect(candidate, internalPoint, MathF.Max(markerSize * 0.8f, 6f));
            }
            wound = bestWound;
            local = bestLocal;
            return wound.WoundId != Guid.Empty;

            void TrySelect(NCFreeformSurgeryWoundSummary candidate, Vector2 center, float radius)
            {
                var distance = Vector2.DistanceSquared(screenPoint, center);
                if (distance > radius * radius || distance >= bestDistance)
                    return;
                bestDistance = distance;
                bestWound = candidate;
                bestLocal = candidate.Position;
            }
        }

        private bool TryResolveIncisionAtScreen(
            Vector2 screenPoint,
            out ProtoId<NCTargetZonePrototype> zone,
            out Vector2 local,
            out NCFreeformSurgeryIncisionSummary incision)
        {
            zone = default;
            local = default;
            incision = default;
            if (_state == null || _visuals == null)
                return false;

            foreach (var candidate in _state.Incisions
                         .Where(value => !value.Sutured &&
                                         value.EdgeSeparation >= _visuals.Profile.MinimumOpenRetraction)
                         .OrderByDescending(value => value.IncisionId == _state.ActiveIncisionId))
            {
                if (!TryOpeningRects(candidate, out _, out var aperture) || !aperture.Contains(screenPoint))
                    continue;
                zone = candidate.Zone;
                local = Vector2.Clamp((screenPoint - aperture.TopLeft) / aperture.Size,
                    Vector2.Zero, Vector2.One);
                incision = candidate;
                return true;
            }

            foreach (var candidate in _state.Incisions
                         .Where(value => value.Sutured ||
                                         value.EdgeSeparation < _visuals.Profile.MinimumOpenRetraction)
                         .OrderByDescending(value => value.IncisionId == _state.ActiveIncisionId))
            {
                if (!TryClosedSiteRect(candidate, out var bounds) || !bounds.Contains(screenPoint) ||
                    !TryGetZone(candidate.Zone, out var projection))
                {
                    continue;
                }
                zone = candidate.Zone;
                local = Vector2.Clamp(projection.InteractionAnchor, Vector2.Zero, Vector2.One);
                incision = candidate;
                return true;
            }
            return false;
        }

        private bool TryResolveOperativePoint(
            Vector2 screenPoint,
            out ProtoId<NCTargetZonePrototype> zone,
            out Vector2 local)
        {
            zone = default;
            local = default;
            if (_state == null || _visuals == null)
                return false;

            foreach (var incision in _state.Incisions
                         .Where(value => !value.Sutured &&
                                         value.EdgeSeparation >= _visuals.Profile.MinimumOpenRetraction)
                         .OrderByDescending(value => value.IncisionId == _state.ActiveIncisionId))
            {
                if (!TryOpeningRects(incision, out _, out var aperture) || !aperture.Contains(screenPoint))
                    continue;
                zone = incision.Zone;
                local = Vector2.Clamp((screenPoint - aperture.TopLeft) / aperture.Size,
                    Vector2.Zero, Vector2.One);
                return true;
            }
            return false;
        }

        private bool TryResolveSkinPoint(
            Vector2 screenPoint,
            out ProtoId<NCTargetZonePrototype> zone,
            out Vector2 local)
        {
            zone = default;
            local = default;
            if (_state == null || _visuals == null || PixelWidth <= 0f || PixelHeight <= 0f)
                return false;

            var canvasPoint = screenPoint / PixelSize * _canvasSize;
            if (canvasPoint.X < 0f || canvasPoint.Y < 0f ||
                canvasPoint.X >= _canvasSize.X || canvasPoint.Y >= _canvasSize.Y)
            {
                return false;
            }

            var texturePoint = new Vector2i(
                (int) MathF.Floor(canvasPoint.X / _canvasSize.X * _visuals.BodySilhouette.Width),
                (int) MathF.Floor(canvasPoint.Y / _canvasSize.Y * _visuals.BodySilhouette.Height));
            if (texturePoint.X < 0 || texturePoint.Y < 0 ||
                texturePoint.X >= _visuals.BodySilhouette.Width ||
                texturePoint.Y >= _visuals.BodySilhouette.Height ||
                !_clickMaps.IsOpaque(_visuals.BodySilhouette, texturePoint))
            {
                return false;
            }

            canvasPoint = UnmirrorCanvasPoint(canvasPoint);
            foreach (var candidate in _zones)
            {
                if (!candidate.Bounds.Contains(canvasPoint))
                    continue;
                zone = candidate.Zone;
                local = Vector2.Clamp((canvasPoint - candidate.Bounds.TopLeft) / candidate.Bounds.Size,
                    Vector2.Zero, Vector2.One);
                return true;
            }
            return false;
        }

        private bool TryOpeningRects(
            NCFreeformSurgeryIncisionSummary incision,
            out UIBox2 site,
            out UIBox2 aperture)
        {
            site = default;
            aperture = default;
            if (!TryGetZone(incision.Zone, out var zone))
                return false;

            var screenCenter = LocalToScreen(incision.Zone, incision.Surface, zone.InteractionAnchor);
            var size = zone.OpeningSize / _canvasSize * PixelSize;
            size = Vector2.Min(Vector2.Max(size, new Vector2(CanvasScale * 8f)), PixelSize);
            var topLeft = Vector2.Clamp(screenCenter - size * 0.5f, Vector2.Zero, PixelSize - size);
            site = UIBox2.FromDimensions(topLeft, size);
            var margin = size * 0.17f;
            aperture = UIBox2.FromDimensions(topLeft + margin, size - margin * 2f);
            return aperture.Width > 0f && aperture.Height > 0f;
        }

        private bool TryGetZone(ProtoId<NCTargetZonePrototype> zone, out ZoneProjection projection)
        {
            foreach (var candidate in _zones)
            {
                if (candidate.Zone != zone)
                    continue;
                projection = candidate;
                return true;
            }

            projection = default;
            return false;
        }

        private bool TryClosedSiteRect(
            NCFreeformSurgeryIncisionSummary incision,
            out UIBox2 destination)
        {
            destination = default;
            if (!TryGetZone(incision.Zone, out var zone))
                return false;
            var screenCenter = LocalToScreen(incision.Zone, incision.Surface, zone.InteractionAnchor);
            var authoredSize = zone.OpeningSize / _canvasSize * PixelSize;
            var scale = incision.Sutured ? 0.52f : 0.58f;
            var size = Vector2.Max(authoredSize * scale, new Vector2(CanvasScale * 6f));
            destination = UIBox2.FromDimensions(screenCenter - size * 0.5f, size);
            return true;
        }

        private Vector2 LocalToScreen(
            ProtoId<NCTargetZonePrototype> zone,
            NCWoundSurface surface,
            Vector2 point)
        {
            if (!TryGetZone(zone, out var projection))
                return PixelSize * 0.5f;
            var canvasPoint = projection.Bounds.TopLeft + Vector2.Clamp(point, Vector2.Zero, Vector2.One) *
                projection.Bounds.Size;
            canvasPoint = MirrorCanvasPoint(canvasPoint, surface);
            return canvasPoint / _canvasSize * PixelSize;
        }

        private Vector2 MirrorCanvasPoint(Vector2 point, NCWoundSurface surface)
        {
            if (surface is NCWoundSurface.Rear or NCWoundSurface.Right)
                point.X = _canvasSize.X - point.X;
            return point;
        }

        private Vector2 UnmirrorCanvasPoint(Vector2 point)
        {
            return MirrorCanvasPoint(point, _state?.Surface ?? NCWoundSurface.Front);
        }

        private float CanvasScale => MathF.Min(
            PixelWidth / MathF.Max(1f, _canvasSize.X),
            PixelHeight / MathF.Max(1f, _canvasSize.Y));

        private static bool Contains(Vector2 center, Vector2 size, Vector2 point)
        {
            var half = size * 0.5f;
            return point.X >= center.X - half.X && point.X <= center.X + half.X &&
                   point.Y >= center.Y - half.Y && point.Y <= center.Y + half.Y;
        }

    }

    private static void DrawCell(
        DrawingHandleScreen handle,
        Texture texture,
        Vector2 grid,
        Vector2 cell,
        UIBox2 destination,
        Color color)
    {
        var columns = Math.Max(1f, grid.X);
        var rows = Math.Max(1f, grid.Y);
        var size = new Vector2(texture.Width / columns, texture.Height / rows);
        var source = UIBox2.FromDimensions(new Vector2(cell.X * size.X, cell.Y * size.Y), size);
        handle.DrawTextureRectRegion(texture, destination, source, color);
    }

}
