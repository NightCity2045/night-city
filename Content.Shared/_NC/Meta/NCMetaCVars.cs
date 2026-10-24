// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Robust.Shared;
using Robust.Shared.Configuration;

namespace Content.Shared._NC.Meta;

[CVarDefs]
public sealed class NCMetaCVars : CVars
{
    public static readonly CVarDef<bool> Enabled = CVarDef.Create("nc_meta.enabled", true, CVar.SERVERONLY | CVar.ARCHIVE);
    public static readonly CVarDef<string> RevokedArtifacts = CVarDef.Create("nc_meta.revoked_artifacts", "", CVar.SERVERONLY | CVar.ARCHIVE);
}
