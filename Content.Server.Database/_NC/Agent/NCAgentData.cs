// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

/// <summary>
/// Stable CitiNet address owned by one character profile.
/// </summary>
[Table("nc_agent_profile")]
public sealed class NCAgentProfile
{
    [Key]
    public int ProfileId { get; set; }

    [MaxLength(20)]
    public string Address { get; set; } = string.Empty;

    [MaxLength(32)]
    public string? Alias { get; set; }

    [MaxLength(32)]
    public string? NormalizedAlias { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Profile Profile { get; set; } = null!;
    public List<NCAgentContact> Contacts { get; set; } = [];
}

[Table("nc_agent_contact")]
public sealed class NCAgentContact
{
    public int OwnerProfileId { get; set; }
    public int ContactProfileId { get; set; }
    public DateTime CreatedAt { get; set; }
    public NCAgentProfile Owner { get; set; } = null!;
    public NCAgentProfile Contact { get; set; } = null!;
}

[Table("nc_agent_message")]
public sealed class NCAgentMessage
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }
    public int SenderProfileId { get; set; }
    public int RecipientProfileId { get; set; }

    [MaxLength(1024)]
    public string Body { get; set; } = string.Empty;

    public DateTime SentAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public NCAgentProfile Sender { get; set; } = null!;
    public NCAgentProfile Recipient { get; set; } = null!;
}

[Table("nc_agent_call_log")]
public sealed class NCAgentCallLog
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }
    public int CallerProfileId { get; set; }
    public int TargetProfileId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? AnsweredAt { get; set; }
    public DateTime EndedAt { get; set; }
    public byte Outcome { get; set; }
    public DateTime? TargetReadAt { get; set; }
    public NCAgentProfile Caller { get; set; } = null!;
    public NCAgentProfile Target { get; set; } = null!;
}

[Table("nc_citinet_post")]
public sealed class NCCitiNetPost
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }
    public int AuthorProfileId { get; set; }

    [MaxLength(2048)]
    public string Body { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public NCAgentProfile Author { get; set; } = null!;
    public List<NCCitiNetComment> Comments { get; set; } = [];
    public List<NCCitiNetReaction> Reactions { get; set; } = [];
}

[Table("nc_citinet_comment")]
public sealed class NCCitiNetComment
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }
    public long PostId { get; set; }
    public int AuthorProfileId { get; set; }

    [MaxLength(1024)]
    public string Body { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public NCCitiNetPost Post { get; set; } = null!;
    public NCAgentProfile Author { get; set; } = null!;
}

[Table("nc_citinet_reaction")]
public sealed class NCCitiNetReaction
{
    public long PostId { get; set; }
    public int ProfileId { get; set; }
    public DateTime CreatedAt { get; set; }
    public NCCitiNetPost Post { get; set; } = null!;
    public NCAgentProfile Profile { get; set; } = null!;
}

public abstract partial class ServerDbContext
{
    public DbSet<NCAgentProfile> NCAgentProfiles => Set<NCAgentProfile>();
    public DbSet<NCAgentContact> NCAgentContacts => Set<NCAgentContact>();
    public DbSet<NCAgentMessage> NCAgentMessages => Set<NCAgentMessage>();
    public DbSet<NCAgentCallLog> NCAgentCallLogs => Set<NCAgentCallLog>();
    public DbSet<NCCitiNetPost> NCCitiNetPosts => Set<NCCitiNetPost>();
    public DbSet<NCCitiNetComment> NCCitiNetComments => Set<NCCitiNetComment>();
    public DbSet<NCCitiNetReaction> NCCitiNetReactions => Set<NCCitiNetReaction>();

