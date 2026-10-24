// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Server._NC.Meta.Devices;

public sealed partial class NCMetaDeviceRegistrySystem
{
    private readonly List<Func<EntityUid, EntityUid, bool?>> _claimPolicies = [];
    public void RegisterClaimPolicy(Func<EntityUid, EntityUid, bool?> policy)
    {
        if (_claimPolicies.Contains(policy)) throw new InvalidOperationException("Duplicate META native claim policy.");
        if (_claimPolicies.Count >= 16) throw new InvalidOperationException("META native claim policy limit.");
        _claimPolicies.Add(policy);
    }
    public bool UnregisterClaimPolicy(Func<EntityUid, EntityUid, bool?> policy) => _claimPolicies.Remove(policy);
    private bool CanClaimNative(EntityUid uid, EntityUid user)
    {
        foreach (var policy in _claimPolicies)
        {
            try { if (policy(uid, user) == false) return false; }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { return false; }
        }
        return true;
    }
}
