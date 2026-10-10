$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $root ('artifacts\config-test-' + [guid]::NewGuid().ToString('N'))
[void][System.IO.Directory]::CreateDirectory($testDir)
$text = [System.IO.File]::ReadAllText((Join-Path $root 'src\Config.cs'))
$declaration = 'private static readonly string Path0 = ResolvePath();'
if (-not $text.Contains($declaration)) { throw 'Test path override no longer matches' }
$text = $text.Replace($declaration, 'private static readonly string Path0 = ConfigWriteTest.Path0;')
$source = Join-Path $testDir 'Config.cs'
[System.IO.File]::WriteAllText($source, $text, (New-Object System.Text.UTF8Encoding($true)))
$exe = Join-Path $testDir 'ConfigWriteTest.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /platform:x64 /langversion:5 "/out:$exe" `
    /r:System.dll /r:System.Core.dll $source (Join-Path $PSScriptRoot 'ConfigWriteTest.cs')
if ($LASTEXITCODE -ne 0) { throw 'Config tests compilation failed' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Config tests failed' }
