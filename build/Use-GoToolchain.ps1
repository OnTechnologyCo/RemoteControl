# Build commands run with a pinned, checksum-verified Go toolchain, never the host's older default.
$env:GOTOOLCHAIN='go1.27.3'
$env:GOOS='windows'
$env:GOARCH='amd64'
$env:CGO_ENABLED='0'
$actual=(& go version | Out-String).Trim()
if($LASTEXITCODE -ne 0 -or $actual -notmatch '^go version go1\.27\.3 '){throw "Required Go toolchain unavailable: $actual"}
function Assert-GoBinary([string]$Path) {
    $version=(& go version -m $Path | Out-String)
    if($LASTEXITCODE -ne 0 -or $version -notmatch ': go1\.27\.3\s' -or $version -notmatch 'GOOS=windows' -or $version -notmatch 'GOARCH=amd64'){throw "Wrong embedded Go build version or target: $Path"}
}
