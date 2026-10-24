# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
[CmdletBinding()]
param([string] $BuildId = '', [switch] $UseExistingEngine, [switch] $RuntimeOnly)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if ($BuildId.Length -eq 0) { $BuildId = [Guid]::NewGuid().ToString('N') }
$parsedBuildId = [Guid]::Empty
if (-not [Guid]::TryParseExact($BuildId, 'N', [ref]$parsedBuildId)) { throw 'BuildId must be a UUID in N format.' }
$BuildId = $parsedBuildId.ToString('N')
$cohortRoot = Join-Path $repositoryRoot ('artifacts/meta-acceptance/' + $BuildId)
$outputRoot = Join-Path $cohortRoot 'bin'
$expectedRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts/meta-acceptance')) + [System.IO.Path]::DirectorySeparatorChar
if (-not ($cohortRoot + [System.IO.Path]::DirectorySeparatorChar).StartsWith($expectedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The compiled cohort must remain inside this workspace artifact directory.'
}
if (Test-Path -LiteralPath $cohortRoot) { throw 'Acceptance builds require a fresh cohort directory.' }
New-Item -ItemType Directory -Path $cohortRoot | Out-Null
Write-Output "Building isolated acceptance cohort: $cohortRoot"
$resourceSource = Join-Path $repositoryRoot 'Resources'
$resourceSnapshot = Join-Path $cohortRoot 'Resources'
if (Get-ChildItem -LiteralPath $resourceSource -Recurse -Force -Attributes ReparsePoint | Select-Object -First 1) {
    throw 'The immutable acceptance resource tree cannot contain links.'
}
if (Get-Command robocopy -ErrorAction SilentlyContinue) {
    & robocopy $resourceSource $resourceSnapshot /E /MT:16 /R:0 /W:0 /XJ /SL /NFL /NDL /NJH /NJS /NP
    if ($LASTEXITCODE -ge 8) { throw "Pre-build resource snapshot failed: $LASTEXITCODE" }
} else {
    Copy-Item -LiteralPath $resourceSource -Destination $cohortRoot -Recurse
}
[ordered]@{ version = 1; buildId = $BuildId; capturedAt = [DateTime]::UtcNow.ToString('O');
    directory = $resourceSnapshot; capturedBeforeBuild = $true } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $cohortRoot 'resource-snapshot.json') -Encoding utf8
Push-Location $repositoryRoot
try {
    if ($UseExistingEngine -or $RuntimeOnly) {
        # Content projects have private outputs. Reusing an already built engine
        # avoids writing its shared analyzer DLLs while another task compiles.
        foreach ($assembly in @('Robust.Shared/bin/DebugOpt/Robust.Shared.dll', 'bin/Client/Robust.Client.dll', 'bin/Server/Robust.Server.dll')) {
            if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot ('RobustToolbox/' + $assembly)))) {
                throw 'Build the engine dependencies before selecting UseExistingEngine/RuntimeOnly.'
            }
        }
        $projects = @('Content.Meta/Content.Meta.csproj', 'Content.Shared.Database/Content.Shared.Database.csproj',
            'Content.Server.Database/Content.Server.Database.csproj', 'Content.Packaging/Content.Packaging.csproj',
            'Content.Shared/Content.Shared.csproj', 'Tools/_NC/Meta/CompilerWorker/CompilerWorker.csproj',
            'Content.Client/Content.Client.csproj', 'Content.Server/Content.Server.csproj')
        if (-not $RuntimeOnly) {
            $projects += @('Content.Tests/Content.Tests.csproj', 'Content.IntegrationTests/Content.IntegrationTests.csproj',
                'Content.YAMLLinter/Content.YAMLLinter.csproj')
        }
        foreach ($project in $projects) {
            Write-Output "Building private content project: $project"
            & dotnet build $project -c DebugOpt --no-restore --disable-build-servers /m:1 `
                -p:BuildProjectReferences=false -p:EnableNCDiagnostics=true `
                "-p:NCDiagnosticsOutputRoot=$outputRoot" "-p:NCDiagnosticsBuildId=$BuildId"
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
    } else {
        & dotnet build Content.IntegrationTests/Content.IntegrationTests.csproj -c DebugOpt --no-restore `
            --disable-build-servers /m:1 -p:EnableNCDiagnostics=true `
            "-p:NCDiagnosticsOutputRoot=$outputRoot" "-p:NCDiagnosticsBuildId=$BuildId"
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
} finally {
    Pop-Location
}
if ($RuntimeOnly) {
    Write-Output "Runtime compiled; test projects and complete-acceptance.ps1 are still required: $BuildId"
} else {
    & (Join-Path $PSScriptRoot 'complete-acceptance.ps1') -BuildId $BuildId
}
