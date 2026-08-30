// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.NCUI;

namespace Content.Client._NC.NCUI.State;

public interface IUiPrediction<TState>
{
    void Apply(TState state);
    void Rollback(TState state);
}

/// <summary>
/// Reconciles reversible presentation predictions around authoritative patches and action results.
/// </summary>
public sealed class UiPredictionBuffer<TState>
{
    private readonly List<PendingPrediction> _pending = [];
    private readonly TState _state;

    public int Count => _pending.Count;
    public event Action? Changed;

    public UiPredictionBuffer(TState state)
    {
        _state = state;
    }

    public void Predict(NcUiActionSequence sequence, IUiPrediction<TState> prediction)
    {
        if (_pending.Exists(entry => entry.Sequence == sequence))
            throw new ArgumentException("A prediction sequence must be unique.", nameof(sequence));
        prediction.Apply(_state);
        _pending.Add(new PendingPrediction(sequence, prediction));
        Changed?.Invoke();
    }

    public void ApplyAuthoritative(Action<TState> apply)
    {
        RollbackAll();
        apply(_state);
        ReapplyAll();
        Changed?.Invoke();
    }

    public void ApplyAuthoritative(NcUiRevision revision, Action<TState> apply)
    {
        RollbackAll();
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            if (_pending[i].AcknowledgedAt is { } acknowledged &&
                (revision == acknowledged || revision.IsAfter(acknowledged)))
            {
                _pending.RemoveAt(i);
            }
        }
        apply(_state);
        ReapplyAll();
        Changed?.Invoke();
    }

    public bool Resolve(NcUiActionResultMessage result, bool authoritativeApplied = false)
    {
        var index = _pending.FindIndex(entry => entry.Sequence == result.Sequence);
        if (index < 0)
            return false;
        if (!authoritativeApplied && result.Result is NcUiActionResult.Accepted or
            NcUiActionResult.AcceptedWithCorrection)
        {
            var pending = _pending[index];
            pending.AcknowledgedAt = result.Revision;
            _pending[index] = pending;
            Changed?.Invoke();
            return true;
        }
        RollbackAll();
        _pending.RemoveAt(index);
        ReapplyAll();
        Changed?.Invoke();
        return true;
    }

    public void Clear(bool rollback = true)
    {
        if (rollback)
            RollbackAll();
        _pending.Clear();
        Changed?.Invoke();
    }

    private void RollbackAll()
    {
        for (var i = _pending.Count - 1; i >= 0; i--)
            _pending[i].Prediction.Rollback(_state);
    }

    private void ReapplyAll()
    {
        for (var i = 0; i < _pending.Count; i++)
            _pending[i].Prediction.Apply(_state);
    }

    private struct PendingPrediction
    {
        public NcUiActionSequence Sequence;
        public IUiPrediction<TState> Prediction;
        public NcUiRevision? AcknowledgedAt;

        public PendingPrediction(NcUiActionSequence sequence, IUiPrediction<TState> prediction)
        {
            Sequence = sequence;
            Prediction = prediction;
            AcknowledgedAt = null;
        }
    }
}
