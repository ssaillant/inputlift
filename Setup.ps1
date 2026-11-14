# Copyright (c) 2026 InputLift contributors
# SPDX-License-Identifier: MIT
# Inspect is read-only. Install/Uninstall must be run explicitly as administrator.
[CmdletBinding()]
param(
    [ValidateSet('Inspect','Install','Uninstall')][string]$Mode='Inspect',
    [string]$PackageFolder=(Join-Path $PSScriptRoot 'artifacts\package'),
    [string]$KeyboardAddress,
    [string]$ExpectedManifestSha256,
    [switch]$EnablePersistentLogging,
    [switch]$CreateStartMenuShortcut,
    [switch]$Start
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$fixedOwnerSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$taskName='InputLift-'+$fixedOwnerSid
$taskPath='\'+$taskName
$appDir=[IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'InputLift'))
$dataDir=[IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'InputLift'))
$installManifest=Join-Path $dataDir 'install-manifest.json'
$shortcutPath=Join-Path ([Environment]::GetFolderPath('Programs')) 'InputLift.lnk'
$payloadNames=@('InputLift.exe','InputLiftGuard.exe','InputLiftCapture.exe')
$adminSid='S-1-5-32-544'
$systemSid='S-1-5-18'
$directorySddl='O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FRFX;;;'+$fixedOwnerSid+')'
$fileSddl='O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;'+$fixedOwnerSid+')'
$taskSddl='O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;'+$fixedOwnerSid+')'

