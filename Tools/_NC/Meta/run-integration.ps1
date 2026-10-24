# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
[CmdletBinding()]
param(
    [string] $Filter = 'FullyQualifiedName~NCPersistentMutationPersistenceTest|FullyQualifiedName~Content.IntegrationTests.Tests._NC.Meta',
    [string] $ResultsDirectory = 'Docs/Design/_NC/Meta/validation',
    [string] $ResultName = 'meta-native-integration.trx',
    [string] $BuiltDirectory = '',
    [ValidateRange(30, 1800)] [int] $TestTimeoutSeconds = 1800,
    [ValidateRange(1, 4)] [int] $TestWorkers = 1,
    [ValidatePattern('^$|^[a-f0-9]{32}$')] [string] $SqliteSeedId = '',
    [ValidatePattern('^$|^[a-f0-9]{64}$')] [string] $SqliteSeedSha256 = '',
    [ValidatePattern('^$|^[a-f0-9]{64}$')] [string] $SqliteManifestSha256 = ''
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$seedArguments = @($SqliteSeedId, $SqliteSeedSha256, $SqliteManifestSha256) | Where-Object { $_.Length -gt 0 }
if ($seedArguments.Count -ne 0 -and $seedArguments.Count -ne 3) { throw 'All pristine SQLite seed arguments are required together.' }
if ($seedArguments.Count -eq 3) {
    if ($SqliteSeedId -eq [Guid]::Empty.ToString('N')) { throw 'Seed package identity cannot be empty.' }
    $seedDirectory = Join-Path $repositoryRoot ('artifacts/meta-db-bootstrap/' + $SqliteSeedId)
    foreach ($entry in @(@{ Name='seed.db'; Hash=$SqliteSeedSha256 }, @{ Name='manifest.json'; Hash=$SqliteManifestSha256 })) {
        $entryPath = Join-Path $seedDirectory $entry.Name
        if (-not (Test-Path -LiteralPath $entryPath -PathType Leaf) -or
            ((Get-Item -LiteralPath $entryPath).Attributes -band [IO.FileAttributes]::ReadOnly) -eq 0 -or
            (Get-FileHash -LiteralPath $entryPath -Algorithm SHA256).Hash -ne $entry.Hash) { throw 'The immutable SQLite seed package does not match its explicit hashes.' }
        for ($item = Get-Item -LiteralPath $entryPath; $null -ne $item; $item = if ($item -is [IO.FileInfo]) { $item.Directory } else { $item.Parent }) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'SQLite seed paths cannot traverse reparse points.' }
        }
    }
}
$metaBuiltDirectory = if ($BuiltDirectory.Length -eq 0) {
    Join-Path $repositoryRoot 'bin/NCDiagnostics/Content.IntegrationTests'
} elseif ([System.IO.Path]::IsPathRooted($BuiltDirectory)) {
    [System.IO.Path]::GetFullPath($BuiltDirectory)
} else { [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $BuiltDirectory)) }
$repositoryPrefix = $repositoryRoot + [System.IO.Path]::DirectorySeparatorChar
if (-not ($metaBuiltDirectory + [System.IO.Path]::DirectorySeparatorChar).StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The compiled test directory must be inside this workspace.'
}
$testAssembly = Join-Path $metaBuiltDirectory 'Content.IntegrationTests.dll'
if (-not (Test-Path -LiteralPath $testAssembly)) {
    throw 'Build Content.IntegrationTests with EnableNCDiagnostics=true before running this harness.'
}
$cohortRoot = [System.IO.Directory]::GetParent($metaBuiltDirectory).Parent.FullName
$manifestPath = Join-Path $cohortRoot 'build-manifest.json'
if (Test-Path -LiteralPath $manifestPath) {
    $buildManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($entry in $buildManifest.assemblies) {
        $entryRoot = if ($buildManifest.version -eq 2 -and $entry.name.StartsWith('MetaCompilerWorker/')) { Join-Path $cohortRoot 'bin' } else { $metaBuiltDirectory }
        $entryPath = [System.IO.Path]::GetFullPath((Join-Path $entryRoot $entry.name))
        if (-not $entryPath.StartsWith($entryRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase) -or
            (Get-FileHash -LiteralPath $entryPath -Algorithm SHA256).Hash -ne $entry.sha256) {
            throw ('The compiled cohort hash inventory does not match: ' + $entry.name)
        }
    }
}
# Validate metadata without loading content or starting a client. A shared output
# directory may contain another validation's intentionally reduced test assembly.
$assemblyStream = [System.IO.File]::OpenRead($testAssembly)
$assemblyReader = [System.Reflection.PortableExecutable.PEReader]::new($assemblyStream)
try {
    $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($assemblyReader)
    $compiledTypes = @{}
    foreach ($handle in $metadata.TypeDefinitions) {
        $definition = $metadata.GetTypeDefinition($handle)
        if ($metadata.GetString($definition.Namespace) -eq 'Content.IntegrationTests.Tests._NC.Meta') {
            $compiledTypes[$metadata.GetString($definition.Name)] = $true
        }
    }
    # A source task can advance while an immutable older image is being tested.
    # Its manifest records the inventory checked when that image was sealed.
    $expectedTypes = if ((Test-Path -LiteralPath $manifestPath) -and $buildManifest.frozenFixtureTypes) {
        @($buildManifest.frozenFixtureTypes)
    } else {
        @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'Content.IntegrationTests/Tests/_NC/Meta') -Filter '*.cs' |
            ForEach-Object { ($_.BaseName -split '\.')[0] } | Sort-Object -Unique)
    }
    if ($expectedTypes.Count -gt 128) { throw 'The frozen native fixture inventory exceeds its bound.' }
    $missing = @($expectedTypes | Where-Object { -not $compiledTypes.ContainsKey($_) })
    if ($missing.Count) { throw ('The compiled test cohort is incomplete: ' + ($missing -join ', ')) }
} finally {
    $assemblyReader.Dispose()
    $assemblyStream.Dispose()
}

