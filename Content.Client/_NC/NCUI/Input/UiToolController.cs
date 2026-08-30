// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client._NC.NCUI.Core;

namespace Content.Client._NC.NCUI.Input;

/// <summary>
/// Stable non-localized tool identity. Visible names remain presenter-owned localization keys.
/// </summary>
public readonly record struct UiToolId(uint Value)
{
    public static readonly UiToolId Invalid = new(0);
    public bool IsValid => Value != 0;
}

public enum UiToolResult : byte
{
    Ignored,
    Handled,
    CapturePointer,
    Complete,
    Cancel,
}

/// <summary>
/// Shared surface services exposed to registered tools without granting access to Robust layout internals.
/// </summary>
public sealed record UiToolContext(
    UiSurfaceDocument Document,
    UiViewportTransform Viewport,
    UiSelectionService Selection);

/// <summary>
/// Tool modes consume the same pointer pipeline for surgery, wiring, maps, timelines and editors.
/// </summary>
public interface IUiSurfaceTool
{
    UiToolId Id { get; }
    void Activated(UiToolContext context);
    void Deactivated(UiToolContext context);
    UiToolResult PointerDown(UiToolContext context, in UiPointerEvent args);
    UiToolResult PointerMove(UiToolContext context, in UiPointerEvent args);
    UiToolResult PointerUp(UiToolContext context, in UiPointerEvent args);
    void Cancel(UiToolContext context);
}

/// <summary>
/// Registers domain tools and guarantees balanced activation, deactivation and cancellation.
/// </summary>
public sealed class UiToolController
{
    private readonly Dictionary<UiToolId, IUiSurfaceTool> _tools = [];
    private readonly UiToolContext _context;

    public IUiSurfaceTool? ActiveTool { get; private set; }
    public event Action<UiToolId?>? ActiveToolChanged;

    public UiToolController(UiToolContext context)
    {
        _context = context;
    }

    public void Register(IUiSurfaceTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!tool.Id.IsValid)
            throw new ArgumentException("An NCUI tool requires a valid ID.", nameof(tool));
        _tools.Add(tool.Id, tool);
    }

    public bool Activate(UiToolId id)
    {
        if (!_tools.TryGetValue(id, out var tool))
            return false;
        ActiveTool?.Deactivated(_context);
        ActiveTool = tool;
        ActiveTool.Activated(_context);
        ActiveToolChanged?.Invoke(id);
        return true;
    }

    public void Deactivate()
    {
        if (ActiveTool == null)
            return;
        ActiveTool.Deactivated(_context);
        ActiveTool = null;
        ActiveToolChanged?.Invoke(null);
    }

    public UiToolResult PointerDown(in UiPointerEvent args) =>
        ActiveTool?.PointerDown(_context, args) ?? UiToolResult.Ignored;

    public UiToolResult PointerMove(in UiPointerEvent args) =>
        ActiveTool?.PointerMove(_context, args) ?? UiToolResult.Ignored;

    public UiToolResult PointerUp(in UiPointerEvent args) =>
        ActiveTool?.PointerUp(_context, args) ?? UiToolResult.Ignored;

    public void Cancel()
    {
        ActiveTool?.Cancel(_context);
    }
}
