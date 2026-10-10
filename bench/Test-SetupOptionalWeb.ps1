# ReadOptions/Validate/package extraction only; never invoke installation or HTTPS activation.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$assembly=[Reflection.Assembly]::LoadFile((Join-Path $root 'dist\RemoteControl_Setup.exe'))
$static=[Reflection.BindingFlags]'Static,NonPublic'
$instance=[Reflection.BindingFlags]'Instance,NonPublic'
$web=$assembly.GetType('RemoteControlSetup.WebMcp',$true)
$optionsType=$assembly.GetType('RemoteControlSetup.Options',$true)
$dest=Join-Path $root ('artifacts\setup-optional-web-test\'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($dest)
function Assert($condition,[string]$message){if(-not $condition){throw $message}}
function Field($obj,[string]$name){$obj.GetType().GetField($name,$instance).GetValue($obj)}
function Controls($parent){foreach($child in $parent.Controls){$child;Controls $child}}
$options=[Activator]::CreateInstance($optionsType,$true)
Assert (-not (Field $options 'WebMcp')) 'Silent install unexpectedly enables web setup'
$optionsType.GetField('WebUrl',$instance).SetValue($options,'not a URL')
$web.GetMethod('Validate',$static).Invoke($null,@($options))|Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
[Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)
$form=[Activator]::CreateInstance($assembly.GetType('RemoteControlSetup.SetupForm'),$true)
try {
    $form.Opacity=0;$form.ShowInTaskbar=$false;$form.Show();[Windows.Forms.Application]::DoEvents()
    $all=@(Controls $form)
    $check=@($all|Where-Object Name -eq 'WebConnectNow')[0]
    $address=@($all|Where-Object Name -eq 'WebAddress')[0]
    $port=@($all|Where-Object Name -eq 'WebHttpsPort')[0]
    $defaultPort=@($all|Where-Object Name -eq 'WebDefaultHttpsPort')[0]
    $cert=@($all|Where-Object Name -eq 'WebCertificate')[0]
    $mode=@($all|Where-Object Name -eq 'WebConnectionMode')[0]
    $gateway=@($all|Where-Object Name -eq 'TailscaleGatewayPort')[0]
    $mode.SelectedIndex=0
    Assert (-not $check.Checked) 'Web setup should default to deferred, including upgrades'
    Assert (-not $address.Enabled -and -not $port.Enabled -and -not $cert.Enabled -and -not $defaultPort.Enabled) 'Deferred controls still enabled'
    $read=$form.GetType().GetMethod('ReadOptions',$instance)
    $lan=$web.GetMethod('LanPort',$static).Invoke($null,@())
    $port.Value=[Math]::Max(1024,$lan)
    foreach($text in @('','not a URL','http://invalid.example.test','https://invalid.example.test/mcp')){
        $address.Text=$text
        $selected=$read.Invoke($form,@())
        Assert (-not (Field $selected 'WebMcp') -and $null -eq (Field $selected 'WebUrl')) 'Deferred input was validated or retained'
    }
    $address.Text='';$check.Checked=$true
    Assert ($address.Enabled -and $port.Enabled -and $cert.Enabled) 'Web setup inputs do not enable'
    $rejected=$false;try{$read.Invoke($form,@())|Out-Null}catch{$rejected=$true}
    Assert $rejected 'Enabled web setup accepted an empty URL'
    # Same fields later produce the chosen address, with no account/service dependency.
    $address.Text='https://later.example.test';$port.Value=59443
    $connection=Field $form 'webConnection'
    $connection.GetType().GetMethod('Apply',$instance).Invoke($connection,@($options))|Out-Null
    Assert ((Field $options 'WebUrl') -ceq 'https://later.example.test:59443') 'Custom deferred URL/port lost'
    $defaultPort.Checked=$true
    Assert ($port.Value -eq 443 -and -not $port.Enabled) 'Default HTTPS selection did not choose 443'
    $connection.GetType().GetMethod('Apply',$instance).Invoke($connection,@($options))|Out-Null
    Assert ((Field $options 'WebUrl') -ceq 'https://later.example.test') 'Default HTTPS address retained a port number'
    $web.GetMethod('Validate',$static).Invoke($null,@($options))|Out-Null
    $defaultPort.Checked=$false
    Assert ($port.Value -eq 59443 -and $port.Enabled) 'Disabling default HTTPS lost the custom port'
    $port.Text='443'
    $port.GetType().GetMethod('ValidateEditText',$instance).Invoke($port,@())|Out-Null
    Assert ($defaultPort.Checked -and -not $port.Enabled) 'Entering 443 did not select default HTTPS'
    $mode.SelectedIndex=1
    Assert ($defaultPort.Enabled) 'Tailscale default HTTPS option unavailable'
    $defaultPort.Checked=$true
    $connection.GetType().GetMethod('Apply',$instance).Invoke($connection,@($options))|Out-Null
    Assert ((Field $options 'WebTailscale') -and (Field $options 'WebPublicPort') -eq 443 -and -not $port.Enabled) 'Tailscale default 443 selection failed'
    $web.GetMethod('Validate',$static).Invoke($null,@($options))|Out-Null
    $defaultPort.Checked=$false
    $port.Value=10000
    $defaultPort.Checked=$true
    $defaultPort.Checked=$false
    Assert ($port.Value -eq 10000) 'Tailscale custom port not restored'
    $defaultPort.Checked=$true
    $mode.SelectedIndex=0
    Assert ($defaultPort.Checked -and $port.Value -eq 443 -and -not $port.Enabled) 'Switching back lost the default HTTPS selection'
    $defaultPort.Checked=$false
    $mode.SelectedIndex=1
    Assert ($address.ReadOnly -and -not $cert.Enabled -and $gateway.Enabled) 'Tailscale automatic controls invalid'
    Assert ($defaultPort.Checked -and $port.Value -eq 443) 'Tailscale default choice lost on mode switch'
    $defaultPort.Checked=$false
    $port.Value=10000;$gateway.Value=59535
    $connection.GetType().GetMethod('Apply',$instance).Invoke($connection,@($options))|Out-Null
    Assert ((Field $options 'WebTailscale') -and (Field $options 'WebPublicPort') -eq 10000 -and (Field $options 'WebGatewayPort') -eq 59535 -and $null -eq (Field $options 'WebUrl')) 'Tailscale mode incorrectly requires a manually entered URL'
    $mode.SelectedIndex=0
    Assert ($address.Text -ceq 'https://later.example.test' -and $port.Value -eq 59443 -and -not $address.ReadOnly) 'Switching back changed the manual address/port'
    # A manually entered Tailscale hostname is still manual; never infer auto mode from the hostname.
    $address.Text='https://manual.example.ts.net';$port.Value=10000
    $connection.GetType().GetMethod('Apply',$instance).Invoke($connection,@($options))|Out-Null
    Assert (-not (Field $options 'WebTailscale') -and (Field $options 'WebUrl') -ceq 'https://manual.example.ts.net:10000') 'Manual Tailscale hostname was treated as automatic'
    $check.Checked=$false;$address.Text='';$read.Invoke($form,@())|Out-Null
}finally{$form.Dispose()}
$stage=$web.GetMethod('ExtractPackages',$static)
$stage.Invoke($null,[string[]]@($dest,''))|Out-Null
$plugin=Join-Path $dest 'plugins\RemoteControl_WebPlugin.zip'
Assert (-not (Test-Path -LiteralPath $plugin)) 'Deferred fresh install generated a misleading web plugin'
foreach($relative in @('plugins\RemoteControl_MCP.zip','plugins\RemoteControl_Claude.mcpb','web-mcp\Install-WebMcp.ps1','web-mcp\Install-TailscaleMcp.ps1','web-mcp\tailscale-server.mjs','web-mcp\node.exe')){
    Assert (Test-Path -LiteralPath (Join-Path $dest $relative)) "Missing deferred package: $relative"
}
$web.GetMethod('WritePlugin',$static).Invoke($null,[string[]]@($plugin,'https://later.example.test:59443'))|Out-Null
$before=(Get-FileHash -LiteralPath $plugin).Hash
$stage.Invoke($null,[string[]]@($dest,''))|Out-Null
Assert ((Get-FileHash -LiteralPath $plugin).Hash -ceq $before) 'Deferred upgrade changed existing plugin'
$settings=Join-Path $dest 'settings-fixture.txt'
[IO.File]::WriteAllLines($settings,@('port=59330','mcp_port=59331','mcp_enabled=1','pwhash=fixture','mcp_web_url=https://old.example.test:59443'))
$web.GetMethod('SaveConfiguredUrl',$static).Invoke($null,[string[]]@($settings,'https://later.example.test:59443'))|Out-Null
$saved=[IO.File]::ReadAllLines($settings)
foreach($line in @('port=59330','mcp_port=59331','mcp_enabled=1','pwhash=fixture','mcp_web_url=https://later.example.test:59443')) {Assert ($saved -contains $line) "Lost setting: $line"}
Assert (@($saved|Where-Object {$_ -like 'mcp_web_url=*'}).Count -eq 1) 'Duplicate web URL'
$settingsForm=[Activator]::CreateInstance($assembly.GetType('RemoteControlSetup.WebSettingsForm'),$true)
try {Assert (@((Controls $settingsForm)|Where-Object Name -eq 'ApplyWebSettings').Count -eq 1) 'Post-install settings UI missing'}finally{$settingsForm.Dispose()}
$report=[ordered]@{Version=(Get-Item (Join-Path $root 'dist\RemoteControl_Setup.exe')).VersionInfo.FileVersion;DeferredInvalidInputs=4;SilentDefaultDeferred=$true;FieldsToggle=$true;EnabledEmptyUrlRejected=$true;DeferredPackages=$true;ExistingPluginPreserved=$true;LaterCustomUrlAndPort=$true;Default443Selection=$true;CustomPortRestored=$true;Default443PreservedOnModeSwitch=$true;OtherSettingsPreserved=$true;PostInstallSettingsUi=$true;TailscaleExplicitChoice=$true;ManualTailscaleHostnamePreserved=$true;ManualPortPreservedOnModeSwitch=$true;LiveServicesChanged=$false;FixtureDirectory=$dest}
$report|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $dest 'verification.json') -Encoding UTF8
$report|ConvertTo-Json
