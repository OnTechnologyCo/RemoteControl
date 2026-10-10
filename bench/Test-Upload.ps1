$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $root '빌드.ps1')
$testDir = Join-Path $root 'artifacts\upload-test'
[void][IO.Directory]::CreateDirectory($testDir)
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:exe /platform:x64 /langversion:5 /r:System.dll /r:System.Core.dll "/out:$testDir\UploadFixture.exe" (Join-Path $PSScriptRoot 'UploadFixture.cs')
if ($LASTEXITCODE -ne 0) { throw 'Upload fixture compilation failed' }
$sources = Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter *.cs | ForEach-Object FullName
& $csc /nologo /target:exe /unsafe /platform:x64 /langversion:5 /main:RemoteControl.UploadTest "/out:$testDir\UploadTest.exe" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.ServiceProcess.dll /r:System.Security.dll `
    $sources (Join-Path $PSScriptRoot 'UploadTest.cs')
if ($LASTEXITCODE -ne 0) { throw 'Multipart test compilation failed' }
& (Join-Path $testDir 'UploadTest.exe')
if ($LASTEXITCODE -ne 0) { throw 'Multipart tests failed' }
& node (Join-Path $PSScriptRoot 'ViewerUploadTest.js')
if ($LASTEXITCODE -ne 0) { throw 'Upload queue tests failed' }
& node (Join-Path $PSScriptRoot 'UploadIntegrationTest.js')
if ($LASTEXITCODE -ne 0) { throw 'Upload integration tests failed' }
