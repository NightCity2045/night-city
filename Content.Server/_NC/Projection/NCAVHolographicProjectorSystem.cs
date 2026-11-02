using Content.Shared.DeviceLinking.Events;
using Content.Shared.Power;
using Robust.Server.GameObjects;

namespace Content.Server._NC.Projection.NCAVHolographicProjector;

public sealed class NCAVHolographicProjectorSystem : EntitySystem
{
    [Dependency] private readonly SharedPointLightSystem _light = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCAVHolographicProjectorComponent, ComponentInit>(OnInit);
        SubscribeLocalEvent<NCAVHolographicProjectorComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<NCAVHolographicProjectorComponent, SignalReceivedEvent>(OnSignal);
        SubscribeLocalEvent<NCAVHolographicProjectorComponent, PowerChangedEvent>(OnPowerChanged);
    }

    private void OnInit(Entity<NCAVHolographicProjectorComponent> ent, ref ComponentInit args)
    {
        SetEnabled(ent, false);
    }

    private void OnShutdown(Entity<NCAVHolographicProjectorComponent> ent, ref ComponentShutdown args)
    {
        DespawnProjection(ent);
    }

    private void OnSignal(Entity<NCAVHolographicProjectorComponent> ent, ref SignalReceivedEvent args)
    {
        switch (args.Port)
        {
            case "On":
                SetEnabled(ent, true);
                break;
            case "Off":
                SetEnabled(ent, false);
                break;
            case "Toggle":
                SetEnabled(ent, !ent.Comp.Enabled);
                break;
        }
    }

    private void OnPowerChanged(Entity<NCAVHolographicProjectorComponent> ent, ref PowerChangedEvent args)
    {
        if (!args.Powered)
            SetEnabled(ent, false);
    }

    private void SetEnabled(Entity<NCAVHolographicProjectorComponent> ent, bool enabled)
    {
        if (ent.Comp.Enabled == enabled)
            return;

        ent.Comp.Enabled = enabled;

        if (TryComp<PointLightComponent>(ent, out var light))
            _light.SetEnabled(ent, enabled, light);

        if (enabled)
            SpawnProjection(ent);
        else
            DespawnProjection(ent);
    }

    private void SpawnProjection(Entity<NCAVHolographicProjectorComponent> ent)
    {
        if (ent.Comp.ProjectionEntity is { Valid: true })
            return;

        var xform = Transform(ent);
        var projection = Spawn(ent.Comp.Projection, xform.Coordinates);
        _transform.SetWorldRotation(projection, _transform.GetWorldRotation(xform));

        ent.Comp.ProjectionEntity = projection;
    }

    private void DespawnProjection(Entity<NCAVHolographicProjectorComponent> ent)
    {
        if (ent.Comp.ProjectionEntity is not { Valid: true } projection)
        {
            ent.Comp.ProjectionEntity = null;
            return;
        }

        QueueDel(projection);
        ent.Comp.ProjectionEntity = null;
    }
}
