# 구성/소유권 검사는 운영 서비스나 방화벽을 변경하지 않는다.
function Get-LanWebMcpUri([string]$Address,[int]$HttpsPort,[int]$GatewayPort,[int]$LanPort,[string]$AllowedNetworks) {
    $ip=$null
    if(-not [Net.IPAddress]::TryParse($Address,[ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $ip.ToString() -ne $Address){throw '이 PC에 할당된 IPv4 주소를 입력하세요.'}
    $octets=$ip.GetAddressBytes()
    if($octets[0] -eq 0 -or $octets[0] -eq 127 -or $octets[0] -ge 224 -or ($octets[0] -eq 169 -and $octets[1] -eq 254)){throw '루프백, 자동 사설 주소 및 멀티캐스트 주소는 LAN HTTPS 주소로 사용할 수 없습니다.'}
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
function Get-LanWebMcpUris([string]$Address,[int]$HttpsPort,[int]$GatewayPort,[int]$LanPort,[string]$AllowedNetworks,[string]$AdditionalAddresses='') {
    if($AdditionalAddresses.Length -gt 2048){throw '추가 IPv4 주소는 최대 32개 주소 범위 안에서 입력하세요.'}
    $addresses=@($Address.Trim())
    if(-not [string]::IsNullOrWhiteSpace($AdditionalAddresses)){$addresses+=@($AdditionalAddresses.Split(',')|ForEach-Object{$_.Trim()})}
    $uris=@();$seen=@{}
    foreach($item in $addresses){
        $candidate=Get-LanWebMcpUri $item $HttpsPort $GatewayPort $LanPort $AllowedNetworks
        if(-not $seen.ContainsKey($candidate.Host)){$seen[$candidate.Host]=$true;$uris+=,$candidate}
    }
    if($uris.Count -gt 32){throw '기본 주소를 포함하여 최대 32개 IPv4 주소를 사용할 수 있습니다.'}
    return $uris
}
function Get-LanWebMcpSiteUris([Uri]$Uri,[string[]]$PublicUrls=@()) {
    $uris=@();$seen=@{}
    foreach($value in @($Uri.AbsoluteUri)+@($PublicUrls)){
        $site=[Uri]$value
        if($site.Scheme -ne 'https' -or $site.HostNameType -ne 'IPv4' -or $site.Port -ne $Uri.Port -or $site.AbsolutePath -ne '/' -or $site.Query -or $site.Fragment -or $site.UserInfo){throw 'LAN HTTPS 주소 목록의 형식이나 포트가 다릅니다.'}
        if(-not $seen.ContainsKey($site.Host)){$seen[$site.Host]=$true;$uris+=,$site}
    }
    if($uris.Count -gt 32){throw 'LAN HTTPS 주소는 최대 32개입니다.'}
    return $uris
}
function New-LanWebMcpCaddySites([Uri]$Uri,[int]$GatewayPort,[string]$ProxyKey,[string]$StateDir,[string[]]$PublicUrls=@(),[string]$AllowedNetworks='') {
    $uris=@(Get-LanWebMcpSiteUris $Uri $PublicUrls)
    # 이전 호출 형식도 CA 다운로드를 아무 주소에나 열지 않는다. 설치/갱신은 저장된 CIDR을 전달한다.
    if([string]::IsNullOrWhiteSpace($AllowedNetworks)){$AllowedNetworks=(@($uris|ForEach-Object{$_.Host+'/32'}) -join ',')}
    $networks=@($AllowedNetworks.Split(',')|ForEach-Object{$_.Trim()})
    foreach($item in $networks){
        $parts=$item.Split('/');$ip=$null
        if($parts.Count -gt 2 -or -not [Net.IPAddress]::TryParse($parts[0],[ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $ip.ToString() -ne $parts[0] -or ($parts.Count -eq 2 -and ($parts[1] -notmatch '^\d{1,2}$' -or [int]$parts[1] -lt 8 -or [int]$parts[1] -gt 32))){throw 'LAN HTTPS 접속 허용 대역이 올바르지 않습니다.'}
    }
    $publicCa=(Join-Path $StateDir 'certificates').Replace('\','/')
    $sites=@()
    foreach($site in $uris){$sites+=,@"
$($site.GetLeftPart([UriPartial]::Authority)) {
    bind $($site.Host)
    tls internal
    header Alt-Svc "clear"
    route {
        @lanDenied not remote_ip $($networks -join ' ')
        respond @lanDenied "LAN client address denied" 403
        @lanPublicCa {
            path /ca/RemoteControl-LAN-CA.crt
            method GET HEAD
        }
        handle @lanPublicCa {
            # 한 공개 파일만 고정 경로로 내보내며 인증서 저장소나 개인 키는 라우팅하지 않는다.
            root * "$publicCa"
            rewrite * /RemoteControl-LAN-CA.crt
            header Content-Type "application/x-pem-file"
            header Content-Disposition "attachment; filename=RemoteControl-LAN-CA.crt"
            file_server
        }
        reverse_proxy 127.0.0.1:$GatewayPort {
            header_up Host $($site.Authority)
            header_up X-RC-Proxy-Key $ProxyKey
            header_up X-RC-Client-IP {remote_host}
            header_up X-Forwarded-Proto https
            header_up -X-Forwarded-For
            header_up -Forwarded
        }
    }
}
"@
    }
    return $sites -join "`n"
}
function New-LanWebMcpCaddyConfig([Uri]$Uri,[int]$GatewayPort,[string]$ProxyKey,[string]$StateDir,[string[]]$PublicUrls=@(),[string]$AllowedNetworks='') {
    $store=(Join-Path $StateDir 'caddy').Replace('\','/')
    $log=(Join-Path $StateDir 'https.log').Replace('\','/')
    $sites=New-LanWebMcpCaddySites $Uri $GatewayPort $ProxyKey $StateDir $PublicUrls $AllowedNetworks
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
$sites
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
function Merge-LanWebMcpSharedConfig([string]$Text,[Uri]$Uri,[int]$GatewayPort,[string]$ProxyKey,[string]$StateDir,[string[]]$PublicUrls=@(),[string]$AllowedNetworks='') {
    $base=Remove-LanWebMcpSharedConfig $Text
    if($base -notmatch '(?m)^\s*auto_https off\s*$' -or $base -match '(?m)^\s*(storage|default_sni|skip_install_trust)\b'){throw '공유 웹 HTTPS 전역 설정을 안전하게 합칠 수 없습니다.'}
    $store=(Join-Path $StateDir 'caddy').Replace('\','/')
    $globals=@"
    # RemoteControl LAN globals BEGIN
    auto_https disable_redirects
    skip_install_trust
    persist_config off
    storage file_system {
        root "$store"
    }
    # RemoteControl LAN globals END
"@
    $base=[regex]::Replace($base,'(?m)^\s*auto_https off\s*$',{param($m)$globals})
    $site=New-LanWebMcpCaddySites $Uri $GatewayPort $ProxyKey $StateDir $PublicUrls $AllowedNetworks
    # IP 접속에는 SNI가 없을 수 있다. CertMagic이 연결의 실제 local IP 인증서를 고르게 하며
    # default_sni를 기본 IP에 고정하지 않는다. 각 IP의 SAN 인증서와 기존 CA 자동 갱신을 유지한다.
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
    $urls=@();if($c.publicUrls){$urls=@($c.publicUrls)}
    return Merge-LanWebMcpSharedConfig $Text ([Uri]$c.publicUrl) $c.listenPort $c.proxyKey $state $urls $c.allowedNetworks
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
