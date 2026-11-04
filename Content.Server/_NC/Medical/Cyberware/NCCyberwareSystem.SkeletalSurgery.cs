// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Cyberware;

public sealed partial class NCCyberwareSystem
{
    [Dependency] private NCAnatomySystem _skeletalAnatomy = default!;

    private bool SkeletalSurgeryReady(EntityUid uid, NCCyberwareComponent module, NCCyberwarePrototype profile)
    {
        if (profile.SkeletalSurgery.Count == 0) return true;
        if (!NCSkeletalSurgeryPlan.Complete(profile.SkeletalSurgery, module.SkeletalSurgeryCompleted) ||
            !TryResolveOwningBody(uid, out var body)) return false;
        foreach (var step in profile.SkeletalSurgery)
            if (!_skeletalAnatomy.TryGetOrgan(body, step.Structure, out _)) return false;
        return true;
    }

    /// <summary>Regional contact points are views of the same physical implant, never extra implanted items.</summary>
    public bool TryGetSkeletalSurgeryTarget(EntityUid uid, ProtoId<NCTargetZonePrototype> zone,
        out NCFreeformSurgeryStructureDefinition definition, out string name)
    {
        definition = default!;
        name = string.Empty;
        if (!TryComp<NCCyberwareComponent>(uid, out var module) || module.InstalledSlot == null ||
            !TryGetProfile(module.Profile, out var profile))
            return false;
        var local = profile.SkeletalSurgery.Where(step => step.Zone == zone).ToArray();
        if (local.Length == 0) return false;
        var next = local.FirstOrDefault(step => (module.SkeletalSurgeryCompleted & step.Stage) == 0);
        var depth = next?.MinimumDepth ?? local.Max(step => step.MinimumDepth);
        // The two independent graft sets must remain separately clickable in every operative region.
        var x = _prototypes.Index(module.InstalledSlot.Value).RegionalSurgeryPositionX;
        definition = new NCFreeformSurgeryStructureDefinition
        {
            Position = new Vector2(x, .5f),
            Size = new Vector2(.2f, .28f),
            MinimumDepth = depth,
            Visual = NCFreeformSurgeryStructureVisual.Cyberware,
            Zones = [zone],
        };
        var title = profile.NameId is { } key ? Loc.GetString(key) : profile.Name;
        name = Loc.GetString("nc-skeletal-surgery-target", ("implant", title),
            ("step", next == null ? Loc.GetString("nc-skeletal-surgery-local-complete") : Loc.GetString(next.Name)));
        return true;
    }

    /// <summary>The surgical field validates the held tool, local access, depth, barrier and DoAfter contact.</summary>
    public bool TryPerformSkeletalSurgery(Entity<NCCyberwareComponent?> entity,
        ProtoId<NCTargetZonePrototype> zone, NCFreeformSurgeryAction action, NCActionResolutionId resolution,
        out NCFreeformSurgeryFailure failure)
    {
        failure = NCFreeformSurgeryFailure.InvalidInput;
        if (!resolution.IsValid) return false;
        if (!Resolve(entity, ref entity.Comp, false) ||
            !CanPerformSkeletalSurgery(entity, zone, action, out var step, out failure))
            return false;
        var module = entity.Comp!;
        if (!TryResolveOwningBody(entity, out var body)) return false;
        var key = new NCCyberwareProcessedActionKey(resolution.Value, NCCyberwareActionKind.SkeletalSurgery, 0, 0);
        if (IsDuplicate(body, key)) return true;
        TryAdvanceBodyPower(body, _timing.CurTime);
        module.SkeletalSurgeryCompleted |= step.Stage;
        InvalidateCalibration(entity);
        Remember(body, key);
        RefreshBodyPower(body, _timing.CurTime);
        MarkBodyChanged(body);
        return true;
    }

