// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Globalization;
using System.Linq;
using System.Text;
using Content.Shared._NC.Meta.UI;

namespace Content.Client._NC.Meta.Mui;

/// <summary>Access and validation of data projections. It never parses markup, CLR type names or executable values.</summary>
public static class NCMetaMuiValues
{
    public const int Nodes = 2048, Depth = 32, Controls = 512, InitialBytes = 65536, PatchBytes = 16384, ActionBytes = 8192;
    public const int ChartPoints = 512, Animations = 16, PageRows = 64, StringLength = 4096;
    public static bool TryGetValue<T>(this NCMetaUiScalar value, out T result)
    {
        object? scalar = value.Kind switch
        {
            NCMetaUiValueKind.String => value.Text,
            NCMetaUiValueKind.Boolean => value.Boolean,
            NCMetaUiValueKind.Integer when typeof(T) == typeof(double) => (double) value.Integer,
            NCMetaUiValueKind.Integer when typeof(T) == typeof(int) && value.Integer is >= int.MinValue and <= int.MaxValue => (int) value.Integer,
            NCMetaUiValueKind.Integer => value.Integer,
            NCMetaUiValueKind.Number => value.Number,
            _ => null,
        };
        result = scalar is T typed ? typed : default!;
        return scalar is T;
    }

    public static NCMetaUiScalar Scalar(object? value) => value switch
    {
        null => new(),
        string text => new() { ValueKind = NCMetaUiValueKind.String, Text = text },
        bool boolean => new() { ValueKind = NCMetaUiValueKind.Boolean, Boolean = boolean },
        int integer => new() { ValueKind = NCMetaUiValueKind.Integer, Integer = integer },
        long integer => new() { ValueKind = NCMetaUiValueKind.Integer, Integer = integer },
        double number when double.IsFinite(number) => new() { ValueKind = NCMetaUiValueKind.Number, Number = number },
        _ => throw new ArgumentException("Unsupported UI scalar."),
    };

    public static bool Matches(NCMetaUiValue value, NCMetaMuiField field)
    {
        if (value.Kind == NCMetaUiValueKind.Null) return !field.Required;
        return field.Kind switch
        {
            NCMetaMuiValueKind.String => value is NCMetaUiScalar { ValueKind: NCMetaUiValueKind.String } s && s.Text.Length <= field.MaxLength,
            NCMetaMuiValueKind.Boolean => value.Kind == NCMetaUiValueKind.Boolean,
            NCMetaMuiValueKind.Integer => value is NCMetaUiScalar { ValueKind: NCMetaUiValueKind.Integer } i && InRange(i.Integer, field),
            NCMetaMuiValueKind.Number => value is NCMetaUiScalar n && n.TryGetValue<double>(out var number) && double.IsFinite(number) && InRange(number, field),
            NCMetaMuiValueKind.Object => value is NCMetaUiObject,
            NCMetaMuiValueKind.Array => value is NCMetaUiArray a && a.Count <= field.MaxLength,
            _ => false,
        };
    }
    private static bool InRange(double number, NCMetaMuiField field) =>
        (!field.Minimum.HasValue || number >= field.Minimum.Value) && (!field.Maximum.HasValue || number <= field.Maximum.Value);

    public static bool Validate(NCMetaUiValue value, IReadOnlyDictionary<string, NCMetaMuiField> schema, int maximumBytes)
    {
        if (value is not NCMetaUiObject root || !Budget(value)) return false;
        foreach (var pair in root)
            if (!schema.TryGetValue(pair.Key, out var field) || !Matches(pair.Value, field)) return false;
        if (schema.Any(pair => pair.Value.Required && !root.Fields.ContainsKey(pair.Key))) return false;
        return TryWrite(value, maximumBytes, out _);
    }

