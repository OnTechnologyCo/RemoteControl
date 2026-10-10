# 본체를 빌드한 뒤 실행. 배포 경로는 dist로 고정한다.
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$app = Join-Path $root 'dist\RemoteControl.exe'
if (-not (Test-Path -LiteralPath $app)) { throw '먼저 빌드.ps1을 실행하세요.' }
$version = (Get-Item -LiteralPath $app).VersionInfo.FileVersion -replace '\.0$',''
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Write-Package([string]$destination, [hashtable]$entries) {
    $file = [IO.File]::Open($destination, [IO.FileMode]::Create)
    $zip = New-Object IO.Compression.ZipArchive($file, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($name in ($entries.Keys | Sort-Object)) {
            $entry = $zip.CreateEntry($name.Replace('\','/'), [IO.Compression.CompressionLevel]::Optimal)
            $output = $entry.Open()
            $inputFile = [IO.File]::OpenRead($entries[$name])
            try { $inputFile.CopyTo($output) } finally { $inputFile.Dispose(); $output.Dispose() }
        }
    } finally { $zip.Dispose(); $file.Dispose() }
}
$plugin = Join-Path $root 'plugins\remotecontrol-lan'
$manifest = Join-Path $root 'plugins\claude-mcpb\manifest.json'
foreach ($p in @($manifest, (Join-Path $plugin 'plugin.json'), (Join-Path $plugin '.codex-plugin\plugin.json'))) {
    $data = Get-Content -LiteralPath $p -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($data.version -ne $version) { throw "MCP 패키지와 본체 버전 불일치: $p" }
}
$claude = Join-Path $root 'dist\RemoteControl_Claude.mcpb'
Write-Package $claude @{'manifest.json'=$manifest; 'server/RemoteControl.exe'=$app; 'README.md'=(Join-Path $root 'docs\mcp-lan.md'); 'mcp-validation.md'=(Join-Path $root 'docs\mcp-validation.md')}
$entries = @{}
Get-ChildItem -LiteralPath $plugin -File -Recurse -Force | ForEach-Object {
    $relative = $_.FullName.Substring($plugin.Length + 1).Replace('\','/')
    $entries['remotecontrol-lan/' + $relative] = $_.FullName
}
$entries['remotecontrol-lan/server/RemoteControl.exe'] = $app
$entries['remotecontrol-lan/README.md'] = Join-Path $root 'docs\mcp-lan.md'
$entries['remotecontrol-lan/mcp-validation.md'] = Join-Path $root 'docs\mcp-validation.md'
Write-Package (Join-Path $root 'dist\RemoteControl_MCP.zip') $entries
"MCP ZIP: $(Join-Path $root 'dist\RemoteControl_MCP.zip')"
"Claude 확장: $claude"
