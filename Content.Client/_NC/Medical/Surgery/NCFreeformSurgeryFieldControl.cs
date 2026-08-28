// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Medical.Wounds;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Input;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Medical.Surgery;

/// <summary>
/// Presents the real networked patient sprite as the operative doll. A transparent atlas projected over the
/// sprite resolves anatomical clicks and converts whole-body coordinates into the server's regional coordinates.
/// Medical mutations remain authoritative; only zone choice, stroke preview and working depth are predicted.
/// </summary>
public sealed partial class NCFreeformSurgeryFieldControl : Container
{
    private const float CanvasPadding = 12f;

    private static readonly Color Canvas = Color.FromHex("#030B0F");
    private static readonly Color Grid = Color.FromHex("#16404458");
    private static readonly Color Border = Color.FromHex("#36D7C8C0");

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IResourceCache _resources = default!;

    public event Action<ProtoId<NCTargetZonePrototype>, Vector2[]>? StrokeSubmitted;
    public event Action<ProtoId<NCTargetZonePrototype>, Vector2>? AttachRequested;
    public event Action<float>? DepthAdjusted;

    private readonly SpriteView _patientSprite;
    private readonly SurgicalOverlay _overlay;
    private ProtoId<NCAnatomyProfilePrototype>? _profile;

    public NCFreeformSurgeryFieldControl()
    {
        IoCManager.InjectDependencies(this);
        MinSize = new Vector2(520f, 460f);
        HorizontalExpand = true;
        VerticalExpand = true;
        RectClipContent = true;
        MouseFilter = MouseFilterMode.Ignore;

        _patientSprite = new SpriteView
        {
            Stretch = SpriteView.StretchMode.Fill,
            MouseFilter = MouseFilterMode.Ignore,
            RectClipContent = true,
        };
        _overlay = new SurgicalOverlay();
        _overlay.StrokeSubmitted += (zone, points) => StrokeSubmitted?.Invoke(zone, points);
        _overlay.AttachRequested += (zone, point) => AttachRequested?.Invoke(zone, point);
        _overlay.DepthAdjusted += delta => DepthAdjusted?.Invoke(delta);
        AddChild(_patientSprite);
        AddChild(_overlay);
    }

    public void SetState(NCFreeformSurgeryState state, float predictedDepth)
    {
        if (state.Patient.Valid)
            _patientSprite.SetEntity(state.Patient);
        else
            _patientSprite.SetEntity((EntityUid?) null);

        if (_profile != state.AnatomyProfile)
        {
            _profile = state.AnatomyProfile;
            RebuildAtlas(state.AnatomyProfile);
        }

        _patientSprite.OverrideDirection = SurfaceDirection(state.Surface);
        _overlay.SetState(state, predictedDepth);
    }

    public void SetPredictedDepth(float depth)
    {
        _overlay.SetPredictedDepth(depth);
    }

    public void ResolvePendingStroke()
    {
        _overlay.ResolvePendingStroke();
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        foreach (var child in Children)
            child.Measure(availableSize);
        return MinSize;
    }

