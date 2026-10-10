$ErrorActionPreference='Stop'
$appDir=Join-Path $env:ProgramFiles 'RemoteControlPrivateMcp'
$stateDir=Join-Path $env:ProgramData 'RemoteControlPrivateMcp'
$task=Get-ScheduledTask -TaskName 'RemoteControlPrivateMcp' -ErrorAction SilentlyContinue
$expected='"{0}" "{1}"' -f (Join-Path $appDir 'private-server.mjs'),(Join-Path $stateDir 'gateway.json')
if($task){
    if($task.Actions.Count -ne 1 -or $task.Actions[0].Execute -ine (Join-Path $appDir 'node.exe') -or $task.Actions[0].Arguments -cne $expected){throw 'Private MCP task ownership mismatch. Nothing stopped.'}
    Disable-ScheduledTask -InputObject $task|Out-Null
    Stop-ScheduledTask -InputObject $task
}
'Private MCP automatic startup stopped; configuration retained.'
