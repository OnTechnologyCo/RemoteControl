# Dedicated loopback gateway only. Does not change Caddy, DDNS, router or other services.
[CmdletBinding()]
param([ValidateSet(443,8443,10000)][int]$PublicPort=443,[ValidateRange(1024,65535)][int]$GatewayPort=59335,[ValidateRange(1024,65535)][int]$LanPort=5901,[switch]$CheckOnly)
$ErrorActionPreference='Stop'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator rights required for the Tailscale MCP runtime.'}
if($GatewayPort -eq $LanPort){throw 'Tailscale gateway and LAN MCP ports must differ.'}
$appDir=Join-Path $env:ProgramFiles 'RemoteControlTailscaleMcp'
$stateDir=Join-Path $env:ProgramData 'RemoteControlTailscaleMcp'
$taskName='RemoteControlTailscaleMcp'
$task=Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
$marker=Join-Path $appDir 'owner.json'
$expectedExe=Join-Path $appDir 'node.exe'
$expectedArgs='"{0}" "{1}"' -f (Join-Path $appDir 'tailscale-server.mjs'),(Join-Path $stateDir 'gateway.json')
foreach($path in @($appDir,$stateDir)){
    if((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked runtime directory rejected.'}
}
if(Test-Path -LiteralPath $marker){
    $owner=Get-Content -LiteralPath $marker -Raw|ConvertFrom-Json
    if($owner.product -cne $taskName -or $owner.directory -ine $appDir){throw 'Tailscale MCP ownership mismatch.'}
}elseif((Test-Path -LiteralPath $appDir) -or (Test-Path -LiteralPath $stateDir) -or $task){throw 'Unowned Tailscale MCP runtime found.'}
if($task -and ($task.Actions.Count -ne 1 -or $task.Actions[0].Execute -ine $expectedExe -or $task.Actions[0].Arguments -cne $expectedArgs)){throw 'Tailscale MCP task ownership mismatch.'}
foreach($listener in Get-NetTCPConnection -State Listen -LocalPort $GatewayPort -ErrorAction SilentlyContinue){
    $process=Get-CimInstance Win32_Process -Filter ("ProcessId="+$listener.OwningProcess)
    if(-not $task -or $process.ExecutablePath -ine $expectedExe -or $process.CommandLine -notlike ('*'+$expectedArgs)){throw "TCP $GatewayPort is already used by another program. No services stopped."}
}
foreach($name in @('node.exe','tailscale-server.mjs','gateway.mjs','LanWebMcpConfig.ps1','auth-state.mjs','transfers.mjs','transfer-client.mjs','node_modules','package.json')){if(-not(Test-Path -LiteralPath (Join-Path $PSScriptRoot $name))){throw "Missing runtime package: $name"}}
if($CheckOnly){'Tailscale MCP preflight passed';return}
function PrivateDirectory([string]$path,[bool]$writable){
    [void][IO.Directory]::CreateDirectory($path)
    $acl=New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl','ContainerInherit,ObjectInherit','None','Allow')))}
    $rights=if($writable){'Modify'}else{'ReadAndExecute'}
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-19')),$rights,'ContainerInherit,ObjectInherit','None','Allow')))
    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    Set-Acl -LiteralPath $path -AclObject $acl
}
PrivateDirectory $appDir $false
PrivateDirectory $stateDir $false
PrivateDirectory (Join-Path $stateDir 'data') $true
if($task){Stop-ScheduledTask -InputObject $task}
for($n=0;$n -lt 20;$n++){
    $owned=@(Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object {$_.ExecutablePath -ieq $expectedExe -and $_.CommandLine -like ('*'+$expectedArgs)})
    if($owned.Count -eq 0){break};Start-Sleep -Milliseconds 250
}
if($owned.Count -gt 0){throw 'The owned Tailscale MCP gateway did not stop; files were not replaced.'}
@{product=$taskName;directory=$appDir}|ConvertTo-Json|Set-Content -LiteralPath $marker -Encoding UTF8
foreach($name in @('node.exe','tailscale-server.mjs','gateway.mjs','LanWebMcpConfig.ps1','auth-state.mjs','transfers.mjs','transfer-client.mjs','node_modules','package.json','package-lock.json','NODE-LICENSE.txt','Disable-TailscaleMcp.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $appDir -Recurse -Force}
$configPath=Join-Path $stateDir 'gateway.json'
$random=New-Object byte[] 32;$rng=[Security.Cryptography.RandomNumberGenerator]::Create()
try{$rng.GetBytes($random)}finally{$rng.Dispose()}
$proxyKey=[Convert]::ToBase64String($random).TrimEnd('=').Replace('+','-').Replace('/','_')
if(Test-Path -LiteralPath $configPath){$prior=Get-Content -LiteralPath $configPath -Raw|ConvertFrom-Json;if($prior.proxyKey -match '^[A-Za-z0-9_-]{43}$'){$proxyKey=$prior.proxyKey}}
@{listenPort=$GatewayPort;lanPort=$LanPort;publicPort=$PublicPort;proxyKey=$proxyKey}|ConvertTo-Json|Set-Content -LiteralPath $configPath -Encoding UTF8
'{}'|Set-Content -LiteralPath (Join-Path $stateDir 'active.json') -Encoding UTF8
$action=New-ScheduledTaskAction -Execute $expectedExe -Argument $expectedArgs -WorkingDirectory $appDir
$principal=New-ScheduledTaskPrincipal -UserId 'S-1-5-19' -LogonType ServiceAccount -RunLevel Limited
$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings -Trigger (New-ScheduledTaskTrigger -AtStartup) -Description 'RemoteControl Tailscale MCP gateway; loopback only; waits for active tunnel' -Force|Out-Null
Start-ScheduledTask -TaskName $taskName
for($n=0;$n -lt 20;$n++){if(Get-NetTCPConnection -State Listen -LocalPort $GatewayPort -ErrorAction SilentlyContinue){'Tailscale MCP runtime ready; waiting for the generated Tailscale address';return};Start-Sleep -Milliseconds 250}
throw 'Tailscale MCP runtime did not start.'
