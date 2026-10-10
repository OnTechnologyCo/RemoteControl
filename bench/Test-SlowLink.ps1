$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root 'artifacts\slow-link-bench'
[void][IO.Directory]::CreateDirectory($dest)
$exe = Join-Path $dest 'SlowLinkBench.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /platform:x64 /langversion:5 "/out:$exe" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll (Join-Path $PSScriptRoot 'SlowLinkBench.cs') `
    (Join-Path $root 'src\FrameFlow.cs') (Join-Path $root 'src\WebSocket.cs')
if ($LASTEXITCODE -ne 0) { throw 'Slow link bench compilation failed' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Slow link bench failed' }
