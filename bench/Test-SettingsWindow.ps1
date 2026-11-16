param([string]$App)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(-not $App){$App=Join-Path $root 'dist\RemoteControl.exe'}
$work=Join-Path $root ('artifacts\settings-window-test\'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
$test=Join-Path $work 'SettingsWindowTest.exe'
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:x64 /langversion:5 "/out:$test" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll (Join-Path $PSScriptRoot 'SettingsWindowTest.cs')
if($LASTEXITCODE -ne 0){throw 'Settings window fixture compilation failed'}
& $test $App $work
if($LASTEXITCODE -ne 0){throw 'Settings window regression failed'}
Write-Output ('Synthetic render: '+(Join-Path $work 'first.png'))
