$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$dest=Join-Path $root ('artifacts\telegram-setup-test\'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($dest)
$sources=@('Setup.cs','src\ConfigProtection.cs','src\TelegramCredentials.cs','SetupLayout.cs','SetupWebForm.cs','SetupLanMcp.cs','SetupLanForm.cs','SetupVdd.cs','SetupWebMcp.cs','bench\TelegramSetupTest.cs') | ForEach-Object {Join-Path $root $_}
$exe=Join-Path $dest 'TelegramSetupTest.exe'
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:x64 /langversion:5 /main:RemoteControlSetup.TelegramSetupTest "/out:$exe" /r:System.dll /r:System.Core.dll /r:System.Security.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.ServiceProcess.dll /r:System.IO.Compression.dll /r:System.Web.Extensions.dll @sources
if($LASTEXITCODE-ne 0){throw 'Telegram setup fixture compile failed'}
& $exe $dest
if($LASTEXITCODE-ne 0){throw 'Telegram setup test failed'}
"Report: $dest"
