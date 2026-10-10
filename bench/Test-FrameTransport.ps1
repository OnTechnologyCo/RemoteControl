$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root 'artifacts\frame-transport'
[void][IO.Directory]::CreateDirectory($dest)
$exe = Join-Path $dest 'FrameTransportBench.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /optimize+ /target:exe /platform:x64 /langversion:5 "/out:$exe" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll (Join-Path $PSScriptRoot 'FrameTransportBench.cs') `
    (Join-Path $root 'src\FrameFlow.cs') (Join-Path $root 'src\WebSocket.cs')
if ($LASTEXITCODE -ne 0) { throw 'Transport bench compilation failed' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Transport bench failed' }
