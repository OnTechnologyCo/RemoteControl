$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $root ('artifacts\ssh-test ' + [guid]::NewGuid().ToString('N'))
[void][System.IO.Directory]::CreateDirectory($testDir)
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $testDir 'SshTunnelTest.exe'
& $csc /nologo /target:exe /platform:x64 /langversion:5 "/out:$exe" /r:System.dll /r:System.Core.dll /r:System.Security.dll `
    (Join-Path $root 'bench\SshTunnelTest.cs') (Join-Path $root 'bench\SshTunnelTestStub.cs') `
    (Join-Path $root 'src\SshTunnel.cs') (Join-Path $root 'src\RemoteTunnel.cs') `
    (Join-Path $root 'src\TunnelGuard.cs') (Join-Path $root 'src\Interop.cs') (Join-Path $root 'src\WebSocket.cs')
if ($LASTEXITCODE -ne 0) { throw 'SSH test compilation failed' }
Push-Location (Join-Path $PSScriptRoot 'ssh-fixture')
try { & go build -o (Join-Path $testDir 'fixture.exe') .; if ($LASTEXITCODE -ne 0) { throw 'SSH fixture compilation failed' } }
finally { Pop-Location }
$fixture = Start-Process -FilePath (Join-Path $testDir 'fixture.exe') -ArgumentList ('"' + $testDir + '"') `
    -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $testDir 'fixture.out') -RedirectStandardError (Join-Path $testDir 'fixture.err')
try {
    $infoPath = Join-Path $testDir 'info.json'
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath $infoPath)) {
        if ($fixture.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'SSH fixture did not start' }
        Start-Sleep -Milliseconds 100
    }
    $info = Get-Content -Raw -LiteralPath $infoPath | ConvertFrom-Json
    # 일회용 개인키에만 ACL을 적용한다. 사용자 SSH 키는 읽거나 바꾸지 않는다.
    $owner = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = New-Object System.Security.AccessControl.FileSecurity
    $acl.SetOwner($owner); $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($owner, 'FullControl', 'Allow')))
    Set-Acl -LiteralPath $info.keyPath -AclObject $acl
    & $exe $info.sshPort $info.keyPath (Join-Path $testDir 'known_hosts') (Join-Path $testDir 'wrong_known_hosts')
    if ($LASTEXITCODE -ne 0) { throw 'SSH tests failed' }
} finally {
    if (-not $fixture.HasExited) { $fixture.Kill(); [void]$fixture.WaitForExit(3000) }
    $fixture.Dispose()
}
