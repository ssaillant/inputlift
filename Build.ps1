# Copyright (c) 2026 InputLift contributors
# SPDX-License-Identifier: MIT
# Default: pure core library/tests and synthetic examples. Optional adapter build remains inert.
[CmdletBinding()]
param([switch]$IncludeDeskflowAdapter,[switch]$IncludeWindowsCapture,[switch]$IncludeKanataAdapter)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repoRoot=[IO.Path]::GetFullPath($PSScriptRoot)
$sourceRoot=Join-Path $repoRoot 'src'
$coreRoot=Join-Path $repoRoot 'core'
$examplesRoot=Join-Path $repoRoot 'examples'
$kanataRoot=Join-Path $repoRoot 'integrations\kanata'
$coreBuildRoot=Join-Path $repoRoot 'artifacts\core'
$examplesBuildRoot=Join-Path $repoRoot 'artifacts\examples'
$kanataBuildRoot=Join-Path $repoRoot 'artifacts\kanata'
$packageRoot=Join-Path $repoRoot 'artifacts\package'
$buildRuntime=$IncludeDeskflowAdapter -or $IncludeWindowsCapture
function Assert-NoReparse([string]$Path) {
    $probe=[IO.Path]::GetFullPath($Path)
    while($probe) {
        if(Test-Path -LiteralPath $probe) {
            if((Get-Item -LiteralPath $probe -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Build path must not be a reparse point: $probe" }
        }
        $parent=[IO.Directory]::GetParent($probe)
        if($null -eq $parent) { break }
        $probe=$parent.FullName
    }
}
function Initialize-BuildDirectory([string]$Path) {
    Assert-NoReparse $Path
    if(-not(Test-Path -LiteralPath $Path)) { $null=New-Item -ItemType Directory -Path $Path }
}
function Build-Target([string]$OutputRoot,[string]$Name,[string]$Kind,[string]$Platform,[string[]]$Sources,[string[]]$References) {
    $outputPath=Join-Path $OutputRoot $Name
    Assert-NoReparse $outputPath
    $buildArgs=@('/nologo',('/target:'+$Kind),('/platform:'+$Platform),'/optimize+',('/out:'+$outputPath))
    foreach($reference in $References) { $buildArgs+=('/r:'+$reference) }
    foreach($sourcePath in $Sources) {
        Assert-NoReparse $sourcePath
        if(-not(Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "Build source missing: $sourcePath" }
        $buildArgs+=$sourcePath
    }
    & $compilerPath @buildArgs
    if($LASTEXITCODE -ne 0) { throw "$Name compile failed with code $LASTEXITCODE. No automatic retry." }
}
function Run-PureTests([string]$OutputRoot,[string]$Name) {
    $testPath=Join-Path $OutputRoot $Name
    Assert-NoReparse $testPath
    $start=[Diagnostics.ProcessStartInfo]::new($testPath,'--self-test')
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.WorkingDirectory=$OutputRoot; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$start
    try {
        if(-not $process.Start()) { throw "Could not start $Name pure tests." }
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(30000)) {
            $process.Kill(); [void]$process.WaitForExit(3000)
            throw "$Name pure tests exceeded 30 seconds; only this owned test process was terminated."
        }
        if(-not $stdout.Wait(1000) -or -not $stderr.Wait(1000)) { throw "$Name pure-test output did not complete." }
        $errorText=$stderr.Result
        if($process.ExitCode -ne 0 -or -not [string]::IsNullOrWhiteSpace($errorText)) { throw "$Name pure tests failed, exit $($process.ExitCode): $errorText $($stdout.Result)" }
        $report=$stdout.Result.Trim() | ConvertFrom-Json
        if($report.status -ne 'ok' -or @($report.errors).Count -ne 0) { throw "$Name pure tests did not return an empty-error success report." }
        [pscustomobject]@{Executable=$Name;Report=$report}
    } finally { $process.Dispose() }
}
function Source-Paths([string[]]$Names) {
    foreach($name in $Names) { Join-Path $sourceRoot $name }
}
$encoding=[Text.UTF8Encoding]::new($false)
# Invalidate only fixed generated success records before any build validation or
# compilation can fail. Payloads and unrelated files are never removed here.
# A default core/examples build deliberately leaves an optional package alone.
$staleSuccessPaths=@(
    (Join-Path $coreBuildRoot 'self-test-results.json'),
    (Join-Path $examplesBuildRoot 'self-test-results.json')
)
if($buildRuntime) {
    $staleSuccessPaths+=(Join-Path $packageRoot 'package.json')
    $staleSuccessPaths+=(Join-Path $packageRoot 'self-test-results.json')
    $staleSuccessPaths+=(Join-Path $repoRoot 'artifacts\ephemeral-status\self-test-results.json')
}
if($IncludeKanataAdapter) { $staleSuccessPaths+=(Join-Path $kanataBuildRoot 'self-test-results.json') }
$artifactRoot=[IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')).TrimEnd([IO.Path]::DirectorySeparatorChar)
foreach($path in $staleSuccessPaths) {
    $resolved=[IO.Path]::GetFullPath($path)
    if(-not $resolved.StartsWith($artifactRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Generated success artifact escaped the fixed artifacts directory.'
    }
    Assert-NoReparse $resolved
    if(Test-Path -LiteralPath $resolved) {
        if((Get-Item -LiteralPath $resolved -Force).PSIsContainer) { throw "Success artifact is unexpectedly a directory: $resolved" }
        [IO.File]::Delete($resolved)
    }
}
$compilerPath=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(-not(Test-Path -LiteralPath $compilerPath -PathType Leaf)) { throw 'Existing Windows .NET Framework x64 compiler not found. No download attempted.' }
Initialize-BuildDirectory $coreBuildRoot
$coreLibrary=Join-Path $coreBuildRoot 'InputLift.Core.dll'
Build-Target $coreBuildRoot 'InputLift.Core.dll' 'library' 'anycpu' @((Join-Path $sourceRoot 'GlobePacketDecoder.cs'),(Join-Path $coreRoot 'FnDecoder.cs'),(Join-Path $coreRoot 'InputEvent.cs'),(Join-Path $coreRoot 'FnInputAdapter.cs'),(Join-Path $coreRoot 'AssemblyInfo.cs')) @()
Build-Target $coreBuildRoot 'InputLift.CoreTests.exe' 'exe' 'anycpu' @((Join-Path $sourceRoot 'GlobePacketDecoderTests.cs'),(Join-Path $coreRoot 'CoreTests.cs'),(Join-Path $coreRoot 'EventTests.cs')) @($coreLibrary,'System.Web.Extensions.dll')
$coreReport=Run-PureTests $coreBuildRoot 'InputLift.CoreTests.exe'
[IO.File]::WriteAllText((Join-Path $coreBuildRoot 'self-test-results.json'),($coreReport|ConvertTo-Json -Depth 10),$encoding)
Initialize-BuildDirectory $examplesBuildRoot
$examplesCoreLibrary=Join-Path $examplesBuildRoot 'InputLift.Core.dll'
Assert-NoReparse $examplesCoreLibrary
Copy-Item -LiteralPath $coreLibrary -Destination $examplesCoreLibrary -Force
$examplesSources=@((Join-Path $examplesRoot 'Consumers.cs'),(Join-Path $examplesRoot 'SyntheticProfiles.cs'),(Join-Path $examplesRoot 'Program.cs'))
Build-Target $examplesBuildRoot 'InputLift.Examples.exe' 'exe' 'anycpu' $examplesSources @($coreLibrary,'System.Web.Extensions.dll')
$examplesReport=Run-PureTests $examplesBuildRoot 'InputLift.Examples.exe'
[IO.File]::WriteAllText((Join-Path $examplesBuildRoot 'self-test-results.json'),($examplesReport|ConvertTo-Json -Depth 10),$encoding)
$adapterReports=@(); $payload=@(); $manifestHash=$null
$kanataReport=$null
if($IncludeKanataAdapter) {
    & (Join-Path $repoRoot 'tests\Test-KanataLauncher.ps1')
    Initialize-BuildDirectory $kanataBuildRoot
    $kanataCoreLibrary=Join-Path $kanataBuildRoot 'InputLift.Core.dll'
    Assert-NoReparse $kanataCoreLibrary
    Copy-Item -LiteralPath $coreLibrary -Destination $kanataCoreLibrary -Force
    Assert-NoReparse $kanataRoot
    $kanataSources=@(Get-ChildItem -LiteralPath $kanataRoot -Filter '*.cs' -File | Sort-Object Name | ForEach-Object { $_.FullName })
    if($kanataSources.Count -eq 0) { throw 'Kanata adapter sources are missing.' }
    Build-Target $kanataBuildRoot 'InputLift.Kanata.exe' 'exe' 'anycpu' $kanataSources @($coreLibrary,'System.Web.Extensions.dll')
    $kanataReport=Run-PureTests $kanataBuildRoot 'InputLift.Kanata.exe'
    [IO.File]::WriteAllText((Join-Path $kanataBuildRoot 'self-test-results.json'),($kanataReport|ConvertTo-Json -Depth 10),$encoding)
}
if($buildRuntime) {
    Initialize-BuildDirectory $packageRoot
    & (Join-Path $repoRoot 'tests\Test-SetupValidation.ps1')
    $shared=@(Source-Paths @('DailyPaths.cs','EphemeralStatusPublisher.cs','NativeSearchPolicy.cs'))
    $capture=@(Source-Paths @('DailyCapture.cs','FnEventPipe.cs','GlobeShortcutRelay.cs','OwnedMouseOutput.cs','DeskflowRouteGate.cs','DeskflowRouteProbe.cs','BridgeOwnership.cs','GlobePacketDecoder.cs','GlobePacketDecoderTests.cs','BoundedConsoleWriter.cs','ConnectionHeaderDiagnostics.cs','CaptureContinuity.cs','KeyboardLinkState.cs','ReconnectGuide.cs'))
    $guard=@(Source-Paths @('DailyGuard.cs','OwnedCleanupCoordinator.cs','BluetoothLoggingRefresh.cs','GlobeShortcutRelay.cs','OwnedMouseOutput.cs','DeskflowRouteGate.cs','DeskflowRouteProbe.cs','BoundedConsoleWriter.cs','OwnedBootRecovery.cs'))
    $tray=@(Source-Paths @('InputLift.cs','CaptureRecoveryPolicy.cs','DeskflowRouteProbe.cs'))
    Build-Target $packageRoot 'InputLiftCapture.exe' 'exe' 'x64' ($capture+$shared) @('System.Web.Extensions.dll','System.Management.dll')
    Build-Target $packageRoot 'InputLiftGuard.exe' 'exe' 'x64' ($guard+$shared) @('System.Web.Extensions.dll','System.Management.dll')
    Build-Target $packageRoot 'InputLift.exe' 'winexe' 'x64' ($tray+$shared) @('System.Web.Extensions.dll','System.Windows.Forms.dll','System.Drawing.dll','Microsoft.CSharp.dll','System.Xml.dll')
    foreach($name in @('InputLiftCapture.exe','InputLiftGuard.exe','InputLift.exe')) { $adapterReports+=Run-PureTests $packageRoot $name }
    $recoveryReport=(& (Join-Path $repoRoot 'tests\recovery\Test-Recovery.ps1')) | ConvertFrom-Json -ErrorAction Stop
    if($recoveryReport.status -ne 'ok' -or @($recoveryReport.errors).Count -ne 0) { throw 'Recovery policy/JSON boundary tests did not report success.' }
    $adapterReports+=[pscustomobject]@{Executable='InputLift.RecoveryTests.exe';Report=$recoveryReport}
    $statusPublicationReport=(& (Join-Path $repoRoot 'tests\ephemeral-status\Invoke-Cases.ps1')) | ConvertFrom-Json -ErrorAction Stop
    if($statusPublicationReport.status -ne 'ok' -or @($statusPublicationReport.errors).Count -ne 0) { throw 'Ephemeral status publication fixtures did not report success.' }
    $adapterReports+=[pscustomobject]@{Executable='InputLift.StatusPublisherTests.exe';Report=$statusPublicationReport}
    foreach($name in @('InputLift.exe','InputLiftGuard.exe','InputLiftCapture.exe')) {
        $payload+=[ordered]@{Name=$name;Sha256=(Get-FileHash -LiteralPath (Join-Path $packageRoot $name) -Algorithm SHA256).Hash}
    }
    $manifest=[ordered]@{Version=1;Files=$payload}
    $manifestPath=Join-Path $packageRoot 'package.json'
    [IO.File]::WriteAllText($manifestPath,($manifest|ConvertTo-Json -Depth 5),$encoding)
    [IO.File]::WriteAllText((Join-Path $packageRoot 'self-test-results.json'),($adapterReports|ConvertTo-Json -Depth 10),$encoding)
    $manifestHash=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
}
[pscustomobject]@{
    Build='ok';CoreLibrary=$coreLibrary;CoreReport=$coreReport.Report
    ExamplesExecutable=(Join-Path $examplesBuildRoot 'InputLift.Examples.exe');ExamplesReport=$examplesReport.Report
    WindowsCaptureBuilt=[bool]$buildRuntime;DeskflowAdapterBuilt=[bool]$buildRuntime;PackageManifestSha256=$manifestHash;Payload=$payload
    KanataAdapterBuilt=[bool]$IncludeKanataAdapter;KanataReport=$(if($null -ne $kanataReport){$kanataReport.Report}else{$null})
    Installed=$false;LiveCaptureStarted=$false;NativeInputSent=$false
}|ConvertTo-Json -Depth 7
