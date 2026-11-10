# 화면 로그인이 대리 프로세스 재시작을 넘어 이어지는지 시험한다 (bench/ScreenSessionTest.cs).
# 임시 폴더만 쓴다. 운영 설정·서비스·실제 로그인 저장소는 건드리지 않는다. 관리자 권한이 필요 없다.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $root ('artifacts\screen-session-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
$exe = Join-Path $work 'ScreenSessionTest.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /platform:x64 /langversion:5 "/out:$exe" `
    /r:System.dll /r:System.Core.dll /r:System.Security.dll (Join-Path $root 'src\Auth.cs') (Join-Path $PSScriptRoot 'ScreenSessionTest.cs')
if ($LASTEXITCODE -ne 0) { throw 'Screen session test compilation failed' }
& $exe run $work
if ($LASTEXITCODE -ne 0) { throw 'Screen session tests failed' }