    public static bool Budget(NCMetaUiValue value)
    {
        var pending = new Stack<(NCMetaUiValue Value, int Depth)>();
        pending.Push((value, 0)); var remaining = Nodes;
        while (pending.TryPop(out var current))
        {
            if (--remaining < 0 || current.Depth > Depth) return false;
            switch (current.Value)
            {
                case NCMetaUiObject obj:
                    if (obj.Count > Nodes) return false;
                    foreach (var pair in obj)
                    { if (pair.Key.Length > 128 || pair.Value == null || pending.Count >= remaining) return false; pending.Push((pair.Value, current.Depth + 1)); }
                    break;
                case NCMetaUiArray array:
                    if (array.Count > 512) return false;
                    foreach (var item in array) { if (item == null || pending.Count >= remaining) return false; pending.Push((item, current.Depth + 1)); }
                    break;
                case NCMetaUiScalar scalar:
                    if (scalar.Kind is NCMetaUiValueKind.Object or NCMetaUiValueKind.Array || !Enum.IsDefined(scalar.Kind) ||
                        scalar.Kind == NCMetaUiValueKind.String && (scalar.Text == null || scalar.Text.Length > 4096) ||
                        scalar.Kind == NCMetaUiValueKind.Number && !double.IsFinite(scalar.Number)) return false;
                    break;
                default: return false;
            }
        }
        return true;
    }

    public static string ToJsonString(this NCMetaUiValue value) => TryWrite(value, InitialBytes, out var json) ? json : "";
    public static bool TryWrite(NCMetaUiValue value, int maximumBytes, out string json)
    {
        json = "";
        if (!Budget(value)) return false;
        var output = new StringBuilder();
        void Quote(string text)
        {
            output.Append('"');
            foreach (var c in text)
            {
                if (c is '"' or '\\') { output.Append('\\'); output.Append(c); }
                else if (c < 32) { output.Append("\\u"); output.Append(((int) c).ToString("x4", CultureInfo.InvariantCulture)); }
                else output.Append(c);
            }
            output.Append('"');
        }
        void Write(NCMetaUiValue item)
        {
            if (output.Length > maximumBytes) throw new ArgumentException();
            if (item is NCMetaUiObject obj)
            {
                output.Append('{'); var first = true;
                foreach (var pair in obj) { if (!first) output.Append(','); first = false; Quote(pair.Key); output.Append(':'); Write(pair.Value); }
                output.Append('}');
            }
            else if (item is NCMetaUiArray array)
            {
                output.Append('['); for (var i = 0; i < array.Count; i++) { if (i != 0) output.Append(','); Write(array[i]); } output.Append(']');
            }
            else if (item is NCMetaUiScalar scalar)
            {
                switch (scalar.Kind)
                {
                    case NCMetaUiValueKind.Null: output.Append("null"); break;
                    case NCMetaUiValueKind.String: Quote(scalar.Text); break;
                    case NCMetaUiValueKind.Boolean: output.Append(scalar.Boolean ? "true" : "false"); break;
                    case NCMetaUiValueKind.Integer: output.Append(scalar.Integer.ToString(CultureInfo.InvariantCulture)); break;
                    case NCMetaUiValueKind.Number: output.Append(scalar.Number.ToString("R", CultureInfo.InvariantCulture)); break;
                }
            }
        }
        try
        {
            Write(value); if (output.Length > maximumBytes) return false;
            var text = output.ToString();
            for (var i = 0; i < text.Length; i++)
                if (char.IsSurrogate(text[i]) && (!char.IsHighSurrogate(text[i]) || ++i >= text.Length || !char.IsLowSurrogate(text[i]))) return false;
            if (Encoding.UTF8.GetByteCount(text) > maximumBytes) return false;
            json = text; return true;
        }
        catch (ArgumentException) { return false; }
    }

    public static bool Same(NCMetaUiValue left, NCMetaUiValue right)
    {
        if (left.Kind != right.Kind) return false;
        if (left is NCMetaUiObject a && right is NCMetaUiObject b) return a.Count == b.Count && a.All(pair => b.Fields.TryGetValue(pair.Key, out var value) && Same(pair.Value, value));
        if (left is NCMetaUiArray x && right is NCMetaUiArray y) return x.Count == y.Count && x.Select((item, index) => Same(item, y[index])).All(equal => equal);
        return left is NCMetaUiScalar p && right is NCMetaUiScalar q && p.Text == q.Text && p.Integer == q.Integer && p.Number == q.Number && p.Boolean == q.Boolean;
    }

