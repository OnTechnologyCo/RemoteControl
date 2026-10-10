# 기존 본체/서비스를 건드리지 않고 웹 중계 배포 패키지만 dist에 만든다.
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$source=Join-Path $root 'web-mcp'
$stage=Join-Path $root 'dist\web-mcp'
[void][IO.Directory]::CreateDirectory($stage)
$allowed=@('node.exe','caddy.exe','server.mjs','tailscale-server.mjs','Install-TailscaleMcp.ps1','Disable-TailscaleMcp.ps1','gateway.mjs','check-certificate.mjs','WebMcpConfig.ps1','package.json','package-lock.json','node_modules','Install-WebMcp.ps1','Disable-WebMcp.ps1','Export-Plugin.ps1','README.md','THIRD-PARTY.txt','NODE-LICENSE.txt','CADDY-LICENSE.txt')
Get-ChildItem -LiteralPath $stage -Force | ForEach-Object { if($_.Name -notin $allowed){throw "배포 폴더에 예상하지 않은 파일이 있습니다: $($_.Name). 설정/인증서를 패키지에 담지 않습니다."} }
$cache=Join-Path $root 'artifacts\web-mcp-downloads'
[void][IO.Directory]::CreateDirectory($cache)
$version='2.11.7'
$zipName="caddy_${version}_windows_amd64.zip"
$checksums="caddy_${version}_checksums.txt"
$base="https://github.com/caddyserver/caddy/releases/download/v$version/"
foreach($name in @($zipName,$checksums)) {
    $file=Join-Path $cache $name
    if(-not(Test-Path -LiteralPath $file)){Invoke-WebRequest -UseBasicParsing ($base+$name) -OutFile $file}
}
$line=Get-Content -LiteralPath (Join-Path $cache $checksums) | Where-Object { $_ -match ([regex]::Escape($zipName)+'$') }
if(@($line).Count -ne 1){throw 'Caddy checksum entry missing'}
$expected=($line -split '\s+')[0]
if($expected -notmatch '^[a-fA-F0-9]{128}$'){throw 'Caddy SHA-512 checksum format mismatch'}
if((Get-FileHash -LiteralPath (Join-Path $cache $zipName) -Algorithm SHA512).Hash -ine $expected){throw 'Caddy SHA-512 mismatch'}
$caddyDir=Join-Path $cache "caddy-$version"
Expand-Archive -LiteralPath (Join-Path $cache $zipName) -DestinationPath $caddyDir -Force
$node=(Get-Command node.exe).Source
if((Get-AuthenticodeSignature -LiteralPath $node).Status -ne 'Valid'){throw '설치된 Node.exe 서명 확인 실패'}
if([version]((& $node --version).TrimStart('v')) -lt [version]'24.14.0'){throw 'Node 24.14.0 이상이 필요합니다.'}
Copy-Item -LiteralPath $node -Destination (Join-Path $stage 'node.exe')
Copy-Item -LiteralPath (Join-Path $caddyDir 'caddy.exe') -Destination (Join-Path $stage 'caddy.exe')
foreach($name in @('package.json','package-lock.json','server.mjs','tailscale-server.mjs','Install-TailscaleMcp.ps1','Disable-TailscaleMcp.ps1','gateway.mjs','check-certificate.mjs','WebMcpConfig.ps1','Install-WebMcp.ps1','Disable-WebMcp.ps1','Export-Plugin.ps1','README.md')) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination $stage
}
Push-Location $stage
try { & npm.cmd ci --omit=dev --ignore-scripts --no-audit --no-fund; if($LASTEXITCODE -ne 0){throw '배포 의존성 설치 실패'} } finally {Pop-Location}
$nodeVersion=(& $node --version).Trim()
$nodeLicense=Join-Path $cache "NODE-$nodeVersion-LICENSE.txt"
if(-not(Test-Path -LiteralPath $nodeLicense)){Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/nodejs/node/$nodeVersion/LICENSE" -OutFile $nodeLicense}
Copy-Item -LiteralPath $nodeLicense -Destination (Join-Path $stage 'NODE-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $caddyDir 'LICENSE') -Destination (Join-Path $stage 'CADDY-LICENSE.txt')
@"
Node.js: $(& $node --version) (https://nodejs.org/), NODE-LICENSE.txt
Caddy: $version (https://github.com/caddyserver/caddy), CADDY-LICENSE.txt
Caddy release ZIP SHA-512: $expected
MCP SDK, Express and dependencies: package-lock.json, licenses in node_modules.
This is a server deployment ZIP, not an account-installed ChatGPT plugin.
No RemoteControl password, OAuth token, TLS private key or existing PC settings are included.
"@ | Set-Content -LiteralPath (Join-Path $stage 'THIRD-PARTY.txt') -Encoding UTF8
Add-Type -AssemblyName System.IO.Compression
$dest=Join-Path $root 'dist\RemoteControl_WebMCP.zip'
$output=[IO.File]::Open($dest,[IO.FileMode]::Create)
$zip=New-Object IO.Compression.ZipArchive($output,[IO.Compression.ZipArchiveMode]::Create,$false)
try {
    Get-ChildItem -LiteralPath $stage -File -Recurse -Force | ForEach-Object {
        $relative=$_.FullName.Substring($stage.Length+1).Replace('\','/')
        $entry=$zip.CreateEntry('web-mcp/'+$relative,[IO.Compression.CompressionLevel]::Optimal)
        $inputFile=[IO.File]::OpenRead($_.FullName);$entryStream=$entry.Open()
        try{$inputFile.CopyTo($entryStream)}finally{$inputFile.Dispose();$entryStream.Dispose()}
    }
}finally{$zip.Dispose();$output.Dispose()}
Get-Item -LiteralPath $dest | Select-Object FullName,Length
