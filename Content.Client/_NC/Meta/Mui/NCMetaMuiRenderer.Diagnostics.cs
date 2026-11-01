// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
#if TOOLS && NC_DIAGNOSTICS
using System.Text.Json;
using Content.Shared._NC.Meta.UI;

namespace Content.Client._NC.Meta.Mui;

public sealed partial class NCMetaMuiRenderer
{
    // Only the explicitly trusted isolated diagnostic build accepts raw test input.
    // Ordinary controls use the same typed-input validation without a client JSON dependency.
    public bool TryRequestAction(string actionId, string json)
    {
        if (json.Length > NCMetaMuiValues.ActionBytes || System.Text.Encoding.UTF8.GetByteCount(json) > NCMetaMuiValues.ActionBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = NCMetaMuiValues.Depth });
            var nodes = NCMetaMuiValues.Nodes;
            return Read(document.RootElement, ref nodes) is NCMetaUiObject values && TryRequestAction(actionId, values);
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static NCMetaUiValue Read(JsonElement element, ref int nodes)
    {
        if (--nodes < 0) throw new InvalidOperationException();
        switch (element.ValueKind)
        {
            case JsonValueKind.Null: return new NCMetaUiScalar();
            case JsonValueKind.True: case JsonValueKind.False: return NCMetaMuiValues.Scalar(element.GetBoolean());
            case JsonValueKind.String: return NCMetaMuiValues.Scalar(element.GetString());
            case JsonValueKind.Number: return element.TryGetInt64(out var integer) ? NCMetaMuiValues.Scalar(integer) : NCMetaMuiValues.Scalar(element.GetDouble());
            case JsonValueKind.Array:
                var array = new NCMetaUiArray();
                if (element.GetArrayLength() > NCMetaMuiValues.ChartPoints) throw new InvalidOperationException();
                foreach (var item in element.EnumerateArray()) array.Items.Add(Read(item, ref nodes));
                return array;
            case JsonValueKind.Object:
                var obj = new NCMetaUiObject();
                foreach (var property in element.EnumerateObject())
                { if (!obj.Fields.TryAdd(property.Name, Read(property.Value, ref nodes))) throw new InvalidOperationException(); }
                return obj;
            default: throw new InvalidOperationException();
        }
    }
}
#endif
