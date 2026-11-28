// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace Content.Shared._NC.Police.Components;

/// <summary>
/// NCPD city monitoring console: a map of the city with a dot for every person the police network sees. How fresh
/// a dot is depends on the district's coverage (<see cref="Content.Shared._NC.Police.NCPoliceCoveragePrototype"/>).
/// </summary>
[RegisterComponent]
public sealed partial class NCPoliceMonitorConsoleComponent : Component;
