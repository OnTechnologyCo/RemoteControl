$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$output=Join-Path $root 'dist\tsproxy.exe'
[void][IO.Directory]::CreateDirectory((Join-Path $root 'dist'))
Push-Location (Join-Path $root 'tsproxy')
try {
    & go mod verify
    if($LASTEXITCODE -ne 0){throw 'Tailscale module verification failed'}
    # 보안 분석기가 실제 포함 함수를 확인할 수 있도록 Go 심볼 표를 보존한다.
    & go build -mod=readonly -trimpath -ldflags '-w' -o $output .
    if($LASTEXITCODE -ne 0){throw 'Tailscale helper build failed'}
}finally{Pop-Location}
$inputFile=[IO.File]::OpenRead($output)
$outFile=[IO.File]::Create((Join-Path $root 'tsproxy.exe.gz'))
$gzip=New-Object IO.Compression.GZipStream($outFile,[IO.Compression.CompressionLevel]::Optimal)
try{$inputFile.CopyTo($gzip)}finally{$gzip.Dispose();$outFile.Dispose();$inputFile.Dispose()}
'Tailscale helper built (no running tunnels changed)'
