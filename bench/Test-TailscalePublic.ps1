# Reuses the configured key only to create an isolated ephemeral test node.
# Does not stop, replace, or change the live remote-control node or its services.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$assembly=[Reflection.Assembly]::LoadFile((Join-Path $root 'dist\RemoteControl.exe'))
$flags=[Reflection.BindingFlags]'Static,NonPublic'
$tunnel=$assembly.GetType('RemoteControl.Tunnel',$true)
$key=$tunnel.GetMethod('LoadKey',$flags).Invoke($null,@())
if([string]::IsNullOrEmpty($key)){throw 'No configured Tailscale key is available for the isolated public test.'}
$prepared=$tunnel.GetMethod('PrepareKey',$flags).Invoke($null,[string[]]@($key))
$before=$env:TS_AUTHKEY
try{
    $env:TS_AUTHKEY=$prepared
    Push-Location $root
    try{& node bench/tailscale-public.mjs;if($LASTEXITCODE -ne 0){throw 'Isolated public Tailscale test failed'}}finally{Pop-Location}
}finally{$env:TS_AUTHKEY=$before;$key=$null;$prepared=$null}
