// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Server._NC.Meta.Authority;

public sealed partial class NCMetaAuthoritySystem
{
    /// <summary>Native session proof: keeps its original world/prototype/employment generation, without a lookup-cache TTL.</summary>
    public bool CanContinueManagement(NCMetaManagementProof proof) => IsManagementCurrent(proof, requireFreshCapture: false);
}
