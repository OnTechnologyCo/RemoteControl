# 사용법: 빌드_setup.ps1                       본체 + 설치 파일, 항상 dist에 생성
#         빌드_setup.ps1 -IncludeTailscaleKey   기존 Tailscale 키·알림 설정 포함
#         빌드_setup.ps1 -NoKey                기본 동작과 같음 (기존 명령 호환)
[CmdletBinding()]
param([switch]$NoKey, [switch]$IncludeTailscaleKey)

# 설치 파일 만들기.
# 본체(RemoteControl.exe)를 설치 관리자 안에 넣는다. 배포는 이 한 파일이면 끝.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$buildDir = Join-Path $root 'dist'
$app = Join-Path $buildDir 'RemoteControl.exe'
$ico = Join-Path $root 'remotecontrol.ico'
$out = Join-Path $buildDir 'RemoteControl_Setup.exe'

if ($NoKey -and $IncludeTailscaleKey) { throw "-NoKey와 -IncludeTailscaleKey는 함께 지정할 수 없습니다." }
$NoKey = -not $IncludeTailscaleKey

# 오래된 exe를 포장하는 일을 막기 위해 setup을 만들 때 항상 현재 소스부터 빌드한다.
& (Join-Path $root '빌드_tsproxy.ps1')
& (Join-Path $root '빌드.ps1')
& (Join-Path $root '빌드_mcp.ps1')
& (Join-Path $root '빌드_webmcp.ps1')

# 설치 창에 표시하는 버전과 실제로 담는 본체 버전이 다르면 배포하지 않는다.
$setupSource = Get-Content -LiteralPath (Join-Path $root 'Setup.cs') -Raw -Encoding UTF8
$versionMatch = [regex]::Match($setupSource, 'const string Version = "([0-9.]+)"')
$appVersion = (Get-Item -LiteralPath $app).VersionInfo.FileVersion
if (-not $versionMatch.Success -or $appVersion -ne ($versionMatch.Groups[1].Value + '.0')) {
    throw "설치 관리자와 본체 버전이 다릅니다. Setup.cs와 src/Program.cs의 Version을 확인하세요."
}

# 관리자 권한을 요구하는 매니페스트. 서비스 등록과 Program Files 쓰기에 필요하다.
$man = Join-Path $env:TEMP "rc_setup.manifest"
@'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <assemblyIdentity version="1.0.0.0" name="RemoteControl.Setup" type="win32"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="requireAdministrator" uiAccess="false"/>
      </requestedPrivileges>
    </security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/>
      <supportedOS Id="{1f676c76-80e1-4239-95bb-83d0f6d0da78}"/>
    </application>
  </compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
    </windowsSettings>
  </application>
</assembly>
'@ | Set-Content -Path $man -Encoding UTF8

# 비밀을 함께 넣을지 정한다 (Tailscale 키 + 알림 받을 곳).
# 넣으면 새 PC 에서 다시 칠 필요가 없지만, 설치 파일 안에 그대로 들어간다.
# 그 파일은 남에게 주면 안 된다. 남 줄 것은 -NoKey 로 만든다.
$presetArg = @()

# 기본 빌드는 설정 파일 쪽(Tailscale 키, chat_id)을 포함하지 않는다.
# 텔레그램 봇 토큰은 src\Notify.cs 에 박혀 있어 exe 안에 그대로 들어간다.
# 받은 사람은 그 봇으로 글을 보낼 수 있다 (곤란해지면 @BotFather /revoke 로 갈면 된다).
if ($NoKey) { "봇 토큰은 프로그램에 박혀 있어 이 파일에도 들어갑니다." }

if (-not $NoKey) {
    Add-Type -AssemblyName System.Security

    # 설정은 공용 폴더로 옮겼다. 옛 위치도 한 번 본다.
    $cfg = Join-Path $env:ProgramData "RemoteControl\settings.txt"
    if (-not (Test-Path $cfg)) { $cfg = Join-Path $root "settings.txt" }
    if (-not (Test-Path $cfg)) { throw "설정 파일을 찾지 못했습니다: $cfg" }

    $conf = Get-Content $cfg -Encoding UTF8

    # DPAPI 로 봉인된 값을 푼다.
    # 내 계정 몫이 먼저다. 이 PC 가 설치 파일로 값을 받은 경우에는 기계용 몫만
    # 있으므로 그쪽으로 넘어간다. 안 그러면 "분명히 넣었는데 안 실린다" 가 된다.
    function Unseal([string]$name) {
        foreach ($pair in @(@("$name=", "CurrentUser"), @("${name}m=", "LocalMachine"))) {
            # [string] 로 굳혀야 한다. 찾은 줄이 없으면 -replace 가 빈 배열을 내놓는데,
            # 배열에는 .Trim() 도 없고 FromBase64String 도 못 먹는다. 실제로 터졌다.
            $enc = [string](($conf | Where-Object { $_ -like ($pair[0] + "*") }) -replace ("^" + [regex]::Escape($pair[0])), "")
            if (-not $enc) { continue }

            try {
                return [System.Text.Encoding]::UTF8.GetString(
                    [System.Security.Cryptography.ProtectedData]::Unprotect(
                        [Convert]::FromBase64String($enc), $null, $pair[1]))
            } catch { }
        }
        return ""
    }

    $key = Unseal "tskey"
    if (-not $key) { throw "$cfg 에 Tailscale 키가 없습니다. 창에서 [외부망 열기] 를 한 번 하세요." }

    $lines = @("tskey=" + $key)

    # 알림 받을 곳만 싣는다. 봇 토큰은 src\Notify.cs 에 박혀 있어 옮길 것이 없다.
    $chat = ([string](($conf | Where-Object { $_ -like "tgchat=*" }) -replace "^tgchat=","")).Trim()

    if ($chat) {
        $lines += "tgchat=" + $chat
        "텔레그램 알림도 함께 넣습니다 (chat_id $chat)"
    } else {
        "텔레그램 알림은 넣지 않습니다 - 창의 [알림: 꺼짐] 에서 chat_id 를 먼저 정하세요."
    }

    $preset = Join-Path $env:TEMP "rc_preset.txt"
    [System.IO.File]::WriteAllText($preset, ($lines -join "`n"), (New-Object System.Text.UTF8Encoding($false)))
    $presetArg = @("/resource:" + $preset + ",preset.txt")
}

