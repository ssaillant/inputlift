# Copyright (c) 2026 InputLift contributors
# SPDX-License-Identifier: MIT
# Compiles production code and exercises synthetic files in ignored artifacts.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$outputRoot=Join-Path $repoRoot 'artifacts\ephemeral-status'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources=@((Join-Path $repoRoot 'src\EphemeralStatusPublisher.cs'),(Join-Path $PSScriptRoot 'StatusPublisherTests.cs'))
foreach($path in @($outputRoot,$compiler)+$sources) {
    $cursor=[IO.Path]::GetFullPath($path)
    while($cursor) {
        if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Fixture paths must not contain reparse points.' }
        $parent=[IO.Directory]::GetParent($cursor);if($null -eq $parent){break};$cursor=$parent.FullName
    }
}
if(-not(Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Existing x64 Framework compiler unavailable; no download attempted.' }
[void][IO.Directory]::CreateDirectory($outputRoot)
$exe=Join-Path $outputRoot 'InputLift.StatusPublisherTests.exe'
if((Test-Path -LiteralPath $exe) -and ((Get-Item -LiteralPath $exe -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Fixture executable is a reparse point.' }
$report=Join-Path $outputRoot 'self-test-results.json'
if(Test-Path -LiteralPath $report) {
    $item=Get-Item -LiteralPath $report -Force
    if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected fixture report path.' }
    [IO.File]::Delete($report)
}
& $compiler /nologo /target:exe /platform:x64 /optimize+ /r:System.Web.Extensions.dll ('/out:'+$exe) @sources
if($LASTEXITCODE -ne 0) { throw 'Status publisher fixture compilation failed; no test was started.' }
$fixture=Join-Path $outputRoot ('fixture-'+[guid]::NewGuid().ToString('N'))
$info=[Diagnostics.ProcessStartInfo]::new($exe,('--fixture "'+$fixture+'"'))
$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WorkingDirectory=$outputRoot
$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
$child=[Diagnostics.Process]::new();$child.StartInfo=$info
try {
    if(-not $child.Start()) { throw 'Owned synthetic fixture did not start.' }
    $stdout=$child.StandardOutput.ReadToEndAsync();$stderr=$child.StandardError.ReadToEndAsync()
    if(-not $child.WaitForExit(10000)) { $child.Kill();[void]$child.WaitForExit(2000);throw 'Synthetic fixture exceeded ten seconds; only its owned child was stopped.' }
    if(-not $stdout.Wait(1000) -or -not $stderr.Wait(1000)) { throw 'Fixture output did not complete.' }
    $result=$stdout.Result.Trim()|ConvertFrom-Json
    if($child.ExitCode -ne 0 -or $stderr.Result -or $result.status -ne 'ok' -or @($result.errors).Count -ne 0) { throw ('Status publisher tests failed: '+$stdout.Result+' '+$stderr.Result) }
    [IO.File]::WriteAllText($report,$stdout.Result.Trim(),[Text.UTF8Encoding]::new($false))
    $stdout.Result.Trim()
} finally { $child.Dispose() }