    public static bool PresentationValid(NCMetaMuiPresentation definition, string viewId)
    {
        if (definition == null || definition.Root == null || definition.Views == null || definition.Actions == null || definition.Resources == null || definition.Styles == null ||
            definition.Version != "1.0" || definition.NodeCount is < 1 or > Nodes || definition.Views.Count is < 1 or > 32 ||
            !definition.Views.ContainsKey(viewId) || definition.Actions.Count > 128 || definition.Resources.Count > 128 ||
            definition.Resources.Distinct(StringComparer.Ordinal).Count() != definition.Resources.Count || definition.Styles.Count > 256) return false;
        var pending = new Stack<(NCMetaMuiNode Node, int Depth)>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        if (definition.Resources.Any(resource => resource is not { Length: > 0 and <= 256 }) ||
            definition.Views.Any(pair => pair.Key is not { Length: > 0 and <= 128 } || pair.Value == null || pair.Value.Fields == null || pair.Value.Actions == null ||
                pair.Value.Fields.Count > 256 || pair.Value.Actions.Count > 128 || pair.Value.Actions.Any(action => !definition.Actions.ContainsKey(action))) ||
            definition.Actions.Any(pair => pair.Key is not { Length: > 0 and <= 128 } || pair.Value == null || pair.Value.Inputs == null || pair.Value.Scopes == null ||
                pair.Value.Inputs.Count > 256 || pair.Value.Scopes.Count > 128) ||
            definition.Views.Values.SelectMany(view => view.Fields).Concat(definition.Actions.Values.SelectMany(action => action.Inputs)).Any(pair =>
                pair.Key is not { Length: > 0 and <= 128 } || pair.Value == null || !Enum.IsDefined(pair.Value.Kind) ||
                pair.Value.MaxLength is < 0 or > StringLength || pair.Value.Minimum is { } min && !double.IsFinite(min) || pair.Value.Maximum is { } max && !double.IsFinite(max)) ||
            definition.Styles.Any(rule => rule == null || rule.Selector is not { Length: > 1 and <= 129 } || rule.State == null || rule.Properties == null ||
                rule.Properties.Count > 32 || rule.Properties.Any(pair => pair.Key.Length > 128 || pair.Value is not { Length: <= StringLength }))) return false;
        pending.Push((definition.Root, 0)); var count = 0; var cost = 0; var animated = 0; var charts = 0;
        while (pending.TryPop(out var current))
        {
            var node = current.Node;
            if (node == null || node.Properties == null || node.Bindings == null || node.Children == null ||
                ++count > Nodes || current.Depth >= Depth || node.Id is not { Length: > 0 and <= 128 } || !ids.Add(node.Id) ||
                !Enum.IsDefined(node.Kind) || node.Properties.Count + node.Bindings.Count > 24 || node.Children.Count > Nodes) return false;
            foreach (var property in node.Properties)
                if (property.Key.Length > 128 || property.Value is not { Length: <= StringLength }) return false;
            foreach (var binding in node.Bindings.Values)
                if (binding == null || binding.Path is not { Length: > 0 and <= 128 } || binding.Format?.Length > 32) return false;
            cost += node.Kind switch { NCMetaMuiControlKind.Button => 2, NCMetaMuiControlKind.Toggle => 4,
                NCMetaMuiControlKind.Select => 12 + node.Children.Count * 2, NCMetaMuiControlKind.Scroll => 3,
                NCMetaMuiControlKind.List or NCMetaMuiControlKind.Table => 64, NCMetaMuiControlKind.Option => 0, _ => 1 };
            if (node.Kind is NCMetaMuiControlKind.Chart or NCMetaMuiControlKind.Map or NCMetaMuiControlKind.Schematic && ++charts > 8) return false;
            if (cost > Controls) return false;
            var classes = node.Properties.GetValueOrDefault("class", "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (definition.Styles.Any(rule => rule.Properties.ContainsKey("animation") && rule.Selector.Length > 1 &&
                    (rule.Selector[0] == '#' ? rule.Selector[1..] == node.Id : classes.Contains(rule.Selector[1..]))) && ++animated > Animations) return false;
            foreach (var child in node.Children)
            {
                if (child == null || pending.Count + count >= Nodes || node.Kind == NCMetaMuiControlKind.Select &&
                    (child.Kind != NCMetaMuiControlKind.Option || child.Children == null || child.Children.Count != 0)) return false;
                pending.Push((child, current.Depth + 1));
            }
        }
        return count == definition.NodeCount && cost <= Controls;
    }
}

