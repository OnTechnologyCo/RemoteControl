$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root 'artifacts\gemini-settings-test'
[void][IO.Directory]::CreateDirectory($dest)
$native = Join-Path $root 'artifacts\control-test\Native.cs'
if (-not (Test-Path -LiteralPath $native)) { & (Join-Path $PSScriptRoot 'Test-Control.ps1') }
$config = [IO.File]::ReadAllText((Join-Path $root 'src\Config.cs'))
$declaration = 'private static readonly string Path0 = ResolvePath();'
if (-not $config.Contains($declaration)) { throw 'Test path override no longer matches' }
$config = $config.Replace($declaration, 'private static readonly string Path0 = GeminiSettingsTest.Path0;').Replace('RemoteControlSecrets.ConfigProtection.', 'GeminiConfigProtection.')
[IO.File]::WriteAllText((Join-Path $dest 'Config.cs'), $config, (New-Object Text.UTF8Encoding($true)))
$form = [IO.File]::ReadAllText((Join-Path $root 'src\GeminiSettingsForm.cs')).Replace('RemoteControlSecrets.ConfigProtection.RequireAdministrator();', 'GeminiConfigProtection.RequireAdministrator();')
[IO.File]::WriteAllText((Join-Path $dest 'GeminiSettingsForm.cs'), $form, (New-Object Text.UTF8Encoding($true)))
$src = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | Where-Object { $_.Name -notin @('Native.cs','DesktopDup.cs','Desktops.cs','Config.cs','GeminiSettingsForm.cs') } | ForEach-Object FullName
$exe = Join-Path $dest 'GeminiSettingsTest.exe'
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /unsafe /optimize+ /target:exe /platform:x64 /langversion:5 "/out:$exe" /main:RemoteControl.GeminiSettingsTest `
    "/resource:$(Join-Path $root 'web-mcp\web-tools.json'),web-tools.json" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.ServiceProcess.dll /r:System.Security.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll `
    $native (Join-Path $PSScriptRoot 'ControlTest.cs') (Join-Path $PSScriptRoot 'GeminiSettingsTest.cs') (Join-Path $dest 'Config.cs') (Join-Path $dest 'GeminiSettingsForm.cs') $src
if ($LASTEXITCODE -ne 0) { throw 'Gemini settings fixture compilation failed' }
$case = Join-Path $dest ('case-' + [guid]::NewGuid().ToString('N'))
& $exe test $case
if ($LASTEXITCODE -ne 0) { throw 'Gemini settings tests failed' }
& $exe restart $case
if ($LASTEXITCODE -ne 0) { throw 'Gemini settings restart test failed' }
Write-Output ('ARTIFACTS ' + $case)
