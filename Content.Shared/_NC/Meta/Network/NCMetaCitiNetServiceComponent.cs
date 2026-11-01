// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Shared._NC.Meta.Network;

/// <summary>Native public service endpoint. A network label alone never authorizes online marketplace access.</summary>
[RegisterComponent]
public sealed partial class NCMetaCitiNetServiceComponent : Component
{
    [DataField] public bool SoftwareMarketplace = true;
}
