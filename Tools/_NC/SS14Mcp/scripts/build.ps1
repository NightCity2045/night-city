# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
[CmdletBinding()]
param([switch]$Publish, [switch]$WithGame)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
Push-Location $workspace
try {
    $env:MSBuildEnableWorkloadResolver = 'false'
    dotnet restore Tools/_NC/SS14Mcp/SS14Mcp.slnx --disable-parallel /m:1
    if ($LASTEXITCODE -ne 0) { throw 'Tool restore failed.' }
    dotnet build Tools/_NC/SS14Mcp/SS14Mcp.slnx --no-restore --disable-build-servers /m:1
    if ($LASTEXITCODE -ne 0) { throw 'Tool build failed.' }
    dotnet test Tools/_NC/SS14Mcp/Tests/SS14Mcp.Tests.csproj --no-build --no-restore /m:1
    if ($LASTEXITCODE -ne 0) { throw 'Tool tests failed.' }
    if ($WithGame) {
        dotnet restore Tools/_NC/SS14Mcp/Game.Diagnostics.slnx --disable-parallel /m:1 -p:EnableNCDiagnostics=true -p:Configuration=DebugOpt
        if ($LASTEXITCODE -ne 0) { throw 'Game restore failed.' }
        dotnet build Tools/_NC/SS14Mcp/Game.Diagnostics.slnx -c DebugOpt --no-restore --disable-build-servers /m:1 -p:EnableNCDiagnostics=true
        if ($LASTEXITCODE -ne 0) { throw 'Game build failed.' }
    }
    if ($Publish) {
        foreach ($project in @('Server', 'Cli')) {
            dotnet publish "Tools/_NC/SS14Mcp/$project/SS14Mcp.$project.csproj" -c Release --no-self-contained -o "artifacts/ss14-mcp-package/$project" --disable-build-servers /m:1
            if ($LASTEXITCODE -ne 0) { throw "$project publish failed." }
        }
        Copy-Item Tools/_NC/SS14Mcp/README.md,Tools/_NC/SS14Mcp/mcp-config.example.json,Tools/_NC/SS14Mcp/codex-config.example.toml artifacts/ss14-mcp-package/
    }
}
finally { Pop-Location }
