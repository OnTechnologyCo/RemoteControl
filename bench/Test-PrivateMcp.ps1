$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$dest=Join-Path $root ('artifacts\private-controller-test\'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($dest)
$exe=Join-Path $dest 'PrivateMcpTest.exe'
$sources=@('bench\PrivateMcpTest.cs','src\PrivateMcp.cs','src\ConfigProtection.cs','src\Config.cs','src\McpWebPackage.cs','src\NotifyAddresses.cs','src\NotifyForm.cs')|ForEach-Object{Join-Path $root $_}
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /platform:x64 /langversion:5 "/out:$exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll @sources
if($LASTEXITCODE -ne 0){throw 'Controller fixture compilation failed'}
& $exe $dest
if($LASTEXITCODE -ne 0){throw 'Private MCP controller tests failed'}
"Fixture: $dest"
