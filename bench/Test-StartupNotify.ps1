$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dir = Join-Path $root ('artifacts\startup-notify-test-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($dir)
$exe = Join-Path $dir 'StartupNotifyTest.exe'
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:x64 /langversion:5 "/out:$exe" /r:System.dll /r:System.Core.dll /r:System.Management.dll (Join-Path $PSScriptRoot 'StartupNotifyTest.cs') (Join-Path $root 'src\StartupNotify.cs')
if ($LASTEXITCODE -ne 0) { throw 'Startup notification test compilation failed' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Startup notification tests failed' }
