# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SourcePath,
    [Parameter(Mandatory)] [ValidatePattern('^[a-f0-9]{64}$')] [string] $SourceSha256,
    [Parameter(Mandatory)] [string] $DatabaseAssembly
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$source = [IO.Path]::GetFullPath((Join-Path $workspace $SourcePath))
$assembly = [IO.Path]::GetFullPath((Join-Path $workspace $DatabaseAssembly))
$prefix = $workspace.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $assembly.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $assembly -PathType Leaf)) { throw 'Database assembly must exist inside the owned workspace.' }
& dotnet run --project (Join-Path $PSScriptRoot 'SqliteSeed/SqliteSeed.csproj') -- $workspace $source $SourceSha256 $assembly
if ($LASTEXITCODE -ne 0) { throw 'Pristine SQLite packaging failed.' }
