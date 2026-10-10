$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$dest=Join-Path $root ('artifacts\private-mcp-relay-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($dest)
$native=Join-Path $root 'artifacts\control-test\Native.cs'
if(-not(Test-Path -LiteralPath $native)){throw 'Run Test-Control.ps1 to prepare isolated capture stubs first.'}
$src=Get-ChildItem (Join-Path $root 'src') -Filter *.cs|Where-Object {$_.Name -notin @('Native.cs','DesktopDup.cs','Desktops.cs')}|ForEach-Object FullName
$fixture=Join-Path $root 'artifacts\mcp-test\McpFixture.exe'
[void][IO.Directory]::CreateDirectory((Split-Path $fixture))
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /unsafe /optimize+ /target:exe /platform:x64 /langversion:5 "/out:$fixture" /main:RemoteControl.McpFixture /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.ServiceProcess.dll /r:System.Security.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll $native (Join-Path $PSScriptRoot 'ControlTest.cs') (Join-Path $PSScriptRoot 'McpFixture.cs') (Join-Path $PSScriptRoot 'McpSessionsTest.cs') $src
if($LASTEXITCODE -ne 0){throw 'MCP fixture compilation failed'}
Push-Location (Join-Path $PSScriptRoot 'sshtunnel-fixture')
try{go build -o (Join-Path $dest 'fixture.exe') .;if($LASTEXITCODE -ne 0){throw 'SSH fixture build failed'}}finally{Pop-Location}
$env:RC_SSH_FIXTURE=Join-Path $dest 'fixture.exe'
Push-Location (Join-Path $root 'web-mcp')
try{node --test test/sshtunnel.test.mjs;if($LASTEXITCODE -ne 0){throw 'Private tunnel MCP integration failed'}}finally{Pop-Location;Remove-Item Env:\RC_SSH_FIXTURE}
