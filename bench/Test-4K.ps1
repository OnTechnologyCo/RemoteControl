param([ValidateSet(128,256,512,1024)][int]$TileSize = 128)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root 'artifacts\frame-transport'
[void][IO.Directory]::CreateDirectory($dest)
$native = Join-Path $root 'artifacts\control-test\Native.cs'
if (-not (Test-Path -LiteralPath $native)) { & (Join-Path $PSScriptRoot 'Test-Control.ps1') }
$exe = Join-Path $dest 'FrameEncodeBench.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /unsafe /optimize+ /target:exe /platform:x64 /langversion:5 "/out:$exe" /main:RemoteControl.FrameEncodeBench `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll $native `
    (Join-Path $root 'src\InputInjector.cs') (Join-Path $root 'src\ScreenSource.cs') (Join-Path $root 'src\FrameFlow.cs') (Join-Path $root 'src\DesktopRegions.cs') `
    (Join-Path $PSScriptRoot 'ControlTest.cs') (Join-Path $PSScriptRoot 'FrameEncodeBench.cs')
if ($LASTEXITCODE -ne 0) { throw '4K bench compilation failed' }
& $exe $dest $TileSize
if ($LASTEXITCODE -ne 0) { throw '4K encode bench failed' }
& node (Join-Path $PSScriptRoot 'Viewer4KBench.js')
if ($LASTEXITCODE -ne 0) { throw '4K browser bench failed' }
