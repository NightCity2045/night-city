// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Text.Json.Nodes;
using Content.Meta.Mui;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._NC.Meta.Mui;

/// <summary>One server page and one visible native item window. Sorting/filtering request new authoritative pages.</summary>
public sealed class NCMetaMuiTable : BoxContainer
{
    private readonly ItemList _rows = new() { MinHeight = 120, VerticalExpand = true };
    private readonly LineEdit _filter = new() { HorizontalExpand = true, PlaceHolder = Loc.GetString("nc-meta-table-filter") };
    private readonly Button _previous = new() { Text = Loc.GetString("nc-meta-table-previous") };
    private readonly Button _next = new() { Text = Loc.GetString("nc-meta-table-next") };
    private readonly OptionButton _sort = new();
    private readonly Label _page = new();
    private string[] _columns = [];
    private readonly List<string?> _rowKeys = [];
    private string _rowField = "";
    private bool _applying;
    private int _pageIndex;
    public event Action<string, IReadOnlyDictionary<string, object?>>? Requested;

    public NCMetaMuiTable()
    {
        Orientation = LayoutOrientation.Vertical;
        var toolbar = new BoxContainer { Orientation = LayoutOrientation.Horizontal };
        toolbar.AddChild(_filter);
        toolbar.AddChild(_sort);
        AddChild(toolbar);
        AddChild(_rows);
        var footer = new BoxContainer { Orientation = LayoutOrientation.Horizontal };
        footer.AddChild(_previous);
        footer.AddChild(_page);
        footer.AddChild(_next);
        AddChild(footer);
        _previous.OnPressed += _ => Requested?.Invoke("pageAction", new Dictionary<string, object?> { ["page"] = Math.Max(0, _pageIndex - 1) });
        _next.OnPressed += _ => Requested?.Invoke("pageAction", new Dictionary<string, object?> { ["page"] = _pageIndex + 1 });
        _filter.OnTextEntered += _ =>
        {
            if (_filter.Text.Length <= 128)
                Requested?.Invoke("filterAction", new Dictionary<string, object?> { ["filter"] = _filter.Text });
        };
        _sort.OnItemSelected += args =>
        {
            if (args.Id < 0 || args.Id >= _columns.Length)
                return;
            _sort.SelectId(args.Id);
            Requested?.Invoke("sortAction", new Dictionary<string, object?> { ["sort"] = _columns[args.Id] });
        };
        _rows.OnItemSelected += args =>
        {
            if (!_applying && args.ItemIndex >= 0 && args.ItemIndex < _rowKeys.Count && _rowKeys[args.ItemIndex] is { } key)
                Requested?.Invoke("rowAction", new Dictionary<string, object?> { [_rowField] = key });
        };
    }

    public void Apply(JsonArray? rows, int page, int pageSize, bool paging, bool sorting, bool filtering,
        string rowKey = "", string rowField = "", string[]? displayColumns = null)
    {
        _applying = true;
        var selected = _rows.GetSelected().Select(_rows.IndexOf).FirstOrDefault(-1);
        var selectedKey = selected >= 0 && selected < _rowKeys.Count ? _rowKeys[selected] : null;
        _rowField = rowField;
        _pageIndex = Math.Clamp(page, 0, 65536);
        _page.Text = Loc.GetString("nc-meta-table-page", ("page", _pageIndex + 1));
        _previous.Visible = _next.Visible = _page.Visible = paging;
        _previous.Disabled = _pageIndex == 0;
        _next.Disabled = rows is null || rows.Count < pageSize;
        _filter.Visible = filtering;
        _sort.Visible = sorting;
        _rows.Clear();
        _rowKeys.Clear();
        var columns = displayColumns ?? (rows?.FirstOrDefault() is JsonObject first ? first.Select(x => x.Key).Take(16).ToArray() : []);
        if (!columns.SequenceEqual(_columns))
        {
            _columns = columns;
            _sort.Clear();
            for (var i = 0; i < columns.Length; i++)
                _sort.AddItem(columns[i], i);
        }
        if (rows is not null)
            foreach (var row in rows.Take(Math.Min(MuiLimits.PageRows, pageSize)))
            {
                var key = rowField.Length > 0 && row is JsonObject keyed && keyed[rowKey] is JsonValue scalar && scalar.TryGetValue<string>(out var value) &&
                    value.Length is > 0 and <= MuiLimits.StringLength ? value : null;
                _rowKeys.Add(key);
                var item = _rows.AddItem(row is JsonObject obj ? string.Join("  |  ", _columns.Select(x => Text(obj[x]))) : Text(row), selectable: key is not null);
                if (key is not null && key == selectedKey) item.Selected = true;
            }
        _applying = false;
    }

    private static string Text(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value?.ToJsonString() ?? "";
}
