# Copyright (c) 2026 InputLift contributors
# SPDX-License-Identifier: MIT
# Isolated generated-file regression only. No setup, capture, input or settings.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repoRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$readinessRoot=[IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\readiness'))
$runId=[guid]::NewGuid().ToString('N')
$fixtureRoot=Join-Path $readinessRoot $runId
$encoding=[Text.UTF8Encoding]::new($false)
$checks=0

function Assert-Check([bool]$Condition,[string]$Description) {
    if(-not $Condition) { throw "Build failure regression failed: $Description" }
    $script:checks++
}
function Assert-NoReparse([string]$Path) {
    $probe=[IO.Path]::GetFullPath($Path)
    while($probe) {
        if(Test-Path -LiteralPath $probe) {
            if((Get-Item -LiteralPath $probe -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Fixture path must not contain reparse points: $probe"
            }
        }
        $parent=[IO.Directory]::GetParent($probe)
        if($null -eq $parent) { break }
        $probe=$parent.FullName
    }
}
function New-FixtureDirectory([string]$Path) {
    $resolved=[IO.Path]::GetFullPath($Path)
    if(-not $resolved.StartsWith($readinessRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Fixture escaped the fixed ignored readiness directory.'
    }
    Assert-NoReparse $resolved
    if(Test-Path -LiteralPath $resolved) { throw "Fixture already exists; no overwrite attempted: $resolved" }
    $null=[IO.Directory]::CreateDirectory($resolved)
    Assert-NoReparse $resolved
}
function Write-NewFixtureFile([string]$Path,[string]$Text) {
    Assert-NoReparse $Path
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try { $bytes=$encoding.GetBytes($Text);$stream.Write($bytes,0,$bytes.Length) }
    finally { $stream.Dispose() }
}
function Invoke-FailingBuild([string]$Fixture,[string]$BuildMode) {
    $build=Join-Path $Fixture 'Build.ps1'
    if($build.Contains('"')) { throw 'Unexpected quote in fixed fixture path.' }
    $arguments='-NoProfile -File "'+$build+'"'
    if($BuildMode -eq 'adapter') { $arguments+=' -IncludeDeskflowAdapter' }
    if($BuildMode -eq 'kanata') { $arguments+=' -IncludeKanataAdapter' }
    $engine=[Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    $start=[Diagnostics.ProcessStartInfo]::new($engine,$arguments)
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.WorkingDirectory=$Fixture;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $process=[Diagnostics.Process]::new();$process.StartInfo=$start
    try {
        if(-not $process.Start()) { throw 'The isolated build process did not start.' }
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(20000)) {
            $process.Kill();[void]$process.WaitForExit(3000)
            throw 'The isolated missing-source build exceeded 20 seconds; only that owned child was stopped.'
        }
        if(-not $stdout.Wait(1000) -or -not $stderr.Wait(1000)) { throw 'The isolated build output did not complete.' }
        $details=$stdout.Result+"`n"+$stderr.Result
        Assert-Check ($process.ExitCode -ne 0) 'A fixture without C# sources must fail.'
        Assert-Check ($details.Contains('Build source missing:')) ('Expected the missing-source validation failure; got: '+$details)
    }
    finally { $process.Dispose() }
}

Assert-NoReparse $readinessRoot
New-FixtureDirectory $fixtureRoot
$successMarker='{"status":"ok","fixture":"previous-success"}'
$unrelatedMarker='unrelated fixture file; preserve exactly'
$payloadMarker='non-executable payload sentinel; preserve exactly'
foreach($mode in @('default','adapter','kanata')) {
    $fixture=Join-Path $fixtureRoot $mode
    New-FixtureDirectory $fixture
    Copy-Item -LiteralPath (Join-Path $repoRoot 'Build.ps1') -Destination (Join-Path $fixture 'Build.ps1')
    foreach($directory in @('core','examples','package','kanata')) {
        New-FixtureDirectory (Join-Path $fixture ('artifacts\'+$directory))
        $folder=Join-Path $fixture ('artifacts\'+$directory)
        Write-NewFixtureFile (Join-Path $folder 'self-test-results.json') $successMarker
        Write-NewFixtureFile (Join-Path $folder 'unrelated.txt') $unrelatedMarker
    }
    $core=Join-Path $fixture 'artifacts\core'
    $examples=Join-Path $fixture 'artifacts\examples'
    $package=Join-Path $fixture 'artifacts\package'
    $kanata=Join-Path $fixture 'artifacts\kanata'
    Write-NewFixtureFile (Join-Path $core 'InputLift.Core.dll') $payloadMarker
    Write-NewFixtureFile (Join-Path $examples 'InputLift.Examples.exe') $payloadMarker
    Write-NewFixtureFile (Join-Path $package 'InputLift.exe') $payloadMarker
    Write-NewFixtureFile (Join-Path $kanata 'InputLift.Kanata.exe') $payloadMarker
    Write-NewFixtureFile (Join-Path $package 'package.json') $successMarker
    $adapter=$mode -eq 'adapter'
    Invoke-FailingBuild $fixture $mode
    Assert-Check (-not(Test-Path -LiteralPath (Join-Path $core 'self-test-results.json'))) "$mode core success report was invalidated."
    Assert-Check (-not(Test-Path -LiteralPath (Join-Path $examples 'self-test-results.json'))) "$mode examples success report was invalidated."
    foreach($name in @('package.json','self-test-results.json')) {
        $path=Join-Path $package $name
        if($adapter) { Assert-Check (-not(Test-Path -LiteralPath $path)) "Adapter failure invalidated $name before core compilation." }
        else { Assert-Check ([IO.File]::ReadAllText($path) -ceq $successMarker) "Default failure left optional $name unchanged." }
    }
    $kanataSuccess=Join-Path $kanata 'self-test-results.json'
    if($mode -eq 'kanata') { Assert-Check (-not(Test-Path -LiteralPath $kanataSuccess)) 'Kanata failure invalidated its success report before core compilation.' }
    else { Assert-Check ([IO.File]::ReadAllText($kanataSuccess) -ceq $successMarker) "$mode failure left optional Kanata report unchanged." }
    foreach($folder in @($core,$examples,$package,$kanata)) {
        Assert-Check ([IO.File]::ReadAllText((Join-Path $folder 'unrelated.txt')) -ceq $unrelatedMarker) "$mode preserved an unrelated file."
    }
    foreach($path in @((Join-Path $core 'InputLift.Core.dll'),(Join-Path $examples 'InputLift.Examples.exe'),(Join-Path $package 'InputLift.exe'),(Join-Path $kanata 'InputLift.Kanata.exe'))) {
        Assert-Check ([IO.File]::ReadAllText($path) -ceq $payloadMarker) "$mode preserved a payload sentinel."
    }
}
[pscustomobject]@{status='ok';checks=$checks;errors=@();LiveActionsPerformed=$false;RetainedFixture=('artifacts/readiness/'+$runId)}|ConvertTo-Json -Compress
