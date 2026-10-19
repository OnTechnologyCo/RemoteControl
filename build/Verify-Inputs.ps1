$ErrorActionPreference = 'Stop'

function Assert-FileHash([string]$Path, [string]$Expected) {
    if ($Expected -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid pinned SHA-256' }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse-point build input: $Path" }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Expected) {
        throw "Pinned SHA-256 mismatch: $Path"
    }
}

function Get-PinnedDownload([string]$Uri, [string]$Path, [string]$Expected) {
    if (Test-Path -LiteralPath $Path) { Assert-FileHash $Path $Expected; return }
    $temporary = $Path + '.' + [guid]::NewGuid().ToString('N') + '.download'
    # 완료 전에는 캐시에 정상 파일 이름을 남기지 않는다. 다운로드 실패를 무한 대기하지 않는다.
    & "$env:WINDIR\System32\curl.exe" --fail --silent --show-error --location --proto '=https' --connect-timeout 15 --max-time 180 --output $temporary $Uri
    if ($LASTEXITCODE -ne 0) { throw "Download failed; incomplete bytes retained at $temporary" }
    Assert-FileHash $temporary $Expected
    Move-Item -LiteralPath $temporary -Destination $Path
}

function Assert-VddPayload([string]$Directory, $Pins) {
    if (-not (Test-Path -LiteralPath $Directory)) { return }
    # 폴더 전체를 임베드하면 잘못 놓은 실행 파일·설정까지 관리자 권한으로 배포된다.
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Force) {
        $pin = $Pins.PSObject.Properties | Where-Object { $_.Name -ieq $file.Name }
        if ($file.PSIsContainer -or @($pin).Count -ne 1) { throw "Unexpected VDD payload: $($file.Name)" }
        Assert-FileHash $file.FullName $pin.Value
        if ($file.Extension -in @('.dll', '.cat', '.sys', '.exe')) {
            if ((Get-AuthenticodeSignature -LiteralPath $file.FullName).Status -ne 'Valid') {
                throw "VDD signature validation failed: $($file.Name)"
            }
        }
    }
    # GitHub는 바이너리를 담지 않는다. 드라이버를 넣는 경우에만 완전한 세트를 요구한다.
    if ((Test-Path -LiteralPath (Join-Path $Directory 'MttVDD.dll')) -or (Test-Path -LiteralPath (Join-Path $Directory 'mttvdd.cat'))) {
        foreach ($pin in $Pins.PSObject.Properties) {
            if (-not (Test-Path -LiteralPath (Join-Path $Directory $pin.Name) -PathType Leaf)) {
                throw "Incomplete VDD payload: $($pin.Name)"
            }
        }
    }
}
