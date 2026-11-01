// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text;

namespace Content.Client._NC.Meta.Editor;

/// <summary>SHA-256 for source-transfer integrity inside the ordinary client sandbox. Server admission computes its own digest.</summary>
public static class NCMetaSourceHash
{
    private static readonly uint[] K =
    {
        0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
        0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
        0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
        0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
        0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
        0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
        0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
        0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2,
    };
    private static uint Rotate(uint value, int bits) => value >> bits | value << (32 - bits);
    public static string HashText(string text) => ToHexStringLower(HashData(Encoding.UTF8.GetBytes(text)));
    public static string DecodeUtf8(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var encoded = Encoding.UTF8.GetBytes(text);
        if (bytes.Length != encoded.Length) throw new ArgumentException("Invalid UTF-8 source.");
        for (var index = 0; index < bytes.Length; index++)
            if (bytes[index] != encoded[index]) throw new ArgumentException("Invalid UTF-8 source.");
        return text;
    }
    public static byte[] HashData(byte[] data)
    {
        var state = new uint[] { 0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19 };
        var words = new uint[64];
        var block = new byte[64];
        var full = data.Length / 64;
        for (var n = 0; n < full; n++) { Array.Copy(data, n * 64, block, 0, 64); Compress(); }
        var remainder = data.Length % 64;
        Array.Clear(block); Array.Copy(data, full * 64, block, 0, remainder); block[remainder] = 0x80;
        if (remainder >= 56) { Compress(); Array.Clear(block); }
        var bits = (ulong) data.Length * 8;
        for (var i = 0; i < 8; i++) block[63 - i] = (byte) (bits >> (8 * i));
        Compress();
        var result = new byte[32];
        for (var i = 0; i < 8; i++)
            for (var j = 0; j < 4; j++) result[i * 4 + j] = (byte) (state[i] >> (24 - 8 * j));
        return result;

        void Compress()
        {
            unchecked
            {
                for (var i = 0; i < 16; i++) words[i] = (uint) block[i * 4] << 24 | (uint) block[i * 4 + 1] << 16 | (uint) block[i * 4 + 2] << 8 | block[i * 4 + 3];
                for (var i = 16; i < 64; i++)
                {
                    var x = words[i - 15]; var y = words[i - 2];
                    words[i] = words[i - 16] + (Rotate(x, 7) ^ Rotate(x, 18) ^ x >> 3) + words[i - 7] + (Rotate(y, 17) ^ Rotate(y, 19) ^ y >> 10);
                }
                var a = state[0]; var b = state[1]; var c = state[2]; var d = state[3];
                var e = state[4]; var f = state[5]; var g = state[6]; var h = state[7];
                for (var i = 0; i < 64; i++)
                {
                    var t1 = h + (Rotate(e, 6) ^ Rotate(e, 11) ^ Rotate(e, 25)) + ((e & f) ^ (~e & g)) + K[i] + words[i];
                    var t2 = (Rotate(a, 2) ^ Rotate(a, 13) ^ Rotate(a, 22)) + ((a & b) ^ (a & c) ^ (b & c));
                    h = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
                }
                state[0] += a; state[1] += b; state[2] += c; state[3] += d;
                state[4] += e; state[5] += f; state[6] += g; state[7] += h;
            }
        }
    }

    public static string ToHexStringLower(byte[] bytes)
        => Hex(bytes, "0123456789abcdef");
    public static string ToHexString(byte[] bytes)
        => Hex(bytes, "0123456789ABCDEF");
    private static string Hex(byte[] bytes, string digits)
    {
        var chars = new char[bytes.Length * 2];
        for (var i = 0; i < bytes.Length; i++) { chars[2 * i] = digits[bytes[i] >> 4]; chars[2 * i + 1] = digits[bytes[i] & 15]; }
        return new string(chars);
    }
}
