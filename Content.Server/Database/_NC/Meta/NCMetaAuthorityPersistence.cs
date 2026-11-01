// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using System.Threading;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Persistence.Identity;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Log;

namespace Content.Server.Database;

public sealed record NCPlayerEmploymentProof(int CharacterId, string JobPrototypeId, long LastEventId);
public sealed record NCMetaDeviceAuthorityProof(string DeviceId, string Owner, ulong AuthorityEpoch);
public sealed record NCMetaAuthorityConstraints(NCPlayerEmploymentProof? Employment, NCMetaDeviceAuthorityProof[] Devices);

public partial interface IServerDbManager
{
    /// <summary>Changes only after a committed native player employment/profile mutation.</summary>
    long NCPlayerEmploymentGeneration { get; }
    event Action? NCPlayerEmploymentChanged;
    Task<NCPlayerEmploymentProof?> GetNCPlayerEmploymentProofAsync(NCCharacterId character);
}

public sealed partial class ServerDbManager
{
    private long _ncPlayerEmploymentGeneration;
    public long NCPlayerEmploymentGeneration => Interlocked.Read(ref _ncPlayerEmploymentGeneration);
    public event Action? NCPlayerEmploymentChanged;

    public Task<NCPlayerEmploymentProof?> GetNCPlayerEmploymentProofAsync(NCCharacterId character)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCPlayerEmploymentProofAsync(character));
    }

    private void NotifyNCPlayerEmploymentChanged()
    {
        // Subscribers only invalidate native caches here; ECS work must be scheduled on the game thread.
        Interlocked.Increment(ref _ncPlayerEmploymentGeneration);
        if (NCPlayerEmploymentChanged == null) return;
        foreach (Action subscriber in NCPlayerEmploymentChanged.GetInvocationList())
            try { subscriber(); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            { Logger.ErrorS("nc-employment", $"Native employment invalidation subscriber failed: {exception}"); }
    }
}

public abstract partial class ServerDbBase
{
    public async Task<NCPlayerEmploymentProof?> GetNCPlayerEmploymentProofAsync(NCCharacterId character)
    {
        if (!character.IsValid) return null;
        await using var db = await GetDb();
        return await ReadNCPlayerEmploymentProofAsync(db.DbContext, character.Value);
    }

    private static async Task<NCPlayerEmploymentProof?> ReadNCPlayerEmploymentProofAsync(ServerDbContext context, int character)
    {
        // One SQL projection binds role and append-only event identity to the same database snapshot.
        return await context.NCCharacterEmployment.AsNoTracking()
            .Where(value => value.ProfileId == character && value.State == NCEmploymentState.Active)
            .Select(value => new NCPlayerEmploymentProof(value.ProfileId, value.JobPrototypeId,
                context.NCEmploymentEvents.Where(history => history.ProfileId == value.ProfileId)
                    .Select(history => (long?)history.Id).Max() ?? 0)).SingleOrDefaultAsync();
    }

    private static void ValidateNCMetaAuthority(NCMetaAuthorityConstraints? authority)
    {
        if (authority == null) return;
        if (authority.Devices is not { Length: > 0 and <= 65 } ||
            authority.Devices.Any(device => device == null || device.AuthorityEpoch < 2 ||
                !Guid.TryParseExact(device.DeviceId, "D", out var id) || id == Guid.Empty ||
                device.DeviceId != id.ToString("D") || !NCPartyRef.TryParse(device.Owner, out var owner) ||
                owner.ToString() != device.Owner) ||
            authority.Devices.Select(device => device.DeviceId).Distinct(StringComparer.Ordinal).Count() != authority.Devices.Length ||
            authority.Employment is { } proof && (proof.CharacterId <= 0 || proof.LastEventId <= 0 ||
                proof.JobPrototypeId is not { Length: > 0 and <= 64 }))
            throw new ArgumentException("Invalid native META authority constraints.", nameof(authority));
    }

    private static async Task<bool> CheckNCMetaAuthorityAsync(ServerDbContext context, NCMetaAuthorityConstraints? authority)
    {
        if (authority == null) return true;
        if (authority.Employment is { } expected)
        {
            // Acquire a row lock on both providers, without changing persisted employment/history data.
            // This prevents a role mutation from committing between this exact proof check and the grant commit.
            if (await context.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE nc_character_employment SET updated_at = updated_at WHERE profile_id = {expected.CharacterId}") != 1 ||
                await ReadNCPlayerEmploymentProofAsync(context, expected.CharacterId) != expected) return false;
        }
        var ids = authority.Devices.Select(device => device.DeviceId).ToArray();
        foreach (var id in ids.Order(StringComparer.Ordinal))
            if (await context.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE nc_aggregate_snapshot SET aggregate_version = aggregate_version WHERE aggregate_type = 'meta.device' AND aggregate_id = {id}") != 1)
                return false;
        var snapshots = await context.NCAggregateSnapshots.AsNoTracking()
            .Where(value => value.AggregateType == "meta.device" && ids.Contains(value.AggregateId)).ToListAsync();
        if (snapshots.Count != ids.Length) return false;
        foreach (var proof in authority.Devices)
        {
            var snapshot = snapshots.Single(value => value.AggregateId == proof.DeviceId);
            if (snapshot.Payload.Length > 32768 || snapshot.FormatVersion != 1) return false;
            try
            {
                using var document = JsonDocument.Parse(snapshot.Payload, new() { MaxDepth = 8 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.GetProperty("DeviceId").GetString() != proof.DeviceId ||
                    root.GetProperty("Owner").GetString() != proof.Owner ||
                    root.GetProperty("AuthorityEpoch").GetUInt64() != proof.AuthorityEpoch ||
                    root.GetProperty("Revision").GetInt64() != snapshot.AggregateVersion) return false;
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            { return false; }
        }
        return true;
    }
}
