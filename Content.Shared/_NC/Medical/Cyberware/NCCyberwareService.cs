// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Cyberware;

[Prototype("ncCyberwareFirmware")]
public sealed partial class NCCyberwareFirmwarePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public LocId Name { get; private set; }
    [DataField] public bool InhibitActiveModes { get; private set; }
    [DataField] public List<ProtoId<NCCyberwarePrototype>> CompatibleProfiles { get; private set; } = [];
}

[RegisterComponent]
public sealed partial class NCCyberwareFirmwareCardComponent : Component
{
    [DataField(required: true)] public ProtoId<NCCyberwareFirmwarePrototype> Firmware;
}

/// <summary>Private physical provenance, carried by the item, never an authorization or soul binding.</summary>
[DataDefinition]
public sealed partial class NCCyberwareServiceState
{
    [DataField] public ProtoId<NCCyberwareFirmwarePrototype> Firmware = "NCFirmwareStandard";
    [DataField] public uint Revision;
    [DataField] public ulong OmittedEvents;
    [DataField] public List<NCCyberwareServiceEntry> History = [];
}

public enum NCCyberwareServiceOperation : byte
{
    Installed,
    Removed,
    Configuration,
    Repaired,
    Tested,
    Firmware,
}

[DataDefinition]
public sealed partial class NCCyberwareServiceEntry
{
    [DataField] public uint Revision;
    [DataField] public Guid ResolutionId;
    [DataField] public NCCyberwareServiceOperation Operation;
    // Existing medical stable owner namespace, or empty for an unbound/world item. Never EntityUid.
    [DataField] public string Owner = string.Empty;
}

public static class NCCyberwareServiceHistory
{
    public const int Capacity = 64;

    public static bool Valid(NCCyberwareServiceState? state)
    {
        if (state == null || string.IsNullOrWhiteSpace(state.Firmware.Id) || state.History == null ||
            state.History.Count > Capacity || state.OmittedEvents > state.Revision ||
            (ulong) state.History.Count + state.OmittedEvents != state.Revision)
            return false;
        uint previous = (uint) state.OmittedEvents;
        var resolutions = new HashSet<Guid>();
        foreach (var entry in state.History)
        {
            if (entry == null || entry.Revision != ++previous || entry.ResolutionId == Guid.Empty ||
                !Enum.IsDefined(entry.Operation) || entry.Owner == null || entry.Owner.Length > 160 ||
                !resolutions.Add(entry.ResolutionId))
                return false;
        }
        return previous == state.Revision;
    }

    public static bool TryAppend(NCCyberwareServiceState state, uint expectedRevision, Guid resolution,
        NCCyberwareServiceOperation operation, string owner)
    {
        if (!Valid(state) || state.Revision != expectedRevision || state.Revision == uint.MaxValue ||
            resolution == Guid.Empty || !Enum.IsDefined(operation) || owner == null || owner.Length > 160 ||
            state.History.Any(entry => entry.ResolutionId == resolution))
            return false;
        if (state.History.Count == Capacity)
        {
            state.History.RemoveAt(0);
            state.OmittedEvents++;
        }
        state.History.Add(new NCCyberwareServiceEntry
        {
            Revision = ++state.Revision, ResolutionId = resolution, Operation = operation, Owner = owner,
        });
        return true;
    }

    public static NCCyberwareServiceState Clone(NCCyberwareServiceState state) => new()
    {
        Firmware = state.Firmware, Revision = state.Revision, OmittedEvents = state.OmittedEvents,
        History = state.History.Select(entry => new NCCyberwareServiceEntry
        {
            Revision = entry.Revision, ResolutionId = entry.ResolutionId, Operation = entry.Operation, Owner = entry.Owner,
        }).ToList(),
    };
}
