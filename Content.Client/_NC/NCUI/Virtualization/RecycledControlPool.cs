// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Client.UserInterface;

namespace Content.Client._NC.NCUI.Virtualization;

/// <summary>
/// Reuses detached Robust controls and never disposes rows while their virtualized owner remains alive.
/// </summary>
public sealed class RecycledControlPool
{
    private readonly Stack<Control> _available = [];
    private Func<Control> _factory;

    public int AvailableCount => _available.Count;
    public int CreatedCount { get; private set; }

    public RecycledControlPool(Func<Control> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public Control Rent()
    {
        if (_available.TryPop(out var control))
            return control;
        CreatedCount++;
        return _factory();
    }

    public void Return(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.Visible = false;
        _available.Push(control);
    }

    public void ReplaceFactory(Func<Control> factory)
    {
        if (CreatedCount != 0 || _available.Count != 0)
            throw new InvalidOperationException("The row factory cannot change after virtualization has started.");
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }
}

public sealed class RecycledControlPool<TControl> where TControl : Control
{
    private readonly Stack<TControl> _available = [];
    private readonly Func<TControl> _factory;

    public int AvailableCount => _available.Count;
    public int CreatedCount { get; private set; }

    public RecycledControlPool(Func<TControl> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public TControl Rent()
    {
        if (_available.TryPop(out var control))
            return control;
        CreatedCount++;
        return _factory();
    }

    public void Return(TControl control)
    {
        control.Visible = false;
        _available.Push(control);
    }
}
