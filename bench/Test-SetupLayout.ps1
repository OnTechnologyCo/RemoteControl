param([switch]$BuiltSetup)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$dest=Join-Path $root ('artifacts\setup-layout-test\'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($dest)
$exe=Join-Path $dest 'SetupLayoutTest.exe'
$csc='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$manifest=Join-Path $dest 'test.manifest'
@'
<?xml version="1.0" encoding="UTF-8"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <assemblyIdentity version="1.0.0.0" name="RemoteControl.SetupLayoutTest" type="win32"/>
  <application xmlns="urn:schemas-microsoft-com:asm.v3"><windowsSettings>
    <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
  </windowsSettings></application>
</assembly>
'@ | Set-Content -LiteralPath $manifest -Encoding UTF8
$sources=@('Setup.cs','SetupLayout.cs','SetupWebForm.cs','SetupVdd.cs','SetupWebMcp.cs','bench\SetupLayoutTest.cs')|ForEach-Object {Join-Path $root $_}
& $csc /nologo /target:exe /platform:x64 /langversion:5 /main:RemoteControlSetup.SetupLayoutTest "/out:$exe" "/win32manifest:$manifest" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.ServiceProcess.dll /r:System.IO.Compression.dll /r:System.Web.Extensions.dll @sources
if($LASTEXITCODE -ne 0){throw 'Setup layout fixture compilation failed'}
$testArgs=@($dest)
if($BuiltSetup){$testArgs+=Join-Path $root 'dist\RemoteControl_Setup.exe'}
& $exe @testArgs
if($LASTEXITCODE -ne 0){throw "Setup layout test failed: $dest"}
"Layout screenshots: $dest"
