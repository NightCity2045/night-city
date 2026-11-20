// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

[Table("nc_character_skill_progress")]
public sealed class NCCharacterSkillProgress
{
    [Key] public int ProfileId { get; set; }
    public long ActiveTicks { get; set; }
    public string LearnedSkills { get; set; } = "[]";
    public int SurvivedRounds { get; set; }
    public int? LastSurvivedRound { get; set; }
    public Profile Profile { get; set; } = null!;
}

public abstract partial class ServerDbContext
{
    public DbSet<NCCharacterSkillProgress> NCCharacterSkillProgress => Set<NCCharacterSkillProgress>();

    private static void ConfigureNCSkillModels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NCCharacterSkillProgress>()
            .HasOne(progress => progress.Profile)
            .WithOne()
            .HasForeignKey<NCCharacterSkillProgress>(progress => progress.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
