// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Meta.UI;

// These are presentation data, not VM types or executable markup. The server validates MUI before creating them.
[Serializable, NetSerializable]
public enum NCMetaMuiControlKind : byte
{
    Window, Page, Row, Column, Grid, Panel, Text, Header, Button, Form, Input, NumberInput,
    Select, Option, Toggle, Tabs, Tab, Scroll, Dialog, List, Table, Validation, Image,
    Chart, Map, Schematic, Component,
}

[Serializable, NetSerializable]
public enum NCMetaMuiValueKind : byte { String, Integer, Number, Boolean, Object, Array }

[Serializable, NetSerializable]
public sealed record NCMetaMuiField(NCMetaMuiValueKind Kind, bool Required, int MaxLength, double? Minimum, double? Maximum);

[Serializable, NetSerializable]
public sealed record NCMetaMuiBinding(string Path, string? Format, bool Negated);

[Serializable, NetSerializable]
public sealed class NCMetaMuiNode
{
    public string Id = "";
    public NCMetaMuiControlKind Kind;
    public Dictionary<string, string> Properties = new();
    public Dictionary<string, NCMetaMuiBinding> Bindings = new();
    public List<NCMetaMuiNode> Children = new();
}

[Serializable, NetSerializable]
public sealed record NCMetaMuiAction(string Function, Dictionary<string, NCMetaMuiField> Inputs, List<string> Scopes, string? InputType);

[Serializable, NetSerializable]
public sealed record NCMetaMuiView(string Function, string Document, Dictionary<string, NCMetaMuiField> Fields, List<string> Actions, string? StateType);

[Serializable, NetSerializable]
public sealed record NCMetaMuiStyleRule(string Selector, string State, int MinWidth, int MaxWidth, Dictionary<string, string> Properties);

[Serializable, NetSerializable]
public sealed class NCMetaMuiPresentation
{
    public string Version = "1.0";
    public NCMetaMuiNode Root = new();
    public int NodeCount;
    public Dictionary<string, NCMetaMuiView> Views = new();
    public Dictionary<string, NCMetaMuiAction> Actions = new();
    public List<string> Resources = new();
    public List<NCMetaMuiStyleRule> Styles = new();
}

[Serializable, NetSerializable]
public enum NCMetaUiValueKind : byte { Null, String, Integer, Number, Boolean, Object, Array }

/// <summary>Bounded projection values. No type names, deserialization directives, entity handles or executable data.</summary>
[Serializable, NetSerializable]
public abstract class NCMetaUiValue
{
    public abstract NCMetaUiValueKind Kind { get; }
    public abstract NCMetaUiValue DeepClone();
}

[Serializable, NetSerializable]
public sealed class NCMetaUiScalar : NCMetaUiValue
{
    public NCMetaUiValueKind ValueKind;
    public override NCMetaUiValueKind Kind => ValueKind;
    public string Text = "";
    public long Integer;
    public double Number;
    public bool Boolean;
    public override NCMetaUiValue DeepClone() => new NCMetaUiScalar
    { ValueKind = ValueKind, Text = Text, Integer = Integer, Number = Number, Boolean = Boolean };
}

[Serializable, NetSerializable]
public sealed class NCMetaUiObject : NCMetaUiValue, IEnumerable<KeyValuePair<string, NCMetaUiValue>>
{
    public override NCMetaUiValueKind Kind => NCMetaUiValueKind.Object;
    public Dictionary<string, NCMetaUiValue> Fields = new(StringComparer.Ordinal);
    public int Count => Fields.Count;
    public NCMetaUiValue? this[string name] { get => Fields.GetValueOrDefault(name); set => Fields[name] = value ?? new NCMetaUiScalar(); }
    public bool TryGetPropertyValue(string name, out NCMetaUiValue? value)
    { var found = Fields.TryGetValue(name, out var result); value = result; return found; }
    public bool Remove(string name) => Fields.Remove(name);
    public void Clear() => Fields.Clear();
    public override NCMetaUiValue DeepClone()
    {
        var copy = new NCMetaUiObject();
        foreach (var pair in Fields) copy.Fields.Add(pair.Key, pair.Value.DeepClone());
        return copy;
    }
    public IEnumerator<KeyValuePair<string, NCMetaUiValue>> GetEnumerator() => Fields.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

[Serializable, NetSerializable]
public sealed class NCMetaUiArray : NCMetaUiValue, IEnumerable<NCMetaUiValue>
{
    public override NCMetaUiValueKind Kind => NCMetaUiValueKind.Array;
    public List<NCMetaUiValue> Items = new();
    public int Count => Items.Count;
    public NCMetaUiValue this[int index] => Items[index];
    public override NCMetaUiValue DeepClone()
    {
        var copy = new NCMetaUiArray();
        foreach (var item in Items) copy.Items.Add(item.DeepClone());
        return copy;
    }
    public IEnumerator<NCMetaUiValue> GetEnumerator() => Items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
