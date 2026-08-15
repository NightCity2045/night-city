// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Medical.Wounds;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Bleeding;

/// <summary>
/// PVS-scoped visual-only notification. Blood-volume mutation and persistent decals are resolved on the server.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCImpactBloodEffectEvent : EntityEventArgs
{
    public NetCoordinates Origin;
    public Vector2 Direction;
    public ProtoId<NCImpactBloodProfilePrototype> Profile;
    public NCWoundType WoundType;
    public ulong Seed;
    public byte DropletCount;
    public byte SplatCount;
    public byte StreakCount;
    public float DistanceMultiplier;

    public NCImpactBloodEffectEvent(
        NetCoordinates origin,
        Vector2 direction,
        ProtoId<NCImpactBloodProfilePrototype> profile,
        NCWoundType woundType,
        ulong seed,
        byte dropletCount,
        byte splatCount,
        byte streakCount,
        float distanceMultiplier)
    {
        Origin = origin;
        Direction = direction;
        Profile = profile;
        WoundType = woundType;
        Seed = seed;
        DropletCount = dropletCount;
        SplatCount = splatCount;
        StreakCount = streakCount;
        DistanceMultiplier = distanceMultiplier;
    }
}