# 가상 모니터 드라이버.
#
# 모니터를 안 꽂은 PC 는 윈도우가 640x480 하나만 내놓는다. 장치의 한계라 프로그램으로는
# 못 늘린다. 가상 모니터를 만들어 줘야 FHD 가 열린다.
#
# 드라이버는 여기 넣지 않았다 - 제3자 서명 드라이버라 재배포는 쓰는 사람이 정할 일이다.
# 쓰려면 usbmmidd_v2 를 받아 압축을 풀고, 그 안의 파일들을 이 폴더에 넣는다.
#
#   원격제어\vdd\deviceinstaller64.exe
#   원격제어\vdd\usbmmidd.inf
#   원격제어\vdd\...
#
# 넣어 두면 설치 파일 안에 함께 담기고, 설치 창에 체크 항목이 생긴다.
#
# 압축하지 않고 파일 하나씩 넣는다.
# zip 으로 넣었더니 설치할 때 풀리지 않았다. 푸는 데 쓰는 Shell.Application 이
# STA 스레드에서만 제대로 도는데, 설치는 창을 막지 않으려고 배경 스레드에서 하기
# 때문이다. 조용히 실패해서 원인을 찾기 어려웠다. 파일 몇 개뿐이니 그냥 하나씩 넣는다.
$vddArg = @()
$vddDir = Join-Path $root "vdd"

if (Test-Path $vddDir) {
    $files = Get-ChildItem $vddDir -File
    foreach ($f in $files) {
        $vddArg += "/resource:" + $f.FullName + ",vdd/" + $f.Name
    }

    if ($files.Count -gt 0) {
        "가상 모니터 드라이버 포함  ({0}개 파일, {1:N0} KB)" -f `
            $files.Count, (($files | Measure-Object Length -Sum).Sum / 1KB)
    }
}

$args = @(
    "/nologo"
    "/target:winexe"
    "/platform:x64"
    "/optimize+"
    "/langversion:5"
    "/win32icon:" + $ico
    "/win32manifest:" + $man
    "/out:" + $out
    "/r:System.dll"
    "/r:System.Core.dll"
    "/r:System.Drawing.dll"
    "/r:System.Windows.Forms.dll"
    "/r:System.ServiceProcess.dll"
    "/r:System.IO.Compression.dll"
    "/r:System.Web.Extensions.dll"
    "/unsafe-"
    "/resource:" + $app + ",RemoteControl.exe"
    "/resource:" + $ico + ",remotecontrol.ico"
    "/resource:" + (Join-Path $buildDir 'RemoteControl_MCP.zip') + ",RemoteControl_MCP.zip"
    "/resource:" + (Join-Path $buildDir 'RemoteControl_Claude.mcpb') + ",RemoteControl_Claude.mcpb"
    "/resource:" + (Join-Path $buildDir 'RemoteControl_WebMCP.zip') + ",RemoteControl_WebMCP.zip"
    (Join-Path $root "Setup.cs")
    (Join-Path $root "SetupLayout.cs")
    (Join-Path $root "SetupWebForm.cs")
    (Join-Path $root "SetupLanMcp.cs")
    (Join-Path $root "SetupLanForm.cs")
    (Join-Path $root "SetupVdd.cs")
    (Join-Path $root "SetupWebMcp.cs")
) + $presetArg + $vddArg

$log = & $csc @args 2>&1 | Out-String

if ($LASTEXITCODE -ne 0) {
    "빌드 실패"
    ($log -split "`r?`n" | Where-Object { $_ -match 'error|오류' } | Select-Object -First 25) -join "`r`n"
    throw "설치 파일 빌드에 실패했습니다."
}

Remove-Item $man -Force -ErrorAction SilentlyContinue

$fi = Get-Item $out
"설치 파일 완료  {0:N2} MB  {1:HH:mm:ss}" -f ($fi.Length / 1MB), $fi.LastWriteTime
"설치 파일 경로: $out"
& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $root 'web-mcp\Export-SetupPlugin.ps1') -Setup $out -Destination (Join-Path $buildDir 'RemoteControl_WebPlugin.zip')
if($LASTEXITCODE -ne 0){throw 'Setup 웹 플러그인 ZIP 생성 실패'}
