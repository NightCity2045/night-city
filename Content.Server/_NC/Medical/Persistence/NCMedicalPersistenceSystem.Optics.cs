// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Medical.Persistence;
using Content.Shared.Body;

namespace Content.Server._NC.Medical.Persistence;

public sealed partial class NCMedicalPersistenceSystem
{
    private const string LeftEyeCategory = "Eyes";
    private const string RightEyeCategory = "EyesRight";
    private const string RightEyeZone = "NCZoneEyesRight";
    private const string PairEyeZone = "NCZoneEyes";

    /// <summary>
    /// Merges the short-lived left/right eye split back into one paired eye organ. A cyber side wins over a
    /// natural one, damage and necrosis keep the worse side, and a missing pair stays missing. Records that
    /// pointed at the right eye are retargeted so no wound, scar or incision is orphaned.
    /// </summary>
    private bool PrepareOpticMigration(InitialBodyComponent initial, ushort sourceVersion, NCMedicalStateSnapshot snapshot)
    {
        // A body that still defines a separate right eye (for example a borg carrier) keeps both organs.
        var initialOrgans = initial.Organs;
        if (initialOrgans.ContainsKey(RightEyeCategory))
            return true;

        var hadRight = snapshot.Organs.Remove(RightEyeCategory, out var right);
        var rightMissing = snapshot.MissingOrganCategories.Remove(RightEyeCategory);
        snapshot.Organs.TryGetValue(LeftEyeCategory, out var left);
        var leftMissing = snapshot.MissingOrganCategories.Contains(LeftEyeCategory);

        if (left != null && right != null)
        {
            var survivor = IsCyberOrgan(left) || !IsCyberOrgan(right) ? left : right;
            var other = ReferenceEquals(survivor, left) ? right : left;
            MergeEyeStates(survivor, other);
            snapshot.Organs[LeftEyeCategory] = survivor;
        }
        else if (left == null && right != null)
        {
            // Only the former right eye is left: it becomes the pair.
            snapshot.MissingOrganCategories.Remove(LeftEyeCategory);
            snapshot.Organs[LeftEyeCategory] = right;
            NormalizePairedEye(right);
        }
        else if (left != null)
        {
            NormalizePairedEye(left);
        }
        else if (rightMissing && !leftMissing)
        {
            snapshot.MissingOrganCategories.Add(LeftEyeCategory);
        }

        RetargetRightEyeReferences(snapshot);
        return true;
    }

    private static bool IsCyberOrgan(NCOrganMedicalStateSnapshot organ) =>
        organ.Cyberware != null || organ.CyberwareIdentity.Length != 0;

    /// <summary>Maps a left/right-eye prototype id to its paired equivalent; unrelated ids pass through.</summary>
    private static string PairPrototype(string id)
    {
        id = id.Replace("NCCyberEyeLeft", "NCCyberEyes").Replace("NCCyberEyeRight", "NCCyberEyes");
        if (id.EndsWith("Right", StringComparison.Ordinal) && (id.Contains("Eyes") || id.Contains("Optics")))
            id = id[..^"Right".Length];
        return id;
    }

    private static void NormalizePairedEye(NCOrganMedicalStateSnapshot eye)
    {
        eye.EntityPrototype = PairPrototype(eye.EntityPrototype);
        if (eye.CyberwareIdentity.Length != 0)
            eye.CyberwareIdentity = PairPrototype(eye.CyberwareIdentity);
        if (eye.Cyberware is { } cyberware)
        {
            cyberware.EntityPrototype = PairPrototype(cyberware.EntityPrototype);
            cyberware.HostCategory = null;
        }

        if (eye.Function is { } function)
        {
            var ratio = function.BaseCapacity > 0f ? function.CurrentCapacity / function.BaseCapacity : 1f;
            function.BaseCapacity = 1f;
            function.CurrentCapacity = Math.Clamp(ratio, 0f, 1f);
        }
    }

    private static void MergeEyeStates(NCOrganMedicalStateSnapshot survivor, NCOrganMedicalStateSnapshot other)
    {
        var survivorRatio = CapacityRatio(survivor);
        var otherRatio = CapacityRatio(other);
        NormalizePairedEye(survivor);
        if (survivor.Function is { } function)
            function.CurrentCapacity = Math.Clamp(MathF.Min(survivorRatio, otherRatio), 0f, 1f);

        if (survivor.Integrity is { } integrity && other.Integrity is { } otherIntegrity)
        {
            integrity.CurrentIntegrity = MathF.Min(integrity.CurrentIntegrity, otherIntegrity.CurrentIntegrity);
            integrity.FunctionalEfficiency = MathF.Min(integrity.FunctionalEfficiency, otherIntegrity.FunctionalEfficiency);
            integrity.Necrosis = MathF.Max(integrity.Necrosis, otherIntegrity.Necrosis);
        }

        if (other.SurgicalSite is { } otherSite)
        {
            survivor.SurgicalSite ??= new NCSurgicalSiteStateSnapshot { Contamination = otherSite.Contamination };
            survivor.SurgicalSite.Contamination = MathF.Max(survivor.SurgicalSite.Contamination, otherSite.Contamination);
            survivor.SurgicalSite.Incisions.AddRange(otherSite.Incisions);
        }
    }

    private static float CapacityRatio(NCOrganMedicalStateSnapshot organ) =>
        organ.Function is { BaseCapacity: > 0f } function ? function.CurrentCapacity / function.BaseCapacity : 1f;

    private static void RetargetRightEyeReferences(NCMedicalStateSnapshot snapshot)
    {
        foreach (var organ in snapshot.Organs.Values)
        {
            if (organ.SurgicalSite == null)
                continue;
            foreach (var incision in organ.SurgicalSite.Incisions)
            {
                if (incision.TargetZone == RightEyeZone)
                    incision.TargetZone = PairEyeZone;
            }
        }

        if (snapshot.Wounds is { } wounds)
        {
            foreach (var wound in wounds.Records.Values)
            {
                if (wound.TargetZone == RightEyeZone)
                    wound.TargetZone = PairEyeZone;
                if (wound.RegionCategory == RightEyeCategory)
                    wound.RegionCategory = LeftEyeCategory;
            }

            foreach (var scar in wounds.Scars)
            {
                if (scar.TargetZone == RightEyeZone)
                    scar.TargetZone = PairEyeZone;
                if (scar.RegionCategory == RightEyeCategory)
                    scar.RegionCategory = LeftEyeCategory;
            }

            if (wounds.OverflowTraumaByZone.Remove(RightEyeZone, out var overflow))
            {
                wounds.OverflowTraumaByZone.TryGetValue(PairEyeZone, out var existing);
                wounds.OverflowTraumaByZone[PairEyeZone] = existing + overflow;
            }
        }

        if (snapshot.Bleeding is { } bleeding)
        {
            foreach (var source in bleeding.Sources)
            {
                if (source.TargetZone == RightEyeZone)
                    source.TargetZone = PairEyeZone;
                if (source.RegionCategory == RightEyeCategory)
                    source.RegionCategory = LeftEyeCategory;
            }

            if (bleeding.OverflowInternalRatesByRegion.Remove(RightEyeCategory, out var rate))
            {
                bleeding.OverflowInternalRatesByRegion.TryGetValue(LeftEyeCategory, out var existing);
                bleeding.OverflowInternalRatesByRegion[LeftEyeCategory] = existing + rate;
            }
        }

        foreach (var module in snapshot.CyberwareModules)
        {
            if (module.HostCategory == RightEyeCategory)
                module.HostCategory = LeftEyeCategory;
        }
    }
}
