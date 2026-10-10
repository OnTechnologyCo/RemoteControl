# Narrow repair for the owned Caddy only. Preserve URLs, ports, routes and OAuth process.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputFile)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$allowed=[IO.Path]::GetFullPath((Join-Path $root 'artifacts\web-registration-diagnostic')).TrimEnd('\')+'\'
$output=[IO.Path]::GetFullPath($OutputFile)
if(-not $output.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected report location'}
$app=Join-Path $env:ProgramFiles 'RemoteControlWebMcp'
$state=Join-Path $env:ProgramData 'RemoteControlWebMcp'
$file=Join-Path $state 'Caddyfile'
$owner=Get-Content -LiteralPath (Join-Path $app 'owner.json') -Raw|ConvertFrom-Json
if($owner.product -ne 'RemoteControlWebMcp' -or $owner.directory -ine $app){throw 'Owner mismatch'}
$service=Get-CimInstance Win32_Service -Filter "Name='RemoteControlWebHttps'"
$expected='"'+(Join-Path $app 'caddy.exe')+'" run --config "'+$file+'" --adapter caddyfile'
if(-not $service -or $service.PathName -cne $expected -or $service.State -ne 'Running'){throw 'Owned running service not found'}
$config=Get-Content -LiteralPath (Join-Path $state 'gateway.json') -Raw|ConvertFrom-Json
$nodeBefore=@(Get-NetTCPConnection -State Listen -LocalPort $config.listenPort|Select-Object -ExpandProperty OwningProcess -Unique)
$before=[IO.File]::ReadAllText($file)
if($before -match 'protocols h1 h2' -and $before -match 'header Alt-Svc "clear"'){throw 'Repair already applied'}
if($before -match '(?m)^\s*servers\b' -or $before -notmatch '(?m)^\s*auto_https off\s*$'){throw 'Unrecognized Caddy global settings'}
$after=[regex]::Replace($before,'(?m)^(\s*)auto_https off\r?$','$1auto_https off'+"`n    servers {`n        protocols h1 h2`n    }")
$after=$after.Replace('header Strict-Transport-Security "max-age=0"',"header Strict-Transport-Security `"max-age=0`"`n    header Alt-Svc `"clear`"")
if($after -eq $before -or $after -notmatch 'header Alt-Svc "clear"'){throw 'Expected HTTPS configuration missing'}
$stamp=Get-Date -Format yyyyMMdd-HHmmss
$candidate=Join-Path $state ('http3-repair-'+$stamp+'.Caddyfile')
$backup=Join-Path $state ('before-http3-repair-'+$stamp+'.Caddyfile')
$utf8=New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($candidate,$after,$utf8)
$validation=Join-Path $state ('http3-repair-'+$stamp+'.validation.log')
$ErrorActionPreference='Continue'
& (Join-Path $app 'caddy.exe') validate --config $candidate --adapter caddyfile *> $validation
$valid=$LASTEXITCODE -eq 0
$ErrorActionPreference='Stop'
if(-not $valid){throw 'Candidate validation failed; running service unchanged'}
if([IO.File]::ReadAllText($file) -cne $before){throw 'Settings changed during repair; running service unchanged'}
[IO.File]::Copy($file,$backup,$false)
$result=[ordered]@{at=[DateTime]::UtcNow.ToString('o');publicUrl=$config.publicUrl;portsChanged=$false;firewallChanged=$false;backup=$backup;service='RemoteControlWebHttps';success=$false}
try{
    [IO.File]::WriteAllText($file,$after,$utf8)
    Restart-Service -Name 'RemoteControlWebHttps'
    [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
    $ready=$false
    for($n=0;$n -lt 20;$n++){
        try{
            $reply=Invoke-WebRequest -UseBasicParsing -Uri ($config.publicUrl+'/.well-known/oauth-protected-resource') -TimeoutSec 3
            $doc=$reply.Content|ConvertFrom-Json
            if($reply.StatusCode -eq 200 -and $reply.Headers['Alt-Svc'] -eq 'clear' -and $doc.resource -ceq ($config.publicUrl+'/mcp')){$ready=$true;break}
        }catch{}
        Start-Sleep -Milliseconds 250
    }
    if(-not $ready){throw 'Public metadata validation failed'}
    $nodeAfter=@(Get-NetTCPConnection -State Listen -LocalPort $config.listenPort|Select-Object -ExpandProperty OwningProcess -Unique)
    $result.oauthProcessPreserved=($nodeBefore -join ',') -ceq ($nodeAfter -join ',')
    if(-not $result.oauthProcessPreserved){throw 'OAuth process changed during repair'}
    $result.success=$true
}catch{
    [IO.File]::Copy($backup,$file,$true)
    Restart-Service -Name 'RemoteControlWebHttps'
    $result.rolledBack=$true
    $result.error=$_.Exception.Message
}finally{
    $result|ConvertTo-Json|Set-Content -LiteralPath $output -Encoding UTF8
}
if(-not $result.success){throw 'Repair failed and original HTTPS configuration restored'}
