// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Security.Cryptography;
using System.Text;

namespace Content.Server._NC.Meta.Devices;

/// <summary>
/// META device identity. A device gets a short code when it is created (placed on the map, assembled from a flatpack);
/// item-backed devices derive it from their persistent item id. Long GUIDs stay valid only for data saved before the
/// short code existed. The code is not a secret and never grants access.
/// </summary>
public static class NCMetaDeviceIds
{
    public const int Length = 6;

    // Crockford base32: no I, L, O or U, so a code read aloud or typed from a screen is hard to get wrong.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static bool IsShort(string? id)
    {
        if (id is not { Length: Length })
            return false;
        foreach (var symbol in id)
        {
            if (Alphabet.IndexOf(symbol) < 0)
                return false;
        }

        return true;
    }

    public static bool IsLegacy(string? id) =>
        Guid.TryParseExact(id, "D", out var guid) && guid != Guid.Empty && id == guid.ToString("D");

    public static bool IsValid(string? id) => IsShort(id) || IsLegacy(id);

    /// <summary>Deterministic code for a stable key, such as a persistent item id.</summary>
    public static string FromKey(string key) => Encode(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static string Random() => Encode(RandomNumberGenerator.GetBytes(4));

    /// <summary>Groups of three for people: K7Q-4MD. Legacy identifiers are shown unchanged.</summary>
    public static string Format(string id) => IsShort(id) ? string.Concat(id.AsSpan(0, 3), "-", id.AsSpan(3)) : id;

    /// <summary>Reads what a person typed: case, separators and the look-alike letters are forgiven.</summary>
    public static bool TryNormalize(string? input, out string id)
    {
        id = "";
        if (input == null)
            return false;
        var builder = new StringBuilder(Length);
        foreach (var symbol in input.Trim().ToUpperInvariant())
        {
            if (symbol is '-' or ' ')
                continue;
            builder.Append(symbol switch { 'I' or 'L' => '1', 'O' => '0', _ => symbol });
        }

        id = builder.ToString();
        return IsShort(id);
    }

    /// <summary>The stable GUID that systems built around GUID keys (site publications) use for a device.</summary>
    public static bool TryToGuid(string? id, out Guid guid)
    {
        guid = Guid.Empty;
        if (IsLegacy(id))
            return Guid.TryParseExact(id, "D", out guid);
        if (!IsShort(id))
            return false;
        guid = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("meta-device-key:" + id)).AsSpan(0, 16));
        return true;
    }

    private static string Encode(byte[] bytes)
    {
        var value = BitConverter.ToUInt32(bytes, 0);
        var code = new char[Length];
        for (var index = 0; index < Length; index++)
            code[index] = Alphabet[(int) ((value >> (5 * index)) & 31)];
        return new string(code);
    }
}
