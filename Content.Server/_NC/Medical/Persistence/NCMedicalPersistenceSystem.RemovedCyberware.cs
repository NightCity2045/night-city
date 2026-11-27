// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Medical.Persistence;

namespace Content.Server._NC.Medical.Persistence;

public sealed partial class NCMedicalPersistenceSystem
{
    /// <summary>
    /// Cyberware profile ids whose prototypes were deleted from the game. Installed copies vanish on restore.
    /// Plain strings on purpose: these prototypes must not exist, so they cannot be validated <c>ProtoId</c>s.
    /// </summary>
    private static readonly HashSet<string> RemovedCyberware = new(StringComparer.Ordinal)
    {
        // The neural interface jack only connected to META devices, which were removed.
        "NCNeuralInterface",
    };

    /// <summary>
    /// Drops installed modules of removed cyberware before any step indexes their prototypes, then lowers the
    /// stored humanity burden to what the remaining hardware actually carries.
    /// </summary>
    private bool DropRemovedCyberware(EntityUid character, NCMedicalStateSnapshot snapshot)
    {
        var removed = snapshot.CyberwareModules.RemoveAll(module => RemovedCyberware.Contains(module.Profile.Id));
        foreach (var organ in snapshot.Organs.Values)
        {
            if (organ.Cyberware is { } module && RemovedCyberware.Contains(module.Profile.Id))
                return false; // The jack was never an organ replacement; refuse rather than leave a hole in the body.
        }

        return removed == 0 || _humanity.TryRebaseBurden(character, snapshot);
    }
}