function Write-Result([object]$Value) { $Value | ConvertTo-Json -Depth 10 -Compress }
function Get-Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
function Get-NormalizedKeyboardAddress([string]$Address) {
    if($Address -notmatch '\A[0-9A-Fa-f]{12}\z' -or $Address -eq '000000000000' -or $Address -eq 'FFFFFFFFFFFF') {
        throw 'KeyboardAddress must be exactly 12 hexadecimal digits without separators, excluding zero and broadcast addresses.'
    }
    return $Address.ToUpperInvariant()
}
function Assert-Identity {
    if([IntPtr]::Size -ne 8) { throw 'Use 64-bit PowerShell.' }
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    if($identity.User.Value -ne $fixedOwnerSid) { throw 'The current account changed during setup; no other account may be targeted.' }
    $principal=[Security.Principal.WindowsPrincipal]::new($identity)
    if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Install/Uninstall requires an administrator PowerShell window. This script does not elevate itself.' }
}
function Assert-NoReparse([string]$Path) {
    $current=[IO.Path]::GetFullPath($Path)
    while($current) {
        if(Test-Path -LiteralPath $current) {
            $item=Get-Item -LiteralPath $current -Force
            if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse-point path refused: $current" }
        }
        $parent=[IO.Directory]::GetParent($current)
        if($null -eq $parent) { break }
        $current=$parent.FullName
    }
}
function Assert-ExpectedPath([string]$Path,[string]$Root,[switch]$RootItself) {
    $resolved=[IO.Path]::GetFullPath($Path)
    $resolvedRoot=[IO.Path]::GetFullPath($Root).TrimEnd('\')
    $okay=if($RootItself) { $resolved.TrimEnd('\').Equals($resolvedRoot,[StringComparison]::OrdinalIgnoreCase) }
          else { $resolved.StartsWith($resolvedRoot+'\',[StringComparison]::OrdinalIgnoreCase) }
    if(-not $okay) { throw "Path escaped the explicitly owned directory: $resolved" }
    Assert-NoReparse $resolved
    return $resolved
}
function Get-Sid([string]$Identity) {
    if($Identity -match '^S-1-') { return ([Security.Principal.SecurityIdentifier]::new($Identity)).Value }
    return ([Security.Principal.NTAccount]::new($Identity)).Translate([Security.Principal.SecurityIdentifier]).Value
}
function Assert-ProtectedAcl([string]$Path,[switch]$Task,[object]$TaskObject) {
    if($Task) { $raw=[Security.AccessControl.RawSecurityDescriptor]::new([string]$TaskObject.GetSecurityDescriptor(7)) }
    else {
        Assert-NoReparse $Path
        $acl=Get-Acl -LiteralPath $Path
        $raw=[Security.AccessControl.RawSecurityDescriptor]::new($acl.GetSecurityDescriptorSddlForm('All'))
    }
    if($raw.Owner.Value -ne $adminSid) { throw "Owner must be Administrators: $Path" }
    if(($raw.ControlFlags -band [Security.AccessControl.ControlFlags]::DiscretionaryAclProtected) -eq 0) { throw "ACL inheritance is not protected: $Path" }
    $seenUser=$false; $seenAdmin=$false; $seenSystem=$false
    foreach($ace in $raw.DiscretionaryAcl) {
        if($ace -isnot [Security.AccessControl.CommonAce] -or $ace.AceQualifier -ne [Security.AccessControl.AceQualifier]::AccessAllowed) { throw "Unexpected ACL entry: $Path" }
        $sid=$ace.SecurityIdentifier.Value
        if($sid -notin @($adminSid,$systemSid,$fixedOwnerSid)) { throw "Unexpected ACL principal on $Path" }
        $mask=([long]$ace.AccessMask -band 4294967295L)
        if($sid -eq $fixedOwnerSid) {
            # Generic write/all; file writes, delete, WriteDAC and WriteOwner.
            if(($mask -band 1343029590L) -ne 0) { throw "The unelevated owner account has write rights on $Path" }
            $seenUser=$true
        }
        else {
            if(($mask -band 268435456L) -eq 0 -and ($mask -band 2032127L) -ne 2032127L) { throw "Administrator/SYSTEM full control is missing on $Path" }
            if($sid -eq $adminSid) { $seenAdmin=$true } else { $seenSystem=$true }
        }
    }
    if(-not ($seenUser -and $seenAdmin -and $seenSystem)) { throw "Required ACL principals are missing: $Path" }
}
function Initialize-NativeDirectoryCreator {
    if('InputLiftSetup.NativeDirectory' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
namespace InputLiftSetup {
 public static class NativeDirectory {
  [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
  private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl,uint revision,out IntPtr descriptor,out uint bytes);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
  private static extern bool CreateDirectoryW(string path,ref SecurityAttributes attributes);
  [DllImport("kernel32.dll",ExactSpelling=true)] private static extern IntPtr LocalFree(IntPtr memory);
  public static void Create(string path,string sddl) {
   IntPtr descriptor; uint bytes;
   if(!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl,1,out descriptor,out bytes)) throw new Win32Exception(Marshal.GetLastWin32Error());
   try { var attributes=new SecurityAttributes { Length=Marshal.SizeOf(typeof(SecurityAttributes)),Descriptor=descriptor,Inherit=0 };
    if(!CreateDirectoryW(path,ref attributes)) throw new Win32Exception(Marshal.GetLastWin32Error(),"Create protected app directory failed; existing paths are never taken over.");
   } finally { LocalFree(descriptor); }
  }
 }
}
'@ -ErrorAction Stop
}
function New-ProtectedDirectory([string]$Path,[ref]$Created) {
    Assert-NoReparse $Path
    if(Test-Path -LiteralPath $Path) { throw "Create-only install refuses an existing directory: $Path" }
    Initialize-NativeDirectoryCreator
    [InputLiftSetup.NativeDirectory]::Create($Path,$directorySddl)
    $Created.Value=$true # Record ownership even if subsequent ACL verification fails.
    Assert-ProtectedAcl $Path
}
function Protect-File([string]$Path) {
    $acl=[Security.AccessControl.FileSecurity]::new()
    $acl.SetSecurityDescriptorSddlForm($fileSddl)
    Set-Acl -LiteralPath $Path -AclObject $acl
    Assert-ProtectedAcl $Path
}
function Write-ProtectedJson([string]$Path,[object]$Value) {
    $temporary=$Path+'.new'
    if(Test-Path -LiteralPath $temporary) { throw "Unexpected pending manifest file: $temporary" }
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 10))
    $stream=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    Protect-File $temporary
    # PowerShell casts ordinary $null to an empty string for this .NET string
    # argument. NullString preserves a real null (no backup file) for Replace.
    if(Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporary,$Path,[NullString]::Value) } else { [IO.File]::Move($temporary,$Path) }
    Assert-ProtectedAcl $Path
}
function Assert-PackageData([object]$Manifest) {
    if($manifest.Version -ne 1 -or @($manifest.Files).Count -ne $payloadNames.Count) { throw 'Unsupported package manifest version or payload count.' }
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($file in @($manifest.Files)) {
        $name=[string]$file.Name
        if($name -notin $payloadNames -or [IO.Path]::GetFileName($name) -cne $name -or -not $seen.Add($name)) { throw "Unexpected or duplicate package filename: $name" }
        if([string]$file.Sha256 -notmatch '^[0-9A-Fa-f]{64}$') { throw "Invalid SHA-256 for $name" }
    }
}
function Read-Package {
    $packageRoot=[IO.Path]::GetFullPath($PackageFolder)
    Assert-NoReparse $packageRoot
    if(-not (Test-Path -LiteralPath $packageRoot -PathType Container)) { throw 'PackageFolder is not an existing directory.' }
    $manifestPath=Join-Path $packageRoot 'package.json'
    Assert-NoReparse $manifestPath
    $hash=Get-Hash $manifestPath
    if($Mode -eq 'Install' -and ($ExpectedManifestSha256 -notmatch '^[0-9A-Fa-f]{64}$' -or $hash -ne $ExpectedManifestSha256.ToUpperInvariant())) { throw 'package.json SHA-256 is not the explicitly reviewed ExpectedManifestSha256.' }
    $manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    Assert-PackageData $manifest
    foreach($file in @($manifest.Files)) {
        $name=[string]$file.Name
        $source=Join-Path $packageRoot $name
        Assert-NoReparse $source
        if((Get-Hash $source) -ne ([string]$file.Sha256).ToUpperInvariant()) { throw "Package file hash differs: $name" }
    }
    return [pscustomobject]@{Root=$packageRoot;Hash=$hash;Manifest=$manifest}
}
function Copy-LockedPayload([string]$Source,[string]$Destination,[string]$ExpectedHash) {
    $sourceStream=[IO.FileStream]::new($Source,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        $algorithm=[Security.Cryptography.SHA256]::Create()
        try { $hash=[BitConverter]::ToString($algorithm.ComputeHash($sourceStream)).Replace('-','') } finally { $algorithm.Dispose() }
        if($hash -ne $ExpectedHash.ToUpperInvariant()) { throw "Source payload changed before locked copy: $Source" }
        $sourceStream.Position=0
        $destinationStream=[IO.FileStream]::new($Destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $sourceStream.CopyTo($destinationStream); $destinationStream.Flush($true) } finally { $destinationStream.Dispose() }
    } finally { $sourceStream.Dispose() }
    Protect-File $Destination
    if((Get-Hash $Destination) -ne $ExpectedHash.ToUpperInvariant()) { throw "Installed file hash differs: $Destination" }
}
function Connect-Scheduler { $service=New-Object -ComObject 'Schedule.Service'; $service.Connect(); return $service }
function Get-NormalizedTaskHash([string]$Xml) {
    $settings=[Xml.XmlReaderSettings]::new(); $settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit; $settings.XmlResolver=$null
    $text=[IO.StringReader]::new($Xml); $reader=[Xml.XmlReader]::Create($text,$settings)
    try { $document=[Xml.XmlDocument]::new(); $document.XmlResolver=$null; $document.PreserveWhitespace=$false; $document.Load($reader) }
    finally { $reader.Dispose(); $text.Dispose() }
    $enabled=$document.SelectSingleNode("/*[local-name()='Task']/*[local-name()='Settings']/*[local-name()='Enabled']")
    if($null -eq $enabled) {
        # Scheduler omits the default true value. Restore its persisted position
        # so enabled tasks still match the Version 1 hash recorded while disabled.
        $taskSettings=$document.SelectSingleNode("/*[local-name()='Task']/*[local-name()='Settings']")
        if($null -eq $taskSettings) { throw 'Task Settings node is missing.' }
        $executionLimit=$taskSettings.SelectSingleNode("*[local-name()='ExecutionTimeLimit']")
        if($null -eq $executionLimit) { throw 'Task Settings/ExecutionTimeLimit node is missing.' }
        $enabled=$document.CreateElement($taskSettings.Prefix,'Enabled',$taskSettings.NamespaceURI)
        [void]$taskSettings.InsertBefore($enabled,$executionLimit)
    }
    $enabled.InnerText='true' # The only legitimately mutable definition field.
    $algorithm=[Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash([Text.UTF8Encoding]::new($false).GetBytes($document.OuterXml))).Replace('-','') }
    finally { $algorithm.Dispose() }
}
function Get-OwnedTaskOrNull([object]$Folder) {
    try { return $Folder.GetTask($taskName) }
    catch {
        $failure=$_.Exception
        while($null -ne $failure.InnerException) { $failure=$failure.InnerException }
        if(($failure.HResult -band 4294967295L) -eq 2147942402L) { return $null }
        throw
    }
}
function Assert-TaskOwned([object]$Task,[object]$Manifest,[switch]$CheckSecurity) {
    $definition=$Task.Definition
    if($definition.RegistrationInfo.Description -cne $Manifest.TaskMarker) { throw 'Task ownership marker differs; no unrelated task is changed.' }
    if((Get-Sid $definition.Principal.UserId) -ne $fixedOwnerSid -or $definition.Principal.RunLevel -ne 1 -or $definition.Principal.LogonType -ne 3) { throw 'Task principal differs from the reviewed interactive elevated account.' }
    if($definition.Actions.Count -ne 1 -or $definition.Triggers.Count -ne 1) { throw 'Task action/trigger count differs.' }
    $action=$definition.Actions.Item(1); $trigger=$definition.Triggers.Item(1)
    if($action.Type -ne 0 -or -not ([string]$action.Path).Equals((Join-Path $appDir 'InputLift.exe'),[StringComparison]::OrdinalIgnoreCase) -or $action.Arguments -cne '--tray' -or -not ([string]$action.WorkingDirectory).Equals($appDir,[StringComparison]::OrdinalIgnoreCase)) { throw 'Task executable/arguments/working directory differs.' }
    if($trigger.Type -ne 9 -or (Get-Sid $trigger.UserId) -ne $fixedOwnerSid) { throw 'Task logon trigger differs.' }
    if($definition.Settings.ExecutionTimeLimit -ne 'PT0S' -or $definition.Settings.MultipleInstances -ne 2 -or $definition.Settings.DisallowStartIfOnBatteries -or $definition.Settings.StopIfGoingOnBatteries) { throw 'Task lifetime/battery/single-instance settings differ.' }
    if($null -ne $Manifest.TaskXmlSha256 -and (Get-NormalizedTaskHash ([string]$Task.Xml)) -ne $Manifest.TaskXmlSha256) { throw 'Task definition changed beyond the allowed Enabled toggle.' }
    if($CheckSecurity) { Assert-ProtectedAcl $taskPath -Task -TaskObject $Task }
}
function New-Task([object]$Service,[object]$Folder,[object]$Manifest) {
    $definition=$Service.NewTask(0)
    $definition.RegistrationInfo.Description=$Manifest.TaskMarker
    $definition.RegistrationInfo.Author='InputLift setup'
    $definition.Principal.UserId=$fixedOwnerSid; $definition.Principal.LogonType=3; $definition.Principal.RunLevel=1
    $definition.Settings.Enabled=$false; $definition.Settings.Hidden=$false
    $definition.Settings.ExecutionTimeLimit='PT0S'; $definition.Settings.MultipleInstances=2
    $definition.Settings.DisallowStartIfOnBatteries=$false; $definition.Settings.StopIfGoingOnBatteries=$false
    $definition.Settings.RunOnlyIfIdle=$false; $definition.Settings.RunOnlyIfNetworkAvailable=$false
    $definition.Settings.StartWhenAvailable=$true; $definition.Settings.AllowDemandStart=$true
    $definition.Settings.WakeToRun=$false
    $trigger=$definition.Triggers.Create(9); $trigger.UserId=$fixedOwnerSid; $trigger.Enabled=$true
    $action=$definition.Actions.Create(0); $action.Path=Join-Path $appDir 'InputLift.exe'; $action.Arguments='--tray'; $action.WorkingDirectory=$appDir
    # TASK_CREATE2, TASK_DONT_ADD_PRINCIPAL_ACE16, InteractiveToken3; no password.
    $registered=$Folder.RegisterTaskDefinition($taskName,$definition,18,$fixedOwnerSid,$null,3,$taskSddl)
    Assert-TaskOwned $registered $Manifest -CheckSecurity
    return $registered
}
function New-OwnedShortcut {
    Assert-NoReparse $shortcutPath
    if(Test-Path -LiteralPath $shortcutPath) { throw 'Create-only shortcut install refuses an existing InputLift.lnk.' }
    $staging=Join-Path $appDir '.setup-shortcut.lnk'
    if(Test-Path -LiteralPath $staging) { throw 'Unexpected staged Start Menu shortcut.' }
    $shell=New-Object -ComObject 'WScript.Shell'
    try {
        $link=$shell.CreateShortcut($staging)
        $link.TargetPath=Join-Path $appDir 'InputLift.exe'; $link.Arguments='--show'
        $link.WorkingDirectory=$appDir; $link.Description='InputLift status and controls'
        $link.Save()
        Protect-File $staging
        # No overwrite; creation races cannot replace an unrelated shortcut.
        [IO.File]::Move($staging,$shortcutPath)
        return Get-Hash $shortcutPath
    }
    finally { $null=[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}
function Test-SensitiveFlag {
    $key=Get-Item -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters'
    return $key.GetValueNames() -contains 'EtwLogSensitiveData'
}
function Get-OwnedProcesses {
    $filter=($payloadNames | ForEach-Object { "Name='$_'" }) -join ' OR '
    $found=@()
    foreach($process in @(Get-CimInstance Win32_Process -Filter $filter)) {
        if([string]::IsNullOrWhiteSpace([string]$process.ExecutablePath)) { throw 'A matching helper process path cannot be inspected; cleanup is unconfirmed.' }
        $path=[IO.Path]::GetFullPath([string]$process.ExecutablePath)
        if($path.StartsWith($appDir+'\',[StringComparison]::OrdinalIgnoreCase)) { $found+= $process }
    }
    return $found
}
function Read-InstalledManifest {
    Assert-ProtectedAcl $appDir; Assert-ProtectedAcl $dataDir; Assert-ProtectedAcl $installManifest
    $manifest=Get-Content -LiteralPath $installManifest -Raw | ConvertFrom-Json
    $parsed=[guid]::Empty
    if($manifest.Version -ne 1 -or $manifest.OwnerSid -ne $fixedOwnerSid -or -not [guid]::TryParseExact([string]$manifest.InstanceId,'N',[ref]$parsed) -or $manifest.TaskName -cne $taskName) { throw 'Installed manifest identity differs.' }
    if((Get-NormalizedKeyboardAddress ([string]$manifest.KeyboardAddress)) -cne [string]$manifest.KeyboardAddress) { throw 'Installed keyboard address is not canonical uppercase hexadecimal.' }
    if(-not ([string]$manifest.AppDir).Equals($appDir,[StringComparison]::OrdinalIgnoreCase) -or -not ([string]$manifest.DataDir).Equals($dataDir,[StringComparison]::OrdinalIgnoreCase)) { throw 'Installed manifest paths differ.' }
    if($manifest.TaskMarker -cne ('InputLift managed instance '+$manifest.InstanceId+'; owner '+$fixedOwnerSid)) { throw 'Installed task marker differs.' }
    if([string]$manifest.TaskXmlSha256 -notmatch '^[0-9A-Fa-f]{64}$') { throw 'Installed normalized task XML hash is invalid.' }
    if($null -ne $manifest.ShortcutPath -and $manifest.ShortcutPath -cne $shortcutPath) { throw 'Installed shortcut path differs.' }
    if(@($manifest.Files).Count -ne 3) { throw 'Installed payload count differs.' }
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($file in @($manifest.Files)) {
        if([string]$file.Name -notin $payloadNames -or -not $seen.Add([string]$file.Name) -or [string]$file.Sha256 -notmatch '^[0-9A-Fa-f]{64}$') { throw 'Installed payload manifest is invalid.' }
        $path=Assert-ExpectedPath (Join-Path $appDir $file.Name) $appDir
        Assert-ProtectedAcl $path
        if((Get-Hash $path) -ne ([string]$file.Sha256).ToUpperInvariant()) { throw "Installed payload changed: $($file.Name). Automatic execution/removal refused." }
    }
    return $manifest
}
function Remove-OwnedFile([string]$Path,[string]$Root) {
    $resolved=Assert-ExpectedPath $Path $Root
    if(Test-Path -LiteralPath $resolved) {
        if((Get-Item -LiteralPath $resolved -Force).PSIsContainer) { throw "File removal refused for directory: $resolved" }
        Remove-Item -LiteralPath $resolved -Force
    }
}
function Remove-EmptyOwnedDirectory([string]$Path) {
    $resolved=Assert-ExpectedPath $Path $Path -RootItself
    if(Test-Path -LiteralPath $resolved) {
        if(@(Get-ChildItem -LiteralPath $resolved -Force).Count -eq 0) { [IO.Directory]::Delete($resolved,$false) }
    }
}
function Get-CleanupProof([object]$Active) {
    $path=Join-Path $dataDir 'cleanup.json'
    if(-not (Test-Path -LiteralPath $path)) { return $false }
    Assert-NoReparse $path
    try {
        $proof=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if($proof.Version -ne 1 -or $proof.RunNonce -cne $Active.RunNonce) { return $false }
        foreach($field in @('TrayPid','TrayStartUtcTicks','GuardPid','GuardStartUtcTicks','WorkerPid','WorkerStartUtcTicks')) { if($proof.$field -ne $Active.$field) { return $false } }
        foreach($flag in @('Clean','RegistryAbsenceConfirmed','OwnedTraceStoppedOrAbsent','ChildQuiescenceConfirmed','ShortcutReleaseConfirmed')) { if($proof.$flag -isnot [bool] -or -not $proof.$flag) { return $false } }
        return $true
    }
    catch { $script:cleanupProofError=$_.Exception.Message; return $false }
}
function Test-LaterBootRecovery([long]$ActiveBoot,[long]$CurrentBoot,[bool]$NoOwnedProcesses,[bool]$FlagAbsent,[bool]$TaskDisabledIdle) {
    return $ActiveBoot -gt 0 -and $CurrentBoot -gt $ActiveBoot -and $NoOwnedProcesses -and $FlagAbsent -and $TaskDisabledIdle
}
function Assert-RecoveryWindowData([object]$Window) {
    $nonce=[guid]::Empty
    if(($window.Version -isnot [int] -and $window.Version -isnot [long]) -or $window.Version -ne 1 -or
       $window.OldRunNonce -isnot [string] -or -not [guid]::TryParseExact($window.OldRunNonce,'N',[ref]$nonce) -or
       ($window.CurrentBootUtcTicks -isnot [int] -and $window.CurrentBootUtcTicks -isnot [long]) -or $window.CurrentBootUtcTicks -le 0 -or
       $window.MayHaveOccurred -isnot [bool] -or -not $window.MayHaveOccurred) { throw 'Recovery-window ownership/boot schema is invalid; files retained.' }
    if($null -ne $window.PSObject.Properties['Removed'] -and $window.Removed -isnot [bool]) { throw 'Recovery-window Removed field is not a boolean.' }
}
function Read-RecoveryWindow {
    $path=Join-Path $dataDir 'recovery-window.json'
    if(-not (Test-Path -LiteralPath $path)) { return $null }
    Assert-ProtectedAcl $path
    $window=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    Assert-RecoveryWindowData $window
    return $window
}

try {
    if($Mode -eq 'Install') { $KeyboardAddress=Get-NormalizedKeyboardAddress $KeyboardAddress }
    elseif($PSBoundParameters.ContainsKey('KeyboardAddress')) { throw '-KeyboardAddress is allowed only with Install.' }
    if($Start -and $Mode -ne 'Install') { throw '-Start is allowed only with Install.' }
    if($CreateStartMenuShortcut -and $Mode -ne 'Install') { throw '-CreateStartMenuShortcut is allowed only with Install.' }
    if($Mode -eq 'Install' -and -not $EnablePersistentLogging) { throw 'Install requires -EnablePersistentLogging after explicit approval of ongoing Bluetooth logging and automatic startup.' }
    Assert-NoReparse $appDir; Assert-NoReparse $dataDir
    if($Mode -eq 'Inspect') {
        $packageInfo=$null; $packageError=$null
        try { $packageInfo=Read-Package } catch { $packageError=$_.Exception.Message }
        $service=Connect-Scheduler; $folder=$service.GetFolder('\'); $task=Get-OwnedTaskOrNull $folder
        $installed=$null; $installedError=$null
        if(Test-Path -LiteralPath $installManifest) { try { $installed=Read-InstalledManifest; if($null -ne $task) { Assert-TaskOwned $task $installed -CheckSecurity } } catch { $installedError=$_.Exception.Message } }
        Write-Result ([pscustomobject]@{Mode='Inspect';Status='read-only';OwnerSid=$fixedOwnerSid;AppDir=$appDir;DataDir=$dataDir;TaskName=$taskName;TaskPresent=($null -ne $task);InstalledManifestValid=($null -ne $installed);InstalledError=$installedError;PackageManifestSha256=$(if($null -ne $packageInfo){$packageInfo.Hash}else{$null});PackageError=$packageError;SensitiveFlagPresent=(Test-SensitiveFlag);SettingsChanged=$false})
        exit 0
    }
    Assert-Identity
    $service=Connect-Scheduler; $folder=$service.GetFolder('\'); $existingTask=Get-OwnedTaskOrNull $folder
    if($Mode -eq 'Install') {
        $package=Read-Package
        if($null -ne $existingTask) { throw 'Create-only install refuses an existing task for this account.' }
        if((Test-Path -LiteralPath $appDir) -or (Test-Path -LiteralPath $dataDir)) { throw 'Create-only install refuses existing app/state directories. Inspect them before any repair.' }
        if($CreateStartMenuShortcut) { Assert-NoReparse $shortcutPath; if(Test-Path -LiteralPath $shortcutPath) { throw 'Create-only install refuses an existing InputLift.lnk.' } }
        if(Test-SensitiveFlag) { throw 'EtwLogSensitiveData already exists; stop and verify the prior capture before installing.' }
        $createdApp=$false; $createdData=$false; $createdShortcut=$false; $committed=$false; $registered=$null
        $instance=[guid]::NewGuid().ToString('N')
        $manifest=[ordered]@{Version=1;InstanceId=$instance;OwnerSid=$fixedOwnerSid;KeyboardAddress=$KeyboardAddress;AppDir=$appDir;DataDir=$dataDir;TaskName=$taskName;TaskMarker=('InputLift managed instance '+$instance+'; owner '+$fixedOwnerSid);PackageManifestSha256=$package.Hash;TaskXmlSha256=$null;Files=@($package.Manifest.Files);ShortcutPath=$null;ShortcutSha256=$null;InstalledUtc=[DateTime]::UtcNow.ToString('o')}
        try {
            New-ProtectedDirectory $appDir ([ref]$createdApp)
            New-ProtectedDirectory $dataDir ([ref]$createdData)
            foreach($file in @($package.Manifest.Files)) { Copy-LockedPayload (Join-Path $package.Root $file.Name) (Join-Path $appDir $file.Name) ([string]$file.Sha256) }
            Write-ProtectedJson (Join-Path $dataDir 'consent.json') ([ordered]@{Version=1;ApprovedPersistentLogging=$true;OwnerSid=$fixedOwnerSid;ApprovedAtUtc=[DateTime]::UtcNow.ToString('o');PackageSha256=$package.Hash})
            Write-ProtectedJson $installManifest $manifest
            $registered=New-Task $service $folder $manifest
            if($CreateStartMenuShortcut) { $manifest.ShortcutSha256=New-OwnedShortcut; $createdShortcut=$true; $manifest.ShortcutPath=$shortcutPath }
            $manifest.TaskXmlSha256=Get-NormalizedTaskHash ([string]$registered.Xml)
            Write-ProtectedJson $installManifest $manifest
            $committed=$true
            $registered.Enabled=$true
            $runRequested=$false
            if($Start) { $null=$registered.Run($null); $runRequested=$true }
            Write-Result ([pscustomobject]@{Mode='Install';Status='installed';TaskName=$taskName;AppDir=$appDir;DataDir=$dataDir;TaskRunRequested=$runRequested;CaptureReadyNotYetVerified=$runRequested;RegistryChangedBySetup=$false;RestartMayBeRequiredAfterRuntime=$runRequested})
            exit 0
        }
        catch {
            $installError=$_.Exception.Message; $rollbackErrors=[Collections.Generic.List[string]]::new()
            if(-not $committed) {
                $safeToRemove=$true
                try { $candidate=Get-OwnedTaskOrNull $folder; if($null -ne $candidate) { Assert-TaskOwned $candidate $manifest; $folder.DeleteTask($taskName,0) } } catch { $safeToRemove=$false; $rollbackErrors.Add($_.Exception.Message) }
                if($safeToRemove) {
                    if($createdShortcut) { try { if((Get-Hash $shortcutPath) -eq $manifest.ShortcutSha256) { Remove-OwnedFile $shortcutPath ([Environment]::GetFolderPath('Programs')) } } catch { $rollbackErrors.Add($_.Exception.Message) } }
                    if($createdApp) { foreach($name in @($payloadNames)+@('.setup-shortcut.lnk')) { try { Remove-OwnedFile (Join-Path $appDir $name) $appDir } catch { $rollbackErrors.Add($_.Exception.Message) } } }
                    if($createdData) { foreach($name in @('install-manifest.json','install-manifest.json.new','consent.json','consent.json.new')) { try { Remove-OwnedFile (Join-Path $dataDir $name) $dataDir } catch { $rollbackErrors.Add($_.Exception.Message) } } }
                    if($createdApp) { try { Remove-EmptyOwnedDirectory $appDir } catch { $rollbackErrors.Add($_.Exception.Message) } }
                    if($createdData) { try { Remove-EmptyOwnedDirectory $dataDir } catch { $rollbackErrors.Add($_.Exception.Message) } }
                }
            }
            Write-Result ([pscustomobject]@{Mode='Install';Status='error';Error=$installError;InstallationCommitted=$committed;RollbackErrors=@($rollbackErrors);NoAutomaticRetry=$true})
            exit 1
        }
    }
    $manifest=Read-InstalledManifest
    if($null -eq $existingTask) { throw 'Owned task is missing; automatic uninstall refuses an inconsistent install.' }
    Assert-TaskOwned $existingTask $manifest -CheckSecurity
    $existingTask.Enabled=$false
    $activePath=Join-Path $dataDir 'active.json'; $active=$null
    if(Test-Path -LiteralPath $activePath) {
        Assert-NoReparse $activePath
        $active=Get-Content -LiteralPath $activePath -Raw | ConvertFrom-Json
        $parsed=[guid]::Empty
        if($active.Version -ne 1 -or -not [guid]::TryParseExact([string]$active.RunNonce,'N',[ref]$parsed)) { throw 'Active-run identity is invalid; task disabled but files retained.' }
        foreach($field in @('TrayPid','TrayStartUtcTicks','GuardPid','GuardStartUtcTicks','WorkerPid','WorkerStartUtcTicks')) { if([long]$active.$field -lt 0) { throw 'Active-run process identity is invalid.' } }
    }
    $processes=@(Get-OwnedProcesses)
    $wait=[Diagnostics.Stopwatch]::StartNew(); $activeBoot=0L; $currentBoot=0L
    if($null -ne $active -and $null -ne $active.PSObject.Properties['BootUtcTicks']) { $activeBoot=[long]$active.BootUtcTicks }
    $recoveryWindow=Read-RecoveryWindow
    $requiredRecoveryBoot=$activeBoot
    if($null -ne $recoveryWindow) {
        $requiredRecoveryBoot=[Math]::Max($requiredRecoveryBoot,[long]$recoveryWindow.CurrentBootUtcTicks)
        $currentBoot=(Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().Ticks
        if($recoveryWindow.CurrentBootUtcTicks -gt $currentBoot) { throw 'Recovery window belongs to a later/unverified boot; automatic removal refused.' }
    }
    if($processes.Count -ne 0) {
        if($null -eq $active) { throw 'Helper is running without a protected active-run journal; task disabled, no forced termination or deletion.' }
        $stopProcess=Start-Process -FilePath (Join-Path $appDir 'InputLift.exe') -ArgumentList '--stop' -WorkingDirectory $appDir -WindowStyle Hidden -PassThru
        try { if(-not $stopProcess.WaitForExit(30000)) { throw 'Graceful --stop did not finish within 30 seconds; no process was killed and files were retained.' }; if($stopProcess.ExitCode -ne 0) { throw "Graceful --stop exited with code $($stopProcess.ExitCode); files retained." } } finally { $stopProcess.Dispose() }
    }
    $clean=$false; $recoveryAfterRestart=$false; $kernelClearedByLaterBoot=$false; $script:cleanupProofError=$null
    do {
        $processes=@(Get-OwnedProcesses)
        $flagPresent=Test-SensitiveFlag
        $proofOkay=if($null -ne $active) { Get-CleanupProof $active } else { $null -eq $recoveryWindow }
        $taskNow=Get-OwnedTaskOrNull $folder
        if($null -eq $taskNow) { throw 'Owned task disappeared during cleanup; files retained.' }
        Assert-TaskOwned $taskNow $manifest -CheckSecurity
        $taskIdle=(-not $taskNow.Enabled -and $taskNow.State -eq 1)
        if($requiredRecoveryBoot -gt 0 -and $processes.Count -eq 0 -and -not $flagPresent -and $taskIdle) {
            if($currentBoot -le 0) { $currentBoot=(Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().Ticks }
            $kernelClearedByLaterBoot=Test-LaterBootRecovery $requiredRecoveryBoot $currentBoot $true $true $taskIdle
            $recoveryAfterRestart=(-not $proofOkay -and $kernelClearedByLaterBoot)
        }
        $clean=($processes.Count -eq 0 -and -not $flagPresent -and ($proofOkay -or $recoveryAfterRestart) -and $taskIdle)
        if($clean) { break }
        Start-Sleep -Milliseconds 250
    } while($wait.ElapsedMilliseconds -lt 30000)
    if(-not $clean) { throw 'Cleanup is unconfirmed after 30 seconds; task disabled, executable/state files retained. Verify cleanup and restart before removal.' }
    Assert-TaskOwned $existingTask $manifest -CheckSecurity
    $folder.DeleteTask($taskName,0)
    $shortcutStatus='not-installed'
    if($null -ne $manifest.ShortcutPath) {
        $shortcutStatus='already-absent'
        if(Test-Path -LiteralPath $shortcutPath) {
            Assert-NoReparse $shortcutPath
            if((Get-Hash $shortcutPath) -eq $manifest.ShortcutSha256) { Remove-OwnedFile $shortcutPath ([Environment]::GetFolderPath('Programs')); $shortcutStatus='removed-owned' }
            else { $shortcutStatus='retained-changed-shortcut' }
        }
    }
    foreach($file in @($manifest.Files)) { Remove-OwnedFile (Join-Path $appDir $file.Name) $appDir }
    foreach($name in @('active.json','cleanup.json','install-manifest.json','consent.json','recovery-window.json')) { Remove-OwnedFile (Join-Path $dataDir $name) $dataDir }
    Remove-EmptyOwnedDirectory $appDir; Remove-EmptyOwnedDirectory $dataDir
    Write-Result ([pscustomobject]@{Mode='Uninstall';Status='removed';TaskDisabledAndRemoved=$true;OwnedPayloadRemoved=$true;ShortcutStatus=$shortcutStatus;RemainingStateDirectory=(Test-Path -LiteralPath $dataDir);RecoveryAfterRestart=$recoveryAfterRestart;RecoveryBootBaselineUtcTicks=$requiredRecoveryBoot;CleanupProofError=$script:cleanupProofError;KernelStateClearedByLaterBoot=$kernelClearedByLaterBoot;RestartRequiredToClearDriverCache=(($null -ne $active -or $null -ne $recoveryWindow) -and -not $kernelClearedByLaterBoot);RegistryChangedBySetup=$false})
    exit 0
}
catch {
    Write-Result ([pscustomobject]@{Mode=$Mode;Status='error';Error=$_.Exception.Message;NoAutomaticRetry=$true})
    exit 1
}