    protected override Vector2 ArrangeOverride(Vector2 finalSize)
    {
        // The target atlas and humanoid RSI both use a square 32 px canvas. Keeping both children in the same
        // centered square prevents wounds and incision geometry from drifting when the window is resized.
        var side = MathF.Max(64f, MathF.Min(finalSize.X, finalSize.Y) - CanvasPadding * 2f);
        var position = (finalSize - new Vector2(side)) * 0.5f;
        var rect = UIBox2.FromDimensions(position, new Vector2(side));
        _patientSprite.Arrange(rect);
        _overlay.Arrange(rect);
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
        if (!_prototypes.TryIndex(profileId, out var anatomy) ||
            !_prototypes.TryIndex(anatomy.TargetDoll, out NCTargetDollProfilePrototype? doll))
        {
            _overlay.ConfigureAtlas(new Vector2(32f), []);
            return;
        }

        var zones = new List<ZoneProjection>(doll.Zones.Count);
        foreach (var presentation in doll.Zones)
        {
            if (!anatomy.TargetZones.Contains(presentation.Zone) ||
                !_prototypes.TryIndex(presentation.Zone, out NCTargetZonePrototype? zone) ||
                !_resources.TryGetResource<TextureResource>(presentation.SelectedTexture, out var texture))
            {
                continue;
            }

            var displaySize = presentation.DisplaySize ??
                              presentation.SelectedRegion?.Size ??
                              texture.Texture.Size;
            var projectionPosition = presentation.Position +
                                     (presentation.WoundRegion?.Position ?? Vector2.Zero);
            var projectionSize = presentation.WoundRegion?.Size ?? displaySize;
            var hitPosition = presentation.InteractionRegion != null
                ? presentation.Position + presentation.InteractionRegion.Position
                : presentation.Position - presentation.HitboxPadding;
            var hitSize = presentation.InteractionRegion?.Size ??
                          displaySize + presentation.HitboxPadding * 2f;
            var maskRegion = presentation.SelectedRegion is { } selectedRegion
                ? UIBox2.FromDimensions(selectedRegion.Position, selectedRegion.Size)
                : UIBox2.FromDimensions(Vector2.Zero, texture.Texture.Size);
            zones.Add(new ZoneProjection(
                presentation.Zone,
                UIBox2.FromDimensions(projectionPosition, projectionSize),
                UIBox2.FromDimensions(hitPosition, hitSize),
                texture.Texture,
                maskRegion,
                zone.HitPriority));
        }

        _overlay.ConfigureAtlas(
            doll.CanvasSize,
            zones.OrderByDescending(value => value.Priority)
                .ThenBy(value => value.HitBounds.Size.LengthSquared())
                .ToArray());
    }

    private static Direction SurfaceDirection(NCWoundSurface surface) => surface switch
    {
        NCWoundSurface.Front => Direction.South,
        NCWoundSurface.Rear => Direction.North,
        NCWoundSurface.Left => Direction.East,
        NCWoundSurface.Right => Direction.West,
        _ => Direction.South,
    };

    private readonly record struct ZoneProjection(
        ProtoId<NCTargetZonePrototype> Zone,
        UIBox2 ProjectionBounds,
        UIBox2 HitBounds,
        Texture MaskTexture,
        UIBox2 MaskRegion,
        int Priority);

    /// <summary>
    /// Input and medical geometry layer above the real SpriteView. It deliberately owns no entities.
    /// </summary>
    private sealed class SurgicalOverlay : Control
    {
        private static readonly Color Incision = Color.FromHex("#FF5969");
        private static readonly Color Sutured = Color.FromHex("#8DD9C4");
        private static readonly Color Wound = Color.FromHex("#FF923D");
        private static readonly Color Attachment = Color.FromHex("#FFD76A");
        private static readonly Color Preview = Color.FromHex("#80FFF2");
        private static readonly Color SkinEdgeLight = Color.FromHex("#F19A8E");
        private static readonly Color SkinEdgeDark = Color.FromHex("#8E2435");
        private static readonly Color SurfaceTissue = Color.FromHex("#B84A55");
        private static readonly Color SoftTissue = Color.FromHex("#D39A48");
        private static readonly Color MuscleTissue = Color.FromHex("#982D3E");
        private static readonly Color StructuralTissue = Color.FromHex("#D8C9A4");
        private static readonly Color CavityTissue = Color.FromHex("#3A0C19");
        private static readonly Color InternalTissue = Color.FromHex("#671627");
        private static readonly Color MechanicalTissue = Color.FromHex("#315B66");
        private static readonly Color DamagedStructure = Color.FromHex("#52121D");

        public event Action<ProtoId<NCTargetZonePrototype>, Vector2[]>? StrokeSubmitted;
        public event Action<ProtoId<NCTargetZonePrototype>, Vector2>? AttachRequested;
        public event Action<float>? DepthAdjusted;

        private NCFreeformSurgeryState? _state;
        private Vector2 _atlasSize = new(32f);
        private ZoneProjection[] _zones = [];
        private ProtoId<NCTargetZonePrototype> _strokeZone;
        private float _predictedDepth;
        private readonly List<Vector2> _stroke = [];
        private bool _drawing;
        private bool _awaitingStrokeResult;
        private NCFreeformSurgeryAction? _pendingAction;

