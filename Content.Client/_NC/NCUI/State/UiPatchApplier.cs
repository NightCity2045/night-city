// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Diagnostics;
using Content.Shared._NC.NCUI;

namespace Content.Client._NC.NCUI.State;

public enum UiPatchApplyResult : byte
{
    Applied,
    IgnoredOld,
    RequiresResync,
    WrongSession,
}

/// <summary>
/// Enforces an uninterrupted revision chain before handing typed domain patches to a presenter.
/// </summary>
public sealed class UiPatchApplier
{
    public NcUiSessionId Session { get; private set; } = NcUiSessionId.Invalid;
    public NcUiRevision Revision { get; private set; }
    public double LastApplyMilliseconds { get; private set; }
    public event Action<NcUiRevision>? ResyncRequired;

    public bool ApplySnapshot(NcUiSnapshotMessage snapshot, Action<NcUiSnapshotMessage> apply)
    {
        if (!snapshot.Session.IsValid || snapshot.Revision.Value == 0)
            throw new ArgumentException("An NCUI snapshot requires a valid session and revision.", nameof(snapshot));
        if (snapshot.Session == Session && Revision.IsAfter(snapshot.Revision))
            return false;
        var started = Stopwatch.GetTimestamp();
        apply(snapshot);
        Session = snapshot.Session;
        Revision = snapshot.Revision;
        LastApplyMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return true;
    }

    public UiPatchApplyResult ApplyPatch(NcUiPatchMessage patch, Action<NcUiPatchMessage> apply)
    {
        if (patch.Session != Session)
            return UiPatchApplyResult.WrongSession;
        if (patch.Revision == Revision || !patch.Revision.IsAfter(Revision))
            return UiPatchApplyResult.IgnoredOld;
        if (patch.BaseRevision != Revision || !patch.Revision.IsImmediatelyAfter(Revision))
        {
            ResyncRequired?.Invoke(Revision);
            return UiPatchApplyResult.RequiresResync;
        }
        var started = Stopwatch.GetTimestamp();
        apply(patch);
        Revision = patch.Revision;
        LastApplyMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return UiPatchApplyResult.Applied;
    }

    public void Reset()
    {
        Session = NcUiSessionId.Invalid;
        Revision = default;
        LastApplyMilliseconds = 0;
    }
}