# Robust's development resource mounts expect bin/<project>, with Resources and
# RobustToolbox beside bin. Keep diagnostics output isolated and supply that
# layout in a fresh owned artifact directory instead of modifying the engine.
$runRoot = Join-Path $repositoryRoot ('artifacts/meta-integration/' + [Guid]::NewGuid().ToString('N'))
$binaryDirectory = Join-Path $runRoot 'bin/Content.IntegrationTests'
$expectedRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts/meta-integration')) + [System.IO.Path]::DirectorySeparatorChar
if (-not ([System.IO.Path]::GetFullPath($runRoot) + [System.IO.Path]::DirectorySeparatorChar).StartsWith($expectedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The staged test directory must remain inside this repository artifact directory.'
}
New-Item -ItemType Directory -Path $binaryDirectory -Force | Out-Null
Write-Output "Staging isolated integration runtime: $runRoot"
Get-ChildItem -LiteralPath $metaBuiltDirectory -Force | Copy-Item -Destination $binaryDirectory -Recurse -Force
if ((Test-Path -LiteralPath $manifestPath) -and $buildManifest.version -eq 2) {
    $workerSource = Join-Path $cohortRoot 'bin/MetaCompilerWorker'
    $workerDestination = Join-Path $runRoot 'bin/MetaCompilerWorker'
    New-Item -ItemType Directory -Path $workerDestination | Out-Null
    foreach ($file in @('Meta.CompilerWorker.dll','Meta.CompilerWorker.deps.json','Meta.CompilerWorker.runtimeconfig.json','Content.Meta.dll')) {
        Copy-Item -LiteralPath (Join-Path $workerSource $file) -Destination (Join-Path $workerDestination $file)
    }
}
if (Test-Path -LiteralPath $manifestPath) { Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $runRoot 'build-manifest.json') }
# A changing live resource tree can introduce new prototype types after the
# copied assemblies were compiled. Keep this entire test run on one resource image.
$resourceSource = Join-Path $repositoryRoot 'Resources'
if (Test-Path -LiteralPath (Join-Path $cohortRoot 'Resources')) { $resourceSource = Join-Path $cohortRoot 'Resources' }
$resourceDestination = Join-Path $runRoot 'Resources'
if ($IsWindows -and (Test-Path -LiteralPath (Join-Path $cohortRoot 'Resources'))) {
    # This is the pre-build frozen image, never the live workspace Resources.
    # Reusing its file identities avoids another full small-file copy per test batch.
    New-Item -ItemType Junction -Path $resourceDestination -Target $resourceSource | Out-Null
    Write-Output "Using frozen cohort resource image: $resourceSource"
} elseif (Get-Command robocopy -ErrorAction SilentlyContinue) {
    & robocopy $resourceSource $resourceDestination /E /MT:16 /R:0 /W:0 /NFL /NDL /NJH /NJS /NP
    if ($LASTEXITCODE -ge 8) { throw "Isolated resource copy failed: $LASTEXITCODE" }
} else {
    Copy-Item -LiteralPath $resourceSource -Destination $runRoot -Recurse -Force
}
New-Item -ItemType Junction -Path (Join-Path $runRoot 'RobustToolbox') -Target (Join-Path $repositoryRoot 'RobustToolbox') | Out-Null
New-Item -ItemType Junction -Path (Join-Path $runRoot 'bin/Content.Client') -Target $binaryDirectory | Out-Null
New-Item -ItemType Junction -Path (Join-Path $runRoot 'bin/Content.Server') -Target $binaryDirectory | Out-Null

$resultPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $ResultsDirectory))
New-Item -ItemType Directory -Path $resultPath -Force | Out-Null
Write-Output "Isolated integration runtime: $runRoot"
$previousIsolation = $env:NC_DIAGNOSTICS_ISOLATED
$seedEnvironment = @('NC_DIAGNOSTICS_WORKSPACE','NC_DIAGNOSTICS_SQLITE_SEED_ID','NC_DIAGNOSTICS_SQLITE_SEED_SHA256','NC_DIAGNOSTICS_SQLITE_MANIFEST_SHA256')
$previousSeedEnvironment = @{}
foreach ($name in $seedEnvironment) { $previousSeedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    $env:NC_DIAGNOSTICS_ISOLATED = '1'
    # Do not inherit an implicit package: this invocation explicitly selects all hashes or none.
    foreach ($name in $seedEnvironment) { [Environment]::SetEnvironmentVariable($name, $null) }
    if ($seedArguments.Count -eq 3) {
        $env:NC_DIAGNOSTICS_WORKSPACE = $repositoryRoot
        $env:NC_DIAGNOSTICS_SQLITE_SEED_ID = $SqliteSeedId
        $env:NC_DIAGNOSTICS_SQLITE_SEED_SHA256 = $SqliteSeedSha256
        $env:NC_DIAGNOSTICS_SQLITE_MANIFEST_SHA256 = $SqliteManifestSha256
    }
    & dotnet test (Join-Path $binaryDirectory 'Content.IntegrationTests.dll') --filter $Filter `
        --blame-hang-timeout ($TestTimeoutSeconds.ToString() + 's') --blame-hang-dump-type mini `
        --logger "trx;LogFileName=$ResultName" --results-directory $resultPath -- NUnit.ConsoleOut=0 "NUnit.NumberOfTestWorkers=$TestWorkers"
    $testExitCode = $LASTEXITCODE
} finally {
    $env:NC_DIAGNOSTICS_ISOLATED = $previousIsolation
    foreach ($name in $seedEnvironment) { [Environment]::SetEnvironmentVariable($name, $previousSeedEnvironment[$name]) }
}
exit $testExitCode
