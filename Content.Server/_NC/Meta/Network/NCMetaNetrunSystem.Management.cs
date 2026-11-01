// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server.Database;
using Content.Server._NC.Meta.Commerce;

namespace Content.Server._NC.Meta.Network;

public sealed partial class NCMetaNetrunSystem
{
    private bool CanUseSessionDeck(Session session) =>
        _configurationAuthority.CanContinueManagement(session.DeckManagement) &&
        (session.AccessManagement == null || _configurationAuthority.CanContinueManagement(session.AccessManagement)) &&
        _devices.TryGetEntry(session.Deck, out var deck) && deck.Ready && _devices.IsPowered(session.Deck) &&
        _interaction.InRangeUnobstructed(session.Actor, session.Deck, range: 2);

    private async Task<NCMetaAuthorityConstraints?> CaptureCopyAuthorityAsync(NCMetaSoftwareCopyAdmission admission, string path)
    {
        var captured = await ConfigurationMain(() => CurrentCopy(admission, path) && _sessions.TryGetValue(admission.SessionId, out var session)
            ? (Deck: session.Deck, Management: session.DeckManagement, Access: session.AccessManagement,
                AccessPoint: session.AccessPoint, AccessAuthority: session.AccessAuthority, AccessOwner: session.Security.Owner) : default);
        if (captured.Management == null) return null;
        var fresh = await _configurationAuthority.CaptureManagementAsync(captured.Management.ActorEntity, admission.Owner);
        if (fresh == null) return null;
        return await ConfigurationMain(() =>
        {
            if (!CurrentCopy(admission, path) || !_configurationAuthority.CanContinueManagement(captured.Management) ||
                captured.Access != null && !_configurationAuthority.CanContinueManagement(captured.Access)) return null;
            var result = _configurationAuthority.CaptureWriteConstraints(fresh, captured.Deck);
            if (result == null) return null;
            var employment = result.Employment;
            if (captured.Access?.Employment is { } accessEmployment)
            {
                if (employment != null && employment != accessEmployment) return null;
                employment = accessEmployment;
            }
            if (!_devices.TryGetEntry(captured.AccessPoint, out var access)) return null;
            var devices = result.Devices.ToDictionary(value => value.DeviceId, StringComparer.Ordinal);
            devices[access.Id] = new(access.Id, captured.AccessOwner, captured.AccessAuthority);
            return result with { Employment = employment, Devices = devices.Values.ToArray() };
        });
    }
}
