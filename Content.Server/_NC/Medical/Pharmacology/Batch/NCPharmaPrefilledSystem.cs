// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// A factory-filled pack: it makes its own batch of a known formula when it is created, so it can be spawned,
/// placed on a map or put in a catalogue and still behave like any other batch pack. Every such pack is a real
/// batch with a real quality, a QC history and an audit trail.
/// </summary>
[RegisterComponent, Access(typeof(NCPharmaPrefilledSystem))]
public sealed partial class NCPharmaPrefilledComponent : Component
{
    [DataField(required: true)]
    public string Formula = string.Empty;

    [DataField]
    public ProtoId<NCPharmaProductionProfilePrototype> Profile = "NCPharmaProductionStandard";

    [DataField]
    public int Doses = 1;

    /// <summary>Quality of a factory line. These are the inputs of the batch, not its result.</summary>
    [DataField]
    public float InputPurity = 0.97f;

    [DataField]
    public float Sterility = 0.995f;

    [DataField]
    public float Mixing = 0.98f;
}

public sealed class NCPharmaPrefilledSystem : EntitySystem
{
    [Dependency] private NCPharmaBatchSystem _batches = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCPharmaPrefilledComponent, MapInitEvent>(OnMapInit);
        // A world restore replaces the batch registry; packs that were made before it get their batch again.
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(
            OnSessionStarted,
            after: [typeof(NCPharmaBatchSystem)]);
    }

    private void OnMapInit(Entity<NCPharmaPrefilledComponent> pack, ref MapInitEvent args)
    {
        Ensure(pack);
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        var query = EntityQueryEnumerator<NCPharmaPrefilledComponent>();
        while (query.MoveNext(out var uid, out var prefilled))
            Ensure((uid, prefilled));
    }

    /// <summary>Makes the pack's batch if it has none yet. Safe to call again.</summary>
    private void Ensure(Entity<NCPharmaPrefilledComponent> pack)
    {
        if (!TryComp<NCPharmaContainerComponent>(pack, out var container))
            return;

        // The pack already points at a batch that exists: nothing to do.
        if (container.BatchId != Guid.Empty && _batches.TryGetBatch(container.BatchId, out _))
            return;

        if (!_prototypes.TryIndex(pack.Comp.Profile, out var profile) || profile.YieldMultiplier <= 0f)
            return;

        var units = (int) MathF.Ceiling(pack.Comp.Doses / profile.YieldMultiplier) + 1;
        var inputs = new NCPharmaProductionInputs(units, pack.Comp.InputPurity, pack.Comp.Sterility, pack.Comp.Mixing, 0f);
        if (!_batches.TryCreateBatch(pack.Comp.Formula, pack.Comp.Profile, inputs, "factory", "factory line", out var batch))
        {
            Log.Error($"Prefilled pack {ToPrettyString(pack)} could not make a batch of {pack.Comp.Formula}.");
            return;
        }

        _batches.TryBindContainer(pack, batch.BatchId, pack.Comp.Doses, "factory");
    }
}
