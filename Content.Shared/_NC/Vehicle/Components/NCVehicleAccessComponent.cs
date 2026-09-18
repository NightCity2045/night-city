// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Stacks;
using Content.Shared.Tools;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Vehicle.Components;

/// <summary>Physical key authorization and registration for this world session only.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCVehicleAccessComponent : Component
{
    [DataField, AutoNetworkedField] public bool Registered;
    [DataField, AutoNetworkedField] public new EntityUid? Owner;
    [DataField, AutoNetworkedField] public uint KeyGeneration;
    [DataField, AutoNetworkedField] public bool Hotwired;
    [DataField, AutoNetworkedField] public int SalePrice = -1;
    [DataField, AutoNetworkedField] public uint SaleRevision;
    [DataField] public EntProtoId KeyPrototype = "NCVehicleKey";
    [DataField] public ProtoId<StackPrototype> Currency = "Credit";
    [DataField] public int MaximumSalePrice = 1000000;
    [DataField] public float InteractionRange = 3;
    [DataField] public float MaximumServiceSpeed = 0.1f;
    [DataField] public float BreakLockSeconds = 30;
    [DataField] public float HotwireSeconds = 45;
    [DataField] public ProtoId<ToolQualityPrototype> BreakLockTool = "Prying";
    [DataField] public ProtoId<ToolQualityPrototype> HotwireTool = "Cutting";
}

/// <summary>A carried key names an entity and a revocable lock generation, never a prototype.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCVehicleKeyComponent : Component
{
    [DataField, AutoNetworkedField] public EntityUid? Vehicle;
    [DataField, AutoNetworkedField] public uint Generation;
}
