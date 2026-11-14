# Copyright (c) 2026 InputLift contributors
# SPDX-License-Identifier: MIT
# Compiles a pure verifier using an already built public core library. No downloads.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$coreLibrary = Join-Path $repoRoot 'artifacts\core\InputLift.Core.dll'
$outputRoot = Join-Path $repoRoot 'artifacts\evidence\kanata'
$reportPath = Join-Path $outputRoot 'verification.json'
function Assert-NoReparse([string]$Path) {
    $probe = [IO.Path]::GetFullPath($Path)
    while ($probe) {
        if ((Test-Path -LiteralPath $probe) -and ((Get-Item -LiteralPath $probe -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Fixture build path must not contain a reparse point.'
        }
        $parent = [IO.Directory]::GetParent($probe)
        if ($null -eq $parent) { break }
        $probe = $parent.FullName
    }
}
foreach ($path in @($coreLibrary, $outputRoot, $reportPath, (Join-Path $PSScriptRoot 'VerifyFixture.cs'), (Join-Path $PSScriptRoot 'fn-profile.synthetic.json'))) { Assert-NoReparse $path }
if (Test-Path -LiteralPath $reportPath) { [IO.File]::Delete($reportPath) }
if (-not (Test-Path -LiteralPath $coreLibrary -PathType Leaf)) { throw 'Run ./Build.ps1 first. The fixture verifier does not build or download dependencies.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Existing .NET Framework compiler was not found. No download attempted.' }
$null = New-Item -ItemType Directory -Path $outputRoot -Force
$verifier = Join-Path $outputRoot 'VerifyFixture.exe'
Assert-NoReparse $verifier
& $compiler /nologo /target:exe /platform:anycpu /optimize+ ("/out:" + $verifier) ("/r:" + $coreLibrary) /r:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'VerifyFixture.cs')
if ($LASTEXITCODE -ne 0) { throw "Fixture verifier compile failed: $LASTEXITCODE. No automatic retry." }
$localCore = Join-Path $outputRoot 'InputLift.Core.dll'
Assert-NoReparse $localCore
Copy-Item -LiteralPath $coreLibrary -Destination $localCore -Force
$result = & $verifier (Join-Path $PSScriptRoot 'fn-profile.synthetic.json')
if ($LASTEXITCODE -ne 0) { throw "Fixture verification failed: $LASTEXITCODE. No automatic retry." }
$report = $result | ConvertFrom-Json
if ($report.status -ne 'ok' -or $report.nativeActionsPerformed -or $report.hardwareValidation -or $report.liveTransportTested -or $report.ordinaryKeyReads -ne 0) {
    throw 'Fixture verifier returned an unexpected report.'
}
[IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 6) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
$report | ConvertTo-Json -Depth 6
