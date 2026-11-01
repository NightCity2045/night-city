// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;
using Content.Meta.Runtime;

namespace Content.Server._NC.Meta.Runtime;

/// <summary>Trusted native extension. Admission and commit authorization precede every invocation.</summary>
public interface INCMetaDeviceAdapter
{
    IEnumerable<string> Members { get; }
    bool CanBind(EntityUid device, string capability) => false;
    MetaValue Read(NCMetaDeviceInvocation invocation);
    MetaHostResult Commit(NCMetaDeviceInvocation invocation);
}

public sealed record NCMetaDeviceInvocation(NCMetaRuntimeInstance Runtime, EntityUid Device,
    string DeviceId, MetaHostRequest Request, MetaApiMember Member, long OperationId, EntityUid? Actor);
