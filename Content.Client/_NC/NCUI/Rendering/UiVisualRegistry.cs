// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Client._NC.NCUI.Core;
using Robust.Client.Graphics;
using Robust.Shared.Maths;

namespace Content.Client._NC.NCUI.Rendering;

/// <summary>
/// Primitive kinds supported by the renderer. Composite domain objects are assembled from these definitions.
/// </summary>
public enum UiVisualKind : byte
{
    Rectangle,
    Polygon,
    Line,
    Polyline,
    Arc,
    Sprite,
    NinePatch,
    Text,
    Icon,
    EntityPreview,
}

/// <summary>
/// Immutable visual data registered outside Draw. Arrays and strings are reused for every frame.
/// </summary>
public sealed class UiVisualDefinition
{
    public required UiVisualKind Kind { get; init; }
    public Color Color { get; init; } = Color.White;
    public Color SecondaryColor { get; init; } = Color.Transparent;
    public bool Filled { get; init; } = true;
    public float Thickness { get; init; } = 1f;
    public Texture? Texture { get; init; }
    public UIBox2? TextureRegion { get; init; }
    public StyleBox? StyleBox { get; init; }
    public Font? Font { get; init; }
    public string Text { get; init; } = string.Empty;
    public Vector2[] Points { get; init; } = [];
    public EntityUid? Entity { get; init; }
    public Vector2 EntityScale { get; init; } = Vector2.One;
}

/// <summary>
/// Stable-handle visual registry. Definitions can be replaced outside Draw without growing the registry.
/// </summary>
public sealed class UiVisualRegistry
{
    private readonly List<Entry> _definitions = [];
    private readonly Stack<int> _free = [];
    private int _activeCount;

    public int Count => _activeCount;

    public UiVisualHandle Register(UiVisualDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateDefinition(definition);
        if (_free.TryPop(out var free))
        {
            _definitions[free] = new Entry(definition, 0);
            _activeCount++;
            return new UiVisualHandle((uint) free + 1);
        }
        _definitions.Add(new Entry(definition, 0));
        _activeCount++;
        return new UiVisualHandle((uint)_definitions.Count);
    }

    public bool TryGet(UiVisualHandle handle, out UiVisualDefinition definition)
    {
        var index = (int)handle.Value - 1;
        if (index < 0 || index >= _definitions.Count || _definitions[index].Definition == null)
        {
            definition = default!;
            return false;
        }

        definition = _definitions[index].Definition!;
        return true;
    }

    public bool Update(UiVisualHandle handle, UiVisualDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateDefinition(definition);
        var index = (int) handle.Value - 1;
        if (index < 0 || index >= _definitions.Count || _definitions[index].Definition == null)
            return false;

        _definitions[index] = new Entry(definition, _definitions[index].References);
        return true;
    }

    public bool Retain(UiVisualHandle handle)
    {
        if (!handle.IsValid)
            return true;
        var index = (int) handle.Value - 1;
        if (index < 0 || index >= _definitions.Count || _definitions[index].Definition == null)
            return false;
        var entry = _definitions[index];
        entry.References++;
        _definitions[index] = entry;
        return true;
    }

    public void Release(UiVisualHandle handle)
    {
        if (!handle.IsValid)
            return;
        var index = (int) handle.Value - 1;
        if (index < 0 || index >= _definitions.Count || _definitions[index].Definition == null)
            return;
        var entry = _definitions[index];
        if (entry.References > 0)
            entry.References--;
        if (entry.References != 0)
        {
            _definitions[index] = entry;
            return;
        }
        _definitions[index] = default;
        _free.Push(index);
        _activeCount--;
    }

    public void Clear()
    {
        _definitions.Clear();
        _free.Clear();
        _activeCount = 0;
    }

    private static void ValidateDefinition(UiVisualDefinition definition)
    {
        if (!float.IsFinite(definition.Thickness) || definition.Thickness < 0f ||
            !float.IsFinite(definition.EntityScale.X) || !float.IsFinite(definition.EntityScale.Y) ||
            definition.EntityScale.X < 0f || definition.EntityScale.Y < 0f ||
            !float.IsFinite(definition.Color.R) || !float.IsFinite(definition.Color.G) ||
            !float.IsFinite(definition.Color.B) || !float.IsFinite(definition.Color.A))
        {
            throw new ArgumentException("NCUI visual dimensions must be finite and non-negative.", nameof(definition));
        }
        foreach (var point in definition.Points)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
                throw new ArgumentException("NCUI visual points must be finite.", nameof(definition));
        }
    }

    private struct Entry
    {
        public UiVisualDefinition? Definition;
        public int References;

        public Entry(UiVisualDefinition definition, int references)
        {
            Definition = definition;
            References = references;
        }
    }
}
