// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Input;
using Robust.Shared.Maths;

namespace Content.Client._NC.NCUI.Virtualization;

/// <summary>
/// Fixed-height virtualization host. Only visible rows plus overscan exist as Robust controls.
/// </summary>
[Virtual]
public class VirtualListControl : Control
{
    private readonly List<Control> _realized = [];
    private RecycledControlPool _pool = new(() => new Control());
    private Action<Control, int>? _binder;
    private Func<int, ulong>? _keySelector;
    private int _itemCount;
    private int _firstRealized = -1;
    private float _scrollOffset;
    private ulong? _selectedKey;

    public float ItemHeight { get; set; } = 28f;
    public int OverscanLines { get; set; } = 2;
    public int ItemCount => _itemCount;
    public int PhysicalRowCount => _realized.Count;
    public int CreatedRowCount => _pool.CreatedCount;
    public float ScrollOffset => _scrollOffset;
    public ulong? SelectedKey => _selectedKey;
    protected virtual int ItemsPerLine => 1;

    public event Action<int, ulong>? ItemSelected;
    public event Action? VisibleRangeChanged;

    public VirtualListControl()
    {
        MouseFilter = MouseFilterMode.Stop;
        RectClipContent = true;
    }

    public void Configure<TControl>(
        int itemCount,
        Func<TControl> rowFactory,
        Action<TControl, int> binder,
        Func<int, ulong> stableKey)
        where TControl : Control
    {
        if (_binder != null)
            ReleaseAll();
        _pool = new RecycledControlPool(() => rowFactory());
        _binder = (control, index) => binder((TControl)control, index);
        _keySelector = stableKey;
        SetItemCount(itemCount);
    }

    public void SetItemCount(int count)
    {
        _itemCount = Math.Max(0, count);
        ClampScroll();
        _firstRealized = -1;
        RefreshVisibleRows();
    }

    public void RefreshData()
    {
        for (var i = 0; i < _realized.Count; i++)
        {
            var index = _firstRealized + i;
            if (index < _itemCount)
                _binder?.Invoke(_realized[i], index);
        }
    }

    public void ScrollToIndex(int index, bool center = false)
    {
        if (_itemCount == 0)
            return;
        index = Math.Clamp(index, 0, _itemCount - 1);
        var line = index / ItemsPerLine;
        _scrollOffset = line * ItemHeight;
        if (center)
            _scrollOffset -= Math.Max(0f, Size.Y - ItemHeight) * 0.5f;
        ClampScroll();
        RefreshVisibleRows();
    }

    public (ulong Key, float Offset)? CaptureScrollAnchor()
    {
        if (_keySelector == null || _itemCount == 0)
            return null;
        var index = Math.Clamp((int)(_scrollOffset / ItemHeight) * ItemsPerLine, 0, _itemCount - 1);
        return (_keySelector(index), _scrollOffset - index / ItemsPerLine * ItemHeight);
    }

    public bool RestoreScrollAnchor((ulong Key, float Offset)? anchor)
    {
        if (anchor == null || _keySelector == null)
            return false;
        for (var i = 0; i < _itemCount; i++)
        {
            if (_keySelector(i) != anchor.Value.Key)
                continue;
            _scrollOffset = i / ItemsPerLine * ItemHeight + anchor.Value.Offset;
            ClampScroll();
            RefreshVisibleRows();
            return true;
        }
        return false;
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize) =>
        new(Math.Min(availableSize.X, MinWidth), Math.Min(availableSize.Y, MinHeight));

    protected override Vector2 ArrangeOverride(Vector2 finalSize)
    {
        RefreshVisibleRows();
        ArrangeRows(finalSize);
        return finalSize;
    }

    protected override void Resized()
    {
        base.Resized();
        ClampScroll();
        RefreshVisibleRows();
    }

