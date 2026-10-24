// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Meta.Mui;

/// <summary>Canonical self-hosted CitiNet address. This is a game route, never an Internet URI.</summary>
public sealed record MuiCitiNetAddress(Guid HostDeviceId, string? View, string? Domain = null)
{
    public static bool TryParse(string address, out MuiCitiNetAddress? result)
    {
        result = null;
        if (address.Length is < 10 or > 192 || !address.StartsWith("citi://", StringComparison.Ordinal) ||
            address.Any(c => c is '%' or '?' or '#' or '@' or '\\' || char.IsWhiteSpace(c)))
            return false;
        var parts = address[7..].Split('/');
        if (parts.Length is < 1 or > 2 || parts.Length == 2 && (!MuiBinding.IsPath(parts[1]) || parts[1].Contains(':')))
            return false;
        var deviceAddress = Guid.TryParseExact(parts[0], "D", out var device) && device != Guid.Empty && parts[0] == device.ToString("D");
        if (!deviceAddress && !IsDomain(parts[0]))
            return false;
        result = new(deviceAddress ? device : Guid.Empty, parts.Length == 2 ? parts[1] : null, deviceAddress ? null : parts[0]);
        return true;
    }

    public static bool IsDomain(string name) => name.Length is >= 3 and <= 63 && !Guid.TryParse(name, out _) &&
        name.Split('.').All(x => x.Length is > 0 and <= 48 && x[0] != '-' && x[^1] != '-' &&
            x.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'));

    public override string ToString() => "citi://" + (Domain ?? HostDeviceId.ToString("D")) + (View is null ? "" : "/" + View);
}
