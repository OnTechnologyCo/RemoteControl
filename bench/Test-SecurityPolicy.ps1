$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$testDir=Join-Path $root ('artifacts\security-policy-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testDir)
$exe=Join-Path $testDir 'SecurityPolicyTest.exe'
$files=@('bench\SecurityPolicyTest.cs','src\Config.cs','src\ConfigProtection.cs','src\TelegramCredentials.cs','src\UiPreferences.cs','src\NotifyBroker.cs','src\Auth.cs') | ForEach-Object {Join-Path $root $_}
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:x64 /langversion:5 "/out:$exe" /r:System.dll /r:System.Core.dll /r:System.Security.dll @files
if($LASTEXITCODE -ne 0){throw 'Security fixture compilation failed'}
& $exe $testDir
if($LASTEXITCODE -ne 0){throw 'Security fixture failed; skipped prerequisites are not a pass'}
