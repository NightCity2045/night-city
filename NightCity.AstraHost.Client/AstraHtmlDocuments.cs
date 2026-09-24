// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.Json;
using AstraGraph.Editor.Protocol;
using AstraGraph.Robust.Client;
using Content.Shared._NC.AstraGraph;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;

namespace Content.Client._NC.AstraGraph;

public static class AstraHtmlDocuments
{
    public static bool TryLoad(string documentName, out string html, out string title, out int width, out int height)
    {
        html = "";
        title = documentName;
        width = 400;
        height = 300;
        if (!TryRead(documentName, out var json))
        {
            Logger.GetSawmill("astra.html").Error($"Astra UI document '{documentName}' was not found in the server data directory.");
            return false;
        }

        var document = JsonSerializer.Deserialize<UiDocumentDto>(json, AuthoringJsonContext.Default);
        if (!UiDocumentFactory.TryRender(document, out html, out width, out height, out _, out var error))
        {
            Logger.GetSawmill("astra.html").Error($"Astra UI document '{documentName}' could not be rendered: {error}");
            return false;
        }

        title = string.IsNullOrWhiteSpace(document?.Name) ? documentName : document.Name;
        return true;
    }

    private static bool TryRead(string documentName, out string json)
    {
        json = "";
        var path = AstraHtmlDocumentPaths.File(documentName);
        if (path != null && File.Exists(path))
        {
            json = File.ReadAllText(path);
            if (json.Length > 0)
                return true;
        }

        var resources = IoCManager.Resolve<IResourceManager>();
        if (!resources.TryContentFileRead(AstraHtmlDocumentPaths.ResourcePath(documentName), out var stream))
            return false;

        using var reader = new StreamReader(stream);
        json = reader.ReadToEnd();
        return json.Length > 0;
    }
}

public static class AstraHtmlBui
{
    public static bool TryReadValues(BoundUserInterfaceState state, out Dictionary<string, string> values)
    {
        if (state is not AstraBuiState astra)
        {
            values = [];
            return false;
        }

        values = astra.TypedValues.Count > 0 ? astra.TypedValues : astra.Values;
        return true;
    }

    public static BoundUserInterfaceMessage Action(string name, string? payload)
    {
        return new AstraBuiUiMessage { Action = name, Payload = payload };
    }
}
