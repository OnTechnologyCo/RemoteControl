$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$output=Join-Path $root 'dist\tsproxy.exe'
[void][IO.Directory]::CreateDirectory((Join-Path $root 'dist'))
Push-Location (Join-Path $root 'tsproxy')
try {
    & go build -trimpath -ldflags '-s -w' -o $output .
    if($LASTEXITCODE -ne 0){throw 'Tailscale helper build failed'}
}finally{Pop-Location}
$inputFile=[IO.File]::OpenRead($output)
$outFile=[IO.File]::Create((Join-Path $root 'tsproxy.exe.gz'))
$gzip=New-Object IO.Compression.GZipStream($outFile,[IO.Compression.CompressionLevel]::Optimal)
try{$inputFile.CopyTo($gzip)}finally{$gzip.Dispose();$outFile.Dispose();$inputFile.Dispose()}
'Tailscale helper built (no running tunnels changed)'
