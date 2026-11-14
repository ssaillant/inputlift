# Copyright (c) 2026 InputLift contributors
# SPDX-License-Identifier: MIT
# Compile the production UI classifier with a test Main; never invoke live entrypoints.
[CmdletBinding()]
param([string]$OutputDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repoRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
function Assert-NoReparse([string]$Path) {
    $probe=[IO.Path]::GetFullPath($Path)
    while($probe) {
        if(Test-Path -LiteralPath $probe) {
            if((Get-Item -LiteralPath $probe -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Recovery test path must not be a reparse point: $probe"
            }
        }
        $parent=[IO.Directory]::GetParent($probe)
        if($null -eq $parent) { break }
        $probe=$parent.FullName
    }
}
if([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory=Join-Path $repoRoot 'artifacts\recovery' }
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'The installed Windows .NET Framework compiler is required. No download is performed.' }
Assert-NoReparse $compiler
Assert-NoReparse $OutputDirectory
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$target=Join-Path $OutputDirectory 'InputLift.RecoveryTests.exe'
Assert-NoReparse $target
$sources=@('src\InputLift.cs','src\CaptureRecoveryPolicy.cs','src\DailyPaths.cs','src\DeskflowRouteProbe.cs',
    'tests\recovery\CaptureRecoveryPolicyTests.cs','tests\recovery\RecoveryBoundaryTests.cs') |
    ForEach-Object { Join-Path $repoRoot $_ }
foreach($source in $sources) {
    Assert-NoReparse $source
    if(-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Recovery test source is missing: $source" }
}
$compileOutput=& $compiler /nologo /target:exe /platform:x64 /optimize+ /main:RecoveryBoundaryTests "/out:$target" "/reference:System.Web.Extensions.dll,System.Windows.Forms.dll,System.Drawing.dll,Microsoft.CSharp.dll,System.Xml.dll" @sources 2>&1
if($LASTEXITCODE -ne 0) { throw "Recovery test compilation failed ($LASTEXITCODE): $($compileOutput -join [Environment]::NewLine)" }
foreach($line in $compileOutput) { Write-Verbose $line }
Assert-NoReparse $target
$start=[Diagnostics.ProcessStartInfo]::new($target)
$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=$OutputDirectory
$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
$process=[Diagnostics.Process]::new();$process.StartInfo=$start
try {
    if(-not $process.Start()) { throw 'Could not start the owned recovery test process.' }
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    if(-not $process.WaitForExit(30000)) {
        $process.Kill();[void]$process.WaitForExit(3000)
        throw 'Recovery tests exceeded 30 seconds; only this owned test process was terminated.'
    }
    if(-not $stdout.Wait(1000) -or -not $stderr.Wait(1000)) { throw 'Recovery test output did not complete.' }
    $report=$stdout.Result.Trim()
    if($process.ExitCode -ne 0 -or -not [string]::IsNullOrWhiteSpace($stderr.Result)) {
        throw "Recovery tests failed, exit $($process.ExitCode): $($stderr.Result) $report"
    }
    $parsed=$report | ConvertFrom-Json -ErrorAction Stop
    $errorsProperty=$parsed.PSObject.Properties['errors']
    if($parsed.status -cne 'ok' -or $null -eq $errorsProperty -or $errorsProperty.Value -isnot [Array] -or $errorsProperty.Value.Count -ne 0) {
        throw "Recovery tests did not return an empty-error success report: $report"
    }
    foreach($flag in @('captureStarted','installedStateRead','taskStarted','pipeStarted','nativeInput','settingsChanged')) {
        $property=$parsed.PSObject.Properties[$flag]
        if($null -eq $property -or $property.Value -isnot [bool] -or $property.Value) {
            throw "Recovery tests did not prove $flag=false: $report"
        }
    }
    $report
} finally { $process.Dispose() }
