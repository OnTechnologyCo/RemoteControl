$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$dir=Join-Path $root ('artifacts\notify-test-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($dir)
$exe=Join-Path $dir 'NotifyMessagesTest.exe'
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:x64 /langversion:5 "/out:$exe" /r:System.dll /r:System.Core.dll /r:System.Security.dll (Join-Path $PSScriptRoot 'NotifyMessagesTest.cs') (Join-Path $root 'src\Notify.cs') (Join-Path $root 'src\TelegramCredentials.cs') (Join-Path $root 'src\NotifyAddresses.cs')
if($LASTEXITCODE -ne 0){throw 'Notification test compilation failed'}
& $exe
if($LASTEXITCODE -ne 0){throw 'Notification message tests failed'}
