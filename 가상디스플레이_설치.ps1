# 모니터 없는 PC 에 가상 모니터를 만든다.
#
# 왜 필요한가.
#   모니터를 안 꽂으면 윈도우가 GPU 드라이버를 쓰지 못하고 "Microsoft 기본 디스플레이
#   어댑터" 로 잡는다. 그 장치가 내놓는 해상도는 640x480 하나뿐이다.
#   ChangeDisplaySettingsEx 는 장치가 지원하는 모드만 받으므로, 어떤 프로그램도
#   이걸 늘릴 수 없다. 원격제어 화면이 640x480 으로 나오는 이유가 이것이다.
#
#   가상 디스플레이 드라이버를 넣으면 윈도우가 "모니터가 있다" 고 보고 FHD 이상을
#   내놓는다. 그러면 원격제어가 알아서 1920x1080 으로 맞춘다.
#
# 준비물 (직접 받아야 한다. 배포 허가 문제로 프로그램에 넣지 않았다)
#   usbmmidd_v2  - Amyuni USB Mobile Monitor Display Driver
#   서명된 드라이버이고 무료로 재배포된다. 압축을 풀면 이런 파일들이 나온다.
#     deviceinstaller64.exe / deviceinstaller.exe
#     usbmmidd.inf / usbmmIdd.dll / usbmmidd.cat ...
#
# 쓰는 법 (그 PC 에서 관리자 권한으로)
#   .\가상디스플레이_설치.ps1 -Path C:\usbmmidd_v2
#   .\가상디스플레이_설치.ps1 -Path C:\usbmmidd_v2 -Remove     # 되돌리기
#
# 대안: HDMI/DP 더미 플러그를 꽂는다. 5천~1만원이고 설정이 필요 없다.
#       그 PC 에 손이 닿는다면 이쪽이 확실하다.

param(
    [Parameter(Mandatory = $true)] [string] $Path,
    [switch] $Remove
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'

function Need-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
              [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "관리자 권한으로 실행하세요. (드라이버 설치에 필요합니다)"
    }
}

function Show-Modes([string] $label) {
    Add-Type -AssemblyName System.Windows.Forms
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    "  $label : $($b.Width) x $($b.Height)"
}

Need-Admin

if (-not (Test-Path $Path)) { throw "폴더가 없습니다: $Path" }

# 64비트 윈도우면 64비트 설치기를 쓴다.
$inst = Join-Path $Path "deviceinstaller64.exe"
if (-not (Test-Path $inst)) { $inst = Join-Path $Path "deviceinstaller.exe" }
if (-not (Test-Path $inst)) { throw "deviceinstaller 를 찾지 못했습니다: $Path" }

Push-Location $Path
try {
    Show-Modes "지금"

    if ($Remove) {
        "가상 모니터를 끕니다…"
        & $inst enableidd 0
        "드라이버를 제거합니다…"
        & $inst remove usbmmidd
        "제거했습니다."
    }
    else {
        "드라이버를 설치합니다…"
        & $inst install usbmmidd.inf usbmmidd

        "가상 모니터를 켭니다…"
        & $inst enableidd 1

        Start-Sleep -Seconds 3
        Show-Modes "설치 후"

        ""
        "원격제어가 5초 안에 알아서 1920x1080 으로 맞춥니다."
        "확인:  Get-Content `"`$env:ProgramData\RemoteControl\service.log`" -Tail 5"
        ""
        "PC 를 껐다 켜도 유지됩니다. 되돌리려면 -Remove 를 붙여 다시 실행하세요."
    }
}
finally { Pop-Location }
