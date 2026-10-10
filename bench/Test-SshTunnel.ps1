$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $root ('artifacts\sshtunnel-test-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testDir)
& (Join-Path $root '빌드_sshtunnel.ps1')
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $testDir 'SshTunnelTest.exe'
& $csc /nologo /target:exe /platform:x64 /langversion:5 "/out:$exe" /r:System.dll /r:System.Core.dll /r:System.Security.dll /r:System.IO.Compression.dll `
    ('/resource:' + (Join-Path $root 'sshtunnel-client.exe.gz') + ',sshtunnel-client.exe.gz') `
    (Join-Path $PSScriptRoot 'SshTunnelTest.cs') (Join-Path $PSScriptRoot 'SshTunnelTestStub.cs') `
    (Join-Path $root 'src\SshTunnel.cs') (Join-Path $root 'src\SshHelper.cs') (Join-Path $root 'src\RemoteTunnel.cs') `
    (Join-Path $root 'src\TunnelGuard.cs') (Join-Path $root 'src\Interop.cs') (Join-Path $root 'src\WebSocket.cs') (Join-Path $root 'src\Json.cs')
if ($LASTEXITCODE -ne 0) { throw 'SSHTunnel integration test compilation failed' }
Push-Location (Join-Path $PSScriptRoot 'sshtunnel-fixture')
try {
    & go mod tidy
    if ($LASTEXITCODE -ne 0) { throw 'Fixture dependency preparation failed' }
    & go build -o (Join-Path $testDir 'fixture.exe') .
    if ($LASTEXITCODE -ne 0) { throw 'SSHTunnel fixture build failed' }
} finally { Pop-Location }
$fixture = Start-Process -FilePath (Join-Path $testDir 'fixture.exe') -ArgumentList ('"' + $testDir + '"') `
    -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $testDir 'fixture.out') -RedirectStandardError (Join-Path $testDir 'fixture.err')
try {
    $infoPath = Join-Path $testDir 'info.json'
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath $infoPath)) {
        if ($fixture.HasExited -or [DateTime]::UtcNow -gt $deadline) {
            if (Test-Path -LiteralPath (Join-Path $testDir 'fixture.err')) { Get-Content -LiteralPath (Join-Path $testDir 'fixture.err') }
            throw 'SSHTunnel fixture did not start'
        }
        Start-Sleep -Milliseconds 100
    }
    & $exe $infoPath
    if ($LASTEXITCODE -ne 0) { throw 'SSHTunnel integration tests failed' }
} finally {
    if (-not $fixture.HasExited) { $fixture.Kill(); [void]$fixture.WaitForExit(3000) }
    $fixture.Dispose()
}
