// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Meta.Hosting;

/// <summary>Durable native policy data, never a guest capability or a serialized runtime handle.</summary>
public sealed record MetaServiceGrant(Guid Id, string Slot, string TargetDeviceId, string ContractHash,
    string[] Members, string[] Scopes, ulong TargetAuthorityEpoch, long? ExpiresAtTicks);

public sealed record MetaServiceGrantPolicy(Guid InstallationId, string HostDeviceId, string Principal,
    string ArtifactDigest, ulong HostAuthorityEpoch, bool AllowUnattended, MetaServiceGrant[] Grants,
    string Issuer, long IssuedAtTicks, long Revision, bool Revoked = false, long? ExpiresAtTicks = null);

/// <summary>Bounded structural checks and exact intersections. Only the native authority service admits these DTOs.</summary>
public static class MetaServiceAuthority
{
    public const int MaximumGrants = 64;
    public const int MaximumMembers = 128;
    public const int MaximumScopes = 64;
    public const int MaximumPolicyBytes = 128 * 1024;

    public static bool IsValid(MetaServiceGrantPolicy? policy) => policy is
        { InstallationId: var id, Revision: > 0, IssuedAtTicks: >= 0, Grants.Length: <= MaximumGrants } &&
        id != Guid.Empty && Device(policy.HostDeviceId) && Text(policy.Principal, 128) &&
        Text(policy.Issuer, 128) && Digest(policy.ArtifactDigest) && policy.HostAuthorityEpoch >= 2 &&
        (policy.ExpiresAtTicks == null || policy.ExpiresAtTicks > policy.IssuedAtTicks) &&
        policy.Grants.All(grant => grant is { Id: var grantId, TargetAuthorityEpoch: >= 2,
            Members.Length: <= MaximumMembers, Scopes.Length: <= MaximumScopes } &&
            grantId != Guid.Empty && Text(grant.Slot, 128) && Device(grant.TargetDeviceId) && Digest(grant.ContractHash) &&
            (grant.ExpiresAtTicks == null || grant.ExpiresAtTicks > policy.IssuedAtTicks) &&
            grant.Members.All(member => Text(member, 192)) && grant.Scopes.All(scope => Text(scope, 128)) &&
            grant.Members.Distinct(StringComparer.Ordinal).Count() == grant.Members.Length &&
            grant.Scopes.Distinct(StringComparer.Ordinal).Count() == grant.Scopes.Length) &&
        policy.Grants.Select(grant => grant.Id).Distinct().Count() == policy.Grants.Length &&
        policy.Grants.Select(grant => grant.Slot).Distinct(StringComparer.Ordinal).Count() == policy.Grants.Length &&
        EstimateBytes(policy) <= MaximumPolicyBytes;

    public static long EstimateBytes(MetaServiceGrantPolicy policy) => 1024L +
        2L * (policy.Principal.Length + policy.Issuer.Length) + policy.Grants.Sum(grant => 512L +
            2L * grant.Slot.Length + grant.Members.Sum(member => 48L + 2L * member.Length) +
            grant.Scopes.Sum(scope => 48L + 2L * scope.Length));

    public static bool MatchesInstallation(MetaServiceGrantPolicy policy, Guid installationId, string host,
        string principal, string artifactDigest, ulong hostAuthority, bool unattended) => !policy.Revoked &&
        policy.InstallationId == installationId && policy.HostDeviceId == host && policy.Principal == principal &&
        policy.ArtifactDigest == artifactDigest && policy.HostAuthorityEpoch == hostAuthority &&
        (!unattended || policy.AllowUnattended);

    public static bool Allows(MetaServiceGrantPolicy policy, Guid grantId, long revision, string slot,
        string target, string contract, string member, string scope, ulong targetAuthority, long nowTicks) =>
        !policy.Revoked && nowTicks >= policy.IssuedAtTicks &&
        (policy.ExpiresAtTicks == null || nowTicks < policy.ExpiresAtTicks) && revision == policy.Revision &&
        policy.Grants.Any(grant => grant.Id == grantId && grant.Slot == slot && grant.TargetDeviceId == target &&
            grant.ContractHash == contract && grant.TargetAuthorityEpoch == targetAuthority &&
            (grant.ExpiresAtTicks == null || nowTicks < grant.ExpiresAtTicks) &&
            grant.Members.Contains(member, StringComparer.Ordinal) && grant.Scopes.Contains(scope, StringComparer.Ordinal));

    public static long? EarliestExpiry(MetaServiceGrantPolicy policy) =>
        policy.Grants.Select(grant => grant.ExpiresAtTicks).Append(policy.ExpiresAtTicks).Min();

    private static bool Text(string? value, int limit) => value is { Length: > 0 } && value.Length <= limit &&
        value.All(character => character is >= '!' and <= '~');
    private static bool Digest(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Device(string? value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty &&
        value == id.ToString("D");
}
