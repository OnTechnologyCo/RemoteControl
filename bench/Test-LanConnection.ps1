param()
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$dest=Join-Path $root ('artifacts\lan-connection-test\'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($dest)
$exe=Join-Path $dest 'LanConnectionTest.exe'
$csc='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources=@('Setup.cs','src\TelegramCredentials.cs','SetupLayout.cs','SetupWebForm.cs','SetupLanMcp.cs','SetupLanForm.cs','SetupVdd.cs','SetupWebMcp.cs','bench\LanConnectionTest.cs')|ForEach-Object {Join-Path $root $_}
& $csc /nologo /target:exe /platform:x64 /langversion:5 /main:RemoteControlSetup.LanConnectionTest "/out:$exe" /r:System.dll /r:System.Core.dll /r:System.Security.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.ServiceProcess.dll /r:System.IO.Compression.dll /r:System.Web.Extensions.dll @sources
if($LASTEXITCODE -ne 0){throw 'LAN connection fixture compilation failed'}
& $exe $dest
if($LASTEXITCODE -ne 0){throw "LAN connection tests failed: $dest"}
"LAN connection test output: $dest"
