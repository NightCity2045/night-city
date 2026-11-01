// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Commerce;
using Content.Server.Database;
using Content.Server._NC.Meta.Authority;

namespace Content.Server._NC.Meta.Commerce;

public sealed partial class NCMetaMarketplaceSystem
{
    [Dependency] private NCMetaAuthoritySystem _authority = default!;

    private async Task<bool> CanActFor(Context context, string owner)
    {
        var captured = await Main(() => context.Management.GetValueOrDefault(owner));
        if (captured != null) return await Main(() => Current(context) && _authority.IsCurrent(captured));
        var proof = await _authority.CaptureManagementAsync(context.User, owner);
        return proof != null && await Main(() =>
        {
            if (!Current(context) || !_authority.IsCurrent(proof) || proof.Actor.ToString() != context.Actor ||
                context.Management.Count >= 4 && !context.Management.ContainsKey(owner)) return false;
            context.Management[owner] = proof;
            return true;
        });
    }

    private async Task<NCMetaAuthorityConstraints?> CaptureMutationAuthority(Context context)
    {
        var terminal = await _authority.CaptureWorkspaceManagementAsync(context.Terminal, context.User);
        if (terminal == null) return null;
        return await Main(() =>
        {
            if (!Current(context) || !_authority.IsCurrent(terminal) || terminal.Actor.ToString() != context.Actor) return null;
            var constraints = _authority.CaptureWriteConstraints(context.TerminalManagement, context.Terminal);
            if (constraints == null) return null;
            var employment = constraints.Employment;
            foreach (var proof in context.Management.Values)
            {
                if (!_authority.IsCurrent(proof) || proof.Actor.ToString() != context.Actor) return null;
                if (proof.Employment == null) continue;
                if (employment != null && employment != proof.Employment) return null;
                employment = proof.Employment;
            }
            return constraints with { Employment = employment };
        });
    }

    // Historical acknowledgement is not a new grant. Current private presentation still needs current authority.
    private async Task<MetaSoftwareLicense?> PresentLicense(Context context, MetaSoftwareLicense license) =>
        await CanActFor(context, license.Owner) ? license : null;
}
