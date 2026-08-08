using Content.Shared.Construction.Prototypes;
using Content.Shared.Preferences;
using Content.Shared.Roles; // NC - Persistent employment display.
using Robust.Shared.Prototypes;

namespace Content.Client.Lobby
{
    public interface IClientPreferencesManager
    {
        event Action OnServerDataLoaded;
        // NC start - Server-owned employment shown by the character picker.
        event Action OnNCEmploymentUpdated;

        bool ServerDataLoaded => Settings != null;

        GameSettings? Settings { get; }
        PlayerPreferences? Preferences { get; }
        void Initialize();
        void SelectCharacter(HumanoidCharacterProfile profile);
        void SelectCharacter(int slot);
        void UpdateCharacter(HumanoidCharacterProfile profile, int slot);
        void CreateCharacter(HumanoidCharacterProfile profile);
        void DeleteCharacter(HumanoidCharacterProfile profile);
        void DeleteCharacter(int slot);
        void UpdateConstructionFavorites(List<ProtoId<ConstructionPrototype>> favorites);
        bool TryGetNCEmployment(int slot, out ProtoId<JobPrototype> job);
        bool TryGetNCEmploymentRecord(int slot, out ProtoId<JobPrototype>? job);
        void ResignSelectedNCEmployment();
        // NC end
    }
}
