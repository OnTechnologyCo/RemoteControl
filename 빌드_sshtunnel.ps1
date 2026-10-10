$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$module = Join-Path $root 'sshtunnel-client'
$sdk = Join-Path (Split-Path -Parent $root) 'SSHTunnel\pkg\tunnel\tunnel.go'
if (-not (Test-Path -LiteralPath $sdk)) { throw 'SSHTunnel source is required beside this project (../SSHTunnel).' }
$out = Join-Path $root 'dist\sshtunnel-client.exe'
[void][IO.Directory]::CreateDirectory((Join-Path $root 'dist'))
Push-Location $module
try {
    & go mod tidy
    if ($LASTEXITCODE -ne 0) { throw 'SSHTunnel SDK dependency preparation failed' }
    & go build -trimpath -ldflags '-s -w' -o $out .
    if ($LASTEXITCODE -ne 0) { throw 'SSHTunnel client build failed' }
} finally { Pop-Location }
$inputFile = [IO.File]::OpenRead($out)
$outputFile = [IO.File]::Create((Join-Path $root 'sshtunnel-client.exe.gz'))
$gzip = New-Object IO.Compression.GZipStream($outputFile,[IO.Compression.CompressionLevel]::Optimal)
try { $inputFile.CopyTo($gzip) } finally { $gzip.Dispose(); $outputFile.Dispose(); $inputFile.Dispose() }
'SSHTunnel web client built; running programs are unchanged.'
