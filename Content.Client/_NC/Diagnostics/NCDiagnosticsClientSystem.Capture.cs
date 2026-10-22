// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#if TOOLS && NC_DIAGNOSTICS
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Content.Client.Viewport;
using NightCity.Diagnostics.Contracts;
using Robust.Client.Graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Content.Client._NC.Diagnostics;

public sealed partial class NCDiagnosticsClientSystem
{
    private JsonObject StartCapture(JsonObject args)
    {
        if (!_graphics)
            throw new DiagnosticException("rendering_unavailable", "Headless clients cannot produce a verified rendered screenshot.");
        if (_captures.Count >= 4)
            throw new DiagnosticException("busy", "Consume completed captures before requesting more screenshots.", true);
        var width = args.BoundedInt("maximumWidth", 1280, 320, 1920);
        var height = args.BoundedInt("maximumHeight", 1080, 240, 1440);
        var id = Guid.NewGuid().ToString("N");
        var tick = _timing.CurTick.Value;
        var appliedTick = _timing.LastRealTick.Value;
        var frame = _frame;
        _captures[id] = new JsonObject { ["captureId"] = id, ["state"] = "pending", ["requestedFrame"] = frame };
        void Capture(Image<Rgb24> image)
        {
            _ = Task.Run(() => EncodeCapture(id, image, width, height, tick, appliedTick, frame));
        }
        if (args["mode"]?.GetValue<string>() == "viewport")
        {
            if (_states.CurrentState is not IMainViewportState game)
            {
                _captures.TryRemove(id, out _);
                throw new DiagnosticException("viewport_unavailable", "The current client state has no game viewport.");
            }
            game.Viewport.Viewport.Screenshot(image =>
            {
                using (image)
                    Capture(image.CloneAs<Rgb24>());
            });
        }
        else
            _clyde.Screenshot(ScreenshotType.Final, Capture);
        return new JsonObject { ["captureId"] = id, ["state"] = "pending" };
    }

    private void EncodeCapture(string id, Image<Rgb24> image, int maximumWidth, int maximumHeight, uint tick, uint appliedTick, long frame)
    {
        using (image)
        {
            if (!_captures.TryGetValue(id, out var pending))
                return;
            try
            {
                if (_shutdown.IsCancellationRequested || (long)image.Width * image.Height > 16_000_000)
                    throw new DiagnosticException("capture_cancelled", "The capture was cancelled or exceeds the source pixel limit.");
                var sourceWidth = image.Width;
                var sourceHeight = image.Height;
                if (image.Width > maximumWidth || image.Height > maximumHeight)
                    image.Mutate(context => context.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(maximumWidth, maximumHeight) }));
                using var stream = new MemoryStream();
                image.SaveAsPng(stream);
                if (stream.Length > 1024 * 1024)
                    throw new DiagnosticException("capture_too_large", "Reduce the capture dimensions to stay below the 1 MiB PNG transport limit.");
                var completed = new JsonObject { ["captureId"] = id, ["state"] = "succeeded", ["mediaType"] = "image/png",
                    ["base64"] = Convert.ToBase64String(stream.ToArray()), ["width"] = image.Width, ["height"] = image.Height,
                    ["sourceWidth"] = sourceWidth, ["sourceHeight"] = sourceHeight, ["requestedTick"] = tick,
                    ["requestedLastRealTick"] = appliedTick, ["requestedFrame"] = frame,
                    ["timingCoverage"] = "capture-request-metadata" };
                _captures.TryUpdate(id, completed, pending);
            }
            catch (Exception exception)
            {
                _captures.TryUpdate(id, new JsonObject { ["captureId"] = id, ["state"] = "failed",
                    ["error"] = exception is DiagnosticException diagnostic ? diagnostic.Code : "capture_failed" }, pending);
            }
        }
    }

    private JsonObject GetCapture(JsonObject args)
    {
        var id = args.RequiredString("captureId", 64);
        if (!_captures.TryGetValue(id, out var capture))
            throw new DiagnosticException("capture_not_found", "The capture does not exist or has already been consumed.");
        var result = (JsonObject)capture.DeepClone();
        if (args["consume"]?.GetValue<bool>() == true && capture["state"]?.GetValue<string>() != "pending")
            _captures.TryRemove(id, out _);
        return result;
    }

    private JsonObject DiscardCapture(JsonObject args)
    {
        _captures.TryRemove(args.RequiredString("captureId", 64), out _);
        return new JsonObject { ["discarded"] = true };
    }
}

#endif
