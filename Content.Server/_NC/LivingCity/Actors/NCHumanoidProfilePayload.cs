// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.Json;
using Content.Server.Database;
using Content.Shared.Preferences;
using Robust.Shared.Serialization.Manager;

namespace Content.Server._NC.LivingCity.Actors;

/// <summary>
/// Converts the engine's data-definition profile through DataNode JSON so the full generated appearance can live in
/// the Actor aggregate without introducing a second appearance schema.
/// </summary>
public static class NCHumanoidProfilePayload
{
    public static string Serialize(ISerializationManager serialization, HumanoidCharacterProfile profile)
    {
        var node = serialization.WriteValue(profile, alwaysWrite: true, notNullableOverride: true);
        return node.ToJsonNode()?.ToJsonString() ?? throw new InvalidOperationException(
            "The generated humanoid profile produced an empty payload.");
    }

    public static bool TryDeserialize(
        ISerializationManager serialization,
        string? payload,
        out HumanoidCharacterProfile profile)
    {
        profile = default!;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            using var document = JsonDocument.Parse(payload);
            profile = serialization.Read<HumanoidCharacterProfile>(
                document.RootElement.ToDataNode(),
                notNullableOverride: true);
            return true;
        }
        // Persisted input is an external trust boundary; any profile decoding failure rejects the spawn safely.
        catch (Exception)
        {
            return false;
        }
    }
}
