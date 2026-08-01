// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.IO;
using System.Linq;
using Content.Shared._NC.Coordinates;
using Robust.Shared.ContentPack;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Utility;

namespace Content.Server._NC.ZLevels.Mapping.Commands;

/// <summary>
/// Reads and writes the small manifest stored alongside the maps of a saved Z-network.
/// The manifest preserves the network identity used by persistent city coordinates.
/// </summary>
internal static class NCZNetworkSaveManifest
{
    public static readonly ResPath FileName = new("_network.yml");

    public static bool IsValidSaveName(string saveName)
    {
        return !string.IsNullOrWhiteSpace(saveName) &&
               saveName is not "." and not ".." &&
               saveName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
               !saveName.Contains('/') &&
               !saveName.Contains('\\');
    }

    public static void Write(IWritableDirProvider userData, ResPath folder, NCZNetworkId networkId)
    {
        userData.CreateDir(folder);

        using var writer = userData.OpenWriteText(folder / FileName);
        writer.WriteLine("# SPDX-FileCopyrightText: 2026 Astro");
        writer.WriteLine("# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0");
        writer.WriteLine("# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.");
        writer.WriteLine("version: 1");
        writer.WriteLine($"networkId: {networkId}");
    }

    public static bool TryRead(
        IWritableDirProvider userData,
        ResPath folder,
        out NCZNetworkId networkId,
        out string? error)
    {
        networkId = default;
        error = null;
        var path = folder / FileName;

        if (!userData.Exists(path))
            return false;

        try
        {
            using var reader = userData.OpenText(path);
            var document = DataNodeParser.ParseYamlStream(reader).FirstOrDefault();
            if (document?.Root is not MappingDataNode mapping ||
                !mapping.TryGetValue("networkId", out var node) ||
                node is not ValueDataNode value ||
                !NCZNetworkId.TryParse(value.Value, out networkId))
            {
                error = $"Manifest {path} does not contain a valid networkId.";
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            error = $"Unable to read manifest {path}: {exception.Message}";
            return false;
        }
    }
}
