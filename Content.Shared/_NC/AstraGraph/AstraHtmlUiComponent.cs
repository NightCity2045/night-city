// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.AstraGraph;

[Serializable, NetSerializable]
public enum AstraHtmlUiKey : byte
{
    Key,
}

/// <summary>
/// Names the HTML interface authored in Studio. The client opens that file instead of a XAML window.
/// </summary>
[RegisterComponent]
public sealed partial class AstraHtmlUiComponent : Component
{
    [DataField(required: true)]
    public string Document = string.Empty;
}
