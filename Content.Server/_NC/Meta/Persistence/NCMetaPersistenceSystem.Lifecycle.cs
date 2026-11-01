// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Server._NC.Meta.Persistence;

public sealed partial class NCMetaPersistenceSystem
{
    private sealed class MutationLease(NCMetaPersistenceSystem owner, Guid id, TaskCompletionSource completion) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._mutationGate)
            {
                owner._mutations.Remove(id);
                completion.TrySetResult();
            }
        }
    }
    private readonly object _mutationGate = new();
    private readonly Dictionary<Guid, Task> _mutations = [];
    private bool _accepting = true;
    public bool IsAcceptingCommands { get { lock (_mutationGate) return _accepting; } }
    public IDisposable? TryBeginNativeMutation() => TryBeginMutation();
    public void ResumeIngress() { lock (_mutationGate) _accepting = true; }
    public Task QuiesceAsync()
    {
        lock (_mutationGate)
        {
            _accepting = false;
            return Task.WhenAll(_mutations.Values.ToArray());
        }
    }
    private MutationLease? TryBeginMutation()
    {
        lock (_mutationGate)
        {
            if (!_accepting || _mutations.Count >= 64) return null;
            var id = Guid.NewGuid();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _mutations.Add(id, completion.Task);
            return new MutationLease(this, id, completion);
        }
    }
}
