# Copyright (c) 2026 InputLift contributors
# SPDX-License-Identifier: MIT
# Explicit interactive test. Starts a keyboard hook and a harmless held layer.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$KanataExecutable,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedKanataSha256,
    [switch]$RunInteractiveDemo
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if(-not $RunInteractiveDemo) { throw 'Explicit -RunInteractiveDemo consent is required: this temporarily starts keyboard remapping.' }
$repoRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$kanataPath=(Resolve-Path -LiteralPath $KanataExecutable).Path
if((Get-FileHash -LiteralPath $kanataPath -Algorithm SHA256).Hash -cne $ExpectedKanataSha256.ToUpperInvariant()) { throw 'Kanata executable differs from the explicitly reviewed hash.' }
$adapterPath=Join-Path $repoRoot 'artifacts\kanata\InputLift.Kanata.exe'
$configPath=Join-Path $repoRoot 'integrations\kanata\held-layer.kbd'
if(-not(Test-Path -LiteralPath $adapterPath -PathType Leaf)) { throw 'Build the Kanata adapter before running this test.' }
$outputRoot=Join-Path $repoRoot 'artifacts\kanata'
$reportPath=Join-Path $outputRoot 'real-kanata-results.json'
if(Test-Path -LiteralPath $reportPath) { [IO.File]::Delete($reportPath) }
$port=7070
$listeners=[Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
if(@($listeners | Where-Object Port -eq $port).Count -ne 0) { throw 'Demo port 7070 is already in use. No process was started or replaced.' }
function New-OwnedStart([string]$Executable,[string[]]$Arguments) {
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=$Executable;$start.WorkingDirectory=$repoRoot
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process=[Diagnostics.Process]::new();$process.StartInfo=$start
    if(-not $process.Start()) { $process.Dispose();throw 'Owned test process failed to start.' }
    return $process
}
$kanataProcess=$null;$adapterProcess=$null;$result=$null
try {
    $kanataProcess=New-OwnedStart $kanataPath @('--cfg',$configPath,'--port','127.0.0.1:7070')
    $kanataOut=$kanataProcess.StandardOutput.ReadToEndAsync()
    $kanataError=$kanataProcess.StandardError.ReadToEndAsync()
    $startup=[Diagnostics.Stopwatch]::StartNew();$ready=$false
    while($startup.ElapsedMilliseconds -lt 5000) {
        if($kanataProcess.HasExited) { throw ('Kanata exited before readiness: '+$kanataOut.Result+' '+$kanataError.Result) }
        $probe=[Net.Sockets.TcpClient]::new([Net.Sockets.AddressFamily]::InterNetwork)
        try {
            $pending=$probe.ConnectAsync([Net.IPAddress]::Loopback,$port)
            if($pending.Wait(100) -and $probe.Connected) { $ready=$true;break }
        } catch { }
        finally { $probe.Dispose() }
        Start-Sleep -Milliseconds 100
    }
    if(-not $ready) { throw 'Kanata did not listen on loopback within five seconds. No automatic retry.' }
    $adapterProcess=New-OwnedStart $adapterPath @('--demo','--port','7070','--virtual-key','inputlift-fn')
    $adapterOut=$adapterProcess.StandardOutput.ReadToEndAsync()
    $adapterError=$adapterProcess.StandardError.ReadToEndAsync()
    if(-not $adapterProcess.WaitForExit(10000)) { throw 'Owned synthetic demo exceeded ten seconds.' }
    if(-not $adapterOut.Wait(1000) -or -not $adapterError.Wait(1000)) { throw 'Owned demo output did not finish.' }
    if($adapterProcess.ExitCode -ne 0) { throw ('Demo failed: '+$adapterOut.Result+' '+$adapterError.Result) }
    $demo=$adapterOut.Result | ConvertFrom-Json
    if($demo.status -ne 'ok' -or -not $demo.ownedChildStopped -or -not $demo.finalReleaseSent -or
       -not $demo.exampleBaseObserved -or -not $demo.exampleLiftedObserved -or -not $demo.exampleReturnedToBaseObserved) {
        throw ('Real Kanata layer lifecycle was not proven: '+$adapterOut.Result)
    }
    $result=[ordered]@{status='ok';KanataVersion='1.12.0';OfficialBinarySha256=$ExpectedKanataSha256.ToUpperInvariant();
        SyntheticControlEvents=$true;ObservedBase=$demo.exampleBaseObserved;ObservedLifted=$demo.exampleLiftedObserved;
        ObservedReturnToBase=$demo.exampleReturnedToBaseObserved;OwnedConsumerStopped=$demo.ownedChildStopped;
        CleanupReleaseSent=$demo.finalReleaseSent;KeyActionAcknowledgements=$false;BluetoothCaptureStarted=$false;
        Installed=$false;SettingsChanged=$false;OwnedKanataStopped=$false}
}
finally {
    foreach($owned in @($adapterProcess,$kanataProcess)) {
        if($null -ne $owned) {
            if(-not $owned.HasExited) { $owned.Kill();if(-not $owned.WaitForExit(3000)) { throw 'Owned test process did not stop; cleanup is unconfirmed.' } }
        }
    }
    if($null -ne $result -and $null -ne $kanataProcess) { $result.OwnedKanataStopped=$kanataProcess.HasExited }
    foreach($owned in @($adapterProcess,$kanataProcess)) { if($null -ne $owned) { $owned.Dispose() } }
}
if($null -ne $result -and $result.OwnedKanataStopped) {
    $json=$result | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($reportPath,$json,[Text.UTF8Encoding]::new($false))
    $json
}
