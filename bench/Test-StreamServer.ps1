$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
# Test-Control에서 만든 SendInput 대역만 사용한다. 실제 입력 주입은 절대 호출하지 않는다.
$native = Join-Path $root 'artifacts\control-test\Native.cs'
if (-not (Test-Path -LiteralPath $native)) { & (Join-Path $PSScriptRoot 'Test-Control.ps1') }
$exe = Join-Path $root 'artifacts\control-test\StreamServerTest.exe'
$src = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | Where-Object { $_.Name -notin @('Native.cs', 'DesktopDup.cs', 'Desktops.cs') } | ForEach-Object FullName
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /unsafe /target:exe /platform:x64 /langversion:5 "/out:$exe" /main:RemoteControl.StreamServerTest `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.ServiceProcess.dll /r:System.Security.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll `
    $native (Join-Path $PSScriptRoot 'ControlTest.cs') (Join-Path $PSScriptRoot 'StreamServerTest.cs') $src
if ($LASTEXITCODE -ne 0) { throw 'Stream server test compilation failed' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Stream server tests failed' }
