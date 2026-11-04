$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$dest=Join-Path $root ('artifacts\web-only-test-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($dest)
$flags=[Reflection.BindingFlags]'Static,NonPublic'
$checks=0
function Check($value,[string]$message){if(-not $value){throw $message};$script:checks++;Write-Output ('PASS '+$message)}
$app=Join-Path $root 'dist\RemoteControl.exe'
$core=[Reflection.Assembly]::LoadFile($app)
Check ($null -eq $core.GetType('RemoteControl.McpClient',$false)) 'legacy stdio client is not compiled'
Check ($null -eq $core.GetType('RemoteControl.McpProfile',$false)) 'legacy client credentials/profile component is absent'
Check ($null -eq $core.GetType('RemoteControl.McpClientForm',$false)) 'legacy connection window is absent'
foreach($mode in @('--mcp-client','--mcp-config')){
 $psi=New-Object Diagnostics.ProcessStartInfo($app,$mode)
 $psi.UseShellExecute=$false;$psi.CreateNoWindow=$true
 $process=[Diagnostics.Process]::Start($psi)
 try{if(-not $process.WaitForExit(5000)){$process.Kill();throw 'removed CLI started a window'};Check ($process.ExitCode -eq 64) ('removed CLI returns unsupported: '+$mode)}finally{$process.Dispose()}
}
$config=$core.GetType('RemoteControl.Config',$true)
$config.GetField('Path0',$flags).SetValue($null,(Join-Path $dest 'settings.txt'))
$config.GetMethod('EnsureWritable',$flags).Invoke($null,@())|Out-Null
$config.GetMethod('Reload',$flags).Invoke($null,@())|Out-Null
$config.GetMethod('Set',$flags).Invoke($null,@('pwhash','fixture'))|Out-Null
$config.GetMethod('Set',$flags).Invoke($null,[string[]]@('mcp_roots',$dest))|Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$form=[Activator]::CreateInstance($core.GetType('RemoteControl.McpForm',$true),[Reflection.BindingFlags]'Instance,NonPublic',$null,@([Func[string]]{return 'Web MCP 준비'}),$null)
try{
 Check ($form.Text -eq 'Web MCP 설정') 'default MCP settings are Web MCP'
 Check (@($form.Controls|Where-Object {$_ -is [Windows.Forms.NumericUpDown]}).Count -eq 0) 'no internal port input is exposed'
 Check (@($form.Controls|Where-Object {$_.Text -match '허용 IPv4|LLM PC|stdio'}).Count -eq 0) 'legacy client and direct-IP fields are absent'
 Check (@($form.Controls|Where-Object {$_.Text -eq '웹 HTTPS 연결 설정'}).Count -eq 1) 'web HTTPS configuration is the primary connection'
 Check (@($form.Controls|Where-Object {$_.Text -eq '내부망 HTTPS 설정'}).Count -eq 1) 'LAN remains HTTPS with OAuth'
 foreach($control in $form.Controls){Check ($control.Right -le $form.ClientSize.Width -and $control.Bottom -le $form.ClientSize.Height) ('visible control fits: '+$control.GetType().Name)}
 $bitmap=New-Object Drawing.Bitmap($form.ClientSize.Width,$form.ClientSize.Height)
 try{$g=[Drawing.Graphics]::FromImage($bitmap);try{$g.Clear($form.BackColor)}finally{$g.Dispose()};foreach($control in $form.Controls){$handle=$control.Handle;$control.DrawToBitmap($bitmap,$control.Bounds)};$bitmap.Save((Join-Path $dest 'web-mcp-settings.png'))}finally{$bitmap.Dispose()}
}finally{$form.Dispose()}
$setup=[Reflection.Assembly]::LoadFile((Join-Path $root 'dist\RemoteControl_SetupEngine.exe'))
$resources=$setup.GetManifestResourceNames()
foreach($old in @('RemoteControl_MCP.zip','RemoteControl_Claude.mcpb')){Check ($resources -notcontains $old) ('setup excludes '+$old);Check (-not(Test-Path (Join-Path $root ('dist\'+$old)))) ('dist excludes '+$old)}
$stage=Join-Path $dest 'install'
[void][IO.Directory]::CreateDirectory((Join-Path $stage 'plugins'))
foreach($name in @('RemoteControl_MCP.zip','RemoteControl_Claude.mcpb','RemoteControl_WebPlugin.zip','custom.zip')){[IO.File]::WriteAllText((Join-Path $stage ('plugins\'+$name)),'fixture')}
$setup.GetType('RemoteControlSetup.WebMcp',$true).GetMethod('ExtractPackages',$flags).Invoke($null,[string[]]@($stage,''))|Out-Null
foreach($old in @('RemoteControl_MCP.zip','RemoteControl_Claude.mcpb')){Check (-not(Test-Path (Join-Path $stage ('plugins\'+$old)))) ('upgrade removes '+$old)}
foreach($name in @('RemoteControl_WebPlugin.zip','custom.zip')){Check ([IO.File]::ReadAllText((Join-Path $stage ('plugins\'+$name))) -eq 'fixture') ('upgrade preserves '+$name)}
Write-Output ('ALL '+$checks+' WEB-ONLY CHECKS PASSED')
Write-Output ('UI_PREVIEW '+(Join-Path $dest 'web-mcp-settings.png'))

