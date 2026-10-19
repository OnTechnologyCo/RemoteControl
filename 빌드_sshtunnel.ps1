$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$module = Join-Path $root 'sshtunnel-client'
$sdk = Join-Path (Split-Path -Parent $root) 'SSHTunnel\pkg\tunnel\tunnel.go'
if (-not (Test-Path -LiteralPath $sdk)) { throw 'SSHTunnel source is required beside this project (../SSHTunnel).' }
if (-not (Test-Path -LiteralPath (Join-Path (Split-Path $sdk) 'identity.go'))) { throw 'SSHTunnel 1.5.0 or newer is required for persistent web addresses.' }
$out = Join-Path $root 'dist\sshtunnel-client.exe'
[void][IO.Directory]::CreateDirectory((Join-Path $root 'dist'))
Push-Location $module
try {
    # 로컬 replace SDK는 ziphash가 없으므로 각 원격 의존성만 검증한다.
    $modules = @(& go list -mod=readonly -m -f '{{if not .Main}}{{if not .Replace}}{{.Path}}@{{.Version}}{{end}}{{end}}' all)
    if ($LASTEXITCODE -ne 0) { throw 'SSHTunnel dependency listing failed' }
    foreach ($dependency in $modules) {
        if ($dependency) { & go mod download $dependency; if ($LASTEXITCODE -ne 0) { throw 'SSHTunnel dependency verification failed' } }
    }
    # 보안 분석기가 실제 포함 함수를 확인할 수 있도록 Go 심볼 표를 보존한다.
    & go build -mod=readonly -trimpath -ldflags '-w' -o $out .
    if ($LASTEXITCODE -ne 0) { throw 'SSHTunnel client build failed' }
} finally { Pop-Location }
$inputFile = [IO.File]::OpenRead($out)
$outputFile = [IO.File]::Create((Join-Path $root 'sshtunnel-client.exe.gz'))
$gzip = New-Object IO.Compression.GZipStream($outputFile,[IO.Compression.CompressionLevel]::Optimal)
try { $inputFile.CopyTo($gzip) } finally { $gzip.Dispose(); $outputFile.Dispose(); $inputFile.Dispose() }
'SSHTunnel web client built; running programs are unchanged.'
