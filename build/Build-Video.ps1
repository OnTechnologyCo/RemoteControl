[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Verify-Inputs.ps1')
. (Join-Path $PSScriptRoot 'Use-GoToolchain.ps1')
$pins = (Get-Content (Join-Path $PSScriptRoot 'dependencies.json') -Raw -Encoding UTF8 | ConvertFrom-Json).ffmpeg
$cache = Join-Path $root 'artifacts\video-deps'
[IO.Directory]::CreateDirectory($cache) | Out-Null
$archive = Join-Path $cache 'ffmpeg-n9.0.2-lgpl.zip'
Get-PinnedDownload $pins.url $archive $pins.archiveSha256
$ffmpeg = Join-Path $cache ($pins.directory + '\bin\ffmpeg.exe')
if (!(Test-Path -LiteralPath $ffmpeg)) { Expand-Archive -LiteralPath $archive -DestinationPath $cache -Force }
Assert-FileHash $ffmpeg $pins.exeSha256
[IO.Directory]::CreateDirectory((Join-Path $root 'dist')) | Out-Null
$helper = Join-Path $root 'dist\video-helper.exe'
Push-Location (Join-Path $root 'video-helper')
try {
    & go mod download
    if ($LASTEXITCODE -ne 0) { throw 'Video dependency download failed' }
    & go mod verify
    if ($LASTEXITCODE -ne 0) { throw 'Video dependency verification failed' }
    & go build -mod=readonly -trimpath -ldflags '-w' -o $helper .
    if ($LASTEXITCODE -ne 0) { throw 'WebRTC helper build failed' }
} finally { Pop-Location }
Assert-GoBinary $helper
foreach ($pair in @(@($helper,'video-helper.exe.gz'), @($ffmpeg,'video-ffmpeg.exe.gz'))) {
    $inputFile = [IO.File]::OpenRead($pair[0])
    $outputFile = [IO.File]::Create((Join-Path $root $pair[1]))
    $gzip = New-Object IO.Compression.GZipStream($outputFile,[IO.Compression.CompressionLevel]::Optimal)
    try { $inputFile.CopyTo($gzip) } finally { $gzip.Dispose(); $outputFile.Dispose(); $inputFile.Dispose() }
}
'Pinned LGPL FFmpeg and Pion video helper built. Running services are unchanged.'
