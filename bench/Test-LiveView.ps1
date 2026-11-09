param(
    [string]$Scenes = 'scroll,video,typing',
    [int]$Seconds = 10,
    [int]$Width = 2560,
    [double]$Mbps = 0,
    [double]$Rtt = 0,
    [string]$Label = 'run',
    [switch]$NoBuild
)
# 실제 화면 전송 경로를 합성 화면으로 끝에서 끝까지 잰다 (bench/LiveViewBench.cs 참조).
# 실제 화면 캡처·입력 주입·운영 서비스 변경은 하지 않는다. -Width 는 이 PC 의 모니터 중 하나여야 한다.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root 'artifacts\live-view'
[void][IO.Directory]::CreateDirectory($dest)
$exe = Join-Path $dest 'LiveViewBench.exe'
if (-not $NoBuild) {
    $text = [IO.File]::ReadAllText((Join-Path $root 'src\Native.cs'))
    $pattern = '\[DllImport\("user32.dll", SetLastError = true\)\]\s*internal static extern uint SendInput\(uint nInputs, INPUT\[\] pInputs, int cbSize\);'
    if ([regex]::Matches($text, $pattern).Count -ne 1) { throw 'SendInput bench interception no longer matches' }
    $text = [regex]::Replace($text, $pattern, 'internal static uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize) { return LiveViewBench.BlockInput(nInputs); }')
    $native = Join-Path $dest 'Native.cs'
    [IO.File]::WriteAllText($native, $text, (New-Object Text.UTF8Encoding($true)))
    $src = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | Where-Object { $_.Name -notin @('Native.cs', 'DesktopDup.cs', 'Desktops.cs') } | ForEach-Object FullName
    # 제품 빌드와 같은 최적화로 컴파일해야 압축·지문 비용이 실제와 같다.
    & 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /unsafe /optimize+ /target:exe /platform:x64 /langversion:5 "/out:$exe" /main:RemoteControl.LiveViewBench `
        /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.ServiceProcess.dll /r:System.Security.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.Management.dll `
        $native (Join-Path $PSScriptRoot 'LiveViewBench.cs') $src
    if ($LASTEXITCODE -ne 0) { throw 'Live view bench compilation failed' }
}
& node (Join-Path $PSScriptRoot 'LiveViewBench.js') --scenes $Scenes --secs $Seconds --width $Width --mbps $Mbps --rtt $Rtt --label $Label
if ($LASTEXITCODE -ne 0) { throw 'Live view bench failed' }
