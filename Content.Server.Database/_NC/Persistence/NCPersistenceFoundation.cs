// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

/// <summary>
/// Durable event emitted by one versioned persistent aggregate mutation.
/// </summary>
[Table("nc_event_journal")]
public sealed class NCEventJournalEntry
{
    [Key]
    public Guid EventId { get; set; }

    [MaxLength(96)] public string EventType { get; set; } = string.Empty;
    [MaxLength(64)] public string AggregateType { get; set; } = string.Empty;
    [MaxLength(128)] public string AggregateId { get; set; } = string.Empty;
    public long ExpectedVersion { get; set; }
    public long AggregateVersion { get; set; }
    public Guid CorrelationId { get; set; }
    public Guid? CausationId { get; set; }
    public Guid SessionEpoch { get; set; }
    public long ActiveWorldTicks { get; set; }
    public string Payload { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? AppliedAt { get; set; }
}

public enum NCCommandReceiptState : byte
{
    Started,
    Completed,
    Failed,
}

/// <summary>
/// Idempotency receipt that lets a retried command return its original terminal result.
/// </summary>
[Table("nc_command_receipt")]
public sealed class NCCommandReceipt
{
    [Key]
    public Guid CommandId { get; set; }

    [MaxLength(96)] public string CommandType { get; set; } = string.Empty;
    [MaxLength(64)] public string ScopeType { get; set; } = string.Empty;
    [MaxLength(128)] public string ScopeId { get; set; } = string.Empty;
    public NCCommandReceiptState State { get; set; }
    public Guid SessionEpoch { get; set; }
    public long StartedAtWorldTicks { get; set; }
    public long? CompletedAtWorldTicks { get; set; }
    public string? ResultPayload { get; set; }
    [MaxLength(1024)] public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Latest validated snapshot for one persistent aggregate.
/// </summary>
[Table("nc_aggregate_snapshot")]
public sealed class NCAggregateSnapshot
{
    [MaxLength(64)] public string AggregateType { get; set; } = string.Empty;
    [MaxLength(128)] public string AggregateId { get; set; } = string.Empty;
    public long AggregateVersion { get; set; }
    public int FormatVersion { get; set; }
    public Guid SessionEpoch { get; set; }
    public long ActiveWorldTicks { get; set; }
    public string Payload { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Reliable asynchronous message paired with a committed journal event.
/// Operational retry timestamps use wall-clock time and never advance gameplay.
/// </summary>
[Table("nc_outbox_message")]
public sealed class NCOutboxMessage
{
    [Key]
    public Guid MessageId { get; set; }

    public Guid EventId { get; set; }
    [MaxLength(96)] public string MessageType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public DateTime AvailableAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    [MaxLength(2048)] public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Singleton clock that accumulates gameplay time only while a round is active.
/// </summary>
[Table("nc_active_world_clock")]
public sealed class NCActiveWorldClockRecord
{
    [Key]
    public byte Id { get; set; }

    public Guid SessionEpoch { get; set; }
    public long ActiveWorldTicks { get; set; }
    public bool RoundActive { get; set; }
    public int Version { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public abstract partial class ServerDbContext
{
    public DbSet<NCEventJournalEntry> NCEventJournal => Set<NCEventJournalEntry>();
    public DbSet<NCCommandReceipt> NCCommandReceipts => Set<NCCommandReceipt>();
    public DbSet<NCAggregateSnapshot> NCAggregateSnapshots => Set<NCAggregateSnapshot>();
    public DbSet<NCOutboxMessage> NCOutboxMessages => Set<NCOutboxMessage>();
    public DbSet<NCActiveWorldClockRecord> NCActiveWorldClocks => Set<NCActiveWorldClockRecord>();

    private static void ConfigureNCPersistenceModels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NCEventJournalEntry>()
            .HasIndex(value => new { value.AggregateType, value.AggregateId, value.AggregateVersion })
            .IsUnique();
        modelBuilder.Entity<NCEventJournalEntry>()
            .HasIndex(value => new { value.CorrelationId, value.CreatedAt });

        modelBuilder.Entity<NCCommandReceipt>()
            .HasIndex(value => new { value.ScopeType, value.ScopeId, value.CreatedAt });
        modelBuilder.Entity<NCCommandReceipt>()
            .HasIndex(value => new { value.State, value.UpdatedAt });

        modelBuilder.Entity<NCAggregateSnapshot>()
            .HasKey(value => new { value.AggregateType, value.AggregateId });

        modelBuilder.Entity<NCOutboxMessage>()
            .HasOne<NCEventJournalEntry>()
            .WithMany()
            .HasForeignKey(value => value.EventId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NCOutboxMessage>()
            .HasIndex(value => new { value.ProcessedAt, value.AvailableAt });

        modelBuilder.Entity<NCActiveWorldClockRecord>()
            .ToTable(table => table.HasCheckConstraint(
                "CK_nc_active_world_clock_singleton",
                "id = 1"));
        modelBuilder.Entity<NCActiveWorldClockRecord>()
            .ToTable(table => table.HasCheckConstraint(
                "CK_nc_active_world_clock_ticks",
                "active_world_ticks >= 0"));
    }
}
