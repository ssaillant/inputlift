# Copyright (c) 2026 InputLift contributors
# SPDX-License-Identifier: MIT
# Compilation only. This script never starts the native fixture or changes settings.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$outputRoot=Join-Path $repoRoot 'artifacts\native-pipe'
$reportPath=Join-Path $outputRoot 'build-result.json'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$references=@((Join-Path $repoRoot 'artifacts\kanata\InputLift.Kanata.exe'),(Join-Path $repoRoot 'artifacts\kanata\InputLift.Core.dll'))
function Assert-NoReparse([string]$Path) {
    $cursor=[IO.Path]::GetFullPath($Path)
    while($cursor) {
        if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Native fixture build paths must not contain reparse points.' }
        $parent=[IO.Directory]::GetParent($cursor)
        if($null -eq $parent) { break }
        $cursor=$parent.FullName
    }
}
Assert-NoReparse $reportPath
if(Test-Path -LiteralPath $reportPath) { [IO.File]::Delete($reportPath) }
foreach($path in @($compiler)+$references) { if(-not(Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Build the Kanata adapter first; existing compiler and adapter/core files are required.' } }
$sources=@((Join-Path $repoRoot 'src\FnEventPipe.cs'),(Join-Path $PSScriptRoot 'DailyPathsFixture.cs'),(Join-Path $PSScriptRoot 'NativePipeTests.cs'))
foreach($path in @($outputRoot)+$sources+$references) { Assert-NoReparse $path }
$null=New-Item -ItemType Directory -Path $outputRoot -Force
$output=Join-Path $outputRoot 'InputLift.NativePipeTests.exe'
Assert-NoReparse $output
$arguments=@('/nologo','/target:exe','/platform:x64','/optimize+',('/out:'+$output),'/r:System.Web.Extensions.dll')
foreach($reference in $references) { $arguments+=('/r:'+$reference) }
$arguments+=$sources
& $compiler @arguments
if($LASTEXITCODE -ne 0) { throw "Native pipe fixture compilation failed: $LASTEXITCODE. No native test was started." }
foreach($reference in $references) { Copy-Item -LiteralPath $reference -Destination (Join-Path $outputRoot ([IO.Path]::GetFileName($reference))) -Force }
$report=[ordered]@{status='compiled';fixture='artifacts/native-pipe/InputLift.NativePipeTests.exe';FixtureSha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash;ProductionPipeSourceSha256=(Get-FileHash -LiteralPath $sources[0] -Algorithm SHA256).Hash;NativeFixtureStarted=$false;BluetoothCaptureStarted=$false;SettingsChanged=$false}
$json=$report|ConvertTo-Json -Depth 4 -Compress
[IO.File]::WriteAllText($reportPath,$json,[Text.UTF8Encoding]::new($false))
$json
