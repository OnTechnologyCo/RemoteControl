# 구성/소유권 검사는 운영 서비스나 방화벽을 변경하지 않는다.
function Get-LanWebMcpUri([string]$Address,[int]$HttpsPort,[int]$GatewayPort,[int]$LanPort,[string]$AllowedNetworks) {
    $ip=$null
    if(-not [Net.IPAddress]::TryParse($Address,[ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $ip.ToString() -ne $Address -or $Address -eq '0.0.0.0'){throw '이 PC에 할당된 IPv4 주소를 입력하세요.'}
    if($HttpsPort -ne 443 -and ($HttpsPort -lt 1024 -or $HttpsPort -gt 65535)){throw 'LAN HTTPS는 기본 443 또는 1024~65535 포트입니다.'}
    foreach($port in @($GatewayPort,$LanPort)){if($port -lt 1024 -or $port -gt 65535){throw '포트는 1024~65535 범위여야 합니다.'}}
    if(@(@($HttpsPort,$GatewayPort,$LanPort)|Select-Object -Unique).Count -ne 3){throw 'LAN HTTPS, 내부 중계, MCP 포트는 각각 달라야 합니다.'}
    $items=@($AllowedNetworks.Split(',')|ForEach-Object{$_.Trim()})
    if($items.Count -lt 1 -or $items.Count -gt 32 -or $AllowedNetworks.Length -gt 2048){throw '접속을 허용할 IPv4 대역을 입력하세요.'}
    foreach($item in $items){
        $parts=$item.Split('/');$client=$null
        if($parts.Count -gt 2 -or -not [Net.IPAddress]::TryParse($parts[0],[ref]$client) -or $client.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $client.ToString() -ne $parts[0]){throw '허용 대역은 IPv4 또는 IPv4/8~32 형식입니다.'}
        if($parts.Count -eq 2 -and ($parts[1] -notmatch '^\d{1,2}$' -or [int]$parts[1] -lt 8 -or [int]$parts[1] -gt 32)){throw '허용 CIDR은 /8~32 범위입니다.'}
    }
    return [Uri]("https://"+$Address+":"+$HttpsPort)
}
function New-LanWebMcpCaddyConfig([Uri]$Uri,[int]$GatewayPort,[string]$ProxyKey,[string]$StateDir) {
    $store=(Join-Path $StateDir 'caddy').Replace('\','/')
    $log=(Join-Path $StateDir 'https.log').Replace('\','/')
    return @"
{
    admin off
    persist_config off
    auto_https disable_redirects
    skip_install_trust
    storage file_system {
        root "$store"
    }
    servers {
        protocols h1 h2
    }
    log {
        output file "$log" {
            roll_size 5MiB
            roll_keep 3
        }
    }
}
$($Uri.GetLeftPart([UriPartial]::Authority)) {
    bind $($Uri.Host)
    tls internal
    header Alt-Svc "clear"
    reverse_proxy 127.0.0.1:$GatewayPort {
        header_up Host $($Uri.Authority)
        header_up X-RC-Proxy-Key $ProxyKey
        header_up X-RC-Client-IP {remote_host}
        header_up X-Forwarded-Proto https
        header_up -X-Forwarded-For
        header_up -Forwarded
    }
}
"@
}
function Assert-LanWebMcpOwnership([string]$AppDir,[string]$StateDir) {
    $marker=Join-Path $StateDir 'owner.json'
    $existing=(Test-Path -LiteralPath $AppDir) -or (Test-Path -LiteralPath $StateDir)
    if($existing){
        if(-not(Test-Path -LiteralPath $marker)){throw 'LAN WebMCP 폴더의 소유 표시가 없습니다.'}
        $owner=Get-Content -LiteralPath $marker -Raw|ConvertFrom-Json
        if($owner.product -ne 'RemoteControlLanWebMcp' -or $owner.directory -ine $AppDir){throw '다른 프로그램의 LAN WebMCP 폴더입니다.'}
        foreach($path in @($AppDir,$StateDir)){if((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'LAN WebMCP 폴더가 연결 경로입니다.'}}
    }
    foreach($row in @(@('RemoteControlLanWebMcpGateway','node.exe','server.mjs'),@('RemoteControlLanWebMcpHttps','caddy.exe','Caddyfile'))){
        $task=Get-ScheduledTask -TaskName $row[0] -ErrorAction SilentlyContinue
        if($task){
            $actions=@($task.Actions)
            $expected=if($row[1] -eq 'node.exe'){'"'+(Join-Path $AppDir 'server.mjs')+'" "'+(Join-Path $StateDir 'data\gateway.json')+'"'}else{'run --config "'+(Join-Path $StateDir 'Caddyfile')+'" --adapter caddyfile'}
            if(-not $existing -or $actions.Count -ne 1 -or $actions[0].Execute -ine (Join-Path $AppDir $row[1]) -or $actions[0].Arguments -ine $expected -or $task.Principal.UserId -notin @('S-1-5-19','LOCAL SERVICE','NT AUTHORITY\LOCAL SERVICE')){throw 'LAN WebMCP 예약 작업의 소유권이 다릅니다.'}
        }
    }
    foreach($rule in Get-NetFirewallRule -Name 'RemoteControlLanWebMcp-TCP' -ErrorAction SilentlyContinue){
        $program=($rule|Get-NetFirewallApplicationFilter).Program
        $valid=$existing -and $program -ieq (Join-Path $AppDir 'caddy.exe')
        if($existing -and -not $valid){
            $activeFile=Join-Path $StateDir 'active.json';$configFile=Join-Path $StateDir 'gateway.json'
            if((Test-Path -LiteralPath $activeFile) -and (Test-Path -LiteralPath $configFile)){
                $active=Get-Content -LiteralPath $activeFile -Raw|ConvertFrom-Json;$config=Get-Content -LiteralPath $configFile -Raw|ConvertFrom-Json
                if($active.shared){$hostInfo=Get-LanWebMcpSharedHost ([Uri]$config.publicUrl).Port;if($hostInfo){$valid=$program -ieq (Join-Path $hostInfo.app 'caddy.exe')}}
            }
        }
        if(-not $valid){throw 'LAN WebMCP 방화벽 규칙의 소유권이 다릅니다.'}
    }
}

function Get-LanWebMcpSharedHost([int]$Port) {
    $app=Join-Path $env:ProgramFiles 'RemoteControlWebMcp'
    $state=Join-Path $env:ProgramData 'RemoteControlWebMcp'
    $service=Get-CimInstance Win32_Service -Filter "Name='RemoteControlWebHttps'"
    if(-not $service){return $null}
    $marker=Join-Path $app 'owner.json'
    if(-not(Test-Path -LiteralPath $marker)){return $null}
    $owner=Get-Content -LiteralPath $marker -Raw|ConvertFrom-Json
    $expected='"'+(Join-Path $app 'caddy.exe')+'" run --config "'+(Join-Path $state 'Caddyfile')+'" --adapter caddyfile'
    if($owner.product -ne 'RemoteControlWebMcp' -or $owner.directory -ine $app -or $service.PathName -ine $expected -or $service.StartName -notin @('NT AUTHORITY\LocalService','NT AUTHORITY\LOCAL SERVICE')){throw '443 공유 대상 웹 HTTPS 서비스의 소유권이 다릅니다.'}
    foreach($path in @($app,$state)){if((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint){throw '공유 대상 폴더가 연결 경로입니다.'}}
    $config=Get-Content -LiteralPath (Join-Path $state 'gateway.json') -Raw|ConvertFrom-Json
    if(([Uri]$config.publicUrl).Port -ne $Port){return $null}
    return @{app=$app;state=$state;file=(Join-Path $state 'Caddyfile');service='RemoteControlWebHttps'}
}
function Remove-LanWebMcpSharedConfig([string]$Text) {
    $Text=[regex]::Replace($Text,'(?ms)^\s*# RemoteControl LAN globals BEGIN\r?\n.*?^\s*# RemoteControl LAN globals END\r?\n',"    auto_https off`n")
    return [regex]::Replace($Text,'(?ms)^\s*# RemoteControl LAN site BEGIN\r?\n.*?^\s*# RemoteControl LAN site END\r?\n?','')
}
function Merge-LanWebMcpSharedConfig([string]$Text,[Uri]$Uri,[int]$GatewayPort,[string]$ProxyKey,[string]$StateDir) {
    $base=Remove-LanWebMcpSharedConfig $Text
    if($base -notmatch '(?m)^\s*auto_https off\s*$' -or $base -match '(?m)^\s*(storage|default_sni|skip_install_trust)\b'){throw '공유 웹 HTTPS 전역 설정을 안전하게 합칠 수 없습니다.'}
    $store=(Join-Path $StateDir 'caddy').Replace('\','/')
    $globals=@"
    # RemoteControl LAN globals BEGIN
    auto_https disable_redirects
    skip_install_trust
    persist_config off
    default_sni $($Uri.Host)
    storage file_system {
        root "$store"
    }
    # RemoteControl LAN globals END
"@
    $base=[regex]::Replace($base,'(?m)^\s*auto_https off\s*$',{param($m)$globals})
    $full=New-LanWebMcpCaddyConfig $Uri $GatewayPort $ProxyKey $StateDir
    $start=$full.IndexOf($Uri.GetLeftPart([UriPartial]::Authority)+' {')
    if($start -lt 0){throw 'LAN 사이트 설정을 만들지 못했습니다.'}
    $site=$full.Substring($start)
    # 하나의 :443 리스너에서 SNI/Host로 분기한다. LAN 접근 대역은 Node가 강제한다.
    $site=[regex]::Replace($site,'(?m)^\s*bind [^\r\n]+\r?\n','')
    return $base+"`n# RemoteControl LAN site BEGIN`n"+$site+"`n# RemoteControl LAN site END`n"
}
function Add-ConfiguredLanWebMcpSite([string]$Text,[int]$Port) {
    $state=Join-Path $env:ProgramData 'RemoteControlLanWebMcp'
    $marker=Join-Path $state 'owner.json';$active=Join-Path $state 'active.json';$configFile=Join-Path $state 'gateway.json'
    if(-not(Test-Path -LiteralPath $marker) -or -not(Test-Path -LiteralPath $active) -or -not(Test-Path -LiteralPath $configFile)){return $Text}
    $owner=Get-Content -LiteralPath $marker -Raw|ConvertFrom-Json
    if($owner.product -ne 'RemoteControlLanWebMcp' -or $owner.directory -ine (Join-Path $env:ProgramFiles 'RemoteControlLanWebMcp')){throw 'LAN WebMCP 소유 정보가 다릅니다.'}
    $a=Get-Content -LiteralPath $active -Raw|ConvertFrom-Json
    $c=Get-Content -LiteralPath $configFile -Raw|ConvertFrom-Json
    if(-not $a.enabled -or ([Uri]$c.publicUrl).Port -ne $Port){return $Text}
    return Merge-LanWebMcpSharedConfig $Text ([Uri]$c.publicUrl) $c.listenPort $c.proxyKey $state
}
function Remove-LanWebMcpPublicSite {
    $state=Join-Path $env:ProgramData 'RemoteControlLanWebMcp';$active=Join-Path $state 'active.json'
    if(-not(Test-Path -LiteralPath $active)){return}
    $a=Get-Content -LiteralPath $active -Raw|ConvertFrom-Json
    if(-not $a.shared){return}
    $c=Get-Content -LiteralPath (Join-Path $state 'gateway.json') -Raw|ConvertFrom-Json
    $shared=Get-LanWebMcpSharedHost ([Uri]$c.publicUrl).Port
    if(-not $shared){return}
    $text=Get-Content -LiteralPath $shared.file -Raw
    $base=Remove-LanWebMcpSharedConfig $text
    if($text -eq $base){return}
    $utf8=New-Object Text.UTF8Encoding($false)
    $backup=$shared.file+'.before-lan-stop-'+(Get-Date -Format 'yyyyMMdd-HHmmss')
    Copy-Item -LiteralPath $shared.file -Destination $backup
    [IO.File]::WriteAllText($shared.file,$base,$utf8)
    Restart-Service -Name $shared.service
}
function Resume-LanWebMcpStandalone {
    $state=Join-Path $env:ProgramData 'RemoteControlLanWebMcp';$active=Join-Path $state 'active.json'
    if(-not(Test-Path -LiteralPath $active)){return}
    $a=Get-Content -LiteralPath $active -Raw|ConvertFrom-Json
    if(-not $a.enabled -or -not $a.shared){return}
    Assert-LanWebMcpOwnership (Join-Path $env:ProgramFiles 'RemoteControlLanWebMcp') $state
    Get-NetFirewallRule -Name 'RemoteControlLanWebMcp-TCP' -ErrorAction SilentlyContinue|Get-NetFirewallApplicationFilter|Set-NetFirewallApplicationFilter -Program (Join-Path $env:ProgramFiles 'RemoteControlLanWebMcp\caddy.exe')|Out-Null
    $a.shared=$false
    [IO.File]::WriteAllText($active,($a|ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
    Start-ScheduledTask -TaskName 'RemoteControlLanWebMcpHttps'
}

function Get-LanWebMcpActive([int]$Port) {
    $state=Join-Path $env:ProgramData 'RemoteControlLanWebMcp';$active=Join-Path $state 'active.json';$configFile=Join-Path $state 'gateway.json'
    if(-not(Test-Path -LiteralPath $active) -or -not(Test-Path -LiteralPath $configFile)){return $null}
    $a=Get-Content -LiteralPath $active -Raw|ConvertFrom-Json;$c=Get-Content -LiteralPath $configFile -Raw|ConvertFrom-Json
    if(-not $a.enabled -or ([Uri]$c.publicUrl).Port -ne $Port){return $null}
    $app=Join-Path $env:ProgramFiles 'RemoteControlLanWebMcp';Assert-LanWebMcpOwnership $app $state
    return @{app=$app;state=$state;active=$a;file=$active}
}
