# 설치는 관리자 PowerShell에서 명시적으로 실행한다. 빌드에서는 이 파일을 실행하지 않는다.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PublicUrl,
    [ValidateRange(1024,65535)][int]$LanPort = 5901,
    [ValidateRange(1024,65535)][int]$GatewayPort = 59332,
    [string]$CertificatePath,
    [string]$PrivateKeyPath,
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '관리자 PowerShell에서 실행하세요.' }
. (Join-Path $PSScriptRoot 'WebMcpConfig.ps1')
$uri = Get-WebMcpUri $PublicUrl $LanPort $GatewayPort
$httpsPort = $uri.Port
$PublicUrl = $uri.GetLeftPart([UriPartial]::Authority)
$appDir = Join-Path $env:ProgramFiles 'RemoteControlWebMcp'
$stateDir = Join-Path $env:ProgramData 'RemoteControlWebMcp'
$taskName = 'RemoteControlWebMcp'
$serviceName = 'RemoteControlWebHttps'
if ([IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') -ieq [IO.Path]::GetFullPath($appDir).TrimEnd('\')) { throw '설치된 실행 폴더에서 자기 자신을 덮어쓸 수 없습니다. 새 Setup에서 업데이트하세요.' }
$marker = Join-Path $appDir 'owner.json'
$service = Get-CimInstance Win32_Service -Filter "Name='RemoteControlWebHttps'"
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
$owned = Test-Path -LiteralPath $marker
if($owned) {
    $owner = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
    if($owner.product -ne 'RemoteControlWebMcp' -or $owner.directory -ine $appDir) { throw '웹 MCP 설치 소유 정보가 다릅니다.' }
} elseif ((Test-Path -LiteralPath $appDir) -or (Test-Path -LiteralPath $stateDir) -or $service -or $task) { throw '소유 정보 없는 웹 MCP 설치 흔적이 있습니다. 기존 구성을 확인하세요.' }
foreach($path in @($appDir,$stateDir)) { if((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '웹 MCP 설치/설정 경로가 연결 폴더입니다.' } }
$expectedService = '"' + (Join-Path $appDir 'caddy.exe') + '" run --config "' + (Join-Path $stateDir 'Caddyfile') + '" --adapter caddyfile'
if($service -and $service.PathName -cne $expectedService){throw 'HTTPS 서비스의 실행 경로가 다릅니다.'}
$expectedArguments = '"{0}" "{1}"' -f (Join-Path $appDir 'server.mjs'),(Join-Path $stateDir 'gateway.json')
if($task -and ($task.Actions.Count -ne 1 -or $task.Actions[0].Execute -ine (Join-Path $appDir 'node.exe') -or $task.Actions[0].Arguments -cne $expectedArguments)){throw '웹 MCP 예약 작업의 실행 경로가 다릅니다.'}
# 인증서와 포트 충돌부터 검사한다. 여기까지는 실행 중인 서비스를 멈추지 않는다.
foreach($port in @($httpsPort,$GatewayPort)) {
    foreach($listener in Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) {
        $process=Get-CimInstance Win32_Process -Filter ("ProcessId="+$listener.OwningProcess)
        $ours=($service -and $listener.OwningProcess -eq $service.ProcessId) -or
            ($task -and $process.ExecutablePath -ieq (Join-Path $appDir 'node.exe') -and $process.CommandLine -like ('*'+$expectedArguments))
        if(-not $ours){throw "TCP $port 포트는 다른 프로그램이 사용 중입니다. 다른 포트를 지정하세요. 기존 서비스는 중지하지 않았습니다."}
    }
}
$certificate=Find-WebMcpCertificate $uri.DnsSafeHost $CertificatePath $PrivateKeyPath $stateDir (Join-Path $PSScriptRoot 'node.exe') (Join-Path $PSScriptRoot 'check-certificate.mjs')
$previousConfig=$null
$previousFile=Join-Path $stateDir 'gateway.json'
if(Test-Path -LiteralPath $previousFile){$previousConfig=Get-Content -LiteralPath $previousFile -Raw|ConvertFrom-Json}
$legacyRoot=$null
$oldCaddy=Join-Path $stateDir 'Caddyfile'
if(Test-Path -LiteralPath $oldCaddy){
    $oldText=[IO.File]::ReadAllText($oldCaddy)
    if($previousConfig -and $previousConfig.legacyRoot){$legacyRoot=$previousConfig.legacyRoot}
    elseif($oldText.Contains('@originalRoot')) {
        $match=[regex]::Match($oldText,'redir @originalRoot (https://[^\s]+) 302')
        if(-not $match.Success -or -not $previousConfig -or ([Uri]$previousConfig.publicUrl).Host -ine $uri.Host){throw '기존 홈페이지 복구 경로를 자동 보존할 수 없습니다. 설치를 중단했습니다.'}
        $legacyRoot=@{Origin=([Uri]$previousConfig.publicUrl).GetLeftPart([UriPartial]::Authority);Destination=$match.Groups[1].Value}
    }
}
if($legacyRoot){
    # 같은 소유 Caddy의 443이면 홈페이지의 / 이동만 보존하고 MCP 경로를 함께 구성한다.
    # 다른 프로그램이 포트를 쓰는 경우에는 위의 사전 검사에서 이미 중단한다.
    $null=New-WebMcpCaddyConfig $uri $GatewayPort ('x'*43) $stateDir $legacyRoot
}
foreach($rule in Get-NetFirewallRule -Name 'RemoteControlWebMcp-TCP-*' -ErrorAction SilentlyContinue){
    if(($rule|Get-NetFirewallApplicationFilter).Program -ine (Join-Path $appDir 'caddy.exe')){throw '웹 MCP 방화벽 규칙의 프로그램이 다릅니다. 기존 서비스는 중지하지 않았습니다.'}
}
if($CheckOnly){Write-Output "웹 연결 사전 검사 완료: TCP $httpsPort, 인증서 만료 $($certificate.ValidUntil)";return}
$lanReady = $false
for($n=0;$n -lt 15;$n++) { if(Get-NetTCPConnection -State Listen -LocalPort $LanPort -ErrorAction SilentlyContinue){$lanReady=$true;break}; Start-Sleep -Milliseconds 500 }
if (-not $lanReady) { throw "원격제어의 LAN MCP $LanPort 포트를 먼저 켜세요." }
foreach ($name in @('node.exe','caddy.exe','server.mjs','gateway.mjs','node_modules','package-lock.json')) { if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $name))) { throw "배포 파일이 없습니다: $name" } }
# 새 폴더만 만들고 먼저 ACL을 잠근다. 인터넷 요청을 받는 중계는 LocalService로 실행한다.
function New-PrivateDirectory([string]$path,[bool]$writable) {
    [void][IO.Directory]::CreateDirectory($path)
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    foreach($sid in @('S-1-5-18','S-1-5-32-544')) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    $rights = if($writable){'Modify'}else{'ReadAndExecute'}
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-19')), $rights, 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    Set-Acl -LiteralPath $path -AclObject $acl
}
New-PrivateDirectory $appDir $false
New-PrivateDirectory $stateDir $true
if($owned){
    $backup=Join-Path $stateDir ('before-update-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-PrivateDirectory $backup $true
    foreach($file in @('Caddyfile','gateway.json')){if(Test-Path -LiteralPath (Join-Path $stateDir $file)){Copy-Item -LiteralPath (Join-Path $stateDir $file) -Destination $backup}}
}
@{product='RemoteControlWebMcp';directory=$appDir} | ConvertTo-Json | Set-Content -LiteralPath $marker -Encoding UTF8
if($service){Stop-Service -Name $serviceName}
if($task){Stop-ScheduledTask -InputObject $task}
# 정확히 우리 서비스/작업만 멈춘 뒤 포트와 실행 파일 잠금이 해제됐는지 확인한다.
for($n=0;$n -lt 30;$n++) { if(-not(Get-NetTCPConnection -State Listen -LocalPort $httpsPort,$GatewayPort -ErrorAction SilentlyContinue)){break}; Start-Sleep -Milliseconds 500 }
foreach ($port in @($httpsPort,$GatewayPort)) { if(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) { throw "TCP $port 포트를 다른 프로그램이 사용 중입니다." } }
$modules = Join-Path $appDir 'node_modules'
if(Test-Path -LiteralPath $modules) {
    $resolved = (Resolve-Path -LiteralPath $modules).ProviderPath
    if($resolved -ine ([IO.Path]::GetFullPath($appDir).TrimEnd('\')+'\node_modules')){throw '의존성 삭제 경로가 설치 폴더를 벗어납니다.'}
    if((Get-Item -LiteralPath $modules).Attributes -band [IO.FileAttributes]::ReparsePoint){throw '의존성 폴더가 연결 폴더입니다.'}
    if(Get-ChildItem -LiteralPath $modules -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }){throw '의존성 폴더 안에 연결 경로가 있습니다.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
foreach ($name in @('node.exe','caddy.exe','server.mjs','gateway.mjs','node_modules','package.json','package-lock.json','WebMcpConfig.ps1','check-certificate.mjs','Disable-WebMcp.ps1','Export-Plugin.ps1','README.md','THIRD-PARTY.txt','NODE-LICENSE.txt','CADDY-LICENSE.txt')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $appDir -Recurse }
$raw = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($raw) } finally { $rng.Dispose() }
$proxyKey = [Convert]::ToBase64String($raw).TrimEnd('=').Replace('+','-').Replace('/','_')
if(Test-Path -LiteralPath (Join-Path $stateDir 'gateway.json')) {
    $previous = Get-Content -LiteralPath (Join-Path $stateDir 'gateway.json') -Raw | ConvertFrom-Json
    if($previous.proxyKey -match '^[A-Za-z0-9_-]{43}$'){$proxyKey=$previous.proxyKey}
}
$config = @{publicUrl=$PublicUrl;listenPort=$GatewayPort;lanPort=$LanPort;proxyKey=$proxyKey}
if($legacyRoot){$config.legacyRoot=$legacyRoot}
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $stateDir 'gateway.json'),($config | ConvertTo-Json),$utf8)
$tlsDir=Join-Path $stateDir 'tls'
New-PrivateDirectory $tlsDir $true
foreach($pair in @(@($certificate.Certificate,(Join-Path $tlsDir 'server.crt')),@($certificate.Key,(Join-Path $tlsDir 'server.key')))){
    if($pair[0] -ine $pair[1]){Copy-Item -LiteralPath $pair[0] -Destination $pair[1] -Force}
}
$caddy = New-WebMcpCaddyConfig $uri $GatewayPort $proxyKey $stateDir $legacyRoot
$caddyFile = Join-Path $stateDir 'Caddyfile'
[IO.File]::WriteAllText($caddyFile,$caddy,$utf8)
$ErrorActionPreference='Continue'
& (Join-Path $appDir 'caddy.exe') validate --config $caddyFile --adapter caddyfile
$ErrorActionPreference='Stop'
if ($LASTEXITCODE -ne 0) { throw 'Caddy 설정 검사 실패. 서비스를 설치하지 않았습니다.' }
$action = New-ScheduledTaskAction -Execute (Join-Path $appDir 'node.exe') -Argument ('"{0}" "{1}"' -f (Join-Path $appDir 'server.mjs'),(Join-Path $stateDir 'gateway.json')) -WorkingDirectory $appDir
$principal = New-ScheduledTaskPrincipal -UserId 'S-1-5-19' -LogonType ServiceAccount -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger (New-ScheduledTaskTrigger -AtStartup) -Principal $principal -Settings $settings -Description 'RemoteControl OAuth MCP gateway; loopback only' -Force | Out-Null
$binPath = '"{0}" run --config "{1}" --adapter caddyfile' -f (Join-Path $appDir 'caddy.exe'),$caddyFile
if(-not $service){New-Service -Name $serviceName -BinaryPathName $binPath -DisplayName 'RemoteControl Web HTTPS' -StartupType Automatic -Credential (New-Object Management.Automation.PSCredential('NT AUTHORITY\LocalService',(New-Object Security.SecureString))) | Out-Null}
Set-Service -Name $serviceName -StartupType Automatic
& sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
if($LASTEXITCODE -ne 0){throw 'HTTPS 서비스 복구 설정 실패'}
$firewallPorts=@($httpsPort)
if($legacyRoot -and $firewallPorts -notcontains ([Uri]$legacyRoot.Origin).Port){$firewallPorts+=([Uri]$legacyRoot.Origin).Port}
foreach($rule in Get-NetFirewallRule -Name 'RemoteControlWebMcp-TCP-*' -ErrorAction SilentlyContinue){
    if(($rule|Get-NetFirewallApplicationFilter).Program -ine (Join-Path $appDir 'caddy.exe')){throw '웹 MCP 방화벽 규칙의 프로그램이 다릅니다.'}
    $rule|Remove-NetFirewallRule
}
foreach($port in $firewallPorts){New-NetFirewallRule -Name "RemoteControlWebMcp-TCP-$port" -DisplayName "RemoteControl Web TCP $port" -Direction Inbound -Action Allow -Protocol TCP -LocalPort $port -Program (Join-Path $appDir 'caddy.exe') | Out-Null}
Start-ScheduledTask -TaskName $taskName
Start-Service -Name $serviceName
for($n=0;$n -lt 20;$n++) { if(Get-NetTCPConnection -State Listen -LocalPort $GatewayPort -ErrorAction SilentlyContinue){break}; Start-Sleep -Milliseconds 500 }
if(-not(Get-NetTCPConnection -State Listen -LocalPort $GatewayPort -ErrorAction SilentlyContinue)){throw '웹 MCP 중계가 시작되지 않았습니다.'}
Write-Output "웹 MCP를 시작했습니다. 공유기 TCP $httpsPort 번호를 이 PC의 같은 포트로 연결하세요."
Write-Output "HTTPS 인증서 만료: $($certificate.ValidUntil). 갱신된 인증서를 다시 선택해 설치하세요. 이 설치는 인증서 발급용 80/443을 열지 않습니다."
Write-Output "ChatGPT 연결 주소: $PublicUrl/mcp"
Write-Output "다음 확인: HTTPS 인증서와 metadata를 외부망에서 검사한 뒤 ChatGPT에서 로그인하세요."