        public SurgicalOverlay()
        {
            MouseFilter = MouseFilterMode.Stop;
            RectClipContent = true;
        }

        public void ConfigureAtlas(Vector2 canvasSize, ZoneProjection[] zones)
        {
            _atlasSize = new Vector2(MathF.Max(1f, canvasSize.X), MathF.Max(1f, canvasSize.Y));
            _zones = zones;
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

        public void ResolvePendingStroke()
        {
            _awaitingStrokeResult = false;
            _pendingAction = null;
            _drawing = false;
            _stroke.Clear();
        }

        protected override void KeyBindDown(GUIBoundKeyEventArgs args)
        {
            base.KeyBindDown(args);
            if (_state == null ||
                (args.Function != EngineKeyFunctions.Use && args.Function != EngineKeyFunctions.UseSecondary) ||
                !TryResolveZone(args.RelativePosition, out var zone, out var localPoint))
            {
                return;
            }


            // A submitted physical stroke is immutable until the matching authoritative reply arrives. This
            // prevents a second gesture from replacing the visual prediction while the first is in flight.
            if (_awaitingStrokeResult)
            {
                args.Handle();
                return;
            }

            if (args.Function == EngineKeyFunctions.UseSecondary)
            {
                AttachRequested?.Invoke(zone, localPoint);
                args.Handle();
                return;
            }

            // One physical gesture belongs to the body part under its initial pixel. Crossing into a neighbouring
            // atlas zone cannot silently redirect a scalpel or saw halfway through the stroke.
            _strokeZone = zone;
            _drawing = true;
            _stroke.Clear();
            AddPoint(localPoint);
            args.Handle();
        }

        protected override void MouseMove(GUIMouseMoveEventArgs args)
        {
            base.MouseMove(args);
            if (!_drawing || _state == null ||
                !TryResolveZone(args.RelativePosition, out var zone, out var localPoint) || zone != _strokeZone)
            {
                return;
            }

            AddPoint(localPoint);
            args.Handle();
        }

        protected override void KeyBindUp(GUIBoundKeyEventArgs args)
        {
            base.KeyBindUp(args);
            if (args.Function != EngineKeyFunctions.Use || !_drawing || _state == null)
                return;

            _drawing = false;
            if (TryResolveZone(args.RelativePosition, out var zone, out var localPoint) && zone == _strokeZone)
                AddPoint(localPoint);
            if (_stroke.Count == 1)
                _stroke.Add(Vector2.Clamp(_stroke[0] + new Vector2(0.016f, 0f), Vector2.Zero, Vector2.One));
            if (_stroke.Count >= 2)
            {
                _awaitingStrokeResult = true;
                _pendingAction = ResolveSelectedAction();
                StrokeSubmitted?.Invoke(_strokeZone, _stroke.ToArray());
            }
            else
            {
                _stroke.Clear();
            }
            args.Handle();
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
            if (_state == null)
                return;

            // Every persistent incision is projected onto its own anatomical atlas region. Changing the active
            // working area therefore never erases previous operative traces from the patient's body.
            foreach (var projection in _zones)
                DrawOperativeField(handle, projection, projection.Zone);

            foreach (var incision in _state.Incisions)
            {
                // An open incision is already represented by its pixel aperture and skin lips. Closed incisions
                // and cuts which have not yet been retracted retain an explicit readable surface trace.
                if (incision.Sutured)
                    DrawPolyline(handle, incision.Zone, incision.Polyline, Sutured);
                else if (incision.EdgeSeparation < 0.05f)
                    DrawPolyline(handle, incision.Zone, incision.Polyline, Incision);
            }

            foreach (var wound in _state.Wounds)
            {
                var point = LocalToScreen(wound.Zone, wound.Position);
                var radius = 3f + (float) wound.Severity * 0.7f;
                handle.DrawLine(point - new Vector2(radius, 0f), point + new Vector2(radius, 0f), Wound);
                handle.DrawLine(point - new Vector2(0f, radius), point + new Vector2(0f, radius), Wound);
            }

            foreach (var attachment in _state.Attachments)
            {
                var point = LocalToScreen(_state.SelectedZone, attachment.Position);
                handle.DrawCircle(point, 6f, Attachment.WithAlpha(0.25f));
                handle.DrawCircle(point, 3f, Attachment);
            }

            var predictionColor = _awaitingStrokeResult &&
                                  _pendingAction is NCFreeformSurgeryAction.Incise or
                                      NCFreeformSurgeryAction.Autopsy
                ? Incision
                : Preview;
            DrawPolyline(handle, _strokeZone, _stroke, predictionColor);
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

        /// <summary>
        /// Rasterizes the selected body region on the same virtual pixel grid as the humanoid RSI. The incision
        /// is the only aperture: internal colors and silhouettes can never leak outside the body mask or skin lips.
        /// </summary>
        private void DrawOperativeField(
            DrawingHandleScreen handle,
            ZoneProjection projection,
            ProtoId<NCTargetZonePrototype> zone)
        {
            if (_state == null || !_state.Incisions.Any(value => value.Zone == zone && !value.Sutured))
                return;

            var columns = Math.Max(1, (int) MathF.Round(projection.ProjectionBounds.Width));
            var rows = Math.Max(1, (int) MathF.Round(projection.ProjectionBounds.Height));
            var rasterScale = Math.Max(columns, rows);
            for (var y = 0; y < rows; y++)
            {
                for (var x = 0; x < columns; x++)
                {
                    var local = new Vector2(
                        (x + 0.5f) / columns,
                        (y + 0.5f) / rows);
                    if (!MaskContains(projection, local) ||
                        !TryResolveAperture(zone, local, rasterScale, out var incision, out var distance, out var halfWidth))
                    {
                        continue;
                    }

                    var accessibleDepth = MathF.Min(_predictedDepth, incision.Depth);
                    var color = ResolveLayerColor(accessibleDepth);
                    if (zone == _state.SelectedZone &&
                        TryResolveStructurePixel(local, accessibleDepth, out var structure))
                        color = ResolveStructureColor(structure, x, y);

                    // The last raster cell at either side is an irregular skin lip. Alternating light and dark
                    // pixels keep the wound readable without introducing a smooth vector outline over pixel art.
                    var edgeThickness = MathF.Min(halfWidth * 0.48f, 0.65f / rasterScale);
                    if (distance >= halfWidth - edgeThickness)
                        color = (x + y) % 2 == 0 ? SkinEdgeLight : SkinEdgeDark;
                    else if (incision.Contamination > 0.01f)
                        color = Color.InterpolateBetween(
                            color,
                            SkinEdgeDark,
                            Math.Clamp(incision.Contamination, 0f, 1f) * 0.2f);

                    DrawRasterCell(handle, zone, x, y, columns, rows, color);
                }
            }
        }

        private bool MaskContains(ZoneProjection projection, Vector2 local)
        {
            if (_state == null)
                return false;

            var sample = local;
            if (_state.Surface is NCWoundSurface.Rear or NCWoundSurface.Right)
                sample.X = 1f - sample.X;

            var source = projection.MaskRegion;
            var pixelX = (int) MathF.Floor(source.Left + sample.X * source.Width);
            var pixelY = (int) MathF.Floor(source.Top + sample.Y * source.Height);
            pixelX = Math.Clamp(pixelX, 0, projection.MaskTexture.Width - 1);
            pixelY = Math.Clamp(pixelY, 0, projection.MaskTexture.Height - 1);
            return projection.MaskTexture.GetPixel(pixelX, pixelY).AByte > 16;
        }

        private bool TryResolveAperture(
            ProtoId<NCTargetZonePrototype> zone,
            Vector2 point,
            int rasterScale,
            out NCFreeformSurgeryIncisionSummary incision,
            out float distance,
            out float halfWidth)
        {
            incision = default;
            distance = float.MaxValue;
            halfWidth = 0f;
            if (_state == null)
                return false;

            var bestRatio = float.MaxValue;
            foreach (var candidate in _state.Incisions)
            {
                if (candidate.Zone != zone || candidate.Sutured || candidate.Polyline.Length < 2)
                    continue;

                var candidateDistance = MathF.Sqrt(DistanceSquaredToPolyline(point, candidate.Polyline));
                var minimumRasterWidth = 0.45f / Math.Max(1, rasterScale);
                var candidateHalfWidth = Math.Clamp(
                    MathF.Max(minimumRasterWidth, candidate.Width * 0.5f) + candidate.EdgeSeparation * 0.16f,
                    minimumRasterWidth,
                    0.24f);
                var ratio = candidateDistance / candidateHalfWidth;
                if (ratio > 1f || ratio >= bestRatio)
                    continue;

                bestRatio = ratio;
                incision = candidate;
                distance = candidateDistance;
                halfWidth = candidateHalfWidth;
            }

            return bestRatio <= 1f;
        }

        private Color ResolveLayerColor(float depth)
        {
            if (_state == null)
                return SurfaceTissue;

            var kind = NCTissueLayerKind.Surface;
            foreach (var layer in _state.Layers)
            {
                if (!layer.Exposed || depth + 0.001f < layer.StartDepth || depth > layer.EndDepth + 0.001f)
                    continue;
                kind = layer.Kind;
                break;
            }

            return kind switch
            {
                NCTissueLayerKind.Surface => SurfaceTissue,
                NCTissueLayerKind.SoftTissue => SoftTissue,
                NCTissueLayerKind.Vascular => MuscleTissue,
                NCTissueLayerKind.Structural => StructuralTissue,
                NCTissueLayerKind.Cavity => CavityTissue,
                NCTissueLayerKind.InternalOrgan => InternalTissue,
                NCTissueLayerKind.Mechanical => MechanicalTissue,
                _ => InternalTissue,
            };
        }

        private bool TryResolveStructurePixel(
            Vector2 point,
            float accessibleDepth,
            out NCFreeformSurgeryStructureSummary selected)
        {
            selected = default;
            if (_state == null)
                return false;

            var selectedDepth = -1f;
            var selectedArea = float.MaxValue;
            foreach (var structure in _state.Structures)
            {
                if (!structure.Exposed || structure.Kind == NCFreeformSurgeryStructureKind.Region ||
                    structure.Visual == NCFreeformSurgeryStructureVisual.Region ||
                    accessibleDepth + 0.001f < structure.MinimumDepth)
                {
                    continue;
                }

                var half = structure.Size * 0.5f;
                var minimum = structure.Position - half;
                if (point.X < minimum.X || point.Y < minimum.Y ||
                    point.X > minimum.X + structure.Size.X || point.Y > minimum.Y + structure.Size.Y)
                {
                    continue;
                }

                var local = (point - minimum) /
                            new Vector2(MathF.Max(0.001f, structure.Size.X), MathF.Max(0.001f, structure.Size.Y));
                if (!StructureMaskContains(structure.Visual, local))
                    continue;

                var area = structure.Size.X * structure.Size.Y;
                if (structure.MinimumDepth < selectedDepth ||
                    MathHelper.CloseTo(structure.MinimumDepth, selectedDepth) && area >= selectedArea)
                {
                    continue;
                }

                selected = structure;
                selectedDepth = structure.MinimumDepth;
                selectedArea = area;
            }

            return selectedDepth >= 0f;
        }

        private static bool StructureMaskContains(NCFreeformSurgeryStructureVisual visual, Vector2 point)
        {
            var x = point.X - 0.5f;
            var y = point.Y - 0.5f;
            var ellipse = x * x / 0.24f + y * y / 0.24f <= 1f;
            return visual switch
            {
                NCFreeformSurgeryStructureVisual.Region => false,
                NCFreeformSurgeryStructureVisual.Skull => ellipse,
                NCFreeformSurgeryStructureVisual.Brain => ellipse &&
                    ((int) (point.X * 9f) + (int) (point.Y * 7f)) % 4 != 0,
                NCFreeformSurgeryStructureVisual.Eyes =>
                    Ellipse(point, new Vector2(0.27f, 0.5f), new Vector2(0.22f, 0.42f)) ||
                    Ellipse(point, new Vector2(0.73f, 0.5f), new Vector2(0.22f, 0.42f)),
                NCFreeformSurgeryStructureVisual.Tongue =>
                    Ellipse(point, new Vector2(0.5f, 0.58f), new Vector2(0.43f, 0.34f)),
                NCFreeformSurgeryStructureVisual.Ears =>
                    Ellipse(point, new Vector2(0.16f, 0.5f), new Vector2(0.15f, 0.4f)) ||
                    Ellipse(point, new Vector2(0.84f, 0.5f), new Vector2(0.15f, 0.4f)),
                NCFreeformSurgeryStructureVisual.RibCage =>
                    MathF.Abs(x) < 0.08f ||
                    MathF.Abs(point.Y * 7f - MathF.Round(point.Y * 7f)) < 0.2f && MathF.Abs(x) < 0.46f,
                NCFreeformSurgeryStructureVisual.Lungs =>
                    Ellipse(point, new Vector2(0.29f, 0.48f), new Vector2(0.25f, 0.44f)) ||
                    Ellipse(point, new Vector2(0.71f, 0.48f), new Vector2(0.25f, 0.44f)),
                NCFreeformSurgeryStructureVisual.Heart =>
                    Ellipse(point, new Vector2(0.34f, 0.36f), new Vector2(0.29f, 0.27f)) ||
                    Ellipse(point, new Vector2(0.66f, 0.36f), new Vector2(0.29f, 0.27f)) ||
                    point.Y >= 0.32f && point.Y <= 0.92f && MathF.Abs(x) <= (0.92f - point.Y) * 0.72f,
                NCFreeformSurgeryStructureVisual.Liver =>
                    Ellipse(point, new Vector2(0.43f, 0.52f), new Vector2(0.46f, 0.38f)) &&
                    !(point.X > 0.72f && point.Y > 0.55f),
                NCFreeformSurgeryStructureVisual.Stomach =>
                    Ellipse(point, new Vector2(0.48f, 0.48f), new Vector2(0.42f, 0.44f)) &&
                    !(point.X < 0.35f && point.Y < 0.4f),
                NCFreeformSurgeryStructureVisual.Kidneys =>
                    Ellipse(point, new Vector2(0.25f, 0.5f), new Vector2(0.2f, 0.42f)) ||
                    Ellipse(point, new Vector2(0.75f, 0.5f), new Vector2(0.2f, 0.42f)),
                NCFreeformSurgeryStructureVisual.Appendix =>
                    Vector2.DistanceSquared(point, new Vector2(0.42f, 0.3f)) < 0.08f ||
                    MathF.Abs(point.X - point.Y * 0.62f) < 0.13f,
                NCFreeformSurgeryStructureVisual.Pelvis =>
                    ellipse && (!Ellipse(point, new Vector2(0.5f, 0.35f), new Vector2(0.24f, 0.25f)) ||
                                point.Y > 0.62f && MathF.Abs(x) < 0.14f),
                NCFreeformSurgeryStructureVisual.LongBone =>
                    MathF.Abs(x) < 0.23f && point.Y is > 0.1f and < 0.9f ||
                    Vector2.DistanceSquared(point, new Vector2(0.5f, 0.12f)) < 0.065f ||
                    Vector2.DistanceSquared(point, new Vector2(0.5f, 0.88f)) < 0.065f,
                NCFreeformSurgeryStructureVisual.Tendon => MathF.Abs(x) < 0.18f,
                NCFreeformSurgeryStructureVisual.Nerve =>
                    MathF.Abs(point.X - (0.3f + point.Y * 0.38f)) < 0.11f ||
                    point.Y > 0.48f && MathF.Abs(point.X - (0.78f - point.Y * 0.35f)) < 0.09f,
                NCFreeformSurgeryStructureVisual.Cyberware =>
                    point.X is > 0.08f and < 0.92f && point.Y is > 0.08f and < 0.92f &&
                    !((int) (point.X * 5f) == 2 && (int) (point.Y * 5f) == 2),
                _ => ellipse,
            };
        }

        private static bool Ellipse(Vector2 point, Vector2 center, Vector2 radii)
        {
            var offset = (point - center) /
                         new Vector2(MathF.Max(0.001f, radii.X), MathF.Max(0.001f, radii.Y));
            return offset.LengthSquared() <= 1f;
        }

        private static Color ResolveStructureColor(
            NCFreeformSurgeryStructureSummary structure,
            int pixelX,
            int pixelY)
        {
            var healthy = structure.Visual switch
            {
                NCFreeformSurgeryStructureVisual.Skull => Color.FromHex("#E3D7B8"),
                NCFreeformSurgeryStructureVisual.Brain => Color.FromHex("#D584A5"),
                NCFreeformSurgeryStructureVisual.Eyes => Color.FromHex("#C5E7E5"),
                NCFreeformSurgeryStructureVisual.Tongue => Color.FromHex("#C34E6A"),
                NCFreeformSurgeryStructureVisual.Ears => Color.FromHex("#C67975"),
                NCFreeformSurgeryStructureVisual.RibCage => Color.FromHex("#DDD1AD"),
                NCFreeformSurgeryStructureVisual.Lungs => Color.FromHex("#C76A7D"),
                NCFreeformSurgeryStructureVisual.Heart => Color.FromHex("#D83B4B"),
                NCFreeformSurgeryStructureVisual.Liver => Color.FromHex("#7D2938"),
                NCFreeformSurgeryStructureVisual.Stomach => Color.FromHex("#B45F78"),
                NCFreeformSurgeryStructureVisual.Kidneys => Color.FromHex("#84354A"),
                NCFreeformSurgeryStructureVisual.Appendix => Color.FromHex("#B34E60"),
                NCFreeformSurgeryStructureVisual.Pelvis => Color.FromHex("#D9CCA9"),
                NCFreeformSurgeryStructureVisual.LongBone => Color.FromHex("#E1D5B4"),
                NCFreeformSurgeryStructureVisual.Tendon => Color.FromHex("#DFCFA6"),
                NCFreeformSurgeryStructureVisual.Nerve => Color.FromHex("#E1B94D"),
                NCFreeformSurgeryStructureVisual.Cyberware => Color.FromHex("#54D2DC"),
                _ => Color.FromHex("#B84B60"),
            };
            var condition = Math.Clamp(structure.Integrity * 0.75f + structure.Function * 0.25f, 0f, 1f);
            var result = Color.InterpolateBetween(DamagedStructure, healthy, condition);
            if ((pixelX + pixelY) % 3 == 0)
                result = Color.InterpolateBetween(result, CavityTissue, 0.12f);
            return result;
        }

        private void DrawRasterCell(
            DrawingHandleScreen handle,
            ProtoId<NCTargetZonePrototype> zone,
            int x,
            int y,
            int columns,
            int rows,
            Color color)
        {
            var localBounds = UIBox2.FromDimensions(
                new Vector2(x / (float) columns, y / (float) rows),
                new Vector2(1f / columns, 1f / rows));
            var screen = LocalToScreen(zone, localBounds);
            var left = MathF.Round(screen.Left);
            var top = MathF.Round(screen.Top);
            var right = MathF.Round(screen.Right);
            var bottom = MathF.Round(screen.Bottom);
            var snapped = UIBox2.FromDimensions(
                new Vector2(left, top),
                new Vector2(MathF.Max(1f, right - left), MathF.Max(1f, bottom - top)));
            handle.DrawRect(snapped, color);
        }

        private static float DistanceSquaredToPolyline(Vector2 point, IReadOnlyList<Vector2> polyline)
        {
            var best = float.MaxValue;
            for (var index = 1; index < polyline.Count; index++)
            {
                var start = polyline[index - 1];
                var segment = polyline[index] - start;
                var lengthSquared = segment.LengthSquared();
                var fraction = lengthSquared <= 0.000001f
                    ? 0f
                    : Math.Clamp(Vector2.Dot(point - start, segment) / lengthSquared, 0f, 1f);
                best = MathF.Min(best, Vector2.DistanceSquared(point, start + segment * fraction));
            }
            return best;
        }

        private bool TryResolveZone(
            Vector2 screenPoint,
            out ProtoId<NCTargetZonePrototype> zone,
            out Vector2 localPoint)
        {
            var atlasPoint = ScreenToAtlas(screenPoint);
            foreach (var candidate in _zones)
            {
                var hitBounds = TransformBounds(candidate.HitBounds, _state?.Surface ?? NCWoundSurface.Front);
                if (!hitBounds.Contains(atlasPoint))
                    continue;

                zone = candidate.Zone;
                return TryMapAtlasToProjection(atlasPoint, candidate, out localPoint);
            }

            zone = default;
            localPoint = default;
            return false;
        }

        private bool TryMapAtlasToProjection(Vector2 atlasPoint, ZoneProjection projection, out Vector2 localPoint)
        {
            var bounds = TransformBounds(projection.ProjectionBounds, _state?.Surface ?? NCWoundSurface.Front);
            localPoint = Vector2.Clamp(
                (atlasPoint - bounds.TopLeft) /
                new Vector2(MathF.Max(0.001f, bounds.Width), MathF.Max(0.001f, bounds.Height)),
                Vector2.Zero,
                Vector2.One);
            return true;
        }

        private bool TryGetProjection(ProtoId<NCTargetZonePrototype> zone, out ZoneProjection projection)
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

        private UIBox2 TransformBounds(UIBox2 bounds, NCWoundSurface surface)
        {
            if (surface == NCWoundSurface.Rear)
            {
                return UIBox2.FromDimensions(
                    new Vector2(_atlasSize.X - bounds.Right, bounds.Top),
                    bounds.Size);
            }

            if (surface is not (NCWoundSurface.Left or NCWoundSurface.Right))
                return bounds;

            // Side-facing humanoid layers overlap on the 32 px RSI. Compress horizontal atlas separation while
            // retaining small offsets so precision zones remain selectable instead of floating beside the body.
            var sideSign = surface == NCWoundSurface.Left ? -1f : 1f;
            var centerX = _atlasSize.X * 0.5f +
                          (bounds.Center.X - _atlasSize.X * 0.5f) * 0.18f + sideSign * 0.35f;
            var width = MathF.Max(2.5f, bounds.Width * 0.58f);
            return UIBox2.FromDimensions(
                new Vector2(centerX - width * 0.5f, bounds.Top),
                new Vector2(width, bounds.Height));
        }

        private Vector2 ScreenToAtlas(Vector2 point)
        {
            return new Vector2(
                point.X / MathF.Max(1f, Width) * _atlasSize.X,
                point.Y / MathF.Max(1f, Height) * _atlasSize.Y);
        }

        private UIBox2 AtlasToScreen(UIBox2 bounds)
        {
            return UIBox2.FromDimensions(
                AtlasToScreen(bounds.TopLeft),
                bounds.Size / _atlasSize * new Vector2(PixelWidth, PixelHeight));
        }

        private Vector2 AtlasToScreen(Vector2 point)
        {
            return point / _atlasSize * new Vector2(PixelWidth, PixelHeight);
        }

        private UIBox2 LocalToScreen(
            ProtoId<NCTargetZonePrototype> zoneId,
            UIBox2 localBounds)
        {
            if (_state == null || !TryGetProjection(zoneId, out var projection))
                return default;
            var zone = TransformBounds(projection.ProjectionBounds, _state.Surface);
            var atlas = UIBox2.FromDimensions(
                zone.TopLeft + localBounds.TopLeft * zone.Size,
                localBounds.Size * zone.Size);
            return AtlasToScreen(atlas);
        }

        private Vector2 LocalToScreen(
            ProtoId<NCTargetZonePrototype> zoneId,
            Vector2 point)
        {
            if (_state == null || !TryGetProjection(zoneId, out var projection))
                return Vector2.Zero;
            var zone = TransformBounds(projection.ProjectionBounds, _state.Surface);
            return AtlasToScreen(zone.TopLeft + point * zone.Size);
        }

        private void AddPoint(Vector2 point)
        {
            if (_stroke.Count >= 32)
                return;
            if (_stroke.Count != 0 && Vector2.DistanceSquared(_stroke[^1], point) < 0.000025f)
                return;
            _stroke.Add(point);
        }

        private void DrawPolyline(
            DrawingHandleScreen handle,
            ProtoId<NCTargetZonePrototype> zone,
            IReadOnlyList<Vector2> points,
            Color color)
        {
            for (var index = 1; index < points.Count; index++)
                handle.DrawLine(
                    LocalToScreen(zone, points[index - 1]),
                    LocalToScreen(zone, points[index]),
                    color);
        }
    }
}
