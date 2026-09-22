// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Console;

namespace Content.Client._NC.AstraGraph;

public sealed partial class AstraOpenStudioCommand : LocalizedCommands
{
    [Dependency] private AstraClientIntegrationSystem _astra = default!;

    public override string Command => "astra";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        _astra.TryOpenStudio(shell);
    }
}
