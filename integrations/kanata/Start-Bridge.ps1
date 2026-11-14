# Copyright (c) 2026 InputLift contributors
# SPDX-License-Identifier: MIT
# Default: read-only protected-state inspection. -Connect explicitly starts the
# already-built normal-user companion. No setup, capture, elevation or downloads.
[CmdletBinding()]
param(
    [switch]$Connect,
    [ValidateRange(1024,65535)][int]$Port=7070,
    [ValidatePattern('\A[A-Za-z0-9._-]{1,64}\z')][string]$VirtualKey='inputlift-fn'
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

function Get-BridgeField([object]$Object,[string]$Name) {
    if($null -eq $Object -or $Object -isnot [pscustomobject]) { throw 'BridgeMetadataType: expected a JSON object.' }
    $property=$Object.PSObject.Properties[$Name]
    if($null -eq $property) { throw ('BridgeMetadataField: missing '+$Name+'.') }
    return $property.Value
}
function Assert-BridgeInteger([object]$Value,[long]$Maximum=[long]::MaxValue) {
    if(($Value -isnot [int] -and $Value -isnot [long]) -or $Value -le 0 -or $Value -gt $Maximum) {
        throw 'BridgeMetadataInteger: expected a positive bounded integer.'
    }
}
function Assert-BridgeTextEquals([object]$Value,[string]$Expected) {
    if($Value -isnot [string] -or $Value -cne $Expected) { throw 'BridgeMetadataText: expected an exact text field.' }
}
function Assert-BridgeNonce([object]$Value) {
    if($Value -isnot [string] -or $Value -cnotmatch '\A[0-9a-f]{32}\z') { throw 'BridgeMetadataNonce: expected a canonical session identifier.' }
}
function Assert-BridgeActive([object]$Active) {
    $version=Get-BridgeField $Active 'Version'
    if(($version -isnot [int] -and $version -isnot [long]) -or $version -ne 1) { throw 'BridgeActiveVersion: unsupported state version.' }
    Assert-BridgeNonce (Get-BridgeField $Active 'RunNonce')
    foreach($prefix in @('Tray','Guard','Worker','Guardian')) {
        Assert-BridgeInteger (Get-BridgeField $Active ($prefix+'Pid')) ([int]::MaxValue)
        Assert-BridgeInteger (Get-BridgeField $Active ($prefix+'StartUtcTicks')) ([DateTime]::MaxValue.Ticks)
    }
    Assert-BridgeInteger (Get-BridgeField $Active 'BootUtcTicks') ([DateTime]::MaxValue.Ticks)
}
function Assert-BridgeSameEpoch([object]$First,[object]$Second) {
    Assert-BridgeActive $First
    Assert-BridgeActive $Second
    foreach($field in @('Version','RunNonce','TrayPid','TrayStartUtcTicks','GuardPid','GuardStartUtcTicks',
                        'WorkerPid','WorkerStartUtcTicks','GuardianPid','GuardianStartUtcTicks','BootUtcTicks')) {
        if((Get-BridgeField $First $field) -cne (Get-BridgeField $Second $field)) {
            throw 'BridgeEpochChanged: source session changed during inspection; no connection started.'
        }
    }
}
function Get-BridgeStatusPath([string]$StateDirectory,[object]$Nonce) {
    Assert-BridgeNonce $Nonce
    if([string]::IsNullOrWhiteSpace($StateDirectory) -or -not [IO.Path]::IsPathFullyQualified($StateDirectory)) {
        throw 'BridgeStatePath: fixed absolute state directory required.'
    }
    $root=[IO.Path]::GetFullPath($StateDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $path=[IO.Path]::GetFullPath((Join-Path $root ('daily-'+$Nonce+'.status.json')))
    if(-not [IO.Path]::GetDirectoryName($path).Equals($root,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'BridgeStatePath: status must remain directly beneath the fixed state directory.'
    }
    return $path
}
function Assert-BridgeAclPolicy([object]$Facts,[string]$CurrentSid) {
    if([string]::IsNullOrWhiteSpace($CurrentSid) -or $CurrentSid -in @('S-1-5-18','S-1-5-32-544')) { throw 'BridgeOwnerIdentity: normal user required.' }
    $admin='S-1-5-32-544';$system='S-1-5-18'
    if($Facts.Owner -isnot [string] -or $Facts.Owner -cnotin @($admin,$system) -or $Facts.Protected -isnot [bool] -or -not $Facts.Protected) {
        throw 'BridgeStateAcl: state must have an administrator/SYSTEM owner and protected permissions.'
    }
    $ownerRead=$false;$adminFull=$false;$systemFull=$false
    $writes=[Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
    foreach($rule in @($Facts.Rules)) {
        if($rule.Sid -isnot [string] -or $rule.Sid -cnotin @($CurrentSid,$admin,$system) -or
           $rule.Type -isnot [string] -or $rule.Type -cne 'Allow' -or
           $rule.Rights -isnot [Security.AccessControl.FileSystemRights]) { throw 'BridgeStateAcl: unexpected state access rule.' }
        if($rule.Sid -ceq $CurrentSid) {
            if(($rule.Rights -band $writes) -ne 0 -or ($rule.Rights -band [Security.AccessControl.FileSystemRights]::ReadData) -eq 0) {
                throw 'BridgeStateAcl: selected owner must have read-only access.'
            }
            $ownerRead=$true
        } else {
            if(($rule.Rights -band [Security.AccessControl.FileSystemRights]::FullControl) -ne [Security.AccessControl.FileSystemRights]::FullControl) {
                throw 'BridgeStateAcl: administrators and SYSTEM must retain full control.'
            }
            if($rule.Sid -ceq $admin) { $adminFull=$true } else { $systemFull=$true }
        }
    }
    if(-not $ownerRead -or -not $adminFull -or -not $systemFull) { throw 'BridgeStateAcl: required access rules are missing.' }
}
function Assert-BridgeSnapshot([object]$Manifest,[object]$Active,[object]$Status,[string]$CurrentSid,[DateTimeOffset]$NowUtc) {
    $version=Get-BridgeField $Manifest 'Version'
    if(($version -isnot [int] -and $version -isnot [long]) -or $version -ne 1) { throw 'BridgeInstallIdentity: current-account installation required.' }
    Assert-BridgeTextEquals (Get-BridgeField $Manifest 'OwnerSid') $CurrentSid
    Assert-BridgeTextEquals (Get-BridgeField $Manifest 'TaskName') ('InputLift-'+$CurrentSid)
    Assert-BridgeTextEquals (Get-BridgeField $Manifest 'OutputMode') 'events-only'
    Assert-BridgeActive $Active
    Assert-BridgeTextEquals (Get-BridgeField $Status 'Nonce') $Active.RunNonce
    Assert-BridgeTextEquals (Get-BridgeField $Status 'OutputMode') 'events-only'
    Assert-BridgeTextEquals (Get-BridgeField $Status 'PipeName') ('InputLift.Fn.'+$Active.RunNonce)
    foreach($field in @('WorkerPid','WorkerStartUtcTicks')) {
        $maximum=if($field -eq 'WorkerPid'){[int]::MaxValue}else{[DateTime]::MaxValue.Ticks}
        Assert-BridgeInteger (Get-BridgeField $Status $field) $maximum
        if((Get-BridgeField $Status $field) -ne (Get-BridgeField $Active $field)) { throw 'BridgeWorkerIdentity: status worker differs from the current session.' }
    }
    Assert-BridgeInteger (Get-BridgeField $Status 'ProgressSequence')
    Assert-BridgeTextEquals (Get-BridgeField $Status 'State') 'AwaitConsumer'
    if($null -ne (Get-BridgeField $Status 'Error')) {
        throw 'BridgeSourceState: source must be healthy and waiting for its first consumer.'
    }
    foreach($field in @('KeyboardBound','NeutralConfirmed')) {
        if((Get-BridgeField $Status $field) -isnot [bool]) { throw 'BridgeStatusType: readiness flags must be booleans.' }
    }
    $updated=Get-BridgeField $Status 'UpdatedUtc'
    $parsed=[DateTimeOffset]::MinValue
    # ConvertFrom-Json may deserialize an ISO-Z value as DateTime. Accept only
    # its explicit UTC kind; never reinterpret Local/Unspecified as UTC. Other
    # PowerShell 7 representations retain the strict zero-offset string path.
    if($updated -is [DateTime]) {
        if($updated.Kind -ne [DateTimeKind]::Utc) { throw 'BridgeStatusTime: expected a UTC round-trip timestamp.' }
        $parsed=[DateTimeOffset]::new($updated)
    } elseif($updated -is [string]) {
        if(-not [DateTimeOffset]::TryParseExact($updated,'o',[Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::None,[ref]$parsed) -or $parsed.Offset -ne [TimeSpan]::Zero) {
            throw 'BridgeStatusTime: expected a UTC round-trip timestamp.'
        }
    } else {
        throw 'BridgeStatusTime: expected a UTC round-trip timestamp.'
    }
    $age=($NowUtc.ToUniversalTime()-$parsed).TotalMilliseconds
    if($age -lt -1000 -or $age -gt 5000) { throw 'BridgeStatusStale: source status is not fresh; no automatic retry.' }
}
function Assert-BridgeNoReparse([string]$Path) {
    $cursor=[IO.Path]::GetFullPath($Path)
    while($cursor) {
        if(Test-Path -LiteralPath $cursor) {
            if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'BridgeReparsePath: reparse points are refused.' }
        }
        $parent=[IO.Directory]::GetParent($cursor)
        if($null -eq $parent) { break };$cursor=$parent.FullName
    }
}
function Assert-BridgeProtectedPath([string]$Path,[string]$CurrentSid,[switch]$Directory) {
    Assert-BridgeNoReparse $Path
    $kind=if($Directory){'Container'}else{'Leaf'}
    if(-not(Test-Path -LiteralPath $Path -PathType $kind)) { throw 'BridgeStateMissing: protected state is not ready.' }
    $acl=Get-Acl -LiteralPath $Path
    $facts=[pscustomobject]@{Owner=$acl.GetOwner([Security.Principal.SecurityIdentifier]).Value;Protected=$acl.AreAccessRulesProtected;
        Rules=@($acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]) | ForEach-Object {
            [pscustomobject]@{Sid=$_.IdentityReference.Value;Type=$_.AccessControlType.ToString();Rights=$_.FileSystemRights}
        })}
    Assert-BridgeAclPolicy $facts $CurrentSid
}
function Read-BridgeProtectedJson([string]$Path,[string]$CurrentSid) {
    try {
        Assert-BridgeProtectedPath $Path $CurrentSid
        $stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        try {
            $buffer=[byte[]]::new(65537);$count=0
            while($count -lt $buffer.Length) { $read=$stream.Read($buffer,$count,$buffer.Length-$count);if($read -eq 0){break};$count+=$read }
            if($count -gt 65536) { throw 'BridgeStateSize: protected JSON exceeds 64 KiB.' }
            $text=[Text.UTF8Encoding]::new($false,$true).GetString($buffer,0,$count)
            $text=$text.TrimStart([char]0xFEFF)
            return ($text | ConvertFrom-Json -Depth 12 -ErrorAction Stop)
        } finally { $stream.Dispose() }
    } catch {
        # Do not echo metadata, unique identities, or paths from native exceptions.
        if($_.Exception.Message.StartsWith('Bridge',[StringComparison]::Ordinal)) { throw }
        throw ('BridgeStateRead: '+$_.Exception.GetType().Name+'; protected metadata could not be read or parsed.')
    }
}

try {
    if($PSVersionTable.PSVersion.Major -lt 7 -or -not $IsWindows) { throw 'BridgePlatform: use PowerShell 7 on Windows.' }
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        if($null -eq $identity.User -or [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'BridgeElevation: use normal-user PowerShell, not administrator PowerShell.'
        }
        $currentSid=$identity.User.Value
    } finally { $identity.Dispose() }
    $stateDirectory=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'InputLift'
    Assert-BridgeProtectedPath $stateDirectory $currentSid -Directory
    $manifest=Read-BridgeProtectedJson (Join-Path $stateDirectory 'install-manifest.json') $currentSid
    $activePath=Join-Path $stateDirectory 'active.json'
    $active=Read-BridgeProtectedJson $activePath $currentSid
    Assert-BridgeActive $active
    $status=Read-BridgeProtectedJson (Get-BridgeStatusPath $stateDirectory $active.RunNonce) $currentSid
    Assert-BridgeSnapshot $manifest $active $status $currentSid ([DateTimeOffset]::UtcNow)
    $activeAgain=Read-BridgeProtectedJson $activePath $currentSid
    Assert-BridgeSameEpoch $active $activeAgain
    if(-not $Connect) {
        [pscustomobject]@{status='metadata-ready';mode='inspect';metadataValidated=$true;serverAuthenticated=$false;outputMode='events-only';sourceState='AwaitConsumer';
            keyboardBound=$status.KeyboardBound;neutralConfirmed=$status.NeutralConfirmed;connectionStarted=$false;
            captureStarted=$false;settingsChanged=$false;identifiersPrinted=$false}|ConvertTo-Json -Compress
        exit 0
    }
    $repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $companion=Join-Path $repoRoot 'artifacts\kanata\InputLift.Kanata.exe'
    Assert-BridgeNoReparse $companion
    if(-not(Test-Path -LiteralPath $companion -PathType Leaf)) { throw 'BridgeBuildMissing: build the optional companion first; no automatic build.' }
    # Recheck the same epoch after file checks. Native pipe PID/start/image
    # authentication remains in the companion; these snapshots do not replace it.
    Assert-BridgeSameEpoch $active (Read-BridgeProtectedJson $activePath $currentSid)
    Assert-BridgeSnapshot $manifest $active $status $currentSid ([DateTimeOffset]::UtcNow)
    & $companion --bridge --port $Port --virtual-key $VirtualKey --capture-pipe $status.PipeName `
        --capture-pid $status.WorkerPid --capture-start $status.WorkerStartUtcTicks
    exit $LASTEXITCODE
} catch {
    $message=$_.Exception.Message
    if(-not $message.StartsWith('Bridge',[StringComparison]::Ordinal)) { $message='BridgeLauncher: '+$_.Exception.GetType().Name+'; stopped before starting a connection.' }
    [pscustomobject]@{status='error';error=$message;automaticRetry=$false;captureStarted=$false;settingsChanged=$false;identifiersPrinted=$false}|ConvertTo-Json -Compress
    exit 1
}
