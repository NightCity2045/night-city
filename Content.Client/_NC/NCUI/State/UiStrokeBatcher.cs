// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;

namespace Content.Client._NC.NCUI.State;

/// <summary>
/// Filters dense pointer paths and emits bounded batches at a configured 10-20 Hz network cadence.
/// </summary>
public sealed class UiStrokeBatcher
{
    private readonly List<Vector2> _pending = [];
    private TimeSpan _lastFlush;
    private Vector2 _lastAccepted;
    private bool _hasLast;
    private float _minimumPointDistance = 0.5f;
    private int _maximumPointsPerBatch = 96;
    private TimeSpan _flushInterval = TimeSpan.FromMilliseconds(66);

    public float MinimumPointDistance
    {
        get => _minimumPointDistance;
        set => _minimumPointDistance = float.IsFinite(value) && value >= 0f
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public int MaximumPointsPerBatch
    {
        get => _maximumPointsPerBatch;
        set => _maximumPointsPerBatch = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public TimeSpan FlushInterval
    {
        get => _flushInterval;
        set => _flushInterval = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }
    public int PendingCount => _pending.Count;
    public bool Active { get; private set; }
    public event Action<Vector2[], TimeSpan>? BatchReady;

    public void Begin(Vector2 point, TimeSpan now)
    {
        _pending.Clear();
        Active = true;
        _hasLast = false;
        _lastFlush = now;
        Add(point, now);
    }

    public bool Add(Vector2 point, TimeSpan now)
    {
        if (!Active || !float.IsFinite(point.X) || !float.IsFinite(point.Y))
            return false;
        if (_hasLast && Vector2.DistanceSquared(point, _lastAccepted) <
            MinimumPointDistance * MinimumPointDistance)
        {
            return false;
        }
        _lastAccepted = point;
        _hasLast = true;
        _pending.Add(point);
        if (_pending.Count >= MaximumPointsPerBatch || now - _lastFlush >= FlushInterval)
            Flush(now);
        return true;
    }

    public void Commit(TimeSpan now)
    {
        if (!Active)
            return;
        Flush(now);
        Active = false;
        _hasLast = false;
    }

    public void Cancel()
    {
        _pending.Clear();
        Active = false;
        _hasLast = false;
    }

    public void Update(TimeSpan now)
    {
        if (Active && _pending.Count != 0 && now - _lastFlush >= FlushInterval)
            Flush(now);
    }

    private void Flush(TimeSpan now)
    {
        if (_pending.Count == 0)
            return;
        var batch = _pending.ToArray();
        _pending.Clear();
        _lastFlush = now;
        BatchReady?.Invoke(batch, now);
    }
}
