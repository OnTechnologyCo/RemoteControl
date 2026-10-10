$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root 'artifacts\mcp-test'
[void][IO.Directory]::CreateDirectory($dest)
$native = Join-Path $root 'artifacts\control-test\Native.cs'
if (-not (Test-Path -LiteralPath $native)) { & (Join-Path $PSScriptRoot 'Test-Control.ps1') }
$src = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | Where-Object { $_.Name -notin @('Native.cs','DesktopDup.cs','Desktops.cs') } | ForEach-Object FullName
$exe = Join-Path $dest 'McpFixture.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /unsafe /optimize+ /target:winexe /platform:x64 /langversion:5 "/out:$exe" /main:RemoteControl.McpFixture `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.ServiceProcess.dll /r:System.Security.dll /r:System.Web.Extensions.dll `
    $native (Join-Path $PSScriptRoot 'ControlTest.cs') (Join-Path $PSScriptRoot 'McpFixture.cs') $src
if ($LASTEXITCODE -ne 0) { throw 'MCP fixture compilation failed' }
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'McpIntegrationTest.js')) {
    & node (Join-Path $PSScriptRoot 'McpIntegrationTest.js')
    if ($LASTEXITCODE -ne 0) { throw 'MCP integration failed' }
}
