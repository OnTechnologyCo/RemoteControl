# 원격제어 빌드.
#
# 외부 DLL 없이 exe 한 개로 끝낸다. 웹 화면과 터널 중계는 exe 안에 넣는다.
# 받는 쪽도, 이 PC 도 따로 설치할 것이 없어야 한다.
[CmdletBinding()]
param()
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
# 배포 파일은 항상 dist 한 곳에 둔다. 설치된 서비스와 실행 중인 UI는 건드리지 않는다.
# dist의 파일을 직접 실행 중이면 빌드가 실패하므로 그 실행본을 닫은 뒤 다시 빌드한다.
$buildDir = Join-Path $root 'dist'
[void][System.IO.Directory]::CreateDirectory($buildDir)
$out = Join-Path $buildDir 'RemoteControl.exe'

$srcs = Get-ChildItem (Join-Path $root "src") -Filter *.cs | ForEach-Object { $_.FullName }

$res = @(
    "web\viewer.html,viewer.html"
    "web\viewer.js,viewer.js"
    "web\login.html,login.html"
    "tsproxy.exe.gz,tsproxy.exe.gz"
    "remotecontrol.ico,remotecontrol.ico"
    "rc-busy.ico,rc-busy.ico"
) | ForEach-Object {
    $p, $n = $_ -split ','
    $full = Join-Path $root $p
    if (-not (Test-Path $full)) { throw "리소스가 없습니다: $p" }
    '/resource:' + $full + ',' + $n
}

$args = @(
    "/nologo"
    "/target:winexe"
    "/platform:x64"
    "/unsafe"
    "/optimize+"
    "/langversion:5"
    "/win32icon:" + (Join-Path $root "remotecontrol.ico")
    "/out:" + $out
    "/r:System.dll"
    "/r:System.Core.dll"
    "/r:System.Drawing.dll"
    "/r:System.Windows.Forms.dll"
    "/r:System.ServiceProcess.dll"
    "/r:System.Security.dll"
    "/r:System.Web.Extensions.dll"
    "/r:System.IO.Compression.dll"
) + $res + $srcs

$log = & $csc @args 2>&1 | Out-String

if ($LASTEXITCODE -ne 0) {
    "빌드 실패"
    ($log -split "`r?`n" | Where-Object { $_ -match 'error|오류' } | Select-Object -First 25) -join "`r`n"
    throw "본체 빌드에 실패했습니다."
}

$warn = ($log -split "`r?`n" | Where-Object { $_ -match 'warning|경고' })
if ($warn) { "경고 $($warn.Count)건" }

$fi = Get-Item $out
"빌드 완료  {0:N2} MB  {1:HH:mm:ss}" -f ($fi.Length / 1MB), $fi.LastWriteTime
"본체 경로: $out"
