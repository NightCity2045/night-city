# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $BuildId)

$ErrorActionPreference = 'Stop'
$parsedBuildId = [Guid]::Empty
if (-not [Guid]::TryParseExact($BuildId, 'N', [ref]$parsedBuildId)) { throw 'BuildId must be a UUID in N format.' }
$BuildId = $parsedBuildId.ToString('N')
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$cohortRoot = Join-Path $repositoryRoot ('artifacts/meta-acceptance/' + $BuildId)
$testDirectory = Join-Path $cohortRoot 'bin/Content.IntegrationTests'
$serverDirectory = Join-Path $cohortRoot 'bin/Content.Server'
$clientDirectory = Join-Path $cohortRoot 'bin/Content.Client'
$workerDirectory = Join-Path $cohortRoot 'bin/MetaCompilerWorker'

# ReferenceOutputAssembly=false excludes the worker DLL from transitive test
# assembly copies. Take its complete bundle from this same private server build.
$coreHash = (Get-FileHash -LiteralPath (Join-Path $testDirectory 'Content.Meta.dll') -Algorithm SHA256).Hash
$workerCoreHash = (Get-FileHash -LiteralPath (Join-Path $workerDirectory 'Content.Meta.dll') -Algorithm SHA256).Hash
if ($coreHash -ne $workerCoreHash) { throw 'The native runtime and compiler worker contain different META cores.' }
foreach ($assembly in @('Content.Server.dll', 'Content.Shared.dll', 'Content.Server.Database.dll', 'Content.Shared.Database.dll', 'Content.Meta.dll')) {
    if ((Get-FileHash -LiteralPath (Join-Path $testDirectory $assembly) -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $serverDirectory $assembly) -Algorithm SHA256).Hash) {
        throw "The native test and server bundles disagree on $assembly"
    }
}
foreach ($assembly in @('Content.Client.dll', 'Content.Shared.dll', 'Content.Shared.Database.dll', 'Content.Meta.dll')) {
    if ((Get-FileHash -LiteralPath (Join-Path $testDirectory $assembly) -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $clientDirectory $assembly) -Algorithm SHA256).Hash) {
        throw "The native test and client bundles disagree on $assembly"
    }
}
$workerFiles = @('Meta.CompilerWorker.dll', 'Meta.CompilerWorker.deps.json', 'Meta.CompilerWorker.runtimeconfig.json', 'Content.Meta.dll')
foreach ($file in $workerFiles) {
    $source = Join-Path $workerDirectory $file
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing canonical worker file: $file" }
}
$assemblies = @('Content.IntegrationTests.dll', 'Content.Server.dll', 'Content.Client.dll', 'Content.Shared.dll',
    'Content.Server.Database.dll', 'Content.Shared.Database.dll', 'Content.Meta.dll', 'Content.Tests.dll') +
    @($workerFiles | ForEach-Object { 'MetaCompilerWorker/' + $_ })
$inventory = foreach ($assembly in $assemblies) {
    $assemblyPath = if ($assembly.StartsWith('MetaCompilerWorker/')) { Join-Path $cohortRoot ('bin/' + $assembly) } else { Join-Path $testDirectory $assembly }
    if (-not (Test-Path -LiteralPath $assemblyPath)) { throw "The built cohort is missing $assembly" }
    [ordered]@{ name = $assembly; sha256 = (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = (Get-Item -LiteralPath $assemblyPath).Length }
}
$fixtureTypes = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'Content.IntegrationTests/Tests/_NC/Meta') -Filter '*.cs' |
    ForEach-Object { ($_.BaseName -split '\.')[0] } | Sort-Object -Unique)
$fixtureStream = [IO.File]::OpenRead((Join-Path $testDirectory 'Content.IntegrationTests.dll'))
$fixtureReader = [Reflection.PortableExecutable.PEReader]::new($fixtureStream)
try {
    $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($fixtureReader)
    $types = @{}
    foreach ($handle in $metadata.TypeDefinitions) {
        $definition = $metadata.GetTypeDefinition($handle)
        if ($metadata.GetString($definition.Namespace) -eq 'Content.IntegrationTests.Tests._NC.Meta') {
            $types[$metadata.GetString($definition.Name)] = $true
        }
    }
    foreach ($type in $fixtureTypes) { if (-not $types.ContainsKey($type)) { throw "The compiled native fixture inventory is missing $type" } }
} finally { $fixtureReader.Dispose(); $fixtureStream.Dispose() }
$resourceManifest = Join-Path $cohortRoot 'resource-snapshot.json'
$resources = if (Test-Path -LiteralPath $resourceManifest) { Get-Content -LiteralPath $resourceManifest -Raw | ConvertFrom-Json } else { $null }
$manifest = [ordered]@{ version = 2; buildId = $BuildId; createdAt = [DateTime]::UtcNow.ToString('O'); testDirectory = $testDirectory; assemblies = @($inventory); resources = $resources; frozenFixtureTypes = $fixtureTypes }
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $cohortRoot 'build-manifest.json') -Encoding utf8
Write-Output "Native cohort ready: $testDirectory"
