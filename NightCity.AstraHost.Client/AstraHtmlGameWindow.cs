// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using System.Text;
using System.Text.Json;
using AstraGraph.Editor.Protocol;
using AstraGraph.UI.Html;
using Robust.Client.UserInterface.CustomControls;
using Robust.Client.WebView;
using Robust.Shared.Asynchronous;
using Robust.Shared.ContentPack;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Utility;

namespace Content.Client._NC.AstraGraph;

/// <summary>
/// One in-game window for an HTML interface authored in Studio.
/// </summary>
public sealed class AstraHtmlGameWindow
{
    private const string PagePrefix = "https://astra.local/ui";
    private const string ActionPrefix = "https://astra.local/action";
    private const string TexturePrefix = "https://astra.local/texture";

    private static AstraHtmlGameWindow? _preview;

    private DefaultWindow? _window;
    private WebViewControl? _view;
    private string _html = "";
    private string? _stateJson;
    private int _revision;
    private Action<string, string?>? _onAction;

    public static void Apply(UiPreviewRequestMsg request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Close)
        {
            RunOnUi(() => _preview?.Close());
            return;
        }

        if (!UiDocumentFactory.TryRender(request.Document, out var html, out var width, out var height, out _, out _))
            return;

        var title = string.IsNullOrWhiteSpace(request.Document?.Name) ? "Astra UI" : request.Document.Name;
        var page = Prepare(html);
        RunOnUi(() =>
        {
            _preview ??= new AstraHtmlGameWindow();
            _preview.Open(title, page, width, height, static (_, _) => { });
        });
    }

    public void Open(string title, string html, int width, int height, Action<string, string?> onAction)
    {
        _html = Prepare(html);
        _onAction = onAction;
        _revision++;
        EnsureWindow(title, width, height);
        if (_window == null || _view == null)
            return;

        _window.Title = title;
        _window.SetSize = new Vector2(Math.Max(width, 240), Math.Max(height, 160));
        if (!_window.IsOpen)
            _window.OpenCentered();
        _view.Url = PagePrefix + "?rev=" + _revision.ToString();
    }

    public void ApplyState(IReadOnlyDictionary<string, string> values)
    {
        _stateJson = JsonSerializer.Serialize(values);
        if (_view == null)
            return;

        _view.ExecuteJavaScript(
            "if (window.astraApplyState) astraApplyState(" + _stateJson + ");" +
            "else window.__astraPending = " + _stateJson + ";");
    }

    public void Close()
    {
        if (_window?.IsOpen == true)
            _window.Close();
    }

    private void EnsureWindow(string title, int width, int height)
    {
        if (_window != null && _view != null)
            return;

        _window = new DefaultWindow { Title = title };
        _window.SetSize = new Vector2(Math.Max(width, 240), Math.Max(height, 160));
        _view = new WebViewControl
        {
            HorizontalExpand = true,
            VerticalExpand = true,
            AlwaysActive = true
        };
        _view.AddResourceRequestHandler(OnRequest);
        _view.AddBeforeBrowseHandler(OnBrowse);
        _window.Contents.AddChild(_view);
        _window.OnClose += () =>
        {
            _view = null;
            _window = null;
        };
    }

    private void OnRequest(IRequestHandlerContext context)
    {
        if (context.Url.StartsWith(PagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var page = _stateJson == null
                ? _html
                : _html.Replace("</body>", "<script>astraApplyState(" + _stateJson + ");</script></body>", StringComparison.Ordinal);
            context.DoRespondStream(new MemoryStream(Encoding.UTF8.GetBytes(page)), "text/html");
            return;
        }

        if (context.Url.StartsWith(ActionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            DispatchAction(context.Url);
            if (context.IsNavigation)
                context.DoCancel();
            else
                context.DoRespondStream(new MemoryStream("ok"u8.ToArray()), "text/plain");
            return;
        }

        if (context.Url.StartsWith(TexturePrefix, StringComparison.OrdinalIgnoreCase) &&
            TryReadTexture(context.Url, out var bytes, out var mime))
        {
            context.DoRespondStream(new MemoryStream(bytes), mime);
            return;
        }

        context.DoCancel();
    }

    private string? _lastActionUrl;
    private long _lastActionAt;

    private void DispatchAction(string url)
    {
        var now = Environment.TickCount64;
        if (string.Equals(url, _lastActionUrl, StringComparison.Ordinal) && now - _lastActionAt < 300)
            return;

        _lastActionUrl = url;
        _lastActionAt = now;
        var original = "astra-bui://action" + url[ActionPrefix.Length..];
        if (!UiHtmlPage.TryReadAction(original, out var name, out var payload))
            return;

        _onAction?.Invoke(name, JsonSerializer.Serialize(payload));
    }

    private void OnBrowse(IBeforeBrowseContext context)
    {
        if (!context.Url.StartsWith(ActionPrefix, StringComparison.OrdinalIgnoreCase))
            return;

        context.DoCancel();
        DispatchAction(context.Url);
    }

    private static string Prepare(string html) => html
        .Replace("astra-bui://action", ActionPrefix, StringComparison.Ordinal)
        .Replace("astra-ui://texture", TexturePrefix, StringComparison.Ordinal)
        .Replace("</head>", ButtonFeedback + "</head>", StringComparison.Ordinal)
        .Replace("</body>", ButtonScript + "</body>", StringComparison.Ordinal);

    private const string ButtonFeedback =
        "<style>" +
        "button{cursor:pointer}" +
        "button:hover{background:#556070;border-color:#d7deea}" +
        "button:active,button.is-pressed{background:#243044;border-color:#edbc63}" +
        "button:disabled,button:disabled:hover{background:#3d4454;border-color:#6d7690;cursor:default}" +
        "</style>";

    private const string ButtonScript =
        "<script>" +
        "(function(){" +
        "var send=window.astraSend;" +
        "if(typeof send!==\"function\"||send.__astraPatched)return;" +
        "function patched(name,payload){" +
        "payload=payload||{};" +
        "if(!payload.id){" +
        "var selected=document.querySelector(\".astra-list-row.is-selected\");" +
        "if(selected&&selected.dataset.id)payload=Object.assign({},payload,{id:selected.dataset.id});" +
        "}" +
        "if(!payload.id){" +
        "var label=document.querySelector(\"label.astra-label\");" +
        "if(label)label.textContent=\"Select a barrel in the list\";" +
        "}" +
        "return send(name,payload);" +
        "}" +
        "patched.__astraPatched=true;" +
        "window.astraSend=patched;" +
        "if(window.__astraPending&&window.astraApplyState)window.astraApplyState(window.__astraPending);" +
        "})();" +
        "</script>";

    private static void RunOnUi(Action action)
    {
        try
        {
            IoCManager.Resolve<ITaskManager>().RunOnMainThread(action);
        }
        catch (Exception ex)
        {
            Logger.GetSawmill("astra.html").Error($"Astra HTML window could not reach the UI thread: {ex.Message}");
        }
    }

    private static bool TryReadTexture(string url, out byte[] bytes, out string mime)
    {
        bytes = [];
        mime = "image/png";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var path = ReadQuery(uri.Query, "path");
        if (string.IsNullOrWhiteSpace(path) || path.Contains("..", StringComparison.Ordinal) || !path.StartsWith("/Textures/", StringComparison.Ordinal))
            return false;

        if (path.EndsWith(".rsi", StringComparison.OrdinalIgnoreCase))
            path += "/icon.png";

        mime = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? "image/jpeg"
            : "image/png";

        var resources = IoCManager.Resolve<IResourceManager>();
        if (!resources.TryContentFileRead(new ResPath(path), out var stream))
            return false;

        using (stream)
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            bytes = copy.ToArray();
        }

        return bytes.Length > 0;
    }

    private static string ReadQuery(string query, string key)
    {
        if (query.StartsWith('?'))
            query = query[1..];

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = part.IndexOf('=');
            var name = Uri.UnescapeDataString(split < 0 ? part : part[..split]);
            if (!name.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;

            return split < 0 ? "" : Uri.UnescapeDataString(part[(split + 1)..]);
        }

        return "";
    }
}
