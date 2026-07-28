using Robust.Shared.Configuration;

namespace Content.Shared._NC.CCVar;

public sealed partial class NCCVars
{
    /// <summary>
    /// Disabled by default until the persistent economy has been validated in production.
    /// </summary>
    public static readonly CVarDef<bool> PermadeathEnabled =
        CVarDef.Create("nc.permadeath.enabled", false, CVar.SERVERONLY);
}