    public bool CanPerformSkeletalSurgery(Entity<NCCyberwareComponent?> entity,
        ProtoId<NCTargetZonePrototype> zone, NCFreeformSurgeryAction action,
        out NCSkeletalSurgeryStep step, out NCFreeformSurgeryFailure failure)
    {
        step = default!;
        failure = NCFreeformSurgeryFailure.NoApplicableTarget;
        if (!Resolve(entity, ref entity.Comp, false) || entity.Comp.InstalledSlot == null ||
            !TryGetProfile(entity.Comp.Profile, out var profile) || !TryResolveOwningBody(entity, out var body))
            return false;
        var completed = entity.Comp.SkeletalSurgeryCompleted;
        var pending = profile.SkeletalSurgery.FirstOrDefault(value => value.Zone == zone && (completed & value.Stage) == 0);
        if (pending == null || pending.Action != action) return false;
        step = pending;
        failure = NCFreeformSurgeryFailure.SkeletalPrerequisitesMissing;
        if ((completed & step.Prerequisites) != step.Prerequisites) return false;
        if (profile.RequiredSupportProfile != null && !entity.Comp.SupportReady) return false;
        failure = NCFreeformSurgeryFailure.SkeletalStructureDamaged;
        if (!_skeletalAnatomy.TryGetOrgan(body, step.Structure, out var structure) ||
            !TryComp<NCOrganIntegrityComponent>(structure, out var integrity) ||
            integrity.CurrentIntegrity < integrity.MaxIntegrity * .5f ||
            TryComp<NCBoneComponent>(structure, out var bone) && (bone.RequiresFixation || bone.Displacement > 0f))
            return false;
        failure = NCFreeformSurgeryFailure.None;
        return true;
    }

    public bool TryTestSkeletalLoad(Entity<NCCyberwareComponent?> entity, NCActionResolutionId resolution,
        out NCFreeformSurgeryFailure failure)
    {
        failure = NCFreeformSurgeryFailure.NoApplicableTarget;
        if (!resolution.IsValid || !Resolve(entity, ref entity.Comp, false) ||
            !TryGetProfile(entity.Comp.Profile, out var profile) || !profile.RequiresSkeletalLoadTest ||
            !TryResolveOwningBody(entity, out var body)) return false;
        var key = new NCCyberwareProcessedActionKey(resolution.Value, NCCyberwareActionKind.SkeletalLoadTest, 0, 0);
        if (IsDuplicate(body, key)) { failure = NCFreeformSurgeryFailure.None; return true; }
        failure = NCFreeformSurgeryFailure.CyberwareCalibrationRequired;
        if (!entity.Comp.Calibrated || entity.Comp.SkeletalLoadTestPassed) return false;
        TryAdvanceBodyPower(body, _timing.CurTime);
        // Reuse the real full-load bus allocation and signal/condition checks; a button cannot supply power.
        if (!CanCalibrateCyberware(entity, out failure, out var source)) return false;
        if (source is not { } battery || !_battery.TryUseCharge(battery.AsNullable(), profile.CalibrationEnergy))
        { failure = NCFreeformSurgeryFailure.CyberwareNoPower; return false; }
        entity.Comp.SkeletalLoadTestPassed = true;
        Remember(body, key);
        RefreshBodyPower(body, _timing.CurTime);
        MarkBodyChanged(body);
        return true;
    }

    private void InvalidateSkeletalConnections(EntityUid body, EntityUid removed)
    {
        if (!TryComp<OrganComponent>(removed, out var organ) || organ.Category is not { } category) return;
        foreach (var uid in CollectCyberware(body))
        {
            var module = Comp<NCCyberwareComponent>(uid);
            var profile = _prototypes.Index(module.Profile);
            var before = module.SkeletalSurgeryCompleted;
            foreach (var step in profile.SkeletalSurgery)
            {
                if (step.Structure == category ||
                    _prototypes.Index(step.Zone).MedicalRegionCategory == category)
                    module.SkeletalSurgeryCompleted &= ~step.Stage;
            }
            module.SkeletalSurgeryCompleted = NCSkeletalSurgeryPlan.Prune(profile.SkeletalSurgery, module.SkeletalSurgeryCompleted);
            if (before != module.SkeletalSurgeryCompleted) InvalidateCalibration(uid);
        }
    }
}
