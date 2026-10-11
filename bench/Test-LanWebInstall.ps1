# 실제 설치 스크립트/Node/Caddy를 격리 폴더에서 실행한다.
# 예약 작업·방화벽·서비스 cmdlet은 메모리 fixture로 치환하여 운영 상태를 바꾸지 않는다.
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
$root=Split-Path -Parent $PSScriptRoot
$work=Join-Path $root ('artifacts\lan-install-test\'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
$global:LanFixtureFixtureApp=Join-Path $work 'ProgramFiles';$global:LanFixtureFixtureData=Join-Path $work 'ProgramData'
$package=Join-Path $work 'package';[void][IO.Directory]::CreateDirectory($package)
$utf8=New-Object Text.UTF8Encoding($true)
foreach($file in Get-ChildItem -LiteralPath (Join-Path $root 'web-mcp') -Filter '*.ps1'){
    $text=[IO.File]::ReadAllText($file.FullName).Replace('$env:ProgramFiles',("'"+$global:LanFixtureFixtureApp+"'")).Replace('$env:ProgramData',("'"+$global:LanFixtureFixtureData+"'"))
    [IO.File]::WriteAllText((Join-Path $package $file.Name),$text,$utf8)
}
foreach($name in @('node.exe','caddy.exe','node_modules','THIRD-PARTY.txt','NODE-LICENSE.txt','CADDY-LICENSE.txt')){Copy-Item -LiteralPath (Join-Path $root ('dist\web-mcp\'+$name)) -Destination $package -Recurse}
foreach($name in @('server.mjs','gateway.mjs','auth-state.mjs','transfers.mjs','transfer-client.mjs','package.json','package-lock.json','README.md')){Copy-Item -LiteralPath (Join-Path $root ('web-mcp\'+$name)) -Destination $package}
$global:LanFixtureTasks=@{};$global:LanFixtureProcesses=@{};$global:LanFixtureRule=$null;$global:LanFixturePublicService=$null
function Get-ScheduledTask {param($TaskName,$ErrorAction) if($global:LanFixtureTasks.ContainsKey($TaskName)){$global:LanFixtureTasks[$TaskName]}}
function Register-ScheduledTask {param($TaskName,$Action,$Principal,$Settings,$Trigger,$Description,[switch]$Force) $global:LanFixtureTasks[$TaskName]=[PSCustomObject]@{Actions=@($Action);Principal=$Principal}}
function Stop-ScheduledTask {param($TaskName) if($global:LanFixtureProcesses.ContainsKey($TaskName)){$p=$global:LanFixtureProcesses[$TaskName];if(-not $p.HasExited){$p.Kill();$p.WaitForExit()};$p.Dispose();$global:LanFixtureProcesses.Remove($TaskName)}}
function Start-ScheduledTask {param($TaskName) $a=$global:LanFixtureTasks[$TaskName].Actions[0];$psi=New-Object Diagnostics.ProcessStartInfo;$psi.FileName=$a.Execute;$psi.Arguments=$a.Arguments;$psi.WorkingDirectory=$package;$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.RedirectStandardError=$true;$psi.RedirectStandardOutput=$true;$p=[Diagnostics.Process]::Start($psi);$global:LanFixtureProcesses[$TaskName]=$p}
function Unregister-ScheduledTask {param($TaskName,$Confirm) $global:LanFixtureTasks.Remove($TaskName)}
function Get-NetFirewallRule {param($Name,$ErrorAction) if($Name -eq 'RemoteControlLanWebMcp-TCP' -and $global:LanFixtureRule){$global:LanFixtureRule}}
function Get-NetFirewallApplicationFilter {param([Parameter(ValueFromPipeline=$true)]$InputObject) process{$InputObject}}
function Set-NetFirewallApplicationFilter {param([Parameter(ValueFromPipeline=$true)]$InputObject,$Program) process{$global:LanFixtureRule.Program=$Program}}
function Remove-NetFirewallRule {param([Parameter(ValueFromPipeline=$true)]$InputObject) process{$global:LanFixtureRule=$null}}
function New-NetFirewallRule {param($Name,$DisplayName,$Direction,$Action,$Protocol,$LocalPort,$LocalAddress,$RemoteAddress,$Program,$Profile) $global:LanFixtureRule=[PSCustomObject]@{Program=$Program;Port=$LocalPort;Address=$LocalAddress;Clients=$RemoteAddress}}
function Get-CimInstance {param($ClassName,$Filter) if($ClassName -eq 'Win32_Service'){$global:LanFixturePublicService}else{CimCmdlets\Get-CimInstance -ClassName $ClassName -Filter $Filter}}
function Restart-Service {param($Name) Stop-ScheduledTask 'PublicFixture';Start-ScheduledTask 'PublicFixture';$global:LanFixturePublicService.ProcessId=$global:LanFixtureProcesses.PublicFixture.Id}
function Get-FreePort { $listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Any,0);$listener.Start();try{$listener.LocalEndpoint.Port}finally{$listener.Stop()} }
$address=(Get-NetIPAddress -AddressFamily IPv4|Where-Object{$_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*'}|Select-Object -First 1).IPAddress
$backend=Get-FreePort;$listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,$backend);$listener.Start()
try{
    $https=Get-FreePort;$gateway=Get-FreePort
    & (Join-Path $package 'Install-LanWebMcp.ps1') -Address $address -HttpsPort $https -GatewayPort $gateway -LanPort $backend -AllowedNetworks ($address+'/32') -CheckOnly
    if($global:LanFixtureTasks.Count -or $global:LanFixtureRule){throw '사전 검사에서 상태를 변경했습니다.'}
    & (Join-Path $package 'Install-LanWebMcp.ps1') -Address $address -HttpsPort $https -GatewayPort $gateway -LanPort $backend -AllowedNetworks ($address+'/32')
    $state=Join-Path $global:LanFixtureFixtureData 'RemoteControlLanWebMcp';$app=Join-Path $global:LanFixtureFixtureApp 'RemoteControlLanWebMcp'
    if($global:LanFixtureTasks.Count -ne 2 -or -not(Test-Path -LiteralPath (Join-Path $state 'certificates\RemoteControl-LAN-CA.crt'))){throw 'LAN 실제 설치 경로가 준비되지 않았습니다.'}
    $caBefore=(Get-FileHash -LiteralPath (Join-Path $state 'certificates\RemoteControl-LAN-CA.crt')).Hash
    $configBefore=Get-Content -LiteralPath (Join-Path $state 'gateway.json') -Raw|ConvertFrom-Json
    foreach($p in $global:LanFixtureProcesses.Values){if($p.HasExited){throw ('LAN 설치 프로세스 종료: '+$p.StandardError.ReadToEnd())}}
    & (Join-Path $package 'Install-LanWebMcp.ps1') -Address $address -HttpsPort $https -GatewayPort $gateway -LanPort $backend -AllowedNetworks ($address+'/32')
    if((Get-FileHash -LiteralPath (Join-Path $state 'certificates\RemoteControl-LAN-CA.crt')).Hash -ne $caBefore){throw 'LAN 갱신에서 CA가 변경됐습니다.'}
    $updated=Get-Content -LiteralPath (Join-Path $state 'gateway.json') -Raw|ConvertFrom-Json
    if($updated.proxyKey -ne $configBefore.proxyKey){throw 'LAN 갱신에서 인증 키가 변경됐습니다.'}
    & (Join-Path $app 'Disable-LanWebMcp.ps1')
    if($global:LanFixtureTasks.Count -or $global:LanFixtureRule){throw 'LAN 중지에서 소유 작업/방화벽이 남았습니다.'}
    # 별도 공개 HTTPS 서비스의 동일 포트에 실제 도메인/IP 설정을 합친다.
    $publicApp=Join-Path $global:LanFixtureFixtureApp 'RemoteControlWebMcp';$publicState=Join-Path $global:LanFixtureFixtureData 'RemoteControlWebMcp'
    [void][IO.Directory]::CreateDirectory($publicApp);[void][IO.Directory]::CreateDirectory($publicState)
    Copy-Item -LiteralPath (Join-Path $package 'caddy.exe') -Destination $publicApp
    @{product='RemoteControlWebMcp';directory=$publicApp}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $publicApp 'owner.json') -Encoding UTF8
    @{publicUrl=('https://external.fixture.test:'+$https)}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $publicState 'gateway.json') -Encoding UTF8
    $publicConfig=Join-Path $publicState 'Caddyfile'
    [IO.File]::WriteAllText($publicConfig,("{`nadmin off`npersist_config off`nauto_https off`n}`nhttps://external.fixture.test:"+$https+" {`ntls internal`nrespond fixture`n}`n"),$utf8)
    $action=New-ScheduledTaskAction -Execute (Join-Path $publicApp 'caddy.exe') -Argument ('run --config "'+$publicConfig+'" --adapter caddyfile')
    $global:LanFixtureTasks.PublicFixture=[PSCustomObject]@{Actions=@($action)}
    # 초기 공개 fixture도 시스템 신뢰 저장소를 건드리지 않도록 JSON에서 명시한다.
    $ErrorActionPreference='Continue'
    $jsonText=& (Join-Path $package 'caddy.exe') adapt --config $publicConfig --adapter caddyfile 2>$null
    $code=$LASTEXITCODE;$ErrorActionPreference='Stop'
    if($code -ne 0){throw '공개 fixture 구성 변환 실패'}
    $publicJson=$jsonText|ConvertFrom-Json
    $publicJson.apps|Add-Member -MemberType NoteProperty -Name pki -Value ([PSCustomObject]@{certificate_authorities=[PSCustomObject]@{local=[PSCustomObject]@{install_trust=$false}}}) -Force
    $publicJson|Add-Member -MemberType NoteProperty -Name storage -Value ([PSCustomObject]@{module='file_system';root=(Join-Path $publicState 'bootstrap-storage')}) -Force
    $safeJson=Join-Path $publicState 'fixture-safe.json';[IO.File]::WriteAllText($safeJson,($publicJson|ConvertTo-Json -Depth 40),(New-Object Text.UTF8Encoding($false)))
    $originalArguments=$action.Arguments;$action.Arguments='run --config "'+$safeJson+'"'
    Start-ScheduledTask 'PublicFixture'
    $action.Arguments=$originalArguments
    $global:LanFixturePublicService=[PSCustomObject]@{PathName=('"'+(Join-Path $publicApp 'caddy.exe')+'" '+$action.Arguments);StartName='NT AUTHORITY\LocalService';ProcessId=$global:LanFixtureProcesses.PublicFixture.Id}
    for($i=0;$i -lt 50;$i++){if(Get-NetTCPConnection -State Listen -LocalPort $https -ErrorAction SilentlyContinue){break};Start-Sleep -Milliseconds 100}
    & (Join-Path $package 'Install-LanWebMcp.ps1') -Address $address -HttpsPort $https -GatewayPort $gateway -LanPort $backend -AllowedNetworks ($address+'/32')
    if(-not (Get-Content -LiteralPath (Join-Path $state 'active.json') -Raw|ConvertFrom-Json).shared){throw '동일 HTTPS 포트를 공유하지 않았습니다.'}
    if($global:LanFixtureProcesses.ContainsKey('RemoteControlLanWebMcpHttps')){throw '공유 443에서 중복 HTTPS 프로세스를 시작했습니다.'}
    if($global:LanFixtureRule.Program -ine (Join-Path $publicApp 'caddy.exe')){throw '공유 방화벽 프로그램 경로 오류'}
    & (Join-Path $app 'Disable-LanWebMcp.ps1')
    if([IO.File]::ReadAllText($publicConfig).Contains('LAN site BEGIN')){throw 'LAN 중지에서 공개 사이트 구성이 남았습니다.'}
    if($global:LanFixtureProcesses.PublicFixture.HasExited){throw 'LAN 중지에서 공개 HTTPS가 중단됐습니다.'}
    'LAN installer integration passed: preflight, real startup, CA/key preservation, owned stop, shared HTTPS and external preservation'
}finally{
    foreach($name in @($global:LanFixtureProcesses.Keys)){Stop-ScheduledTask $name}
    $listener.Stop()
}
