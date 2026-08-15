
using Content.Shared._NC.Preferences; // NC - Human-only character profiles.
using Content.Shared.Preferences;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    private void SetName(string newName)
    {
        Profile = Profile?.WithName(newName);
        SetDirty();

        if (!IsDirty)
            return;

        SpriteView.SetName(newName);
    }

    private void UpdateNameEdit()
    {
        NameEdit.Text = Profile?.Name ?? "";
    }

    /// <summary>
    /// Randomize values selectively while respecting locked values.
    /// </summary>
    private void RandomizeProfile()
    {
        // NC start - player profiles are always human, including full and selective randomization.
        var randomize = RandomizeLockButton.RandomizeCfg & ~HumanoidCharacterProfile.RandomizeCfg.Species;
        Profile = Profile == null
            ? HumanoidCharacterProfile.RandomWithSpecies()
            : HumanoidCharacterProfile.Random(randomize, NCHumanProfileRules.Normalize(Profile));
        // NC end
        SetProfile(Profile, CharacterSlot);
        SetDirty();
    }
}
