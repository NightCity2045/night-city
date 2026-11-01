// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Cyberware.Borg;

public enum NCBorgTransferKind : byte { Convert, Dock, Undock }

[Serializable, NetSerializable]
public sealed partial class NCBorgTransferDoAfterEvent : DoAfterEvent
{
    public Guid TransferId;
    public NCBorgTransferKind Kind;
    public NetEntity ExpectedOrgan;
    public override DoAfterEvent Clone() => (NCBorgTransferDoAfterEvent) MemberwiseClone();
}

[RegisterComponent, UnsavedComponent]
public sealed partial class NCBorgPendingTransferComponent : Component
{
    public Guid TransferId;
}
