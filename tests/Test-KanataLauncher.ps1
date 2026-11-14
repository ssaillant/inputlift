# Copyright (c) 2026 InputLift contributors
# SPDX-License-Identifier: MIT
# Extract only pure launcher validation functions. Never dot-source its entrypoint.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$launcher=Join-Path (Split-Path $PSScriptRoot -Parent) 'integrations\kanata\Start-Bridge.ps1'
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($launcher,[ref]$tokens,[ref]$parseErrors)
if(@($parseErrors).Count -ne 0) { throw 'Kanata launcher has parser errors.' }
foreach($name in @('Get-BridgeField','Assert-BridgeInteger','Assert-BridgeTextEquals','Assert-BridgeNonce','Assert-BridgeActive','Assert-BridgeSameEpoch',
                   'Get-BridgeStatusPath','Assert-BridgeAclPolicy','Assert-BridgeSnapshot')) {
    $definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$false)
    if($null -eq $definition) { throw ('Missing pure launcher function: '+$name) }
    . ([ScriptBlock]::Create($definition.Extent.Text))
}
$checks=0
function Check([bool]$Condition,[string]$Description) { if(-not $Condition){throw ('Launcher validation failed: '+$Description)};$script:checks++ }
function Refused([scriptblock]$Operation,[string]$Description) { $refused=$false;try{$null=& $Operation}catch{$refused=$true};Check $refused $Description }
$sid='S-1-5-21-1-2-3-1001'
$now=[DateTimeOffset]::Parse('2026-01-02T03:04:05+00:00',[Globalization.CultureInfo]::InvariantCulture)
function Fixture {
    $active=[pscustomobject]@{Version=1;RunNonce='0123456789abcdef0123456789abcdef';TrayPid=11;TrayStartUtcTicks=100;
        GuardPid=12;GuardStartUtcTicks=101;WorkerPid=13;WorkerStartUtcTicks=102;GuardianPid=14;GuardianStartUtcTicks=103;BootUtcTicks=50}
    return [pscustomobject]@{Manifest=[pscustomobject]@{Version=1;OwnerSid=$sid;TaskName=('InputLift-'+$sid);OutputMode='events-only'};
        Active=$active;Status=[pscustomobject]@{Nonce=$active.RunNonce;WorkerPid=13;WorkerStartUtcTicks=102;OutputMode='events-only';
            PipeName=('InputLift.Fn.'+$active.RunNonce);ProgressSequence=1;State='AwaitConsumer';Error=$null;
            KeyboardBound=$false;NeutralConfirmed=$false;UpdatedUtc=$now.UtcDateTime.ToString('o')}}
}
function Validate([object]$Fixture) { Assert-BridgeSnapshot $Fixture.Manifest $Fixture.Active $Fixture.Status $sid $now }
$good=Fixture;Validate $good;Check $true 'AwaitConsumer can be inspected before keyboard binding or neutral state'
foreach($bad in @($null,0,-1,[double]1,'1',$true,([long]([int]::MaxValue)+1))) { Refused { Assert-BridgeInteger $bad ([int]::MaxValue) } 'Typed positive PID boundary' }
foreach($bad in @('', '../bad','0123456789ABCDEF0123456789ABCDEF',('a'*33),('a'*31),('a'*31)+"`n")) { Refused { Assert-BridgeNonce $bad } 'Canonical nonce rejects path/framing ambiguity' }
Check ((Get-BridgeStatusPath 'C:\ProgramData\InputLift' $good.Active.RunNonce) -ceq 'C:\ProgramData\InputLift\daily-0123456789abcdef0123456789abcdef.status.json') 'Fixed status path'
foreach($path in @('','relative','C:relative')) { Refused { Get-BridgeStatusPath $path $good.Active.RunNonce } 'Relative state root is refused' }
foreach($field in @('Version','OwnerSid','TaskName','OutputMode')) {
    $fixture=Fixture;$fixture.Manifest.PSObject.Properties.Remove($field);Refused { Validate $fixture } 'Missing install identity/mode fails closed'
}
foreach($value in @($null,$true,1,'deskflow','Events-Only','events-only --command')) {
    $fixture=Fixture;$fixture.Manifest.OutputMode=$value;Refused { Validate $fixture } 'Mode must be explicit events-only string'
}
foreach($field in @('OwnerSid','TaskName')) {
    $fixture=Fixture;$fixture.Manifest.$field=$true;Refused { Validate $fixture } 'Boolean cannot impersonate a nonempty install identity string'
}
foreach($field in @('WorkerPid','WorkerStartUtcTicks','ProgressSequence')) {
    foreach($value in @($null,$true,'13',0,-1,[double]13)) { $fixture=Fixture;$fixture.Status.$field=$value;Refused { Validate $fixture } 'Status integer schema is strict' }
}
foreach($field in @('Nonce','PipeName','OutputMode')) { $fixture=Fixture;$fixture.Status.$field='other';Refused { Validate $fixture } 'Status epoch/pipe/mode identity is bound' }
foreach($field in @('Nonce','PipeName','OutputMode','State')) {
    $fixture=Fixture;$fixture.Status.$field=$true;Refused { Validate $fixture } 'Boolean cannot impersonate a nonempty status identity string'
}
foreach($state in @('EventsReady','Ready','Stopped','Error','AwaitNeutral','WaitingForKeyboard')) {
    $fixture=Fixture;$fixture.Status.State=$state;Refused { Validate $fixture } 'Only first-consumer waiting state is accepted'
}
$fixture=Fixture;$fixture.Status.Error='simulated error';Refused { Validate $fixture } 'Error cannot appear in a healthy snapshot'
foreach($field in @('KeyboardBound','NeutralConfirmed')) { $fixture=Fixture;$fixture.Status.$field='false';Refused { Validate $fixture } 'Readiness flags are typed booleans' }
foreach($time in @($null,0,'not-a-time',$now.AddSeconds(-6).UtcDateTime.ToString('o'),$now.AddSeconds(2).UtcDateTime.ToString('o'),$now.ToOffset([TimeSpan]::FromHours(2)).ToString('o'))) {
    $fixture=Fixture;$fixture.Status.UpdatedUtc=$time;Refused { Validate $fixture } 'Timestamp must be current UTC'
}
$fixture=Fixture;Assert-BridgeSameEpoch $fixture.Active (Fixture).Active;Check $true 'Same validated source epoch accepted'
foreach($field in @('RunNonce','TrayPid','TrayStartUtcTicks','GuardPid','GuardStartUtcTicks','WorkerPid','WorkerStartUtcTicks','GuardianPid','GuardianStartUtcTicks','BootUtcTicks')) {
    $first=(Fixture).Active;$second=(Fixture).Active
    if($field -eq 'RunNonce') { $second.$field='abcdef0123456789abcdef0123456789' } else { $second.$field=[long]$second.$field+1 }
    Refused { Assert-BridgeSameEpoch $first $second } 'Any source identity change stops launch'
}
function AclFixture {
    return [pscustomobject]@{Owner='S-1-5-32-544';Protected=$true;Rules=@(
        [pscustomobject]@{Sid='S-1-5-32-544';Type='Allow';Rights=[Security.AccessControl.FileSystemRights]::FullControl},
        [pscustomobject]@{Sid='S-1-5-18';Type='Allow';Rights=[Security.AccessControl.FileSystemRights]::FullControl},
        [pscustomobject]@{Sid=$sid;Type='Allow';Rights=[Security.AccessControl.FileSystemRights]::ReadAndExecute})}
}
Assert-BridgeAclPolicy (AclFixture) $sid;Check $true 'Protected administrator-owned read-only owner policy'
$acl=AclFixture;$acl.Owner='S-1-5-18';Assert-BridgeAclPolicy $acl $sid;Check $true 'SYSTEM owner also accepted'
$acl=AclFixture;$acl.Owner=$sid;Refused { Assert-BridgeAclPolicy $acl $sid } 'User-owned metadata refused'
$acl=AclFixture;$acl.Protected=$false;Refused { Assert-BridgeAclPolicy $acl $sid } 'Inherited DACL refused'
foreach($rights in @([Security.AccessControl.FileSystemRights]::Write,[Security.AccessControl.FileSystemRights]::Modify,[Security.AccessControl.FileSystemRights]::FullControl,[Security.AccessControl.FileSystemRights]::ChangePermissions)) {
    $acl=AclFixture;$acl.Rules[2].Rights=$rights;Refused { Assert-BridgeAclPolicy $acl $sid } 'Owner write/delete/permission access refused'
}
$acl=AclFixture;$acl.Rules[2].Type='Deny';Refused { Assert-BridgeAclPolicy $acl $sid } 'Deny rules refused'
$acl=AclFixture;$acl.Rules[2].Type=$true;Refused { Assert-BridgeAclPolicy $acl $sid } 'Boolean is not an allow rule'
$acl=AclFixture;$acl.Rules[2].Sid=$true;Refused { Assert-BridgeAclPolicy $acl $sid } 'Boolean is not an owner SID'
$acl=AclFixture;$acl.Owner=$true;Refused { Assert-BridgeAclPolicy $acl $sid } 'Boolean is not a protected owner'
$acl=AclFixture;$acl.Rules[2].Sid='S-1-1-0';Refused { Assert-BridgeAclPolicy $acl $sid } 'Unrelated principal refused'
$acl=AclFixture;$acl.Rules=@($acl.Rules[0],$acl.Rules[2]);Refused { Assert-BridgeAclPolicy $acl $sid } 'Required SYSTEM rule cannot be absent'
$acl=AclFixture;$acl.Rules[0].Rights=[Security.AccessControl.FileSystemRights]::Read;Refused { Assert-BridgeAclPolicy $acl $sid } 'Administrator full control required'
$acl=AclFixture;$acl.Rules[2].Rights='ReadAndExecute';Refused { Assert-BridgeAclPolicy $acl $sid } 'Unparsed access-right string refused'
[pscustomobject]@{status='ok';checks=$checks;errors=@();EntryPointInvoked=$false;ProtectedStateRead=$false;SocketsOpened=$false;ProcessStarted=$false;CaptureStarted=$false;SettingsChanged=$false}|ConvertTo-Json -Compress
