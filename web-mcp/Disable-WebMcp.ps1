# 파일/인증서는 보존한다. 설치 상태를 다시 확인할 수 있게 자동 실행만 중지한다.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$task = Get-ScheduledTask -TaskName 'RemoteControlWebMcp' -ErrorAction SilentlyContinue
if($task) {
    $expected = Join-Path $env:ProgramFiles 'RemoteControlWebMcp\node.exe'
    if($task.Actions.Count -ne 1 -or $task.Actions[0].Execute -ne $expected) { throw '예약 작업의 실행 경로가 다릅니다. 중지하지 않았습니다.' }
    Disable-ScheduledTask -InputObject $task | Out-Null
    Stop-ScheduledTask -InputObject $task
}
$service = Get-CimInstance Win32_Service -Filter "Name='RemoteControlWebHttps'"
if($service) {
    $expected = '"' + (Join-Path $env:ProgramFiles 'RemoteControlWebMcp\caddy.exe') + '" run '
    if(-not $service.PathName.StartsWith($expected,[StringComparison]::OrdinalIgnoreCase)) { throw 'HTTPS 서비스의 실행 경로가 다릅니다. 중지하지 않았습니다.' }
    Stop-Service -Name 'RemoteControlWebHttps'
    Set-Service -Name 'RemoteControlWebHttps' -StartupType Disabled
}
foreach($rule in Get-NetFirewallRule -Name 'RemoteControlWebMcp-TCP-*' -ErrorAction SilentlyContinue){
    if(($rule|Get-NetFirewallApplicationFilter).Program -ine (Join-Path $env:ProgramFiles 'RemoteControlWebMcp\caddy.exe')){throw '방화벽 규칙의 프로그램이 다릅니다.'}
    $rule|Disable-NetFirewallRule
}
'웹 MCP 자동 실행과 외부 접속을 중지했습니다. 원격제어 본체와 LAN MCP, 웹 MCP 파일은 유지했습니다.'

$lanHelper=Join-Path $PSScriptRoot 'LanWebMcpConfig.ps1'
if(Test-Path -LiteralPath $lanHelper){. $lanHelper;Resume-LanWebMcpStandalone}
