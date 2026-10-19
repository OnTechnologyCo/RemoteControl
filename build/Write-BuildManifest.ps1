[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Root)
$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$dist = Join-Path $Root 'dist'
$sha = [Security.Cryptography.SHA256]::Create()
$sources = @()
function SourceFiles([string]$directory) {
    foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
        if ($entry.PSIsContainer) {
            if ($entry.Name -in @('dist','artifacts','node_modules','.git','data','tls','tsnet','bin','obj','.llmtools-attachments','.agentclihub-attachments')) { continue }
            SourceFiles $entry.FullName
        } else { $entry }
    }
}
foreach ($file in SourceFiles $Root) {
    $relative = $file.FullName.Substring($Root.Length+1).Replace('\','/')
    if ($relative -match '(^|/)(dist|artifacts|node_modules|\.git|data|tls|tsnet|bin|obj|\.llmtools-attachments)/') { continue }
    if ($file.Extension -notin @('.cs','.ps1','.mjs','.js','.html','.go','.mod','.sum','.json','.inf','.xml','.ico','.iss')) { continue }
    $sources += [ordered]@{Path=$relative; SHA256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
}
$sources = @($sources | Sort-Object { $_.Path })
$fingerprint = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($sources | ConvertTo-Json -Depth 4 -Compress)))).Replace('-','')
$assembly = [Reflection.Assembly]::ReflectionOnlyLoadFrom((Join-Path $dist 'RemoteControl_SetupEngine.exe'))
$core = [Reflection.Assembly]::ReflectionOnlyLoadFrom((Join-Path $dist 'RemoteControl.exe'))
$notify = $core.GetType('RemoteControl.Notify', $true)
$botField = $notify.GetField('BotToken', [Reflection.BindingFlags]'Static,NonPublic,Public')
if ($null -ne $botField -and $botField.IsLiteral -and [string]$botField.GetRawConstantValue()) {
    throw 'An embedded Telegram bot credential remains in the app; do not distribute this build.'
}
$resources = @()
foreach ($name in $assembly.GetManifestResourceNames()) {
    $stream = $assembly.GetManifestResourceStream($name)
    try { $resources += [ordered]@{Name=$name;Length=$stream.Length;SHA256=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')} }
    finally { $stream.Dispose() }
}
foreach ($name in 'RemoteControl.exe','RemoteControl_MCP.zip','RemoteControl_Claude.mcpb','RemoteControl_WebMCP.zip') {
    $resource = @($resources | Where-Object {$_.Name -ceq $name})
    if ($resource.Count -ne 1 -or $resource[0].SHA256 -cne (Get-FileHash -LiteralPath (Join-Path $dist $name)).Hash) {
        throw "Embedded payload does not match this build: $name"
    }
}
$outputs = @(Get-ChildItem -LiteralPath $dist -File | Where-Object {$_.Extension -in @('.exe','.zip','.mcpb')} | ForEach-Object {
    $signature = Get-AuthenticodeSignature -LiteralPath $_.FullName
    [ordered]@{Name=$_.Name;Length=$_.Length;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash;Signature=$signature.Status.ToString();Signer=$signature.SignerCertificate.Subject}
})
$sdk = Join-Path (Split-Path -Parent $Root) 'SSHTunnel'
$sdkSources = @(SourceFiles $sdk | Where-Object { $_.Extension -in @('.go','.mod','.sum') } | ForEach-Object {
    [ordered]@{Path=$_.FullName.Substring($sdk.Length+1).Replace('\','/');SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
$manifest = [ordered]@{SchemaVersion=1;BuiltAtUtc=[DateTime]::UtcNow.ToString('o');ContainsPreset=($assembly.GetManifestResourceNames() -contains 'preset.txt');SourceFingerprint=$fingerprint;Sources=$sources;SSHTunnelSDK=$sdkSources;Resources=$resources;Outputs=$outputs;GoBinaries=@{Tailscale=(& go version -m (Join-Path $dist 'tsproxy.exe') | Out-String);SSHTunnel=(& go version -m (Join-Path $dist 'sshtunnel-client.exe') | Out-String)};Toolchain=@{GoLauncher=(& go version | Out-String).Trim();Csc=(Get-Item "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe").VersionInfo.FileVersion};MalwareAssessment='Build provenance only; requires independent antivirus verification'}
$manifest['InstallerFormat']='Inno Setup'
$inno=@((Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),(Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'))|Where-Object {Test-Path -LiteralPath $_}|Select-Object -First 1
if($inno){$manifest.Toolchain['InnoCompilerSHA256']=(Get-FileHash -LiteralPath $inno -Algorithm SHA256).Hash}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $dist 'RemoteControl.build.json') -Encoding UTF8
$sha.Dispose()
'Build manifest: '+(Join-Path $dist 'RemoteControl.build.json')
