param(
    [string] $TestAssembly = 'bin/Content.IntegrationTests/Content.IntegrationTests.dll',
    [string] $ResultsDirectory = 'TestResults/CyberwareClosure/Restart'
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$assembly = (Resolve-Path (Join-Path $repository $TestAssembly)).Path
$results = [System.IO.Path]::GetFullPath((Join-Path $repository $ResultsDirectory))
if (-not $results.StartsWith($repository + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Restart evidence must stay inside the repository.'
}
New-Item -ItemType Directory -Force -Path $results | Out-Null
$previousPhase = $env:NC_CYBERWARE_RESTART_PHASE
$previousDirectory = $env:NC_CYBERWARE_RESTART_DIRECTORY
try {
    $env:NC_CYBERWARE_RESTART_DIRECTORY = $results
    foreach ($phase in @('save', 'restore')) {
        $env:NC_CYBERWARE_RESTART_PHASE = $phase
        $report = Join-Path $results "$phase.trx"
        if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
        # Each invocation creates a new test host and a new actual Robust server/client pair.
        & dotnet test $assembly --filter "FullyQualifiedName~NCCyberwareRestartTest&Name~$phase" `
            --logger "trx;LogFileName=$phase.trx" --results-directory $results -- `
            NUnit.ConsoleOut=0 NUnit.NumberOfTestWorkers=0 NUnit.MapWarningTo=Failed `
            "RunConfiguration.DotNetHostPath=$((Get-Command dotnet).Source)"
        if ($LASTEXITCODE -ne 0) { throw "Restart phase $phase failed ($LASTEXITCODE)." }
        [xml] $trx = Get-Content -LiteralPath $report -Raw
        $counts = $trx.TestRun.ResultSummary.Counters
        if ($counts.total -ne '1' -or $counts.passed -ne '1') {
            throw "Restart phase $phase must execute and pass exactly one case."
        }
    }
    Write-Output "Restart accepted: save PID $(Get-Content (Join-Path $results 'save-process.txt')), restore PID $(Get-Content (Join-Path $results 'restore-process.txt'))."
}
finally {
    $env:NC_CYBERWARE_RESTART_PHASE = $previousPhase
    $env:NC_CYBERWARE_RESTART_DIRECTORY = $previousDirectory
}
