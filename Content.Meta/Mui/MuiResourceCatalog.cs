// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Meta.Mui;

/// <summary>Versioned native asset aliases. Guest paths never become resource-cache or operating-system paths.</summary>
public static class MuiResourceCatalog
{
    public static bool IsFont(string path) => path is "fonts/noto-sans" or "fonts/noto-bold" or "fonts/mono";
    public static bool IsImage(string path) => path is "images/info" or "images/home" or "images/inventory";
    public static bool IsBuiltin(string path) => IsFont(path) || IsImage(path);
}
