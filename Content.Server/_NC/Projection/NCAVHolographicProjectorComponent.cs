using Robust.Shared.Prototypes;

namespace Content.Server._NC.Projection.NCAVHolographicProjector;

[RegisterComponent]
public sealed partial class NCAVHolographicProjectorComponent : Component
{
    /// <summary>
    /// Сущность, которая спавнится, пока проектор включён.
    /// </summary>
    [DataField(required: true)]
    public EntProtoId Projection;

    /// <summary>
    /// Заспавненная проекция (внутреннее состояние).
    /// </summary>
    [ViewVariables]
    public EntityUid? ProjectionEntity;

    /// <summary>
    /// Текущее состояние проектора. Синхронизируется с <see cref="PointLightComponent.Enabled"/>.
    /// </summary>
    [ViewVariables]
    public bool Enabled;
}