    private static void ConfigureNCAgentModels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NCAgentProfile>()
            .HasOne(value => value.Profile)
            .WithOne()
            .HasForeignKey<NCAgentProfile>(value => value.ProfileId)
            .HasConstraintName("FK_nc_agent_profile_profile_profile_id")
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NCAgentProfile>()
            .HasIndex(value => value.Address)
            .IsUnique();
        modelBuilder.Entity<NCAgentProfile>()
            .HasIndex(value => value.NormalizedAlias)
            .IsUnique();

        modelBuilder.Entity<NCAgentContact>()
            .HasKey(value => new { value.OwnerProfileId, value.ContactProfileId });
        modelBuilder.Entity<NCAgentContact>()
            .HasOne(value => value.Owner)
            .WithMany(value => value.Contacts)
            .HasForeignKey(value => value.OwnerProfileId)
            .HasConstraintName("FK_nc_agent_contact_owner_profile_id")
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NCAgentContact>()
            .HasOne(value => value.Contact)
            .WithMany()
            .HasForeignKey(value => value.ContactProfileId)
            .HasConstraintName("FK_nc_agent_contact_contact_profile_id")
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NCAgentMessage>()
            .HasOne(value => value.Sender)
            .WithMany()
            .HasForeignKey(value => value.SenderProfileId)
            .HasConstraintName("FK_nc_agent_message_sender_profile_id")
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NCAgentMessage>()
            .HasOne(value => value.Recipient)
            .WithMany()
            .HasForeignKey(value => value.RecipientProfileId)
            .HasConstraintName("FK_nc_agent_message_recipient_profile_id")
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NCAgentMessage>()
            .HasIndex(value => new { value.SenderProfileId, value.RecipientProfileId, value.SentAt });
        modelBuilder.Entity<NCAgentMessage>()
            .HasIndex(value => value.ExpiresAt);

        modelBuilder.Entity<NCAgentCallLog>()
            .HasOne(value => value.Caller)
            .WithMany()
            .HasForeignKey(value => value.CallerProfileId)
            .HasConstraintName("FK_nc_agent_call_log_caller_profile_id")
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NCAgentCallLog>()
            .HasOne(value => value.Target)
            .WithMany()
            .HasForeignKey(value => value.TargetProfileId)
            .HasConstraintName("FK_nc_agent_call_log_target_profile_id")
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NCAgentCallLog>()
            .HasIndex(value => new { value.TargetProfileId, value.TargetReadAt });
        modelBuilder.Entity<NCAgentCallLog>()
            .HasIndex(value => value.EndedAt);

        modelBuilder.Entity<NCCitiNetPost>()
            .HasOne(value => value.Author)
            .WithMany()
            .HasForeignKey(value => value.AuthorProfileId)
            .HasConstraintName("FK_nc_citinet_post_author_profile_id")
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NCCitiNetPost>()
            .HasIndex(value => value.CreatedAt);

        modelBuilder.Entity<NCCitiNetComment>()
            .HasOne(value => value.Post)
            .WithMany(value => value.Comments)
            .HasForeignKey(value => value.PostId)
            .HasConstraintName("FK_nc_citinet_comment_post_id")
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NCCitiNetComment>()
            .HasOne(value => value.Author)
            .WithMany()
            .HasForeignKey(value => value.AuthorProfileId)
            .HasConstraintName("FK_nc_citinet_comment_author_profile_id")
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NCCitiNetComment>()
            .HasIndex(value => new { value.PostId, value.CreatedAt });

        modelBuilder.Entity<NCCitiNetReaction>()
            .HasKey(value => new { value.PostId, value.ProfileId });
        modelBuilder.Entity<NCCitiNetReaction>()
            .HasOne(value => value.Post)
            .WithMany(value => value.Reactions)
            .HasForeignKey(value => value.PostId)
            .HasConstraintName("FK_nc_citinet_reaction_post_id")
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NCCitiNetReaction>()
            .HasOne(value => value.Profile)
            .WithMany()
            .HasForeignKey(value => value.ProfileId)
            .HasConstraintName("FK_nc_citinet_reaction_profile_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
