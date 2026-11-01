// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server.Database;

namespace Content.Server._NC.Meta.Applications;

public sealed partial class NCMetaHostingSystem
{
    private Task<NCMetaAuthorityConstraints?> CaptureHostingAuthority(Context context) => Main(() =>
    {
        if (!Current(context)) return null;
        var authority = _authority.CaptureWriteConstraints(context.TerminalManagement, context.Terminal);
        if (authority == null) return null;
        var devices = authority.Devices.ToDictionary(value => value.DeviceId, StringComparer.Ordinal);
        devices[context.HostId] = new(context.HostId, context.Provider, context.HostAuthority);
        return authority with { Devices = devices.Values.ToArray() };
    });
}
