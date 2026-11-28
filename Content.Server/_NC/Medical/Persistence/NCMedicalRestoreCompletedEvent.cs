// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Identity;

namespace Content.Server._NC.Medical.Persistence;

/// <summary>
/// Raised on a player character's body once its stored medical state has been loaded (or found empty). Changes to
/// the body before this point would be overwritten or rejected by the restore.
/// </summary>
[ByRefEvent]
public readonly record struct NCMedicalRestoreCompletedEvent(NCCharacterId Character);
