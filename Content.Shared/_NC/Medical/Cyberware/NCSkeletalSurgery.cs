// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared.Body;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Cyberware;

[Flags, Serializable, NetSerializable]
public enum NCSkeletalSurgeryStage : ushort
{
    None = 0,
    Torso = 1,
    Pelvis = 2,
    TorsoReinforcement = 4,
    PelvisReinforcement = 8,
    ArmLeft = 16,
    ArmRight = 32,
    LegLeft = 64,
    LegRight = 128,
    DermalChest = 256,
    DermalAbdomen = 512,
    DermalGroin = 1024,
    DermalFixation = 2048,
    DermalCheck = 4096,
    MedicalFixation = 8192,
    MedicalConnection = 16384,
    MedicalCheck = 32768,
}

/// <summary>One local intervention on a real attached anatomical structure, through its own operative access.</summary>
[DataDefinition]
public sealed partial class NCSkeletalSurgeryStep
{
    [DataField(required: true)] public NCSkeletalSurgeryStage Stage;
    [DataField(required: true)] public LocId Name;
    [DataField(required: true)] public ProtoId<NCTargetZonePrototype> Zone;
    [DataField(required: true)] public ProtoId<OrganCategoryPrototype> Structure;
    [DataField(required: true)] public NCFreeformSurgeryAction Action;
    [DataField] public NCSkeletalSurgeryStage Prerequisites;
    [DataField] public float MinimumDepth = 0.55f;
}

/// <summary>Shared plan validation and prerequisite closure; never grants client authority to complete a step.</summary>
public static class NCSkeletalSurgeryPlan
{
    public static NCSkeletalSurgeryStage Required(IReadOnlyList<NCSkeletalSurgeryStep> steps) =>
        steps.Aggregate(NCSkeletalSurgeryStage.None, (mask, step) => mask | step.Stage);

    public static bool Complete(IReadOnlyList<NCSkeletalSurgeryStep> steps, NCSkeletalSurgeryStage completed) =>
        (completed & Required(steps)) == Required(steps);

    public static bool Valid(IReadOnlyList<NCSkeletalSurgeryStep> steps, NCSkeletalSurgeryStage completed)
    {
        if ((completed & ~Required(steps)) != 0) return false;
        return steps.All(step => (completed & step.Stage) == 0 ||
            (completed & step.Prerequisites) == step.Prerequisites);
    }

    public static bool ValidDefinition(IReadOnlyList<NCSkeletalSurgeryStep> steps)
    {
        var seen = NCSkeletalSurgeryStage.None;
        foreach (var step in steps)
        {
            var bit = (ushort) step.Stage;
            if (bit == 0 || (bit & (bit - 1)) != 0 || (seen & step.Stage) != 0 ||
                (seen & step.Prerequisites) != step.Prerequisites ||
                !float.IsFinite(step.MinimumDepth) || step.MinimumDepth < 0 || step.MinimumDepth > 1 ||
                step.Action is not (NCFreeformSurgeryAction.AnchorSkeletal or NCFreeformSurgeryAction.ConnectSkeletal or
                    NCFreeformSurgeryAction.DistributeDermal or NCFreeformSurgeryAction.FixDermal or NCFreeformSurgeryAction.CheckDermal or NCFreeformSurgeryAction.FixMedical or
                    NCFreeformSurgeryAction.ConnectMedical or NCFreeformSurgeryAction.CheckMedical))
                return false;
            seen |= step.Stage;
        }
        return true;
    }

    public static NCSkeletalSurgeryStage Prune(IReadOnlyList<NCSkeletalSurgeryStep> steps, NCSkeletalSurgeryStage completed)
    {
        completed &= Required(steps);
        foreach (var step in steps)
            if ((completed & step.Prerequisites) != step.Prerequisites)
                completed &= ~step.Stage;
        return completed;
    }
}
