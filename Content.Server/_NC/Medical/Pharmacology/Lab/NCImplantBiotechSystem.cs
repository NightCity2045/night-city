// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Pharmacology.Batch;
using Content.Server._NC.Medical.Systemic;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Pharmacology.Lab;

public enum NCImplantConsumableKind : byte
{
    /// <summary>Biocompatible coating: the body accepts the implant's surface.</summary>
    Coating,

    /// <summary>Antibacterial material: the implant carries no infection into the surgical site.</summary>
    Antibacterial,

    /// <summary>Tissue interface: the tissue grows into the implant, so it is accepted in less time.</summary>
    TissueInterface,

    /// <summary>Neuroconductive gel: restores the implant's neural contact.</summary>
    NeuralGel,

    /// <summary>Regenerative matrix: the tissue around the implant heals faster, so tolerance is quicker.</summary>
    Matrix,
}

/// <summary>A biotech consumable for implants. It is used on a module before it is installed.</summary>
[RegisterComponent]
public sealed partial class NCImplantConsumableComponent : Component
{
    [DataField(required: true)]
    public NCImplantConsumableKind Kind;

    [DataField]
    public float Amount = 0.3f;
}

/// <summary>
/// How well a module sits with living tissue. A module nobody prepared has an average fit; biotech products
/// improve it, and it decides how rough the body's first minutes with the implant are.
/// </summary>
[RegisterComponent]
public sealed partial class NCImplantBiocompatibilityComponent : Component
{
    /// <summary>Fit of the implant surface with tissue, 0..1. Raised by coatings and interfaces.</summary>
    [DataField]
    public float Fit = 0.5f;

    /// <summary>How clean the implant is, 0..1. Raised by antibacterial material.</summary>
    [DataField]
    public float Cleanliness = 0.5f;

    /// <summary>Share of the tolerance time that a matrix or interface saves.</summary>
    [DataField]
    public float ToleranceSaved;
}

/// <summary>Present on an installed module for as long as the body is still getting used to it.</summary>
[RegisterComponent, Access(typeof(NCImplantBiotechSystem))]
public sealed partial class NCImplantToleranceComponent : Component
{
    [DataField]
    public TimeSpan Remaining;

    [DataField]
    public TimeSpan Total;

    /// <summary>Strength of the reaction at the start, 0..1.</summary>
    [DataField]
    public float Intensity;

    /// <summary>Infection pressure of the implant, 0..1.</summary>
    [DataField]
    public float Contamination;
}

