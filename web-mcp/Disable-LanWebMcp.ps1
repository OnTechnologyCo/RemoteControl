$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'LanWebMcpConfig.ps1')
$appDir=Join-Path $env:ProgramFiles 'RemoteControlLanWebMcp'
$stateDir=Join-Path $env:ProgramData 'RemoteControlLanWebMcp'
if(-not(Test-Path -LiteralPath (Join-Path $stateDir 'owner.json'))){return}
Assert-LanWebMcpOwnership $appDir $stateDir
Remove-LanWebMcpPublicSite
[IO.File]::WriteAllText((Join-Path $stateDir 'active.json'),'{"enabled":false,"shared":false}',(New-Object Text.UTF8Encoding($false)))
foreach($name in @('RemoteControlLanWebMcpHttps','RemoteControlLanWebMcpGateway')){
    if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){Stop-ScheduledTask -TaskName $name;Unregister-ScheduledTask -TaskName $name -Confirm:$false}
}
Get-NetFirewallRule -Name 'RemoteControlLanWebMcp-TCP' -ErrorAction SilentlyContinue|Remove-NetFirewallRule
Write-Output 'LAN WebMCP를 중지했습니다. CA·인증 상태는 재사용을 위해 보존합니다.'
