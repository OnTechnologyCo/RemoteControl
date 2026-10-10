$ErrorActionPreference='Stop'
$appDir=Join-Path $env:ProgramFiles 'RemoteControlTailscaleMcp'
$stateDir=Join-Path $env:ProgramData 'RemoteControlTailscaleMcp'
$task=Get-ScheduledTask -TaskName 'RemoteControlTailscaleMcp' -ErrorAction SilentlyContinue
$expected='"{0}" "{1}"' -f (Join-Path $appDir 'tailscale-server.mjs'),(Join-Path $stateDir 'gateway.json')
if($task){
    if($task.Actions.Count -ne 1 -or $task.Actions[0].Execute -ine (Join-Path $appDir 'node.exe') -or $task.Actions[0].Arguments -cne $expected){throw 'Tailscale MCP task ownership mismatch. Nothing stopped.'}
    Disable-ScheduledTask -InputObject $task|Out-Null
    Stop-ScheduledTask -InputObject $task
}
'Tailscale MCP automatic startup stopped; configuration retained.'
