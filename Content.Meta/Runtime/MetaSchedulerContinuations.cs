// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Meta.Runtime;

/// <summary>Trusted host-work policy, distinct from the artifact's instruction gas table.</summary>
public sealed record MetaContinuationPolicy
{
    public const string Version = "meta.host-work/1";
    public int MinimumStepGas { get; init; } = 8;
    public int MaximumStepGas { get; init; } = 8192;
    public int AuxiliaryLeasesPerProcess { get; init; } = 192;
    public long AuxiliaryBytesPerProcess { get; init; } = 2 * 1024 * 1024;
    public long AuxiliaryBytesPerPrincipal { get; init; } = 8 * 1024 * 1024;
    public long AuxiliaryBytes { get; init; } = 32 * 1024 * 1024;
}

public enum MetaContinuationKind : byte { ValidateIngress, PrepareInvocation, CompleteInvocation, PublishSession, CommitIntent, ApplyHostCompletion }
public readonly record struct MetaContinuationStep(long Ticket, MetaContinuationKind Kind, int GasCost);
public enum MetaContinuationResult : byte { Progress, InvocationStarted, InvocationCompleted, NothingCurrent, Rejected }

/// <summary>
/// Main-thread trusted native work associated with one existing VM registration.
/// A step is precharged against the same tick, hierarchy and physical-device budgets as guest work.
/// </summary>
public interface IMetaSchedulerContinuations
{
    /// <summary>Constant-time, nonmutating lookup; no parsing, allocation, authorization traversal or guest work.</summary>
    bool TryPeek(string processId, out MetaContinuationStep step);
    /// <summary>
    /// Revalidate the exact ticket and fresh native proof, then execute one bounded step. Never run a slice,
    /// await, block or recursively pump the scheduler. InvocationStarted must follow a successful TryStart.
    /// CompleteInvocation may return Progress while LastResult remains rooted; InvocationCompleted or
    /// Rejected releases the result barrier, and must retain no guest result handles.
    /// </summary>
    MetaContinuationResult Execute(string processId, MetaProcess process, in MetaContinuationStep step);
    /// <summary>
    /// Once-only logical cancellation; no guest execution. Completed is normal termination, not revocation
    /// of an already accepted outbound native ticket. Other stop reasons cancel its remaining authority.
    /// </summary>
    void Cancel(string processId, MetaStopReason reason);
}
