$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$dest=Join-Path $root 'artifacts\mcp-bridge-test'
[void][IO.Directory]::CreateDirectory($dest)
$native=Join-Path $root 'artifacts\control-test\Native.cs'
if(-not(Test-Path $native)){& (Join-Path $PSScriptRoot 'Test-Control.ps1')}
$src=Get-ChildItem (Join-Path $root 'src') -Filter *.cs|Where-Object{$_.Name -notin @('Native.cs','DesktopDup.cs','Desktops.cs')}|ForEach-Object FullName
$exe=Join-Path $dest 'McpBridgeFixture.exe'
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /unsafe /target:exe /platform:x64 /langversion:5 "/out:$exe" /main:RemoteControl.McpBridgeFixture "/resource:$(Join-Path $root 'web\login.html'),login.html" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.ServiceProcess.dll /r:System.Security.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll $native (Join-Path $PSScriptRoot 'ControlTest.cs') (Join-Path $PSScriptRoot 'McpBridgeFixture.cs') $src
if($LASTEXITCODE -ne 0){throw 'MCP bridge fixture compilation failed'}
& node (Join-Path $PSScriptRoot 'McpBridgeTest.js')
if($LASTEXITCODE -ne 0){throw 'MCP bridge regression failed'}

