// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Robust.Shared.Configuration;
namespace Content.Shared._NC.Medical.Humanity;
[CVarDefs]
public sealed class NCCyberEpisodeCVars
{
    public static readonly CVarDef<bool> ReducedEffects = CVarDef.Create("nc.psychosis_reduced_effects", false, CVar.CLIENTONLY | CVar.ARCHIVE);
    public static readonly CVarDef<bool> QuietSounds = CVarDef.Create("nc.psychosis_quiet_sounds", false, CVar.CLIENTONLY | CVar.ARCHIVE);
}
