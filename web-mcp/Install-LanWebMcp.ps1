param([Parameter(Mandatory=$true)][string]$Address,[int]$HttpsPort=443,[int]$GatewayPort=59338,[int]$LanPort=5901,[Parameter(Mandatory=$true)][string]$AllowedNetworks,[switch]$CheckOnly)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'LanWebMcpConfig.ps1')
$uri=Get-LanWebMcpUri $Address $HttpsPort $GatewayPort $LanPort $AllowedNetworks
if(-not(Get-NetIPAddress -AddressFamily IPv4 -IPAddress $Address -ErrorAction SilentlyContinue)){throw '이 PC에 할당되지 않은 IPv4 주소입니다.'}
$appDir=Join-Path $env:ProgramFiles 'RemoteControlLanWebMcp'
$stateDir=Join-Path $env:ProgramData 'RemoteControlLanWebMcp'
Assert-LanWebMcpOwnership $appDir $stateDir
$files=@('node.exe','caddy.exe','server.mjs','gateway.mjs','auth-state.mjs','transfers.mjs','transfer-client.mjs','node_modules','package.json','package-lock.json','LanWebMcpConfig.ps1','Disable-LanWebMcp.ps1','Import-LanWebMcpCertificate.ps1','README.md','THIRD-PARTY.txt','NODE-LICENSE.txt','CADDY-LICENSE.txt')
foreach($file in $files){if(-not(Test-Path -LiteralPath (Join-Path $PSScriptRoot $file))){throw "배포 파일이 없습니다: $file"}}
$shared=Get-LanWebMcpSharedHost $HttpsPort
$owned=Test-Path -LiteralPath (Join-Path $stateDir 'owner.json')
foreach($port in @($HttpsPort,$GatewayPort)){
    foreach($listener in Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue){
        $process=Get-CimInstance Win32_Process -Filter ("ProcessId="+$listener.OwningProcess)
        $exe=if($port -eq $HttpsPort){Join-Path $appDir 'caddy.exe'}else{Join-Path $appDir 'node.exe'}
        if($shared -and $port -eq $HttpsPort -and $process.ExecutablePath -ieq (Join-Path $shared.app 'caddy.exe')){continue}
        if(-not $owned -or $process.ExecutablePath -ine $exe){throw "TCP $port 포트를 다른 프로그램이 사용합니다. 기존 연결을 중지하지 않았습니다."}
    }
}
if($CheckOnly){Write-Output ("LAN WebMCP 사전 검사 완료: "+$uri.AbsoluteUri+'mcp');return}
if(-not(Get-NetTCPConnection -State Listen -LocalPort $LanPort -ErrorAction SilentlyContinue)){throw "원격제어의 MCP $LanPort 포트를 먼저 켜고 저장하세요."}
function Protect-Directory([string]$Path,[string]$LocalRights,[bool]$Public=$false){
    [void][IO.Directory]::CreateDirectory($Path)
    $acl=New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl','ContainerInherit,ObjectInherit','None','Allow')))}
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-19')),$LocalRights,'ContainerInherit,ObjectInherit','None','Allow')))
    if($Public){$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow')))}
    Set-Acl -LiteralPath $Path -AclObject $acl
}
Protect-Directory $appDir 'ReadAndExecute'
Protect-Directory $stateDir 'ReadAndExecute'
Protect-Directory (Join-Path $stateDir 'data') 'Modify'
Protect-Directory (Join-Path $stateDir 'caddy') 'Modify'
$utf8=New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $stateDir 'owner.json'),(@{product='RemoteControlLanWebMcp';directory=$appDir}|ConvertTo-Json),$utf8)
Remove-LanWebMcpPublicSite
foreach($name in @('RemoteControlLanWebMcpHttps','RemoteControlLanWebMcpGateway')){if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){Stop-ScheduledTask -TaskName $name}}
$waitPorts=if($shared){@($GatewayPort)}else{@($HttpsPort,$GatewayPort)}
for($n=0;$n -lt 30;$n++){if(-not(Get-NetTCPConnection -State Listen -LocalPort $waitPorts -ErrorAction SilentlyContinue)){break};Start-Sleep -Milliseconds 500}
if(Get-NetTCPConnection -State Listen -LocalPort $waitPorts -ErrorAction SilentlyContinue){throw 'LAN WebMCP 포트가 아직 사용 중입니다.'}
# 예약 작업의 리스너가 닫힌 뒤에도 실행 이미지 잠금은 늦게 풀릴 수 있다.
foreach($exe in @('node.exe','caddy.exe')){
    $image=Join-Path $appDir $exe
    if(-not(Test-Path -LiteralPath $image)){continue}
    $unlocked=$false
    for($attempt=0;$attempt -lt 40;$attempt++){
        try{$probe=[IO.File]::Open($image,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$probe.Dispose();$unlocked=$true;break}
        catch [IO.IOException]{Start-Sleep -Milliseconds 250}
    }
    if(-not $unlocked){throw ("LAN 실행 파일 잠금이 해제되지 않았습니다: "+$exe)}
}
# 소유가 확인된 배포 폴더만 갱신한다. CA와 인증 상태는 보존한다.
$modules=Join-Path $appDir 'node_modules'
if(Test-Path -LiteralPath $modules){
    if((Get-Item -LiteralPath $modules).Attributes -band [IO.FileAttributes]::ReparsePoint -or (Get-ChildItem -LiteralPath $modules -Recurse -Force|Where-Object{$_.Attributes -band [IO.FileAttributes]::ReparsePoint})){throw '배포 의존성에 연결 경로가 있습니다.'}
    Remove-Item -LiteralPath $modules -Recurse -Force
}
foreach($file in $files){
    $sourceFile=Join-Path $PSScriptRoot $file;$destinationFile=Join-Path $appDir $file
    # 같은 실행 이미지는 덮어쓰지 않는다. 종료 직후 Windows 이미지 캐시 잠금을 피한다.
    if((Test-Path -LiteralPath $sourceFile -PathType Leaf) -and (Test-Path -LiteralPath $destinationFile -PathType Leaf) -and (Get-FileHash -LiteralPath $sourceFile).Hash -eq (Get-FileHash -LiteralPath $destinationFile).Hash){continue}
    Copy-Item -LiteralPath $sourceFile -Destination $appDir -Recurse -Force
}
$raw=New-Object byte[] 32;$rng=[Security.Cryptography.RandomNumberGenerator]::Create();try{$rng.GetBytes($raw)}finally{$rng.Dispose()}
$proxyKey=[Convert]::ToBase64String($raw).TrimEnd('=').Replace('+','-').Replace('/','_')
$configFile=Join-Path $stateDir 'gateway.json'
if(Test-Path -LiteralPath $configFile){$prior=Get-Content -LiteralPath $configFile -Raw|ConvertFrom-Json;if($prior.proxyKey -match '^[A-Za-z0-9_-]{43}$'){$proxyKey=$prior.proxyKey}}
$config=@{mode='lan';publicUrl=$uri.GetLeftPart([UriPartial]::Authority);listenPort=$GatewayPort;lanPort=$LanPort;proxyKey=$proxyKey;allowedNetworks=$AllowedNetworks}
# Node와 Caddy는 LocalService로 실행하며 데이터/로그만 쓰기 가능하다.
[IO.File]::WriteAllText($configFile,($config|ConvertTo-Json),$utf8)
$gatewayFile=Join-Path $stateDir 'data\gateway.json'
[IO.File]::WriteAllText($gatewayFile,($config|ConvertTo-Json),$utf8)
$caddyFile=Join-Path $stateDir 'Caddyfile'
$caddy=New-LanWebMcpCaddyConfig $uri $GatewayPort $proxyKey $stateDir
$caddy=$caddy.Replace((Join-Path $stateDir 'https.log').Replace('\','/'),(Join-Path $stateDir 'caddy\https.log').Replace('\','/'))
[IO.File]::WriteAllText($caddyFile,$caddy,$utf8)
$ErrorActionPreference='Continue'; & (Join-Path $appDir 'caddy.exe') validate --config $caddyFile --adapter caddyfile 2>$null; $code=$LASTEXITCODE;$ErrorActionPreference='Stop'
if($code -ne 0){throw 'LAN HTTPS 설정 검사에 실패했습니다.'}
$principal=New-ScheduledTaskPrincipal -UserId 'S-1-5-19' -LogonType ServiceAccount -RunLevel Limited
$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$actions=@(@('RemoteControlLanWebMcpGateway','node.exe',('"'+(Join-Path $appDir 'server.mjs')+'" "'+$gatewayFile+'"')),@('RemoteControlLanWebMcpHttps','caddy.exe',('run --config "'+$caddyFile+'" --adapter caddyfile')))
foreach($row in $actions){
    $action=New-ScheduledTaskAction -Execute (Join-Path $appDir $row[1]) -Argument $row[2] -WorkingDirectory $appDir
    Register-ScheduledTask -TaskName $row[0] -Action $action -Principal $principal -Settings $settings -Trigger (New-ScheduledTaskTrigger -AtStartup) -Description 'RemoteControl LAN WebMCP; offline HTTPS' -Force|Out-Null
}
Get-NetFirewallRule -Name 'RemoteControlLanWebMcp-TCP' -ErrorAction SilentlyContinue|Remove-NetFirewallRule
New-NetFirewallRule -Name 'RemoteControlLanWebMcp-TCP' -DisplayName 'RemoteControl LAN WebMCP HTTPS' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $HttpsPort -LocalAddress $Address -RemoteAddress @($AllowedNetworks.Split(',')|ForEach-Object{$_.Trim()}) -Program $(if($shared){Join-Path $shared.app 'caddy.exe'}else{Join-Path $appDir 'caddy.exe'}) -Profile Any|Out-Null
[IO.File]::WriteAllText((Join-Path $stateDir 'active.json'),(@{enabled=$true;shared=[bool]$shared}|ConvertTo-Json),$utf8)
if($shared){
    $base=Get-Content -LiteralPath $shared.file -Raw
    $merged=Merge-LanWebMcpSharedConfig $base $uri $GatewayPort $proxyKey $stateDir
    $candidate=Join-Path $stateDir 'shared-candidate.caddy'
    [IO.File]::WriteAllText($candidate,$merged,$utf8)
    $ErrorActionPreference='Continue'; & (Join-Path $appDir 'caddy.exe') validate --config $candidate --adapter caddyfile 2>$null; $code=$LASTEXITCODE;$ErrorActionPreference='Stop'
    if($code -ne 0){throw '443 공유 HTTPS 설정 검사가 실패했습니다. 외부 HTTPS 설정은 유지됩니다.'}
    Copy-Item -LiteralPath $shared.file -Destination ($shared.file+'.before-lan-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
    foreach($helper in @('LanWebMcpConfig.ps1','WebMcpConfig.ps1','Disable-WebMcp.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $helper) -Destination $shared.app -Force}
    [IO.File]::WriteAllText($shared.file,$merged,$utf8)
    try {Restart-Service -Name $shared.service} catch {[IO.File]::WriteAllText($shared.file,$base,$utf8);Start-Service -Name $shared.service;throw}
    Start-ScheduledTask -TaskName 'RemoteControlLanWebMcpGateway'
}else{foreach($row in $actions){Start-ScheduledTask -TaskName $row[0]}}
$rootCert=Join-Path $stateDir 'caddy\pki\authorities\local\root.crt'
for($n=0;$n -lt 60;$n++){if((Test-Path -LiteralPath $rootCert) -and (Get-NetTCPConnection -State Listen -LocalPort $HttpsPort -ErrorAction SilentlyContinue) -and (Get-NetTCPConnection -State Listen -LocalPort $GatewayPort -ErrorAction SilentlyContinue)){break};Start-Sleep -Milliseconds 500}
if(-not(Test-Path -LiteralPath $rootCert) -or -not(Get-NetTCPConnection -State Listen -LocalPort $HttpsPort -ErrorAction SilentlyContinue) -or -not(Get-NetTCPConnection -State Listen -LocalPort $GatewayPort -ErrorAction SilentlyContinue)){throw 'LAN HTTPS가 시작되지 않았습니다. 소유 예약 작업과 로그를 확인하세요.'}
$export=Join-Path $stateDir 'certificates';Protect-Directory $export 'ReadAndExecute' $true
Copy-Item -LiteralPath $rootCert -Destination (Join-Path $export 'RemoteControl-LAN-CA.crt') -Force
Copy-Item -LiteralPath (Join-Path $appDir 'Import-LanWebMcpCertificate.ps1') -Destination $export -Force
$fingerprint=(Get-FileHash -LiteralPath $rootCert -Algorithm SHA256).Hash
[IO.File]::WriteAllText((Join-Path $export 'SHA256.txt'),("RemoteControl-LAN-CA.crt SHA256: "+$fingerprint),$utf8)
Write-Output ("LAN WebMCP 주소: "+$uri.AbsoluteUri+'mcp')
Write-Output ("클라이언트 PC로 공개 CA를 복사하세요: "+$export)
Write-Output ("공개 CA SHA256: "+$fingerprint)
