# Setup의 Main/Install을 실행하지 않는다. 실제 EXE의 파일 배치 경로만 격리 폴더에서 호출한다.

$ErrorActionPreference='Stop'

$root=Split-Path -Parent $PSScriptRoot

$setup=Join-Path $root 'dist\RemoteControl_Setup.exe'

$assembly=[Reflection.Assembly]::LoadFile($setup)

$flags=[Reflection.BindingFlags]'Static,NonPublic'

$web=$assembly.GetType('RemoteControlSetup.WebMcp',$true)

$stage=$web.GetMethod('ExtractPackages',$flags)

$write=$web.GetMethod('WritePlugin',$flags)

$normalize=$web.GetMethod('NormalizeUrl',$flags)

$dest=Join-Path $root ('artifacts\setup-web-test\'+[Guid]::NewGuid().ToString('N'))

[void][IO.Directory]::CreateDirectory($dest)

$version=(Get-Item -LiteralPath $setup).VersionInfo.FileVersion

if($version -ne '1.8.15.0'){throw "예상하지 않은 Setup 버전: $version"}

$resources=$assembly.GetManifestResourceNames()

foreach($name in @('RemoteControl.exe','RemoteControl_MCP.zip','RemoteControl_Claude.mcpb','RemoteControl_WebMCP.zip')) {

    if($resources -notcontains $name){throw "Setup 리소스 누락: $name"}

    $stream=$assembly.GetManifestResourceStream($name);$sha=[Security.Cryptography.SHA256]::Create()

    try{$embedded=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$sha.Dispose();$stream.Dispose()}

    if($embedded -cne (Get-FileHash -LiteralPath (Join-Path $root ('dist\'+$name)) -Algorithm SHA256).Hash){throw "Setup 리소스가 dist와 다릅니다: $name"}

}

$stage.Invoke($null,[string[]]@($dest,'https://najhna.dyndns.org:59334')) | Out-Null

$plugin=Join-Path $dest 'plugins\RemoteControl_WebPlugin.zip'

foreach($path in @('plugins\RemoteControl_MCP.zip','plugins\RemoteControl_Claude.mcpb','plugins\RemoteControl_WebPlugin.zip','plugins\웹-ChatGPT-업로드-안내.txt','web-mcp\node.exe','web-mcp\caddy.exe','web-mcp\Install-WebMcp.ps1','web-mcp\gateway.mjs','web-mcp\auth-state.mjs','web-mcp\transfers.mjs','web-mcp\transfer-client.mjs','web-mcp\node_modules\@modelcontextprotocol\sdk\package.json')) {

    if(-not(Test-Path -LiteralPath (Join-Path $dest $path))){throw "설치 파일 배치 누락: $path"}

}

$packagedGateway = (Get-FileHash -LiteralPath (Join-Path $dest 'web-mcp\gateway.mjs') -Algorithm SHA256).Hash

if($packagedGateway -cne (Get-FileHash -LiteralPath (Join-Path $root 'web-mcp\gateway.mjs') -Algorithm SHA256).Hash){throw 'Setup에 현재 웹 로그인 수정 소스가 포함되지 않았습니다.'}

$packagedInstaller = (Get-FileHash -LiteralPath (Join-Path $dest 'web-mcp\Install-WebMcp.ps1') -Algorithm SHA256).Hash

if($packagedInstaller -cne (Get-FileHash -LiteralPath (Join-Path $root 'web-mcp\Install-WebMcp.ps1') -Algorithm SHA256).Hash){throw 'Setup에 현재 HTTPS 설치 수정 소스가 포함되지 않았습니다.'}

foreach($name in @('transfers.mjs','transfer-client.mjs','WebMcpConfig.ps1','check-certificate.mjs','server.mjs','tailscale-server.mjs','private-server.mjs','Install-PrivateMcp.ps1','Disable-PrivateMcp.ps1','Install-TailscaleMcp.ps1','Disable-TailscaleMcp.ps1')){
    if((Get-FileHash -LiteralPath (Join-Path $dest ('web-mcp\'+$name))).Hash -cne (Get-FileHash -LiteralPath (Join-Path $root ('web-mcp\'+$name))).Hash){throw "Setup에 최신 파일 누락: $name"}

}

Add-Type -AssemblyName System.IO.Compression.FileSystem

# 압축 해제만 검사하면 실제 설치의 복사 목록 누락을 놓친다. 서비스는 실행하지 않고
# 설치 스크립트의 실제 Copy-Item 블록을 격리 폴더에서 실행해 모듈 의존성을 확인한다.
foreach($installer in @('Install-WebMcp.ps1','Install-TailscaleMcp.ps1','Install-PrivateMcp.ps1')) {
    $packageRoot=Join-Path $dest 'web-mcp'
    $appDir=Join-Path $dest ($installer+'.runtime')
    [void][IO.Directory]::CreateDirectory($appDir)
    $tokens=$null;$parseErrors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $packageRoot $installer),[ref]$tokens,[ref]$parseErrors)
    if($parseErrors.Count){throw "설치 스크립트 구문 오류: $installer"}
    $copies=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.ForEachStatementAst] -and $node.Extent.Text.Contains('Copy-Item') -and $node.Extent.Text.Contains("'gateway.mjs'")},$true))
    if($copies.Count -ne 1){throw "실행 파일 복사 블록을 확인할 수 없습니다: $installer"}
    & ([scriptblock]::Create($copies[0].Extent.Text.Replace('$PSScriptRoot','$packageRoot')))
    foreach($module in @('gateway.mjs','auth-state.mjs','transfers.mjs','transfer-client.mjs')) {
        if(-not(Test-Path -LiteralPath (Join-Path $appDir $module))){throw "실제 설치 복사 누락: $installer / $module"}
    }
    Push-Location $appDir
    try { & (Join-Path $appDir 'node.exe') --input-type=module --eval "await import('./gateway.mjs'); console.log('Runtime imports passed')";if($LASTEXITCODE -ne 0){throw "설치된 모듈 로드 실패: $installer"} }
    finally { Pop-Location }
}

function Read-ZipText([string]$file,[string]$name) {

    $zip=[IO.Compression.ZipFile]::OpenRead($file)

    try{$reader=New-Object IO.StreamReader($zip.GetEntry($name).Open());try{$reader.ReadToEnd()}finally{$reader.Dispose()}}finally{$zip.Dispose()}

}

$manifest=Read-ZipText $plugin 'remotecontrol-web/plugin.json' | ConvertFrom-Json

$mcp=Read-ZipText $plugin 'remotecontrol-web/mcp.json' | ConvertFrom-Json

if($manifest.name -ne 'remotecontrol-web' -or $manifest.version -ne '1.8.15'){throw '웹 플러그인 ID/버전 불일치'}

if($mcp.mcpServers.'remotecontrol-web'.type -ne 'streamable-http' -or $mcp.mcpServers.'remotecontrol-web'.url -cne 'https://najhna.dyndns.org:59334/mcp'){throw '웹 MCP 연결 정보 불일치'}

$write.Invoke($null,[string[]]@($plugin,'https://another.example.test:59443')) | Out-Null

$custom=Read-ZipText $plugin 'remotecontrol-web/mcp.json' | ConvertFrom-Json

if($custom.mcpServers.'remotecontrol-web'.url -cne 'https://another.example.test:59443/mcp'){throw '설치 시 DDNS/포트 변경이 플러그인에 반영되지 않음'}

$write.Invoke($null,[string[]]@($plugin,'https://another.example.test:443')) | Out-Null
$standard=Read-ZipText $plugin 'remotecontrol-web/mcp.json' | ConvertFrom-Json
if($standard.mcpServers.'remotecontrol-web'.url -cne 'https://another.example.test/mcp'){throw 'Default HTTPS plugin URL contains a port'}
foreach($invalid in @('http://example.test','https://example.test/mcp','https://user:pw@example.test','https://example.test:80','https://example.test/?x=1')) {

    $rejected=$false;try{$normalize.Invoke($null,[string[]]@($invalid))|Out-Null}catch{$rejected=$true};if(-not $rejected){throw "잘못된 주소 허용: $invalid"}

}

# 실제 Setup 창을 생성해 웹 설정 컨트롤을 확인하고 검토용 이미지로 저장한다.

Add-Type -AssemblyName System.Windows.Forms

Add-Type -AssemblyName System.Drawing

[Windows.Forms.Application]::EnableVisualStyles()

$form=[Activator]::CreateInstance($assembly.GetType('RemoteControlSetup.SetupForm'),$true)

try {

    # 투명한 창으로 NumericUpDown의 실제 자식 입력칸까지 그린다. 설치는 실행하지 않는다.
    $form.Opacity=0
    $form.ShowInTaskbar=$false

    $form.StartPosition=[Windows.Forms.FormStartPosition]::Manual

    $form.Location=New-Object Drawing.Point(-30000,-30000)

    $form.Show()

    [Windows.Forms.Application]::DoEvents()

    function Get-SetupControls($parent) { foreach($child in $parent.Controls) { $child; Get-SetupControls $child } }
    $controls=@(Get-SetupControls $form)
    if(@($controls | Where-Object {$_ -is [Windows.Forms.CheckBox] -and $_.Text -like '*웹 ChatGPT*'}).Count -ne 1){throw 'Setup 웹 연결 선택 UI 누락'}
    $portControl=@($controls | Where-Object {$_.Name -eq 'WebHttpsPort'})
    if($portControl.Count -ne 1 -or $portControl[0].Minimum -ne 443 -or $portControl[0].Maximum -ne 65535 -or ($portControl[0].Value -ne 443 -and $portControl[0].Value -lt 1024)){throw 'Setup HTTPS 포트 입력 누락'}
    if(@($controls | Where-Object Name -eq 'WebDefaultHttpsPort').Count -ne 1){throw '기본 HTTPS 포트 선택 누락'}
    if(@($controls | Where-Object {$_ -is [Windows.Forms.Label] -and $_.Text -eq 'HTTPS 포트' -and $_.Height -gt 0}).Count -ne 1){throw 'HTTPS 포트 설명이 보이지 않음'}
    $bitmap=New-Object Drawing.Bitmap($form.ClientSize.Width,$form.ClientSize.Height)

    try {

        $graphics=[Drawing.Graphics]::FromImage($bitmap);try{$graphics.Clear($form.BackColor)}finally{$graphics.Dispose()}

        foreach($control in $form.Controls){$handle=$control.Handle;if($control.Width -gt 0 -and $control.Height -gt 0){$control.DrawToBitmap($bitmap,$control.Bounds)}}

        $bitmap.Save((Join-Path $dest 'setup.png'),[Drawing.Imaging.ImageFormat]::Png)

    }finally{$bitmap.Dispose()}

}finally{$form.Dispose()}

# dist에도 동일한 생성 경로로 업로드 파일을 둔다. 설치할 때는 입력한 DDNS에 맞춰 다시 생성된다.

$write.Invoke($null,[string[]]@((Join-Path $root 'dist\RemoteControl_WebPlugin.zip'),'https://najhna.dyndns.org:59334')) | Out-Null

$report=[ordered]@{Setup=$setup;Version=$version;SetupSha256=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash;EmbeddedResourcesMatch=$true;PluginAutoPlacement=$true;PluginTransport='streamable-http';PluginUrl='https://najhna.dyndns.org:59334/mcp';CustomDdnsAndPortTest=$true;Default443PluginUrl=$true;InvalidUrlsRejected=5;SetupUi=$true;FixtureDirectory=$dest;LiveServicesChanged=$false;PublicHttpsVerified=$false;ChatGptAccountConnected=$false}

$report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dest 'verification.json') -Encoding UTF8

$report | ConvertTo-Json
