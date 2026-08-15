// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Physiology;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Anatomy;

/// <summary>
/// Owns runtime invalidation and revision changes for an authoritative medical body.
/// Medical subsystems use this API instead of writing another component's cache fields directly.
/// </summary>
public sealed partial class NCMedicalBodySystem : EntitySystem
{
    /// <summary>
    /// Applies versioned persistence metadata through the component's declared shared-system owner.
    /// Detailed medical records remain private server state and are restored by their subsystem owners.
    /// </summary>
    public bool TryRestoreSnapshotMetadata(
        Entity<NCMedicalBodyComponent?> body,
        ProtoId<NCAnatomyProfilePrototype> profile,
        ushort snapshotVersion)
    {
        if (!Resolve(body, ref body.Comp, false))
            return false;

        body.Comp.Profile = profile;
        body.Comp.SnapshotVersion = snapshotVersion;
        Dirty(body);
        return true;
    }

    public bool TryMarkDirty(
        Entity<NCMedicalBodyComponent?> body,
        NCMedicalDirtyDomains domains,
        bool raiseEvent = true)
    {
        if (domains == NCMedicalDirtyDomains.None || !Resolve(body, ref body.Comp, false))
            return false;

        body.Comp.DirtyDomains |= domains;
        unchecked
        {
            body.Comp.MedicalRevision++;
        }

        if (raiseEvent)
        {
            var changed = new NCMedicalBodyChangedEvent(domains);
            RaiseLocalEvent(body.Owner, ref changed);
        }

        return true;
    }

    public bool TryClearDirty(
        Entity<NCMedicalBodyComponent?> body,
        NCMedicalDirtyDomains domains)
    {
        if (domains == NCMedicalDirtyDomains.None || !Resolve(body, ref body.Comp, false))
            return false;

        body.Comp.DirtyDomains &= ~domains;
        return true;
    }
}
