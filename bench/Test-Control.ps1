$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $root 'artifacts\control-test'
[void][System.IO.Directory]::CreateDirectory($testDir)
$text = [System.IO.File]::ReadAllText((Join-Path $root 'src\Native.cs'))
$pattern = '\[DllImport\("user32.dll", SetLastError = true\)\]\s*internal static extern uint SendInput\(uint nInputs, INPUT\[\] pInputs, int cbSize\);'
if ([regex]::Matches($text, $pattern).Count -ne 1) { throw 'SendInput test interception no longer matches' }
$text = [regex]::Replace($text, $pattern, 'internal static uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize) { return ControlTest.Record(nInputs, pInputs); }')
$native = Join-Path $testDir 'Native.cs'
[System.IO.File]::WriteAllText($native, $text, (New-Object System.Text.UTF8Encoding($true)))
$exe = Join-Path $testDir 'ControlTest.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /unsafe /target:exe /platform:x64 /langversion:5 "/out:$exe" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll $native `
    (Join-Path $root 'src\InputInjector.cs') (Join-Path $root 'src\ScreenSource.cs') (Join-Path $root 'src\FrameFlow.cs') (Join-Path $PSScriptRoot 'ControlTest.cs')
if ($LASTEXITCODE -ne 0) { throw 'Control test compilation failed' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Control tests failed' }
