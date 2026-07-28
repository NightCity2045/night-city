namespace Content.Shared._NC.Identity;

/// <summary>
/// Stable database identifier of a character profile.
/// Unlike an entity UID or a character slot, this value survives reconnects and rounds.
/// </summary>
public readonly record struct ProfileId(int Value)
{
    /// <summary>
    /// Database-generated profile identifiers are always positive.
    /// </summary>
    public bool IsValid => Value > 0;

    public override string ToString()
    {
        return Value.ToString();
    }
}
