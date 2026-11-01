// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Hosting;
using Content.Meta.Runtime;
using Content.Meta.Verification;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Configuration;

namespace Content.Server._NC.Meta.Runtime;

public enum NCMetaAuditPhase : byte { Queued, Committing, Completed, Rejected, Stopped }
public readonly record struct NCMetaNativeAudit(Guid Generation, Guid Installation, string Principal, string DeviceId,
    string Member, long Operation, NCMetaAuditPhase Phase, long ActiveWorldMilliseconds);
public sealed record NCMetaAdminProcess(Guid Generation, Guid Installation, string Principal, string HostDeviceId,
    string ArtifactDigest, string ContentHash, MetaProcessState State, MetaStopReason StopReason, long MemoryBytes,
    long ReservedBytes, long PendingOperation, bool DebugPaused, long ActivationGas, long DeviceWindowGas,
    int QueuedEvents, long CoalescedEvents, long DroppedLogs);
public sealed record NCMetaAdminSnapshot(bool Enabled, bool PolicyValid, int Processes, int Hosts, int PendingIntents,
    int PendingCompletions, int RevokedArtifacts, long AuditOverwritten, NCMetaAdminProcess[] Page, NCMetaNativeAudit[] Audit);

public sealed partial class NCMetaRuntimeSystem
{
    private const int NativeAuditCapacity = 128;
    [Dependency] private IConfigurationManager _operationalConfig = default!;
    private readonly Queue<NCMetaNativeAudit> _nativeAudit = new(NativeAuditCapacity);
    private HashSet<string> _revokedHashes = new(StringComparer.OrdinalIgnoreCase);
    private long _auditOverwritten;
    private bool _operationalEnabled;
    private bool _operationalPolicyValid;
    private Guid _operationalPolicyEpoch = Guid.NewGuid();
    public bool OperationalEnabled => _operationalEnabled && _operationalPolicyValid;
    public Guid OperationalPolicyEpoch => _operationalPolicyEpoch;

    private void InitializeOperationalControls()
    {
        _operationalConfig.OnValueChanged(NCMetaCVars.Enabled, OnOperationalEnabledChanged, true);
        _operationalConfig.OnValueChanged(NCMetaCVars.RevokedArtifacts, OnArtifactPolicyChanged, true);
    }
    private void ShutdownOperationalControls()
    {
        _operationalConfig.UnsubValueChanged(NCMetaCVars.Enabled, OnOperationalEnabledChanged);
        _operationalConfig.UnsubValueChanged(NCMetaCVars.RevokedArtifacts, OnArtifactPolicyChanged);
        _nativeAudit.Clear();
    }
    private void OnOperationalEnabledChanged(bool value) => RefreshOperationalControls();
    private void OnArtifactPolicyChanged(string value) => RefreshOperationalControls();
    private void RefreshOperationalControls()
    {
        _operationalPolicyEpoch = Guid.NewGuid();
        _operationalEnabled = _operationalConfig.GetCVar(NCMetaCVars.Enabled);
        _operationalPolicyValid = MetaOperationalPolicy.TryParseRevokedArtifacts(_operationalConfig.GetCVar(NCMetaCVars.RevokedArtifacts), out _revokedHashes);
        if (!OperationalEnabled) { SuspendAll(); return; }
        foreach (var runtime in _instances.Values.ToArray())
            if (IsOperationallyBlocked(runtime.Artifact)) Stop(runtime, MetaStopReason.ArtifactRevoked);
    }
    private bool IsOperationallyBlocked(VerifiedArtifact artifact) => !OperationalEnabled ||
        _revokedHashes.Contains(artifact.Digest) || _revokedHashes.Contains(artifact.ContentHash);

    public bool TrySetOperationalArtifactBlock(string digest, bool blocked)
    {
        if (digest.Length != 64 || !digest.All(char.IsAsciiHexDigit) ||
            !MetaOperationalPolicy.TryParseRevokedArtifacts(_operationalConfig.GetCVar(NCMetaCVars.RevokedArtifacts), out var hashes)) return false;
        if (blocked && !hashes.Contains(digest) && hashes.Count >= MetaOperationalPolicy.MaximumRevokedArtifacts) return false;
        if (blocked) hashes.Add(digest.ToLowerInvariant()); else hashes.Remove(digest);
        _operationalConfig.SetCVar(NCMetaCVars.RevokedArtifacts, string.Join(',', hashes.Select(value => value.ToLowerInvariant()).Order(StringComparer.Ordinal)));
        return true;
    }
    public bool TryStopAdministrativeProcess(Guid generation)
    {
        if (!_processes.TryGetValue(generation.ToString("N"), out var process)) return false;
        Stop(process, MetaStopReason.Stop); return true;
    }
    public int StopAdministrativePrincipal(string principal)
    {
        if (!NCPartyRef.TryParse(principal, out _)) return 0;
        var processes = _instances.Values.Where(value => value.Owner == principal).ToArray();
        foreach (var process in processes) Stop(process, MetaStopReason.Stop);
        return processes.Length;
    }
    public NCMetaAdminSnapshot GetAdministrativeSnapshot(EntityUid? host = null, int offset = 0)
    {
        if (offset is < 0 or > 4096) throw new ArgumentOutOfRangeException(nameof(offset));
        return new(OperationalEnabled, _operationalPolicyValid, _instances.Count, _hosts.Count, _intents.Count,
            _completed.Count, _revokedHashes.Count, _auditOverwritten,
            _instances.Values.Where(value => host == null || value.Host == host).Skip(offset).Take(32)
                .Select(value => new NCMetaAdminProcess(value.Generation, value.Installation.Id, value.Owner,
                    value.Installation.HostDeviceId, value.Artifact.Digest, value.Artifact.ContentHash, value.Process.State,
                    value.Process.StopReason, value.Process.MemoryBytes, value.Process.MemoryReservationBytes,
                    value.Process.PendingOperationId, value.Process.DebugPaused, value.Process.ActivationGas,
                    _scheduler.TryGetDeviceUsage(value.Installation.HostDeviceId, out var usage) ? usage.WindowGas : 0,
                    value.Process.QueuedEvents, value.Process.CoalescedEvents, value.Process.DroppedLogs)).ToArray(),
            _nativeAudit.Where(value => host == null || _devices.TryGetEntry(host.Value, out var device) && device.Id == value.DeviceId).ToArray());
    }
    private void AuditNative(NCMetaRuntimeInstance runtime, string device, string member, long operation, NCMetaAuditPhase phase)
    {
        if (_nativeAudit.Count == NativeAuditCapacity)
        {
            _nativeAudit.Dequeue();
            if (_auditOverwritten < long.MaxValue) _auditOverwritten++;
        }
        // Value records and the preallocated ring retain no arguments, source, log text or additional strings.
        _nativeAudit.Enqueue(new(runtime.Generation, runtime.Installation.Id, runtime.Owner, device, member, operation, phase, _now));
    }
}
