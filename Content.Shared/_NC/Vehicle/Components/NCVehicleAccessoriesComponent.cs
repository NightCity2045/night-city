// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Audio;
using Robust.Shared.GameStates;

namespace Content.Shared._NC.Vehicle.Components;

/// <summary>Replayable switches and windshield condition; sound entities remain server-owned.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCVehicleAccessoriesComponent : Component
{
    [AutoNetworkedField] public bool Headlights;
    [AutoNetworkedField] public TimeSpan NextHorn;
    [AutoNetworkedField] public float WindshieldDirt;
    [DataField] public SoundSpecifier? StartupSound;
    [DataField] public SoundSpecifier? IdleSound;
    [DataField] public SoundSpecifier? HornSound;
    [DataField] public float HornCooldown = 1;
    [DataField] public float HeadlightAmps = 10;
    [DataField] public float WasherVolume = 30;
    [DataField] public float WasherCapacity = 3000;
    [DataField] public float DirtPerMeter = 0.0001f;
    [DataField] public float WashAmount = 0.4f;
    [DataField] public Color DirtTint = new(0.3f, 0.25f, 0.18f, 0.45f);
    [DataField] public float IdleVolume = -12;
    [DataField] public float LoadVolume = -2;
}
