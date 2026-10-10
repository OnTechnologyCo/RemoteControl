# Read-only diagnostics. Never exports proxy keys, passwords, cookies or OAuth credentials.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputFile)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$allowed=[IO.Path]::GetFullPath((Join-Path $root 'artifacts\web-registration-diagnostic')).TrimEnd('\')+'\'
$target=[IO.Path]::GetFullPath($OutputFile)
if(-not $target.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Diagnostic output must stay inside its artifact directory.'}
$report=[ordered]@{time=[DateTime]::UtcNow.ToString('o');readOnly=$true;runtimes=@()}
foreach($name in @('RemoteControlWebMcp','RemoteControlTailscaleMcp')){
    $dir=Join-Path $env:ProgramData $name
    if(-not(Test-Path -LiteralPath $dir)){continue}
    $config=Get-Content -LiteralPath (Join-Path $dir 'gateway.json') -Raw|ConvertFrom-Json
    $item=[ordered]@{name=$name;publicUrl=$config.publicUrl;listenPort=$config.listenPort;lanPort=$config.lanPort;publicPort=$config.publicPort;events=@();clients=@()}
    $data=if($name -eq 'RemoteControlTailscaleMcp'){Join-Path $dir 'data'}else{$dir}
    $events=Join-Path $data 'connection-events.jsonl'
    if(Test-Path -LiteralPath $events){
        $item.auditFile=@{modifiedUtc=(Get-Item -LiteralPath $events).LastWriteTimeUtc.ToString('o');size=(Get-Item -LiteralPath $events).Length;serviceRules=@((Get-Acl -LiteralPath $events).GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])|Where-Object {$_.IdentityReference.Value -in @('S-1-5-19','S-1-5-32-545','S-1-1-0')}|ForEach-Object {@{sid=$_.IdentityReference.Value;rights=$_.FileSystemRights.ToString();type=$_.AccessControlType.ToString()}})}
    }
    if($name -eq 'RemoteControlWebMcp'){
        $caddy=[IO.File]::ReadAllText((Join-Path $dir 'Caddyfile'))
        $proxy=[regex]::Match($caddy,'header_up X-RC-Proxy-Key ([^\s]+)')
        $item.proxy=@{keyMatches=$proxy.Success -and $proxy.Groups[1].Value -ceq $config.proxyKey;host=@([regex]::Matches($caddy,'header_up Host ([^\s]+)')|ForEach-Object {$_.Groups[1].Value});proto=@([regex]::Matches($caddy,'header_up X-Forwarded-Proto ([^\s]+)')|ForEach-Object {$_.Groups[1].Value});ip=@([regex]::Matches($caddy,'header_up X-RC-Client-IP ([^\s]+)')|ForEach-Object {$_.Groups[1].Value})}
    }
    if(Test-Path -LiteralPath $events){
        foreach($line in Get-Content -LiteralPath $events -Tail 300){
            try{$e=$line|ConvertFrom-Json;$item.events+=@{at=$e.at;stage=$e.stage;method=$e.method;status=$e.status;ms=$e.ms}}catch{}
        }
    }
    $clients=Join-Path $data 'clients.json'
    if(Test-Path -LiteralPath $clients){
        foreach($client in (Get-Content -LiteralPath $clients -Raw|ConvertFrom-Json)){
            $item.clients+=@{method=$client.token_endpoint_auth_method;issued=$client.client_id_issued_at;callbacks=@($client.redirect_uris|ForEach-Object {try{([Uri]$_).GetLeftPart([UriPartial]::Path)}catch{'invalid'}})}
        }
    }
    $report.runtimes+=,$item
}
$report.firewall=@(Get-NetFirewallRule -Name 'RemoteControlWebMcp-TCP-*' -ErrorAction SilentlyContinue|ForEach-Object{
    $port=$_|Get-NetFirewallPortFilter;$app=$_|Get-NetFirewallApplicationFilter
    @{name=$_.Name;enabled=$_.Enabled.ToString();action=$_.Action.ToString();profile=$_.Profile.ToString();protocol=$port.Protocol;localPort=$port.LocalPort;program=$app.Program}
})
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
$report|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $target -Encoding UTF8
