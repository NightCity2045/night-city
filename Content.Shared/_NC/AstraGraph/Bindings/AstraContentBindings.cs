// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;

namespace Content.Shared._NC.AstraGraph.Bindings;

/// <summary>
/// Night City methods that graphs may call.
/// The catalog marks every indexed method as gameplay, so only this namespace is indexed.
/// </summary>
public static class AstraContentBindings
{
    public const string IndexedNamespace = "Content.Shared._NC.AstraGraph.Bindings";

    public static bool IsIndexedType(Type type)
    {
        if (type is null)
            throw new ArgumentException("Type is required.", nameof(type));
        return type.Namespace != null
               && (type.Namespace == IndexedNamespace
                   || type.Namespace.StartsWith(IndexedNamespace + ".", StringComparison.Ordinal));
    }
}
