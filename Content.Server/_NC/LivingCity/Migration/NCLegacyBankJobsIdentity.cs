// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Security.Cryptography;
using System.Text;
using System.Linq;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Migration;

/// <summary>
/// Stable UUIDv5-shaped identities reserved for Bank/Jobs compatibility. Prototype renames are migration events and
/// must retain an explicit alias instead of silently producing a second organization.
/// </summary>
public static class NCLegacyBankJobsIdentity
{
    private static readonly Guid NamespaceId = new("c10885c5-f07d-58ea-8ef4-b66e8903c843");

    public static NCPartyRef ClearingParty => NCPartyRef.ForCityService(
        new NCCityServiceId(CreateGuid("party/system/legacy-bank-clearing")));

    public static NCAccountId ClearingAccount => new(CreateGuid("account/system/legacy-bank-clearing"));

    public static NCPartyRef OrganizationParty(string organizationPrototypeId)
    {
        return NCPartyRef.ForOrganization(new NCOrganizationId(
            CreateGuid($"party/department/{Normalize(organizationPrototypeId)}")));
    }

    public static NCAccountId PrimaryAccount(NCPartyRef party)
    {
        if (!party.IsValid)
            throw new ArgumentOutOfRangeException(nameof(party));
        return new NCAccountId(CreateGuid($"account/primary/{party}"));
    }

    public static NCEmploymentId Employment(
        NCCharacterId characterId,
        DateTime legacyStartedAt,
        string jobPrototypeId,
        string organizationPrototypeId)
    {
        if (!characterId.IsValid)
            throw new ArgumentOutOfRangeException(nameof(characterId));
        return new NCEmploymentId(CreateGuid(
            $"employment/character/{characterId.Value}/{legacyStartedAt.Ticks}/" +
            $"{Normalize(organizationPrototypeId)}/{Normalize(jobPrototypeId)}"));
    }

    public static Guid Binding(string sourceKind, string sourceKey)
    {
        return CreateGuid($"binding/{Normalize(sourceKind)}/{Normalize(sourceKey)}");
    }

    public static Guid Command(string operation, string sourceKey, string sourceRevision)
    {
        return CreateGuid($"command/{Normalize(operation)}/{Normalize(sourceKey)}/{Normalize(sourceRevision)}");
    }

    public static NCTransactionId Transaction(string operation, string sourceKey, string sourceRevision)
    {
        return new NCTransactionId(CreateGuid(
            $"transaction/{Normalize(operation)}/{Normalize(sourceKey)}/{Normalize(sourceRevision)}"));
    }

    public static string Revision(params object[] values)
    {
        var normalized = string.Join('\n', values.Select(value => value?.ToString()?.Trim() ?? "<null>"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
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
