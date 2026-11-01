// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Meta.UI;

/// <summary>Private authorized public-ABI projection. JSON contains no executable artifact or private dependency source.</summary>
[Serializable, NetSerializable]
public sealed record NCMetaLanguageSymbols(Guid ProjectId, long ProjectRevision, string RevisionStamp,
    Guid TransferId, int Utf8Offset, int TotalBytes, string Sha256, string Json, bool Complete, bool Revoked = false);
