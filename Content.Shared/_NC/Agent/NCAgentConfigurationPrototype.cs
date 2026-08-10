// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Agent;

/// <summary>
/// Data-driven limits and persistence policy for Agent services.
/// </summary>
[Prototype("ncAgentConfiguration")]
public sealed partial class NCAgentConfigurationPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField(required: true)]
    public string AddressPrefix = string.Empty;

    [DataField(required: true)]
    public TimeSpan MessageRetention;

    [DataField]
    public int MaximumDirectoryQueryLength = 64;

    [DataField]
    public int MinimumAliasLength = 3;

    [DataField]
    public int MaximumAliasLength = 32;

    [DataField]
    public int MaximumMessageLength = 1024;

    [DataField]
    public int MaximumPostLength = 2048;

    [DataField]
    public int MaximumCommentLength = 1024;

    [DataField]
    public int DirectoryResultLimit = 30;

    [DataField]
    public int ConversationMessageLimit = 100;

    [DataField]
    public int ConversationPeerLimit = 50;

    [DataField]
    public int FeedPostLimit = 50;

    [DataField]
    public int FeedCommentLimit = 200;
}
