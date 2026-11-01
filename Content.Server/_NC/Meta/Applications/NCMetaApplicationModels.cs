// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Immutable;
using Content.Meta.Mui;

namespace Content.Server._NC.Meta.Applications;

/// <summary>Server admission policy. Neither markup nor action data can create or change it.</summary>
public sealed record NCMetaAppPolicy(ImmutableHashSet<string> AudienceParties, ImmutableHashSet<string> PublicViews,
    ImmutableHashSet<string> PublicActions, ImmutableHashSet<string> GrantedScopes)
{
    public static NCMetaAppPolicy OwnerOnly { get; } = new(ImmutableHashSet<string>.Empty,
        ImmutableHashSet<string>.Empty, ImmutableHashSet<string>.Empty, ImmutableHashSet<string>.Empty);
}

public sealed record NCMetaAppRegistration(EntityUid Host, string Owner, Guid RuntimeGeneration,
    MuiVerifiedApplication Application, NCMetaAppPolicy Policy)
{
    internal readonly Dictionary<string, Content.Shared._NC.Meta.UI.NCMetaMuiPresentation> Presentations = new(StringComparer.Ordinal);
}

public enum NCMetaAppPresentation : byte { Desktop, Agent, Website, Dashboard }
