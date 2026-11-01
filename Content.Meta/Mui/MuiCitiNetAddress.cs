// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Meta.Mui;

/// <summary>A registered CitiNet name and path. Device identities are not public website addresses.</summary>
public sealed record MuiCitiNetAddress(Guid HostDeviceId, string? View, string? Domain = null)
{
    public static bool TryParse(string address, out MuiCitiNetAddress? result)
    {
        result = null;
        if (address.Length is < 10 or > 192 || !address.StartsWith("citi://", StringComparison.Ordinal) ||
            address.Any(c => c is '%' or '?' or '#' or '@' or '\\' || char.IsWhiteSpace(c)))
            return false;
        var domain = address[7..];
        if (!IsDomain(domain))
            return false;
        result = new(Guid.Empty, null, domain);
        return true;
    }

    public static bool IsDomain(string name) => name.Length is >= 3 and <= 63 &&
        name.Split('/') is { Length: <= 8 } parts && parts.All(part => !Guid.TryParse(part, out _) &&
            part.Split('.').All(x => x.Length is > 0 and <= 48 && x[0] != '-' && x[^1] != '-' &&
                x.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')));

    public override string ToString() => Domain is null ? "" : "citi://" + Domain;
}
