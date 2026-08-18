// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Security.Cryptography;
using System.Text;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Identity;

/// <summary>
/// Produces stable identities for canonical Living City participants and commands. A renamed prototype keeps its
/// explicit stable key instead of silently creating a second legal party.
/// </summary>
public static class NCPersistentIdentity
{
    private static readonly Guid NamespaceId = new("34ef622e-f578-50c5-a83a-e3cfe67eed86");

    public static NCPartyRef Organization(string stablePrototypeId) => NCPartyRef.ForOrganization(
        new NCOrganizationId(CreateGuid($"party/organization/{Normalize(stablePrototypeId)}")));

    public static NCPartyRef CityService(string stableServiceId) => NCPartyRef.ForCityService(
        new NCCityServiceId(CreateGuid($"party/city-service/{Normalize(stableServiceId)}")));

    public static NCAccountId PrimaryAccount(NCPartyRef owner)
    {
        if (!owner.IsValid)
            throw new ArgumentOutOfRangeException(nameof(owner));

        return new NCAccountId(CreateGuid($"account/primary/{owner}"));
    }

    public static NCAccountId SystemAccount(string stableAccountId) =>
        new(CreateGuid($"account/system/{Normalize(stableAccountId)}"));

    public static Guid Command(string domain, string operation, string stableKey) =>
        CreateGuid($"command/{Normalize(domain)}/{Normalize(operation)}/{Normalize(stableKey)}");

    public static Guid Correlation(string domain, string operation, string stableKey) =>
        CreateGuid($"correlation/{Normalize(domain)}/{Normalize(operation)}/{Normalize(stableKey)}");

    public static NCTransactionId Transaction(string domain, string operation, string stableKey) =>
        new(CreateGuid($"transaction/{Normalize(domain)}/{Normalize(operation)}/{Normalize(stableKey)}"));

    public static NCRelationshipId Relationship(NCPartyRef observer, NCPartyRef subject)
    {
        if (!observer.IsValid || !subject.IsValid || observer == subject)
            throw new ArgumentOutOfRangeException(nameof(observer));

        return new NCRelationshipId(CreateGuid($"relationship/{observer}/{subject}"));
    }

    public static Guid RelationshipIndex(NCPartyRef observer)
    {
        if (!observer.IsValid)
            throw new ArgumentOutOfRangeException(nameof(observer));

        return CreateGuid($"relationship-index/{observer}");
    }

    public static Guid RelationshipApplication(NCRelationshipId relationshipId, Guid worldEventId)
    {
        if (!relationshipId.IsValid || worldEventId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(relationshipId));

        return CreateGuid($"relationship-application/{relationshipId}/{worldEventId:D}");
    }

    public static NCReputationId Reputation(NCPartyRef subject)
    {
        if (!subject.IsValid)
            throw new ArgumentOutOfRangeException(nameof(subject));

        return new NCReputationId(CreateGuid($"reputation/{subject}"));
    }

    public static NCReputationEntryId ReputationEntry(NCReputationId reputationId, string audienceKey)
    {
        if (!reputationId.IsValid)
            throw new ArgumentOutOfRangeException(nameof(reputationId));

        return new NCReputationEntryId(CreateGuid($"reputation-entry/{reputationId}/{Normalize(audienceKey)}"));
    }

    public static Guid ReputationApplication(NCReputationEntryId entryId, Guid worldEventId)
    {
        if (!entryId.IsValid || worldEventId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(entryId));

        return CreateGuid($"reputation-application/{entryId}/{worldEventId:D}");
    }

    private static Guid CreateGuid(string value)
    {
        Span<byte> scope = stackalloc byte[16];
        NamespaceId.TryWriteBytes(scope);
        var text = Encoding.UTF8.GetBytes(value);
        var input = new byte[scope.Length + text.Length];
        scope.CopyTo(input);
        text.CopyTo(input.AsSpan(scope.Length));

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        // Stable UUIDv5-shaped value; SHA-256 is used because the identity is local and not an RFC UUID namespace.
        hash[6] = (byte) ((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte) ((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16]);
    }

    private static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant();
    }
}
