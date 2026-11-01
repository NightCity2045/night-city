// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Server._NC.Meta.Contracts;
using Content.Shared._NC.Meta.UI;

namespace Content.Server._NC.Meta.Projects;

public sealed partial class NCMetaWorkspaceSystem
{
    [Dependency] private NCMetaDevelopmentContractSystem _development = default!;
    private async Task<bool> DevelopmentRequestAsync(Context context, NCMetaWorkspaceRequestMessage message, NCMetaWorkspaceState state)
    {
        NCMetaDevelopmentRequest? request;
        try
        {
            using var document = JsonDocument.Parse(message.Source, new JsonDocumentOptions { MaxDepth = 16 });
            var nodes = 0;
            Validate(document.RootElement);
            request = document.RootElement.Deserialize<NCMetaDevelopmentRequest>(new JsonSerializerOptions
            { MaxDepth = 16, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow });
            void Validate(JsonElement element)
            {
                if (++nodes > 4096) throw new JsonException("Contract request node limit.");
                if (element.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject())
                    { if (!names.Add(property.Name)) throw new JsonException("Duplicate contract property."); Validate(property.Value); }
                }
                else if (element.ValueKind == JsonValueKind.Array)
                    foreach (var item in element.EnumerateArray()) Validate(item);
            }
        }
        catch (JsonException) { state.Notice = Loc.GetString("nc-meta-development-input"); return false; }
        if (request == null) { state.Notice = Loc.GetString("nc-meta-development-input"); return false; }
        request = request with { ActingParty = context.Owner,
            ProjectId = request.Action is NCMetaDevelopmentAction.Offer or NCMetaDevelopmentAction.Deliver ? message.ProjectId : request.ProjectId,
            ReleaseId = request.Action == NCMetaDevelopmentAction.Deliver ? message.ReleaseId : request.ReleaseId,
            Text = request.Action == NCMetaDevelopmentAction.ImportSource ? message.DeviceId : request.Text };
        var result = await _development.TryExecuteAsync(context.Terminal, context.User, message.RequestId, request);
        state.Notice = Loc.GetString(result.MessageKey);
        if (result.Fixture != null)
        {
            state.Development = new NCMetaDevelopmentState { Partial = true, Owner = context.Owner, Fixture = result.Fixture };
            return false;
        }
        state.Development = await _development.ReadStateAsync(context.Terminal, context.User, context.Owner,
            result.ContractId != Guid.Empty ? result.ContractId : request.ContractId, request.Offset,
            request.Action == NCMetaDevelopmentAction.SearchContacts ? request.Text : "", result.Prompt);
        if (state.Development != null) state.Development.Fixture = result.Fixture;
        return result.Success && request.Action == NCMetaDevelopmentAction.ImportSource && message.DeviceId == context.DeviceId;
    }
}
