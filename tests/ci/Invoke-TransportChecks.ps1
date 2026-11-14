# Copyright (c) 2026 InputLift contributors
# SPDX-License-Identifier: MIT
# Disposable GitHub-hosted Windows VM only. Never run this account-management
# harness on a workstation or self-hosted runner. It does not change application guards.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or
   $env:RUNNER_OS -cne 'Windows' -or -not $IsWindows -or [IntPtr]::Size -ne 8) {
    throw 'This harness requires a disposable GitHub-hosted Windows x64 Actions runner. No account was created.'
}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$principal=[Security.Principal.WindowsPrincipal]::new($identity)
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The hosted-runner harness requires its administrator token to create a temporary standard account.'
}
function Assert-NoReparse([string]$Path) {
    $cursor=[IO.Path]::GetFullPath($Path)
    while($cursor) {
        if(Test-Path -LiteralPath $cursor) {
            if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'CI paths must not contain reparse points.'
            }
        }
        $parent=[IO.Directory]::GetParent($cursor)
        if($null -eq $parent) { break }
        $cursor=$parent.FullName
    }
}
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if([string]::IsNullOrWhiteSpace($env:GITHUB_WORKSPACE) -or
   [IO.Path]::GetFullPath($env:GITHUB_WORKSPACE).TrimEnd('\') -ine $repoRoot.TrimEnd('\') -or
   [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    throw 'The script must run from the checked-out GitHub workspace with an existing runner temporary directory.'
}
$runnerTemp=[IO.Path]::GetFullPath($env:RUNNER_TEMP)
if(-not (Test-Path -LiteralPath $runnerTemp -PathType Container)) { throw 'Runner temporary directory is missing.' }
Assert-NoReparse $repoRoot
Assert-NoReparse $runnerTemp
$builtFolder=Join-Path $repoRoot 'artifacts\kanata'
$payloadSources=@{
    'InputLift.Kanata.exe'=(Join-Path $builtFolder 'InputLift.Kanata.exe')
    'InputLift.Core.dll'=(Join-Path $builtFolder 'InputLift.Core.dll')
    'InputLift.NativePipeTests.exe'=(Join-Path $repoRoot 'artifacts\native-pipe\InputLift.NativePipeTests.exe')
}
$payloadNames=@($payloadSources.Keys | Sort-Object)
foreach($name in $payloadNames) {
    $path=$payloadSources[$name]
    Assert-NoReparse $path
    if(-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Build the optional Kanata adapter first: missing $name." }
}
$runId=[Guid]::NewGuid().ToString('N')
$accountName='ilci'+$runId.Substring(0,12)
$accountMarker='InputLift CI '+$runId
$stage=Join-Path $runnerTemp ('inputlift-transport-'+$runId)
$reports=Join-Path $repoRoot ('artifacts\ci\transport-'+$runId)
$stdout=Join-Path $reports 'stdout.json'
$stderr=Join-Path $reports 'stderr.txt'
$ownedUser=$null
$ownedProcess=$null
$securePassword=$null
$credential=$null
$testReport=$null
$failure=$null
$cleanupErrors=[Collections.Generic.List[string]]::new()
$stageCreated=$false
$startedUtc=[DateTime]::UtcNow
try {
    Import-Module Microsoft.PowerShell.LocalAccounts -ErrorAction Stop
    if(Get-LocalUser -Name $accountName -ErrorAction SilentlyContinue) { throw 'Random CI account name already exists; no existing account will be reused.' }
    $randomBytes=[byte[]]::new(48)
    $rng=[Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($randomBytes) } finally { $rng.Dispose() }
    $passwordText='Il9!'+[Convert]::ToBase64String($randomBytes)
    $securePassword=ConvertTo-SecureString -String $passwordText -AsPlainText -Force
    $passwordText=$null
    [Array]::Clear($randomBytes,0,$randomBytes.Length)
    $ownedUser=New-LocalUser -Name $accountName -Password $securePassword -Description $accountMarker `
        -AccountExpires ([DateTime]::Now.AddMinutes(30)) -UserMayNotChangePassword
    $standardGroup=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
    $adminGroup=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    if(-not (@(Get-LocalGroupMember -SID $standardGroup | Where-Object { $_.SID.Value -eq $ownedUser.SID.Value }).Count)) {
        Add-LocalGroupMember -SID $standardGroup -Member $ownedUser
    }
    if(@(Get-LocalGroupMember -SID $adminGroup | Where-Object { $_.SID.Value -eq $ownedUser.SID.Value }).Count) {
        throw 'Temporary account unexpectedly belongs to Administrators; tests will not run.'
    }
    if(Test-Path -LiteralPath $stage) { throw 'Random CI staging path already exists; it will not be reused.' }
    $null=New-Item -ItemType Directory -Path $stage
    $stageCreated=$true
    $acl=[Security.AccessControl.DirectorySecurity]::new()
    $acl.SetSecurityDescriptorSddlForm('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FRFX;;;'+$ownedUser.SID.Value+')')
    Set-Acl -LiteralPath $stage -AclObject $acl
    foreach($name in $payloadNames) {
        $source=$payloadSources[$name]
        $destination=Join-Path $stage $name
        Copy-Item -LiteralPath $source -Destination $destination
        if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -cne
           (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash) { throw 'Staged executable/library hash differs from the reviewed build.' }
    }
    Assert-NoReparse $reports
    $null=New-Item -ItemType Directory -Path $reports -Force
    $credential=[Management.Automation.PSCredential]::new(($env:COMPUTERNAME+'\'+$accountName),$securePassword)
    # No profile is loaded. These tests use only framework code, inherited pipes
    # and owned loopback sockets; the standard account has read/execute access
    # to the hash-verified files, never write access to repository binaries.
    $ownedProcess=Start-Process -FilePath (Join-Path $stage 'InputLift.Kanata.exe') `
        -ArgumentList '--test-transport' -Credential $credential -WorkingDirectory $stage `
        -LoadUserProfile:$false -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if(-not $ownedProcess.WaitForExit(90000)) { throw 'Standard-user transport checks exceeded 90 seconds.' }
    $ownedProcess.Refresh()
    if($ownedProcess.ExitCode -ne 0) {
        $detail=if(Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout -Raw } else { '(no output)' }
        throw "Standard-user transport checks failed with exit $($ownedProcess.ExitCode): $detail"
    }
    # Start-Process redirects output asynchronously. Bound report-drain time;
    # this never reruns a test or substitutes a partial success count.
    $drain=[Diagnostics.Stopwatch]::StartNew()
    while($null -eq $testReport -and $drain.ElapsedMilliseconds -lt 3000) {
        try {
            if(Test-Path -LiteralPath $stdout) { $testReport=Get-Content -LiteralPath $stdout -Raw | ConvertFrom-Json -ErrorAction Stop }
        } catch { if($drain.ElapsedMilliseconds -ge 2950) { throw } }
        if($null -eq $testReport) { Start-Sleep -Milliseconds 50 }
    }
    if($null -eq $testReport -or $testReport.status -cne 'ok' -or $testReport.tests -ne 11 -or
       $testReport.mode -cne 'explicit-owned-loopback-stub' -or @($testReport.errors).Count -ne 0 -or
       $testReport.socketsOpened -isnot [bool] -or -not $testReport.socketsOpened -or
       $testReport.realApplicationConnected -isnot [bool] -or $testReport.realApplicationConnected -or
       $testReport.captureStarted -isnot [bool] -or $testReport.captureStarted -or
       $testReport.nativeInputSent -isnot [bool] -or $testReport.nativeInputSent) {
        throw 'Transport report did not prove all eleven owned-stub checks passed without real application/capture/native input.'
    }
    if((Test-Path -LiteralPath $stderr) -and -not [string]::IsNullOrWhiteSpace((Get-Content -LiteralPath $stderr -Raw))) {
        throw 'Standard-user transport checks wrote unexpected stderr; inspect ignored artifacts/ci output.'
    }
    & (Join-Path $repoRoot 'tests\native-pipe\Invoke-Cases.ps1') -ReaderCredential $credential `
        -ReaderSid $ownedUser.SID.Value -FixtureExecutable (Join-Path $stage 'InputLift.NativePipeTests.exe') `
        -ReportDirectory (Join-Path $reports 'native-pipe')
} catch { $failure=$_.Exception }
finally {
    if($null -ne $ownedProcess) {
        try {
            if(-not $ownedProcess.HasExited) {
                $ownedProcess.Kill($true)
                if(-not $ownedProcess.WaitForExit(5000)) { throw 'Owned test process tree did not stop.' }
            }
        } catch { $cleanupErrors.Add($_.Exception.Message) }
    }
    if($null -ne $ownedUser) {
        try {
            $current=Get-LocalUser -SID $ownedUser.SID
            if($current.Name -cne $accountName -or $current.Description -cne $accountMarker) { throw 'Temporary account ownership changed; removal refused.' }
            Disable-LocalUser -SID $ownedUser.SID
            # Account-scoped orphan cleanup is restricted further to this run's
            # exact staged image and start time. Open the process handle before
            # checking it so a later PID reuse cannot select another process.
            foreach($candidate in @(Get-CimInstance Win32_Process -Filter "Name='InputLift.Kanata.exe' OR Name='InputLift.NativePipeTests.exe'")) {
                $orphan=$null
                try {
                    $orphan=[Diagnostics.Process]::GetProcessById([int]$candidate.ProcessId)
                    $null=$orphan.Handle
                    if($orphan.HasExited) { continue }
                    $owner=Invoke-CimMethod -InputObject $candidate -MethodName GetOwnerSid
                    if($owner.ReturnValue -eq 0 -and $owner.Sid -eq $ownedUser.SID.Value -and
                       $orphan.StartTime.ToUniversalTime() -ge $startedUtc -and
                       [IO.Path]::GetFullPath($orphan.MainModule.FileName) -ieq (Join-Path $stage $candidate.Name)) {
                        $orphan.Kill()
                        if(-not $orphan.WaitForExit(5000)) { throw 'Owned standard-user test orphan did not stop.' }
                    }
                } catch [ArgumentException] { } # Already exited between enumeration and handle acquisition.
                finally { if($null -ne $orphan) { $orphan.Dispose() } }
            }
            Remove-LocalUser -SID $ownedUser.SID
            if(Get-LocalUser -SID $ownedUser.SID -ErrorAction SilentlyContinue) { throw 'Temporary CI account removal was not confirmed.' }
        } catch { $cleanupErrors.Add($_.Exception.Message) }
    }
    if($stageCreated) {
        try {
            $resolved=[IO.Path]::GetFullPath($stage)
            $expected=[IO.Path]::GetFullPath((Join-Path $runnerTemp ('inputlift-transport-'+$runId)))
            if($resolved -cne $expected -or -not $resolved.StartsWith($runnerTemp.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) {
                throw 'Staging cleanup target escaped this run beneath RUNNER_TEMP; removal refused.'
            }
            Assert-NoReparse $resolved
            Remove-Item -LiteralPath $resolved -Recurse -Force
        } catch { $cleanupErrors.Add($_.Exception.Message) }
    }
    if($null -ne $ownedProcess) { $ownedProcess.Dispose() }
    $credential=$null
    if($null -ne $securePassword) { $securePassword.Dispose() }
}
if($cleanupErrors.Count -ne 0) { throw ('Disposable CI cleanup failed: '+($cleanupErrors -join '; ')) }
if($null -ne $failure) { throw $failure }
$testReport | ConvertTo-Json -Depth 6 -Compress
[pscustomobject]@{Harness='github-hosted-standard-user';TemporaryAccountRemoved=$true;Checks=8;RealApplicationConnected=$false;CaptureStarted=$false;NativeInputSent=$false} | ConvertTo-Json -Compress
