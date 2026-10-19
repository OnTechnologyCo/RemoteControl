#requires -Version 5.1
[CmdletBinding()]
param([string]$Root)
$ErrorActionPreference='Stop'
if(-not $Root){$Root=Split-Path -Parent $PSScriptRoot}
$Root=[IO.Path]::GetFullPath($Root)
$dir=Join-Path $Root ('artifacts\startup-quiet-test-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($dir)
$source=Join-Path $Root 'src\Program.cs'
$match=[regex]::Match([IO.File]::ReadAllText($source),'const string Version = "([0-9.]+)"')
if(-not $match.Success){throw 'Source version not found'}
$exe=Join-Path $dir 'StartupQuietTest.exe'
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:x64 /langversion:5 "/out:$exe" /r:System.dll /r:System.Core.dll (Join-Path $PSScriptRoot 'StartupQuietTest.cs')
if($LASTEXITCODE -ne 0){throw 'Startup quiet test compilation failed'}
& $exe (Join-Path $Root 'dist\RemoteControl.exe') $source $match.Groups[1].Value
if($LASTEXITCODE -ne 0){throw 'Startup quiet binary regression failed'}
