using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Content.Server.Database;

/// <summary>
/// Persistent RED progression owned by one character profile.
/// </summary>
[Table("nc_character_progression")]
public sealed class NCCharacterProgression
{
    [Key]
    public int ProfileId { get; set; }

    public int CompletedRounds { get; set; }
    public byte Level { get; set; } = 1;
    public int SpentSkillPoints { get; set; }
    public int? LastCountedRoundId { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Accumulated active time for one character in one round.
/// </summary>
[Table("nc_character_round_participation")]
public sealed class NCCharacterRoundParticipation
{
    public int ProfileId { get; set; }
    public Guid AccountId { get; set; }
    public int RoundId { get; set; }
    public int ActiveSeconds { get; set; }
    public bool Counted { get; set; }
    public DateTime FirstJoinedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? CountedAt { get; set; }
}

/// <summary>
/// Account-wide anti-farm record. It remains after character deletion.
/// </summary>
[Table("nc_round_account_credit")]
public sealed class NCRoundAccountCredit
{
    public Guid AccountId { get; set; }
    public int RoundId { get; set; }
    public int CreditedProfileId { get; set; }
    public DateTime CreditedAt { get; set; }
}

/// <summary>
/// Data-driven RED skill rank purchased by a character.
/// </summary>
[Table("nc_character_skill")]
public sealed class NCCharacterSkill
{
    public int ProfileId { get; set; }

    [MaxLength(64)]
    public string SkillPrototypeId { get; set; } = string.Empty;

    public int Rank { get; set; }
    public int SpentPoints { get; set; }
    public DateTime UpdatedAt { get; set; }
}
