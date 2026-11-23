// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Meta.UI;

/// <summary>Typed command data on the existing BUI. Authority still comes exclusively from the server's UI context.</summary>
[Serializable, NetSerializable]
public abstract record NCMetaWorkspacePayload;

[Serializable, NetSerializable]
public sealed record NCMetaBindingsPayload(List<NCMetaSoftwareBinding> Bindings) : NCMetaWorkspacePayload;

[Serializable, NetSerializable]
public sealed record NCMetaHostingQuotePayload(string Domain, string View, int Units) : NCMetaWorkspacePayload;

/// <summary>A new program chip made from the project's last release: the player names and describes it.</summary>
[Serializable, NetSerializable]
public sealed record NCMetaChipPayload(string Name, string Description, bool Open = false) : NCMetaWorkspacePayload;
