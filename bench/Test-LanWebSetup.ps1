# 반사/구성/격리 파일 복사만 검사한다. 운영 서비스, 작업, 방화벽은 실행하지 않는다.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'web-mcp\LanWebMcpConfig.ps1')
$uri=Get-LanWebMcpUri '192.168.0.10' 443 59338 59331 '192.168.0.0/24'
if($uri.GetLeftPart([UriPartial]::Authority) -ne 'https://192.168.0.10'){throw '기본 443에서 포트가 노출됩니다.'}
foreach($caseValues in @(@('192.168.0.10',444,59338,59331,'192.168.0.0/24'),@('192.168.0.10',443,59331,59331,'192.168.0.0/24'),@('192.168.0.10',443,59338,59331,'0.0.0.0/0'),@('192.168.0.10',443,59338,59331,'1.2.3.4/'),@('bad.example',443,59338,59331,'192.168.0.0/24'))){$rejected=$false;try{Get-LanWebMcpUri @caseValues|Out-Null}catch{$rejected=$true};if(-not $rejected){throw '잘못된 LAN 설정을 수락했습니다.'}}
$setup=Join-Path $root 'dist\RemoteControl_SetupEngine.exe'
$assembly=[Reflection.Assembly]::LoadFile($setup)
$flags=[Reflection.BindingFlags]'Instance,NonPublic,Public'
$static=[Reflection.BindingFlags]'Static,NonPublic,Public'
$optionsType=$assembly.GetType('RemoteControlSetup.Options',$true)
$options=[Activator]::CreateInstance($optionsType,$true)
foreach($pair in @(@('LanWebMcp',$true),@('LanAddress','192.168.0.10'),@('LanNetworks','192.168.0.0/24'),@('LanHttpsPort',443),@('LanGatewayPort',59338),@('Port',5900))){$optionsType.GetField($pair[0],$flags).SetValue($options,$pair[1])}
$lan=$assembly.GetType('RemoteControlSetup.LanWebMcp',$true)
$lan.GetMethod('Validate',$static).Invoke($null,@($options))|Out-Null
if($lan.GetMethod('Origin',$static).Invoke($null,@($options)) -ne 'https://192.168.0.10'){throw 'Setup 기본 주소 오류'}
$optionsType.GetField('LanNetworks',$flags).SetValue($options,'192.168.0.0/24" -Command bad')
$rejected=$false;try{$lan.GetMethod('Validate',$static).Invoke($null,@($options))|Out-Null}catch{$rejected=$true};if(-not $rejected){throw '설정 명령 주입이 거부되지 않았습니다.'}
Add-Type -AssemblyName System.Windows.Forms
$form=[Activator]::CreateInstance($assembly.GetType('RemoteControlSetup.LanSettingsForm',$true),$true)
try{$form.Show();[Windows.Forms.Application]::DoEvents();if($form.ClientSize.Width -lt 580){throw 'LAN 설정 화면 폭 오류'};$control=$form.GetType().GetField('connection',$flags).GetValue($form);$port=$control.GetType().GetField('port',$flags).GetValue($control);if($port.Value -ne 443){throw 'LAN UI 기본 포트가 443이 아닙니다.'};$result=$control.GetType().GetField('result',$flags).GetValue($control);if($result.Text.Contains(':443')){throw 'LAN 주소에 기본 포트가 표시됩니다.'}}finally{$form.Close();$form.Dispose()}
$web=$assembly.GetType('RemoteControlSetup.WebMcp',$true)
$dest=Join-Path $root ('artifacts\lan-setup-test\'+[Guid]::NewGuid().ToString('N'));[void][IO.Directory]::CreateDirectory($dest)
$web.GetMethod('ExtractPackages',$static).Invoke($null,[string[]]@($dest,$null))|Out-Null
foreach($name in @('gateway.mjs','LanWebMcpConfig.ps1','Install-LanWebMcp.ps1','Disable-LanWebMcp.ps1','Import-LanWebMcpCertificate.ps1','WebMcpConfig.ps1','Disable-WebMcp.ps1','README.md')){if((Get-FileHash -LiteralPath (Join-Path $dest ('web-mcp\'+$name))).Hash -ne (Get-FileHash -LiteralPath (Join-Path $root ('web-mcp\'+$name))).Hash){throw ("Setup에 최신 LAN 소스가 없습니다: "+$name)}}
$packageRoot=Join-Path $dest 'web-mcp';$appDir=Join-Path $dest 'runtime';[void][IO.Directory]::CreateDirectory($appDir)
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $packageRoot 'Install-LanWebMcp.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'LAN 설치 스크립트 구문 오류'}
$assign=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq '$files'},$true));if($assign.Count -ne 1){throw 'LAN 파일 목록 없음'}
. ([scriptblock]::Create($assign[0].Extent.Text))
$copies=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.ForEachStatementAst] -and $n.Extent.Text.Contains('Copy-Item') -and $n.Extent.Text.Contains('$files')},$true));if($copies.Count -ne 1){throw 'LAN 실제 복사 블록 없음'}
& ([scriptblock]::Create($copies[0].Extent.Text.Replace('$PSScriptRoot','$packageRoot')))
foreach($file in @('node.exe','caddy.exe','gateway.mjs','LanWebMcpConfig.ps1','Disable-LanWebMcp.ps1','Import-LanWebMcpCertificate.ps1','node_modules\@modelcontextprotocol\sdk\package.json')){if(-not(Test-Path -LiteralPath (Join-Path $appDir $file))){throw "LAN 실제 설치 복사 누락: $file"}}
& (Join-Path $appDir 'node.exe') --input-type=module --eval ("await import('"+(Join-Path $appDir 'gateway.mjs').Replace('\','/').Insert(0,'file:///')+"'); console.log('LAN installed gateway imports passed')")
if($LASTEXITCODE -ne 0){throw 'LAN 설치 모듈 의존성 오류'}
'LAN Setup checks passed: default 443/no port, invalid config, UI, embedded runtime and real copy/import'