    protected override void MouseWheel(GUIMouseWheelEventArgs args)
    {
        base.MouseWheel(args);
        if (MathHelper.CloseTo(args.Delta.Y, 0f))
            return;
        _scrollOffset -= args.Delta.Y * ItemHeight * 3f;
        ClampScroll();
        RefreshVisibleRows();
        args.Handle();
    }

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        base.KeyBindDown(args);
        if (args.Function != EngineKeyFunctions.UIClick || _keySelector == null || _itemCount == 0)
            return;
        var column = Math.Clamp((int)(args.RelativePosition.X / Math.Max(1f, Size.X / ItemsPerLine)),
            0, ItemsPerLine - 1);
        var line = (int)((args.RelativePosition.Y + _scrollOffset) / ItemHeight);
        var index = line * ItemsPerLine + column;
        if (index < 0 || index >= _itemCount)
            return;
        _selectedKey = _keySelector(index);
        ItemSelected?.Invoke(index, _selectedKey.Value);
        args.Handle();
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        if (_itemCount == 0)
            return;
        var totalLines = (int)Math.Ceiling(_itemCount / (double)ItemsPerLine);
        var totalHeight = totalLines * ItemHeight;
        if (totalHeight <= PixelHeight / UIScale)
            return;
        var viewportHeight = PixelHeight / UIScale;
        var thumbHeight = Math.Max(12f, viewportHeight * viewportHeight / totalHeight);
        var maxScroll = Math.Max(1f, totalHeight - viewportHeight);
        var thumbY = _scrollOffset / maxScroll * (viewportHeight - thumbHeight);
        var x = PixelWidth - Math.Max(2f, 3f * UIScale);
        handle.DrawRect(UIBox2.FromDimensions(new Vector2(x, thumbY * UIScale),
            new Vector2(Math.Max(2f, 3f * UIScale), thumbHeight * UIScale)), Color.Gray.WithAlpha(0.75f));
    }

    private void RefreshVisibleRows()
    {
        if (_binder == null || _itemCount == 0 || ItemHeight <= 0f || Size.Y <= 0f)
        {
            ReleaseAll();
            return;
        }
        var lineCount = Math.Max(1, (int)MathF.Ceiling(Size.Y / ItemHeight) + OverscanLines * 2);
        var firstLine = Math.Max(0, (int)MathF.Floor(_scrollOffset / ItemHeight) - OverscanLines);
        var first = firstLine * ItemsPerLine;
        var desired = Math.Min(_itemCount - first, lineCount * ItemsPerLine);
        while (_realized.Count < desired)
        {
            var row = _pool.Rent();
            row.Visible = true;
            AddChild(row);
            _realized.Add(row);
        }
        while (_realized.Count > desired)
        {
            var last = _realized.Count - 1;
            var row = _realized[last];
            _realized.RemoveAt(last);
            RemoveChild(row);
            _pool.Return(row);
        }
        if (_firstRealized == first && desired != 0)
        {
            ArrangeRows(Size);
            return;
        }
        _firstRealized = first;
        for (var i = 0; i < _realized.Count; i++)
            _binder(_realized[i], first + i);
        ArrangeRows(Size);
        VisibleRangeChanged?.Invoke();
    }

    private void ArrangeRows(Vector2 finalSize)
    {
        if (_firstRealized < 0 || ItemsPerLine <= 0)
            return;
        var width = finalSize.X / ItemsPerLine;
        for (var i = 0; i < _realized.Count; i++)
        {
            var index = _firstRealized + i;
            var column = index % ItemsPerLine;
            var line = index / ItemsPerLine;
            var y = line * ItemHeight - _scrollOffset;
            _realized[i].Arrange(UIBox2.FromDimensions(new Vector2(column * width, y),
                new Vector2(width, ItemHeight)));
        }
    }

    private void ClampScroll()
    {
        var lines = (int)Math.Ceiling(_itemCount / (double)Math.Max(1, ItemsPerLine));
        var maximum = Math.Max(0f, lines * ItemHeight - Math.Max(0f, Size.Y));
        _scrollOffset = Math.Clamp(_scrollOffset, 0f, maximum);
    }

    private void ReleaseAll()
    {
        foreach (var row in _realized)
        {
            RemoveChild(row);
            _pool.Return(row);
        }
        _realized.Clear();
        _firstRealized = -1;
    }
}

public sealed class VirtualGridControl : VirtualListControl
{
    private int _columns = 4;

    public int Columns
    {
        get => _columns;
        set
        {
            _columns = Math.Max(1, value);
            InvalidateArrange();
        }
    }

    protected override int ItemsPerLine => Columns;
}

/// <summary>
/// A virtual table shares the row recycler; column layout remains inside the row template.
/// </summary>
public sealed class VirtualTableControl : VirtualListControl
{
    public IReadOnlyList<float> ColumnWidths { get; private set; } = [];

    public void SetColumns(params float[] widths)
    {
        foreach (var width in widths)
        {
            if (!float.IsFinite(width) || width <= 0f)
                throw new ArgumentOutOfRangeException(nameof(widths));
        }
        ColumnWidths = (float[]) widths.Clone();
        RefreshData();
    }
}

/// <summary>
/// Fixed-height tree virtualization. The caller supplies a flattened, stable-key view and depth-aware row binder.
/// </summary>
public sealed class VirtualTreeControl : VirtualListControl
{
    private Action? _unsubscribeTree;

    public float IndentWidth { get; set; } = 18f;
    public Func<int, int>? DepthProvider { get; set; }

    public float GetIndent(int index) => Math.Max(0, DepthProvider?.Invoke(index) ?? 0) * IndentWidth;

    public void ConfigureTree<T, TKey, TControl>(
        VirtualTreeView<T, TKey> tree,
        Func<TControl> rowFactory,
        Action<TControl, T, int, bool, bool> binder,
        Func<TKey, ulong> stableKeyEncoder)
        where TKey : notnull
        where TControl : Control
    {
        ArgumentNullException.ThrowIfNull(tree);
        _unsubscribeTree?.Invoke();
        DepthProvider = tree.DepthAt;
        Configure(
            tree.Count,
            rowFactory,
            (control, index) => binder(
                control,
                tree[index],
                tree.DepthAt(index),
                tree.HasChildrenAt(index),
                tree.IsExpanded(tree.KeyAt(index))),
            index => stableKeyEncoder(tree.KeyAt(index)));
        tree.Changed += OnTreeChanged;
        _unsubscribeTree = () => tree.Changed -= OnTreeChanged;

        void OnTreeChanged()
        {
            var anchor = CaptureScrollAnchor();
            SetItemCount(tree.Count);
            RestoreScrollAnchor(anchor);
        }
    }
}
