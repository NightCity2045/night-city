// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Preferences;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Client.Lobby;

public sealed partial class ClientPreferencesManager
{
    private readonly Dictionary<int, ProtoId<JobPrototype>?> _ncEmployment = new();

    public event Action? OnNCEmploymentUpdated;

    private void InitializeNCEmploymentNetworking()
    {
        _netManager.RegisterNetMessage<NCEmploymentSnapshotMessage>(HandleNCEmploymentSnapshot);
        _netManager.RegisterNetMessage<NCResignEmploymentMessage>();
    }

    private void ResetNCEmployment()
    {
        _ncEmployment.Clear();
    }

    public bool TryGetNCEmployment(int slot, out ProtoId<JobPrototype> job)
    {
        job = default;
        if (!_ncEmployment.TryGetValue(slot, out var employment) || employment is not { } activeJob)
            return false;

        job = activeJob;
        return true;
    }

    public bool TryGetNCEmploymentRecord(int slot, out ProtoId<JobPrototype>? job)
    {
        return _ncEmployment.TryGetValue(slot, out job);
    }

    public void ResignSelectedNCEmployment()
    {
        _netManager.ClientSendMessage(new NCResignEmploymentMessage());
    }

    private void HandleNCEmploymentSnapshot(NCEmploymentSnapshotMessage message)
    {
        _ncEmployment.Clear();
        foreach (var (slot, jobId) in message.Employment)
        {
            _ncEmployment[slot] = jobId == null
                ? (ProtoId<JobPrototype>?) null
                : new ProtoId<JobPrototype>(jobId);
        }

        OnNCEmploymentUpdated?.Invoke();
    }
}
