$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $root 'artifacts\tunnel-ui-test'
[void][System.IO.Directory]::CreateDirectory($testDir)
$exe = Join-Path $testDir 'TunnelFormTest.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /platform:x64 /langversion:5 "/out:$exe" `
    /r:System.dll /r:System.Core.dll /r:System.Security.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    (Join-Path $PSScriptRoot 'TunnelFormTest.cs') (Join-Path $PSScriptRoot 'SshTunnelTestStub.cs') `
    (Join-Path $root 'src\TunnelForm.cs') (Join-Path $root 'src\RemoteTunnel.cs') (Join-Path $root 'src\SshTunnel.cs') (Join-Path $root 'src\Interop.cs') (Join-Path $root 'src\WebSocket.cs')
if ($LASTEXITCODE -ne 0) { throw 'Tunnel form test compilation failed' }
& $exe (Join-Path $testDir 'settings.png')
if ($LASTEXITCODE -ne 0) { throw 'Tunnel form tests failed' }
