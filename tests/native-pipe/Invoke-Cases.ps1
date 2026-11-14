# Copyright (c) 2026 InputLift contributors
# SPDX-License-Identifier: MIT
# Uses a temporary account already owned by the disposable hosted CI harness.
# No account/settings/installation changes. Never execute on a workstation.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][Management.Automation.PSCredential]$ReaderCredential,
    [Parameter(Mandatory)][string]$ReaderSid,
    [Parameter(Mandatory)][string]$FixtureExecutable,
    [Parameter(Mandatory)][string]$ReportDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or
   $env:RUNNER_OS -cne 'Windows' -or -not $IsWindows -or [IntPtr]::Size -ne 8) {
    throw 'Native pipe cases require the disposable GitHub-hosted Windows x64 harness.'
}
$principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Only the hosted-runner administrator may orchestrate the native pipe cases.' }
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if([string]::IsNullOrWhiteSpace($env:GITHUB_WORKSPACE) -or [IO.Path]::GetFullPath($env:GITHUB_WORKSPACE).TrimEnd('\') -ine $repoRoot.TrimEnd('\')) {
    throw 'Native pipe test checkout differs from GITHUB_WORKSPACE.'
}
function Assert-NoReparse([string]$Path) {
    $cursor=[IO.Path]::GetFullPath($Path)
    while($cursor) {
        if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Native pipe test paths must not contain reparse points.' }
        $parent=[IO.Directory]::GetParent($cursor)
        if($null -eq $parent) { break }
        $cursor=$parent.FullName
    }
}
$fixture=(Resolve-Path -LiteralPath $FixtureExecutable).Path
$reports=[IO.Path]::GetFullPath($ReportDirectory)
$ciRoot=[IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\ci')).TrimEnd('\')+'\'
$runnerTemp=[IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\')+'\'
if([IO.Path]::GetFileName($fixture) -cne 'InputLift.NativePipeTests.exe' -or
   -not $fixture.StartsWith($runnerTemp,[StringComparison]::OrdinalIgnoreCase) -or
   -not $reports.StartsWith($ciRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Native fixture must be staged under RUNNER_TEMP and report only beneath artifacts/ci.' }
foreach($path in @($fixture,$reports)) { Assert-NoReparse $path }
$buildPath=Join-Path $repoRoot 'artifacts\native-pipe\build-result.json'
Assert-NoReparse $buildPath
$build=Get-Content -LiteralPath $buildPath -Raw | ConvertFrom-Json
if($build.status -cne 'compiled' -or $build.NativeFixtureStarted -or
   (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash -cne $build.FixtureSha256 -or
   (Get-FileHash -LiteralPath (Join-Path $repoRoot 'src\FnEventPipe.cs') -Algorithm SHA256).Hash -cne $build.ProductionPipeSourceSha256) {
    throw 'Native fixture does not match this successful compile and current production pipe source.'
}
$selected=Get-LocalUser -SID ([Security.Principal.SecurityIdentifier]::new($ReaderSid))
if($ReaderCredential.UserName -ine ($env:COMPUTERNAME+'\'+$selected.Name)) { throw 'Credential name differs from the selected owned temporary account.' }
if(@(Get-LocalGroupMember -SID ([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')) | Where-Object { $_.SID.Value -eq $ReaderSid }).Count -ne 0) { throw 'Selected native reader must not belong to Administrators.' }
$null=New-Item -ItemType Directory -Path $reports -Force
$results=[Collections.Generic.List[object]]::new()
foreach($scenario in @('ordered','denied-write','idle-dispose','unarmed-down','blocked-reader')) {
    $nonce=[Guid]::NewGuid().ToString('N')
    $server=$null;$client=$null;$serverOut=$null;$serverErr=$null;$stdout=$null;$stderr=$null
    try {
        $start=[Diagnostics.ProcessStartInfo]::new()
        $start.FileName=$fixture;$start.WorkingDirectory=[IO.Path]::GetDirectoryName($fixture)
        $start.UseShellExecute=$false;$start.CreateNoWindow=$true
        $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
        foreach($argument in @('--server','--reader-sid',$ReaderSid,'--nonce',$nonce,'--scenario',$scenario)) { $start.ArgumentList.Add($argument) }
        $server=[Diagnostics.Process]::new();$server.StartInfo=$start
        if(-not $server.Start()) { throw 'Owned native pipe server did not start.' }
        $server.StandardInput.AutoFlush=$true
        $readyTask=$server.StandardOutput.ReadLineAsync()
        $serverErr=$server.StandardError.ReadToEndAsync()
        if(-not $readyTask.Wait(5000)) { throw 'Native server ready line exceeded five seconds.' }
        $ready=$readyTask.Result | ConvertFrom-Json
        if($null -eq $ready -or $ready.phase -cne 'ready' -or $ready.scenario -cne $scenario -or
           $ready.pid -ne $server.Id -or $ready.startUtcTicks -ne $server.StartTime.ToUniversalTime().Ticks -or
           $ready.pipe -cne ('InputLift.Fn.'+$nonce)) { throw ('Native server did not produce the expected ready proof: '+$readyTask.Result) }
        $serverOut=$server.StandardOutput.ReadToEndAsync()
        $clientReport=$null
        if($scenario -ne 'idle-dispose') {
            $stdout=Join-Path $reports ($scenario+'-client.json')
            $stderr=Join-Path $reports ($scenario+'-client-stderr.txt')
            if((Test-Path -LiteralPath $stdout) -or (Test-Path -LiteralPath $stderr)) { throw 'Native case refuses existing result paths; inspect before any retry.' }
            # All arguments are fixed vocabulary, canonical SIDs, hex nonce and positive numbers.
            $arguments=@('--client','--reader-sid',$ReaderSid,'--nonce',$nonce,'--scenario',$scenario,
                '--server-pid',[string]$ready.pid,'--server-start',[string]$ready.startUtcTicks)
            $client=Start-Process -FilePath $fixture -ArgumentList $arguments -Credential $ReaderCredential `
                -WorkingDirectory ([IO.Path]::GetDirectoryName($fixture)) -WindowStyle Hidden -LoadUserProfile:$false `
                -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
            if($scenario -ne 'denied-write') {
                $connected=$null;$connectWait=[Diagnostics.Stopwatch]::StartNew()
                while($null -eq $connected -and $connectWait.ElapsedMilliseconds -lt 5000) {
                    try {
                        if(Test-Path -LiteralPath $stdout) {
                            $first=Get-Content -LiteralPath $stdout -TotalCount 1
                            if(-not [string]::IsNullOrWhiteSpace($first)) { $connected=$first|ConvertFrom-Json -ErrorAction Stop }
                        }
                    } catch { if($connectWait.ElapsedMilliseconds -ge 4950) { throw } }
                    if($null -eq $connected) { Start-Sleep -Milliseconds 25 }
                }
                if($null -eq $connected -or $connected.phase -cne 'connected' -or $connected.scenario -cne $scenario -or
                   -not $connected.standardAccountConfirmed -or -not $connected.pipeServerPidVerified) {
                    throw ('Native client did not authenticate the still-open server: '+(Get-Content -LiteralPath $stdout -Raw))
                }
                $server.StandardInput.WriteLine('begin')
            }
            if(-not $client.WaitForExit(12000)) { throw 'Owned standard-user native client exceeded twelve seconds.' }
            $client.Refresh()
            $drain=[Diagnostics.Stopwatch]::StartNew()
            while($null -eq $clientReport -and $drain.ElapsedMilliseconds -lt 2000) {
                try {
                    $lines=@(Get-Content -LiteralPath $stdout | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
                    $expectedLines=if($scenario -eq 'denied-write') { 1 } else { 2 }
                    if($lines.Count -eq $expectedLines) { $clientReport=$lines[$lines.Count-1] | ConvertFrom-Json -ErrorAction Stop }
                }
                catch { if($drain.ElapsedMilliseconds -ge 1950) { throw } }
                if($null -eq $clientReport) { Start-Sleep -Milliseconds 50 }
            }
            if($client.ExitCode -ne 0 -or $null -eq $clientReport -or $clientReport.phase -cne 'result' -or
               $clientReport.status -cne 'ok' -or $clientReport.scenario -cne $scenario -or -not $clientReport.standardAccountConfirmed) {
                throw ('Native client failed: '+(Get-Content -LiteralPath $stdout -Raw))
            }
            if(-not [string]::IsNullOrWhiteSpace((Get-Content -LiteralPath $stderr -Raw))) { throw ('Native client stderr: '+(Get-Content -LiteralPath $stderr -Raw)) }
        }
        if(-not $server.HasExited) { $server.StandardInput.WriteLine('finish') }
        if(-not $server.WaitForExit(5000)) { throw 'Owned native server did not stop after finish.' }
        if(-not $serverOut.Wait(1000) -or -not $serverErr.Wait(1000)) { throw 'Native server output did not drain.' }
        $serverReport=$serverOut.Result | ConvertFrom-Json
        if($server.ExitCode -ne 0 -or $null -eq $serverReport -or $serverReport.phase -cne 'result' -or
           $serverReport.status -cne 'ok' -or $serverReport.scenario -cne $scenario -or
           -not $serverReport.actualKernelAclVerified -or -not $serverReport.duplicateInstanceDenied -or
           $serverReport.duplicateInstanceErrorCode -notin @(5,231) -or -not $serverReport.firstInstanceFlagPolicyPresent -or
           $serverReport.firstInstanceFlagBehaviorValidated -or
           -not $serverReport.nativeHandleClosed -or -not $serverReport.disposalBounded) { throw ('Native server failed: '+$serverOut.Result+' '+$serverErr.Result) }
        if(-not [string]::IsNullOrWhiteSpace($serverErr.Result)) { throw ('Native server stderr: '+$serverErr.Result) }
        foreach($report in @($serverReport,$clientReport)) {
            if($null -ne $report -and ($report.bluetoothCaptureStarted -or $report.nativeInputSent -or
               $report.installedDeploymentValidated -or $report.productionOwnerBindingValidated -or
               $report.firstInstanceFlagBehaviorValidated -or $report.remoteSmbBehaviorValidated)) {
                throw 'Native fixture reported a side effect or validation scope it must not perform/claim.'
            }
        }
        switch($scenario) {
            'ordered' { if(-not $clientReport.orderedFramesReachedActualConsumer -or -not $serverReport.disconnectedConsumerStopped) { throw 'Ordered native/consumer/disconnect proof missing.' } }
            'denied-write' { if(-not $clientReport.writeConnectionDenied -or $clientReport.selectedReaderConnected) { throw 'Native write-denial proof missing.' } }
            'unarmed-down' { if(-not $clientReport.unarmedDownRejected) { throw 'Native unarmed-down rejection proof missing.' } }
            'blocked-reader' { if(-not $serverReport.blockedReaderStopped) { throw 'Native blocked-reader stop proof missing.' } }
        }
        [IO.File]::WriteAllText((Join-Path $reports ($scenario+'-server.json')),($serverReport|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
        $results.Add([pscustomobject]@{Scenario=$scenario;Server=$serverReport;Client=$clientReport})
    }
    catch {
        $detail=$_.Exception.Message
        if($null -ne $serverOut -and $serverOut.IsCompleted) {
            try { if(-not [string]::IsNullOrWhiteSpace($serverOut.Result)) { $detail+=' Server result: '+$serverOut.Result } } catch { }
        }
        if($null -ne $serverErr -and $serverErr.IsCompleted) {
            try { if(-not [string]::IsNullOrWhiteSpace($serverErr.Result)) { $detail+=' Server stderr: '+$serverErr.Result } } catch { }
        }
        throw ('Native pipe case '+$scenario+' failed: '+$detail)
    }
    finally {
        if($null -ne $server) { try { $server.StandardInput.Close() } catch { } }
        foreach($owned in @($client,$server)) {
            if($null -ne $owned) {
                try { if(-not $owned.HasExited) { $owned.Kill();if(-not $owned.WaitForExit(3000)) { throw 'An owned native fixture process did not stop.' } } }
                finally { $owned.Dispose() }
            }
        }
    }
}
$summary=[ordered]@{status='ok';mode='hosted-native-pipe';scenarios=$results.Count;results=$results.ToArray();
    BluetoothCaptureStarted=$false;NativeInputSent=$false;InstalledDeploymentValidated=$false;
    ProductionOwnerBindingValidated=$false;FirstInstanceFlagBehaviorValidated=$false;RemoteSmbBehaviorValidated=$false}
$json=$summary|ConvertTo-Json -Depth 8
[IO.File]::WriteAllText((Join-Path $reports 'native-pipe-results.json'),$json,[Text.UTF8Encoding]::new($false))
$json
