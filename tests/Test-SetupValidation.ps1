# Copyright (c) 2026 InputLift contributors
# SPDX-License-Identifier: MIT
# Pure boundary checks. Parse and extract only side-effect-free setup functions;
# never dot-source Setup.ps1 or invoke its install/inspect/uninstall entry point.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$setupPath=Join-Path (Split-Path $PSScriptRoot -Parent) 'Setup.ps1'
$tokens=$null; $parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($setupPath,[ref]$tokens,[ref]$parseErrors)
if(@($parseErrors).Count -ne 0) { throw 'Setup.ps1 has parser errors.' }
foreach($name in @('Get-NormalizedKeyboardAddress','Get-InstalledOutputMode','Assert-PackageData','Test-LaterBootRecovery')) {
    $definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$false)
    if($null -eq $definition) { throw "Missing pure setup function: $name" }
    . ([ScriptBlock]::Create($definition.Extent.Text))
}
$checks=0
function Assert-Check([bool]$Condition,[string]$Description) {
    if(-not $Condition) { throw "Pure setup validation failed: $Description" }
    $script:checks++
}
function Assert-Refused([scriptblock]$Operation,[string]$Description) {
    $refused=$false
    try { $null=& $Operation } catch { $refused=$true }
    Assert-Check $refused $Description
}
Assert-Check ((Get-NormalizedKeyboardAddress '001122aAbBcC') -ceq '001122AABBCC') 'Canonical address normalization'
foreach($bad in @('', '000000000000','ffffffffffff','00:11:22:33:44:55','00112233445','0011223344550','00112233445G',"001122334455`n",'001122334455;calc')) {
    Assert-Refused { Get-NormalizedKeyboardAddress $bad } 'Invalid address cannot enter a child process command line'
}
Assert-Check ((Get-InstalledOutputMode ([pscustomobject]@{Version=1})) -ceq 'deskflow') 'Legacy output defaults to Deskflow'
foreach($modeValue in @('deskflow','events-only')) {
    Assert-Check ((Get-InstalledOutputMode ([pscustomobject]@{OutputMode=$modeValue})) -ceq $modeValue) 'Explicit output mode is preserved'
}
foreach($modeValue in @($null,$true,'','Deskflow','events-only --other','arbitrary')) {
    Assert-Refused { Get-InstalledOutputMode ([pscustomobject]@{OutputMode=$modeValue}) } 'Unknown or mistyped mode cannot select output'
}
$payloadNames=@('InputLift.exe','InputLiftGuard.exe','InputLiftCapture.exe')
$validFiles=@($payloadNames|ForEach-Object { [pscustomobject]@{Name=$_;Sha256=('A'*64)} })
Assert-PackageData ([pscustomobject]@{Version=1;Files=$validFiles}); $checks++
Assert-Refused { Assert-PackageData ([pscustomobject]@{Version=2;Files=$validFiles}) } 'Unknown package version'
Assert-Refused { Assert-PackageData ([pscustomobject]@{Version=1;Files=@($validFiles[0],$validFiles[0],$validFiles[2])}) } 'Duplicate package payload'
Assert-Refused { Assert-PackageData ([pscustomobject]@{Version=1;Files=@([pscustomobject]@{Name='..\InputLift.exe';Sha256=('A'*64)},$validFiles[1],$validFiles[2])}) } 'Package path traversal'
Assert-Refused { Assert-PackageData ([pscustomobject]@{Version=1;Files=@([pscustomobject]@{Name=$payloadNames[0];Sha256='not-a-hash'},$validFiles[1],$validFiles[2])}) } 'Malformed package hash'
Assert-Check (Test-LaterBootRecovery 100 101 $true $true $true) 'Recovery requires a later boot'
Assert-Check (-not (Test-LaterBootRecovery 100 100 $true $true $true)) 'Same boot cannot prove driver state cleared'
Assert-Check (-not (Test-LaterBootRecovery 0 101 $true $true $true)) 'Missing baseline cannot prove driver state cleared'
Assert-Check (-not (Test-LaterBootRecovery 100 101 $false $true $true)) 'Live process blocks recovery'
Assert-Check (-not (Test-LaterBootRecovery 100 101 $true $false $true)) 'Logging flag blocks recovery'
Assert-Check (-not (Test-LaterBootRecovery 100 101 $true $true $false)) 'Enabled or active task blocks recovery'
[pscustomobject]@{status='ok';checks=$checks;errors=@();LiveActionsPerformed=$false}|ConvertTo-Json -Compress