public enum NCMetaMuiUpdateResult : byte { Applied, Duplicate, Gap, Invalid, Closed }

internal sealed class NCMetaMuiSessionState(Guid sessionId, IReadOnlyDictionary<string, NCMetaMuiField> schema) : IDisposable
{
    private NCMetaUiObject _state = new();
    public Guid SessionId { get; } = sessionId;
    public long Revision { get; private set; } = -1;
    public bool Closed { get; private set; }
    public NCMetaMuiUpdateResult TryApplySnapshot(long revision, NCMetaUiValue? values)
    {
        if (Closed) return NCMetaMuiUpdateResult.Closed;
        if (revision < 0 || values == null || !NCMetaMuiValues.Validate(values, schema, NCMetaMuiValues.InitialBytes)) return NCMetaMuiUpdateResult.Invalid;
        if (revision < Revision) return NCMetaMuiUpdateResult.Duplicate;
        if (revision == Revision) return NCMetaMuiValues.Same(_state, values) ? NCMetaMuiUpdateResult.Duplicate : NCMetaMuiUpdateResult.Invalid;
        _state = (NCMetaUiObject) values.DeepClone(); Revision = revision; return NCMetaMuiUpdateResult.Applied;
    }
    public NCMetaMuiUpdateResult TryApplyPatch(long previous, long revision, NCMetaUiValue? values)
    {
        if (Closed) return NCMetaMuiUpdateResult.Closed;
        if (revision <= Revision) return NCMetaMuiUpdateResult.Duplicate;
        if (previous != Revision || revision != previous + 1) return NCMetaMuiUpdateResult.Gap;
        if (values is not NCMetaUiObject patch || !NCMetaMuiValues.TryWrite(patch, NCMetaMuiValues.PatchBytes, out _)) return NCMetaMuiUpdateResult.Invalid;
        var candidate = (NCMetaUiObject) _state.DeepClone(); var touched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in patch)
        {
            if (operation.Key == "set" && operation.Value is NCMetaUiObject set)
            {
                foreach (var pair in set)
                {
                    if (!touched.Add(pair.Key) || !schema.TryGetValue(pair.Key, out var field) || !NCMetaMuiValues.Matches(pair.Value, field)) return NCMetaMuiUpdateResult.Invalid;
                    candidate[pair.Key] = pair.Value.DeepClone();
                }
            }
            else if (operation.Key == "remove" && operation.Value is NCMetaUiArray remove)
            {
                foreach (var item in remove)
                {
                    if (item is not NCMetaUiScalar scalar || !scalar.TryGetValue<string>(out var key) || !touched.Add(key) ||
                        !schema.TryGetValue(key, out var field) || field.Required) return NCMetaMuiUpdateResult.Invalid;
                    candidate.Remove(key);
                }
            }
            else return NCMetaMuiUpdateResult.Invalid;
        }
        if (!NCMetaMuiValues.Validate(candidate, schema, NCMetaMuiValues.InitialBytes)) return NCMetaMuiUpdateResult.Invalid;
        _state = candidate; Revision = revision; return NCMetaMuiUpdateResult.Applied;
    }
    public NCMetaUiValue? Read(string path)
    {
        if (Closed || path.Length > 128) return null;
        if (_state.TryGetPropertyValue(path, out var exact)) return exact?.DeepClone();
        NCMetaUiValue? result = _state;
        foreach (var segment in path.Split('.'))
            if (result is not NCMetaUiObject obj || !obj.TryGetPropertyValue(segment, out result)) return null;
        return result?.DeepClone();
    }
    public void Dispose() { _state.Clear(); Closed = true; }
}
