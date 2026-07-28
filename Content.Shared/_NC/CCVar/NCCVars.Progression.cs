using Robust.Shared;
using Robust.Shared.Configuration;

namespace Content.Shared._NC.CCVar;

public sealed partial class NCCVars
{
    public static readonly CVarDef<int> RoundCreditActiveSeconds =
        CVarDef.Create("nc.progression.round_credit_seconds", 1800, CVar.ARCHIVE | CVar.SERVER);

    public static readonly CVarDef<int> ParticipationSaveIntervalSeconds =
        CVarDef.Create("nc.progression.save_interval_seconds", 60, CVar.ARCHIVE | CVar.SERVER);
}