public sealed class NCImplantBiotechSystem : EntitySystem
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);

    /// <summary>Longest time a body needs to get used to an implant that nobody prepared.</summary>
    private static readonly TimeSpan BaseTolerance = TimeSpan.FromMinutes(10);

    /// <summary>Burden per second at full intensity, applied through the toxin channel.</summary>
    private const float InflammationPerSecond = 0.02f;
    private const float InfectionPerSecond = 0.01f;

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private NCSystemicSystem _systemic = default!;
    [Dependency] private NCPharmaBatchSystem _batches = default!;
    [Dependency] private Content.Server._NC.Medical.Pharmacology.NCPharmacologySystem _pharmacology = default!;

    private TimeSpan _nextTick;
    private TimeSpan _lastTick;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCCyberwareComponent, InteractUsingEvent>(OnModuleUsing);
        SubscribeLocalEvent<NCMedicalBodyComponent, NCCyberwareInstalledEvent>(OnInstalled);
    }

    /// <summary>
    /// What the person about to install a module sees of its preparation. Nothing once it is in a body.
    /// An unprepared module says so, because it will give the patient a rough first minutes.
    /// </summary>
    public string? ExamineLine(EntityUid module)
    {
        if (FindBody(module) != null)
            return null;

        return TryComp<NCImplantBiocompatibilityComponent>(module, out var fit)
            ? Loc.GetString("nc-implant-biotech-examine", ("fit", fit.Fit.ToString("P0")), ("clean", fit.Cleanliness.ToString("P0")))
            : Loc.GetString("nc-implant-biotech-unprepared");
    }

    private void OnModuleUsing(Entity<NCCyberwareComponent> module, ref InteractUsingEvent args)
    {
        if (args.Handled || !TryComp<NCImplantConsumableComponent>(args.Used, out var consumable))
            return;

        args.Handled = true;
        // Biotech products are for a module on the bench: once it is installed, the tissue is already around it.
        if (HasComp<NCImplantToleranceComponent>(module) || IsInstalled(module))
        {
            _popup.PopupEntity(Loc.GetString("nc-implant-biotech-installed"), module, args.User);
            return;
        }

        var fit = EnsureComp<NCImplantBiocompatibilityComponent>(module);
        switch (consumable.Kind)
        {
            case NCImplantConsumableKind.Coating:
                fit.Fit = Math.Clamp(fit.Fit + consumable.Amount, 0f, 1f);
                break;
            case NCImplantConsumableKind.Antibacterial:
                fit.Cleanliness = Math.Clamp(fit.Cleanliness + consumable.Amount, 0f, 1f);
                break;
            case NCImplantConsumableKind.TissueInterface:
                fit.Fit = Math.Clamp(fit.Fit + consumable.Amount * 0.5f, 0f, 1f);
                fit.ToleranceSaved = Math.Clamp(fit.ToleranceSaved + consumable.Amount, 0f, 0.8f);
                break;
            case NCImplantConsumableKind.Matrix:
                fit.ToleranceSaved = Math.Clamp(fit.ToleranceSaved + consumable.Amount, 0f, 0.8f);
                break;
            case NCImplantConsumableKind.NeuralGel:
                module.Comp.NeuralIntegrity = Math.Clamp(module.Comp.NeuralIntegrity + consumable.Amount, 0f, 1f);
                break;
        }

        QueueDel(args.Used);
        _popup.PopupEntity(Loc.GetString("nc-implant-biotech-applied"), module, args.User);
    }

    private bool IsInstalled(Entity<NCCyberwareComponent> module) => FindBody(module.Owner) != null;

    /// <summary>The body a module sits in, found by walking up through the organ and container it is in.</summary>
    private EntityUid? FindBody(EntityUid module)
    {
        var current = module;
        for (var depth = 0; depth < 12; depth++)
        {
            var parent = Transform(current).ParentUid;
            if (!parent.IsValid())
                return null;
            if (HasComp<NCMedicalBodyComponent>(parent))
                return parent;
            current = parent;
        }

        return null;
    }

    /// <summary>A freshly installed module starts a tolerance period set by how well it was prepared.</summary>
    private void OnInstalled(Entity<NCMedicalBodyComponent> body, ref NCCyberwareInstalledEvent args)
    {
        if (!HasComp<NCCyberwareComponent>(args.Module))
            return;

        var fit = CompOrNull<NCImplantBiocompatibilityComponent>(args.Module);
        var tissue = fit?.Fit ?? 0.5f;
        var clean = fit?.Cleanliness ?? 0.5f;
        var saved = fit?.ToleranceSaved ?? 0f;
        var total = BaseTolerance * (1f - saved);
        var tolerance = EnsureComp<NCImplantToleranceComponent>(args.Module);
        tolerance.Total = total;
        tolerance.Remaining = total;
        tolerance.Intensity = Math.Clamp((1f - tissue) * 0.5f, 0f, 1f);
        tolerance.Contamination = Math.Clamp((1f - clean) * 0.5f, 0f, 1f);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        if (now < _nextTick)
            return;

        var elapsed = _lastTick == TimeSpan.Zero ? TickInterval : now - _lastTick;
        _lastTick = now;
        _nextTick = now + TickInterval;
        var seconds = (float) Math.Min(elapsed.TotalSeconds, 60d);
        if (!_batches.TryGetTuning(out var tuning))
            return;

        var query = EntityQueryEnumerator<NCImplantToleranceComponent>();
        while (query.MoveNext(out var uid, out var tolerance))
        {
            if (FindBody(uid) is not { } body)
                continue;

            // The reaction fades as the body gets used to the implant.
            var left = tolerance.Total > TimeSpan.Zero
                ? Math.Clamp((float) (tolerance.Remaining.TotalSeconds / tolerance.Total.TotalSeconds), 0f, 1f)
                : 0f;
            var suppression = Math.Clamp(_pharmacology.GetModifiers(body).Immunosuppression, 0f, 1f);
            var inflammation = tolerance.Intensity * left * InflammationPerSecond * (1f - suppression * 0.8f) * seconds;
            var infection = tolerance.Contamination * left * InfectionPerSecond * (1f + suppression) * seconds;
            if (inflammation > 0f)
                _systemic.Apply(body, tuning.ToxinProfile, inflammation);
            if (infection > 0f)
                _systemic.Apply(body, tuning.SepsisProfile, infection);

            tolerance.Remaining -= TimeSpan.FromSeconds(seconds);
            if (tolerance.Remaining <= TimeSpan.Zero)
                RemCompDeferred<NCImplantToleranceComponent>(uid);
        }
    }
}
