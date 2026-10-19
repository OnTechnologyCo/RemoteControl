# 구성 생성/사전 검사만 한다. 서비스와 방화벽은 여기서 변경하지 않는다.
function Get-WebMcpUri([string]$PublicUrl,[int]$LanPort,[int]$GatewayPort) {
    $uri=[Uri]$PublicUrl
    if($uri.Scheme -ne 'https' -or $uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment -or $uri.UserInfo -or $uri.HostNameType -ne 'Dns' -or ($uri.Port -ne 443 -and $uri.Port -lt 1024)){throw '경로 없는 HTTPS DDNS 주소와 기본 포트 443 또는 전용 포트(1024~65535)를 지정하세요.'}
    if($LanPort -eq $GatewayPort -or $uri.Port -eq $LanPort -or $uri.Port -eq $GatewayPort){throw 'HTTPS, LAN MCP, 내부 중계 포트는 각각 달라야 합니다.'}
    return $uri
}
function Find-WebMcpCertificate([string]$Domain,[string]$CertificatePath,[string]$PrivateKeyPath,[string]$StateDir,[string]$Node,[string]$Checker) {
    if([bool]$CertificatePath -ne [bool]$PrivateKeyPath){throw '인증서와 개인 키를 함께 선택하세요.'}
    $pairs=@()
    if($CertificatePath){$pairs+=,@($CertificatePath,$PrivateKeyPath)}
    else {
        $pairs+=,@((Join-Path $StateDir 'tls\server.crt'),(Join-Path $StateDir 'tls\server.key'))
        $cache=Join-Path $StateDir 'certificates'
        if(Test-Path -LiteralPath $cache){
            foreach($crt in Get-ChildItem -LiteralPath $cache -Filter '*.crt' -File -Recurse){
                if($crt.BaseName -ieq $Domain){$pairs+=,@($crt.FullName,[IO.Path]::ChangeExtension($crt.FullName,'.key'))}
            }
        }
    }
    foreach($pair in $pairs){
        if(-not(Test-Path -LiteralPath $pair[0] -PathType Leaf) -or -not(Test-Path -LiteralPath $pair[1] -PathType Leaf)){continue}
        $oldPreference=$ErrorActionPreference
        $ErrorActionPreference='Continue'
        $result=& $Node $Checker $Domain $pair[0] $pair[1] 2>$null
        $ok=$LASTEXITCODE -eq 0
        $ErrorActionPreference=$oldPreference
        if($ok){return @{Certificate=[IO.Path]::GetFullPath($pair[0]);Key=[IO.Path]::GetFullPath($pair[1]);ValidUntil=($result|ConvertFrom-Json).validUntil}}
    }
    throw '이 DDNS에 맞는 유효한 HTTPS 인증서가 없습니다. 인증서(체인 포함 PEM)와 개인 키를 선택하세요. 80/443 포트를 자동으로 열지 않습니다.'
}
function New-WebMcpCaddyConfig([Uri]$Uri,[int]$GatewayPort,[string]$ProxyKey,[string]$StateDir,$LegacyRoot=$null) {
    $cert=(Join-Path $StateDir 'tls\server.crt').Replace('\','/')
    $key=(Join-Path $StateDir 'tls\server.key').Replace('\','/')
    $log=(Join-Path $StateDir 'https.log').Replace('\','/')
    $rootRedirect='';$separateRoot=''
    if($LegacyRoot){
        $legacyText=New-WebMcpLegacyRootConfig $LegacyRoot $StateDir
        if(([Uri]$LegacyRoot.Origin).Host -ine $Uri.Host){throw '기존 홈페이지 도메인과 다릅니다. 기존 서비스를 변경하지 않았습니다.'}
        if(([Uri]$LegacyRoot.Origin).Port -eq $Uri.Port){
            $rootRedirect="    @originalRoot path /`n    redir @originalRoot $(([Uri]$LegacyRoot.Destination).AbsoluteUri) 302"
        }else{$separateRoot=$legacyText}
    }
    $config=@"
{
    admin off
    auto_https off
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
    tls "$cert" "$key"
    header Strict-Transport-Security "max-age=0"
    header Alt-Svc "clear"
$rootRedirect
    request_body {
        max_size 2MB
    }
    reverse_proxy 127.0.0.1:$GatewayPort {
        header_up Host $($Uri.Authority)
        header_up X-RC-Proxy-Key $ProxyKey
        header_up X-RC-Client-IP {remote_host}
        header_up X-Forwarded-Proto https
        header_up -X-Forwarded-For
        header_up -Forwarded
        transport http {
            response_header_timeout 40s
        }
    }
}
"@
    $helper=Join-Path $PSScriptRoot 'LanWebMcpConfig.ps1'
    if(Test-Path -LiteralPath $helper){. $helper;return Add-ConfiguredLanWebMcpSite ($config+$separateRoot) $Uri.Port}
    return $config+$separateRoot
}
function New-WebMcpLegacyRootConfig($LegacyRoot,[string]$StateDir) {
    $origin=[Uri]$LegacyRoot.Origin
    $destination=[Uri]$LegacyRoot.Destination
    if($origin.Scheme -ne 'https' -or $destination.Scheme -ne 'https' -or $origin.HostNameType -ne 'Dns' -or $destination.HostNameType -ne 'Dns' -or $origin.UserInfo -or $origin.Query -or $origin.Fragment -or $origin.AbsolutePath -ne '/' -or $destination.UserInfo -or $destination.Query -or $destination.Fragment -or $destination.AbsolutePath -ne '/' -or $origin.GetLeftPart([UriPartial]::Authority) -ieq $destination.GetLeftPart([UriPartial]::Authority)) {throw '기존 홈페이지 경로가 올바르지 않습니다.'}
    $cert=(Join-Path $StateDir 'tls\server.crt').Replace('\','/')
    $key=(Join-Path $StateDir 'tls\server.key').Replace('\','/')
    return @"

$($origin.GetLeftPart([UriPartial]::Authority)) {
    tls "$cert" "$key"
    header Strict-Transport-Security "max-age=0"
    header Alt-Svc "clear"
    @originalRoot path /
    redir @originalRoot $($destination.AbsoluteUri) 302
    respond "MCP 주소가 변경되었습니다. 새 플러그인으로 연결하세요." 404
}
"@
}
