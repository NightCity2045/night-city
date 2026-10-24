// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Borg;
using Content.Shared._NC.Medical.Humanity;
using Content.Shared._NC.Medical.Persistence;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Humanity;

public sealed partial class NCHumanitySystem
{
    [Dependency] private IComponentFactory _componentFactory = default!;

    public bool TryPrepareLegacyState(EntityUid body, NCMedicalStateSnapshot snapshot)
    {
        if (!snapshot.Organs.ContainsKey("Brain")) return snapshot.Humanity == null;
        if (!TryCollectSnapshotPackages(body, snapshot, out var packages)) return false;
        // An explicitly loaded old save cannot refill an already known live character's H/S either.
        var state = TryGetBrain(body, out var brain) ? NCHumanityMath.Clone(brain.Comp.State) : new NCHumanityStateSnapshot();
        if (!NCHumanityMath.TryReconcile(state, packages, creditExisting: true)) return false;
        snapshot.Humanity = state;
        return true;
    }

    public bool ValidatePersistentState(EntityUid body, NCMedicalStateSnapshot snapshot)
    {
        if (!snapshot.Organs.ContainsKey("Brain")) return snapshot.Humanity == null;
        if (snapshot.Humanity is not { } state || !NCHumanityMath.Validate(state) ||
            !TryCollectSnapshotPackages(body, snapshot, out var packages)) return false;
        var burden = 0f;
        foreach (var (key, package) in packages)
        {
            burden += package.Burden;
            if (!state.Adaptation.TryGetValue(key, out var adapted) || adapted < package.AcuteCost) return false;
        }
        return MathF.Abs(burden - state.Burden) < 0.0001f;
    }

    private bool TryCollectSnapshotPackages(EntityUid body, NCMedicalStateSnapshot snapshot,
        out Dictionary<string, NCHumanityPackageDose> packages)
    {
        packages = new(StringComparer.Ordinal);
        if (snapshot.BorgCore != null && TryComp<NCBorgCoreComponent>(body, out var core) &&
            !TryAddPackages(core.HumanityPackages, "core", packages, out _)) return false;
        foreach (var (category, organ) in snapshot.Organs)
            if (organ.Cyberware is { } module && !TryAddSnapshotModule(module, category.Id, packages)) return false;
        foreach (var module in snapshot.CyberwareModules)
            if (module.HostCategory is not { } category || !TryAddSnapshotModule(module, category.Id, packages)) return false;
        return packages.Count <= 256;
    }

    private bool TryAddSnapshotModule(NCCyberwareStateSnapshot module, string context,
        Dictionary<string, NCHumanityPackageDose> packages)
    {
        if (!_prototypes.TryIndex(module.Profile, out var definition) ||
            !TryAddPackages(definition.HumanityPackages, context, packages, out _)) return false;
        if (module.Assembly == null) return true;
        foreach (var part in module.Assembly.Parts)
        {
            if (!_prototypes.TryIndex<EntityPrototype>(part.EntityPrototype, out var entity) ||
                !entity.TryComp<NCCyberwarePartComponent>(out var hardware, _componentFactory) ||
                !_prototypes.TryIndex(hardware.Profile, out var partDefinition) ||
                !TryAddPackages(partDefinition.HumanityPackages, context, packages, out _)) return false;
        }
        return true;
    }

    /// <summary>Read-only legacy actor records are imported once; the medical graph becomes the sole writer.</summary>
    public bool TryImportLegacyState(EntityUid body, NCHumanitySnapshot legacy)
    {
        if (legacy.MaximumHumanity is <= 0 or > 100 || legacy.CurrentHumanity < 0 ||
            legacy.CurrentHumanity > legacy.MaximumHumanity || legacy.PermanentCyberwareLoss < 0 ||
            !TryGetBrain(body, out var brain) || !TryReconcile(body, creditExisting: true)) return false;
        brain.Comp.State.Current = MathF.Min(brain.Comp.State.Current, legacy.CurrentHumanity);
        MarkChanged(body);
        return true;
    }
}
