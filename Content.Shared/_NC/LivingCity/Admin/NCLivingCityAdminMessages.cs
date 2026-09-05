// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Admin;

[Serializable, NetSerializable]
public enum NCLivingCityAdminMutationStatus : byte
{
    Applied,
    Rejected,
    Unauthorized,
    RateLimited,
    Failed,
}

/// <summary>One bounded diagnostic value returned to the F7 resident factory tab.</summary>
[Serializable, NetSerializable]
public readonly record struct NCLivingCityAdminField(string Key, string Value);

[Serializable, NetSerializable]
public sealed class NCLivingCityAdminRandomResidentRequest(Guid requestId) : EntityEventArgs
{
    public Guid RequestId { get; } = requestId;
}

[Serializable, NetSerializable]
public sealed class NCLivingCityAdminRandomResidentResult(
    Guid requestId,
    NCLivingCityAdminMutationStatus status,
    NCLivingCityAdminField[] fields,
    string? error) : EntityEventArgs
{
    public Guid RequestId { get; } = requestId;
    public NCLivingCityAdminMutationStatus Status { get; } = status;
    public NCLivingCityAdminField[] Fields { get; } = fields;
    public string? Error { get; } = error;
}
