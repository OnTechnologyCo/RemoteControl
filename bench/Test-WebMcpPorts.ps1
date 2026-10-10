# npm test가 만든 격리 인증서를 사용한다. 실제 서비스/인증서/방화벽은 수정하지 않는다.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
. (Join-Path $root 'web-mcp\WebMcpConfig.ps1')
$state=Join-Path $root ('artifacts\web-port-test\'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory((Join-Path $state 'tls'))
$cert=Get-ChildItem -Path (Join-Path $root 'artifacts\web-mcp-test\browser-*\certs\certificates\local\localhost\localhost.crt') | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if(-not $cert){throw 'web-mcp에서 npm test를 먼저 실행하세요.'}
$key=[IO.Path]::ChangeExtension($cert.FullName,'.key')
$node=(Get-Command node.exe).Source
$checker=Join-Path $root 'web-mcp\check-certificate.mjs'
$validated=Find-WebMcpCertificate 'localhost' $cert.FullName $key $state $node $checker
$rejected=0
foreach($url in @('https://example.test:444','https://example.test:80','http://example.test:59334','https://example.test:59331','https://example.test:59332','https://example.test:59334/mcp')){
    try{$null=Get-WebMcpUri $url 59331 59332;throw 'Unexpected acceptance'}catch{if($_.Exception.Message -eq 'Unexpected acceptance'){throw};$rejected++}
}
$standard=Get-WebMcpUri 'https://localhost:443' 59331 59332
if($standard.Port -ne 443 -or $standard.GetLeftPart([UriPartial]::Authority) -ne 'https://localhost'){throw 'Default HTTPS port rejected or not normalized'}
foreach($pair in @(@('other.test',$cert.FullName,$key),@('localhost',$cert.FullName,(Join-Path $cert.Directory.Parent.Parent.Parent.FullName 'pki\authorities\local\root.key')))){
    try{$null=Find-WebMcpCertificate $pair[0] $pair[1] $pair[2] $state $node $checker;throw 'Unexpected certificate acceptance'}catch{if($_.Exception.Message -eq 'Unexpected certificate acceptance'){throw};$rejected++}
}
Copy-Item -LiteralPath $cert.FullName -Destination (Join-Path $state 'tls\server.crt')
Copy-Item -LiteralPath $key -Destination (Join-Path $state 'tls\server.key')
$listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0)
$listener.Start();$port=$listener.LocalEndpoint.Port;$listener.Stop()
$uri=Get-WebMcpUri ('https://localhost:'+$port) 59331 59332
$config=New-WebMcpCaddyConfig $uri 59332 ('x'*43) $state
$file=Join-Path $state 'Caddyfile'
$utf8=New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($file,$config,$utf8)
$caddy=Join-Path $root 'artifacts\web-mcp-downloads\caddy-2.11.7\caddy.exe'
$ErrorActionPreference='Continue'
$adapted=& $caddy adapt --config $file --adapter caddyfile 2>(Join-Path $state 'adapt.log')
$exit=$LASTEXITCODE
$ErrorActionPreference='Stop'
if($exit -ne 0){throw 'Caddy adapt failed'}
$json=$adapted|ConvertFrom-Json
$servers=@($json.apps.http.servers.PSObject.Properties.Value)
if($servers.Count -ne 1 -or $servers[0].listen.Count -ne 1 -or $servers[0].listen[0] -ne (':'+$port) -or -not $servers[0].automatic_https.disable){throw 'Unexpected extra port or automatic HTTPS'}
if(-not $config.Contains('header_up Host localhost:'+$port)){throw 'Custom port missing in upstream Host'}
# 실제 Caddy가 생성 설정으로 기동하고 지정 포트만 여는지도 검사한다.
$process=Start-Process -FilePath $caddy -ArgumentList ('run --config "'+$file+'" --adapter caddyfile') -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $state 'run.log')
try {
    for($n=0;$n -lt 30;$n++){if(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue){break};Start-Sleep -Milliseconds 100}
    $ports=@(Get-NetTCPConnection -State Listen -OwningProcess $process.Id -ErrorAction SilentlyContinue | Select-Object -ExpandProperty LocalPort -Unique)
    if($ports.Count -ne 1 -or $ports[0] -ne $port){throw 'Caddy opened unexpected TCP ports'}
}finally{if(-not $process.HasExited){$process.Kill();$process.WaitForExit()}}
$legacy=New-WebMcpLegacyRootConfig @{Origin='https://localhost';Destination='https://localhost:9330/'} $state
[IO.File]::WriteAllText($file,($config+$legacy),$utf8)
$ErrorActionPreference='Continue'
$legacyJson=& $caddy adapt --config $file --adapter caddyfile 2>(Join-Path $state 'legacy-adapt.log')
$exit=$LASTEXITCODE
$ErrorActionPreference='Stop'
if($exit -ne 0){throw 'Preserved homepage config failed'}
$legacyPorts=@(($legacyJson|ConvertFrom-Json).apps.http.servers.PSObject.Properties.Value.listen)
if($legacyPorts -notcontains ':443' -or $legacyPorts -notcontains (':'+$port) -or $legacyPorts -contains ':80'){throw 'Existing homepage preservation failed'}
& $node (Join-Path $PSScriptRoot 'web-default-port.test.mjs') $state
if($LASTEXITCODE -ne 0){throw 'Default port shared homepage/MCP routing failed'}
$report=@{CustomPort=$port;OnlySelectedPortOpened=$true;CommonPortsNotOpened=$true;Default443Accepted=$true;SharedHomepageAndMcpRouting=$true;CertificateAndKeyValidated=$true;RejectedCases=$rejected;ExistingHomepageConfigPreserved=$true;LiveServicesChanged=$false;Directory=$state}
$report|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $state 'result.json') -Encoding UTF8
$report|ConvertTo-Json
