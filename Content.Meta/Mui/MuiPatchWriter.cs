// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text;
using System.Text.Json;

namespace Content.Meta.Mui;

/// <summary>One top-level field comparison/write per step. Only validated typed projection values enter raw JSON writes.</summary>
public sealed class MuiPatchWriter : IDisposable
{
    private readonly IReadOnlyDictionary<string, string> _previous;
    private readonly MuiProjectionResult _next;
    private readonly string[] _names;
    private readonly string[] _removed;
    private readonly MemoryStream _bytes = new();
    private readonly Utf8JsonWriter _writer;
    private int _index;
    private int _removedIndex;
    private bool _removing;
    public MuiCodecProgress Progress { get; private set; }
    public bool FullSnapshot { get; private set; }
    public bool Changed { get; private set; }
    public string? Body { get; private set; }
    public long ReservationBytes => 8192L + _bytes.Length * 8;
    public int NextGas => 32 + (_index < _names.Length ? _next.Fields[_names[_index]].Length / 16 : 0);

    public MuiPatchWriter(IReadOnlyDictionary<string, string> previous, MuiProjectionResult next, bool fullSnapshot)
    {
        _previous = previous; _next = next;
        if (previous.Count > 64 || next.Fields.Count > 64 || previous.Keys.Any(key => key.Length > 128))
            throw new ArgumentException("Projection field budget.");
        _names = next.Fields.Keys.ToArray(); _removed = previous.Keys.Where(key => !next.Fields.ContainsKey(key)).ToArray();
        _writer = new(_bytes); _writer.WriteStartObject(); _writer.WritePropertyName("set"); _writer.WriteStartObject(); _writer.Flush();
        if (fullSnapshot) CompleteFull();
    }

    public MuiCodecProgress Step()
    {
        if (Progress != MuiCodecProgress.Working) return Progress;
        try
        {
            if (_index < _names.Length)
            {
                var name = _names[_index++]; var value = _next.Fields[name];
                if (_previous.TryGetValue(name, out var old) && string.Equals(old, value, StringComparison.Ordinal)) return Progress;
                Changed = true;
                // Projection encoding escapes non-ASCII scalars. This bound also covers a raw UTF-8 fallback.
                if (value.Length > MuiLimits.PatchBytes || Encoding.UTF8.GetByteCount(value) > MuiLimits.PatchBytes - _bytes.Length - name.Length * 6 - 16)
                { CompleteFull(); return Progress; }
                _writer.WritePropertyName(name); _writer.WriteRawValue(value, skipInputValidation: true); _writer.Flush();
            }
            else if (!_removing)
            {
                _writer.WriteEndObject(); _writer.WritePropertyName("remove"); _writer.WriteStartArray(); _writer.Flush(); _removing = true;
            }
            else if (_removedIndex < _removed.Length)
            { Changed = true; _writer.WriteStringValue(_removed[_removedIndex++]); _writer.Flush(); }
            else
            {
                _writer.WriteEndArray(); _writer.WriteEndObject(); _writer.Flush();
                if (_bytes.Length > MuiLimits.PatchBytes) CompleteFull();
                else { Body = Encoding.UTF8.GetString(_bytes.GetBuffer().AsSpan(0, checked((int) _bytes.Length))); Progress = MuiCodecProgress.Complete; }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { Progress = MuiCodecProgress.Invalid; }
        return Progress;
    }

    private void CompleteFull() { FullSnapshot = true; Changed = true; Body = _next.Json; Progress = MuiCodecProgress.Complete; }
    public void Dispose() { _writer.Dispose(); _bytes.Dispose(); }
}
