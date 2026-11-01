// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Shared._NC.Meta.UI;
using Content.Shared._NC.Meta.Authority;

namespace Content.Server._NC.Meta.Projects;

public sealed partial class NCMetaWorkspaceSystem
{
    // Keep the existing canonical command handlers and all their authority/idempotency checks.
    // The client sends typed data; only this server boundary performs JSON serialization.
    private static string? EncodeWorkspacePayload(NCMetaWorkspacePayload payload) => !WorkspacePayloadBounded(payload) ? null : payload switch
    {
        NCMetaSoftwareBrowseRequest value => JsonSerializer.Serialize(value),
        NCMetaSoftwareSelection value => JsonSerializer.Serialize(value),
        NCMetaSoftwareQuoteRequest value => JsonSerializer.Serialize(value),
        NCMetaSoftwarePurchaseRequest value => JsonSerializer.Serialize(value),
        NCMetaSoftwareInstallRequest value => JsonSerializer.Serialize(value),
        NCMetaSoftwareSeatRequest value => JsonSerializer.Serialize(value),
        NCMetaSoftwareSourceRequest value => JsonSerializer.Serialize(value),
        NCMetaSoftwarePublishRequest value => JsonSerializer.Serialize(value),
        NCMetaSoftwareContactRequest value => JsonSerializer.Serialize(value),
        NCMetaSoftwareOfferResaleRequest value => JsonSerializer.Serialize(value),
        NCMetaSoftwareAcceptResaleRequest value => JsonSerializer.Serialize(value),
        NCMetaSoftwareDiscardCopyRequest value => JsonSerializer.Serialize(value),
        NCMetaFirmwareSelection value => JsonSerializer.Serialize(value),
        NCMetaFirmwareInstallRequest value => JsonSerializer.Serialize(value),
        NCMetaDevelopmentRequest value => JsonSerializer.Serialize(value),
        NCMetaGrantApprovalRequest value => JsonSerializer.Serialize(value),
        NCMetaBindingsPayload value => JsonSerializer.Serialize(value.Bindings),
        NCMetaHostingQuotePayload value => JsonSerializer.Serialize(value),
        _ => null,
    };

    private static bool WorkspacePayloadBounded(NCMetaWorkspacePayload payload)
    {
        var remaining = 32768;
        bool Text(string? value) => value != null && value.Length <= 32768 && (remaining -= value.Length) >= 0;
        bool Strings(List<string>? values, int maximum = 128) => values is { Count: <= 128 } && values.Count <= maximum && values.All(Text);
        bool Bindings(List<NCMetaSoftwareBinding>? values) => values is { Count: <= 64 } && values.All(value => value != null &&
            Text(value.Slot) && Text(value.DeviceId) && Text(value.ContractHash) && Strings(value.Scopes));
        bool Offer(NCMetaDevelopmentOffer? value) => value == null || Text(value.Customer) && Text(value.Developer) && Text(value.Subject) &&
            Text(value.Requirements) && Text(value.FailureConditions) && Strings(value.DeviceIds, 64) &&
            value.FixtureFiles is { Count: <= 64 } && Strings(value.MaximumScopes) && (value.Arbitrator == null || Text(value.Arbitrator));
        return payload switch
        {
            NCMetaSoftwareBrowseRequest value => Text(value.LicenseOwner),
            NCMetaSoftwareSelection value => Text(value.SlotKind),
            NCMetaSoftwareQuoteRequest value => Text(value.LicenseOwner) && Text(value.SlotKind),
            NCMetaSoftwareInstallRequest value => Bindings(value.Bindings) && Strings(value.ApprovedScopes),
            NCMetaSoftwareSeatRequest value => Text(value.DeviceId),
            NCMetaSoftwareSourceRequest value => Text(value.Path) && Text(value.LicenseOwner),
            NCMetaSoftwarePublishRequest value => Text(value.Name) && Text(value.Description) && Text(value.Changelog) && Text(value.PublisherOwner) && value.Terms != null,
            NCMetaSoftwareContactRequest value => Text(value.Query),
            NCMetaSoftwareOfferResaleRequest value => Text(value.BuyerParty),
            NCMetaFirmwareInstallRequest value => Bindings(value.Bindings) && Strings(value.ApprovedScopes),
            NCMetaDevelopmentRequest value => Text(value.ActingParty) && Text(value.TermsHash) && Text(value.Text) && Text(value.ResolutionHash) && Offer(value.Offer),
            NCMetaGrantApprovalRequest value => value.Bindings is { Count: <= 64 } && value.Bindings.All(binding => binding != null &&
                Text(binding.Slot) && Text(binding.TargetDeviceId) && Strings(binding.Members) && Strings(binding.Scopes)),
            NCMetaBindingsPayload value => Bindings(value.Bindings),
            NCMetaHostingQuotePayload value => Text(value.Domain) && Text(value.View),
            NCMetaSoftwarePurchaseRequest or NCMetaSoftwareAcceptResaleRequest or NCMetaSoftwareDiscardCopyRequest or NCMetaFirmwareSelection => true,
            _ => false,
        };
    }
}
