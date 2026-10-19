# RemoteControl Defender 조사 — 2026-10-15

`Trojan:Win32/Sabsik.FL.A!ml` 원인을 확정한 보고서가 아니다. HomePC에서 기존 파일 및 수정 후보의 로컬 Defender 재검사에서는 탐지가 재현되지 않았다. 실제 신고 PC의 탐지 경로·SHA-256·정의 버전·격리 기록이 확보되어야 한다.

## 수정

- 웹 MCP SDK를 1.32.1로 갱신하고 lockfile을 유지했다. Node 24.22.0, Caddy 2.11.7의 공식 파일 해시를 고정하고 Node의 OpenJS 서명도 확인한다.
- Go helper는 1.26.9로 빌드하고 암호화 모듈을 갱신한다. 빌드 과정에서 `go mod tidy`로 잠금 정보를 바꾸지 않는다.
- 본체의 텔레그램 봇 토큰 상수를 제거했다. 새 설치는 `REMOTE_CONTROL_TELEGRAM_BOT_TOKEN` 환경 변수를 사용할 수 있다. 업데이트 시 Setup이 구버전 설치 EXE의 상수 메타데이터만 읽어 PC별 DPAPI 저장소로 보존한다. 구버전 실행·토큰 재발급·설정 초기화는 하지 않는다.
- 기본 설치 파일에는 Tailscale preset이나 알림 토큰을 담지 않는다. `-IncludeTailscaleKey`를 명시한 개인 배포물은 여전히 공유 대상이 아니다.
- VDD 패키지는 고정된 파일명·해시·서명만 허용한다. 이전 폴더의 임의 설치 스크립트/실행 파일을 실행하거나 루트 인증서를 추가하는 경로를 제거했다.
- `dist/RemoteControl.build.json`은 소스·SDK·리소스·도구 체인·출력 파일 해시를 연결한다. 본체의 토큰 상수가 남으면 검증을 실패시킨다. 서명 대신의 신뢰 증명이나 악성코드 무해 판정은 아니다.

## 검증과 한계

`bench/Test-BuildSecurity.ps1`은 변조된 입력·서명 없는 DLL·예상 밖 파일 거부, 기존 토큰의 암호화 보존/반복 이관/손상 거부, 남은 드라이버 스크립트 거부를 격리 폴더에서 검사한다. 실제 드라이버 설치나 텔레그램 전송은 하지 않는다.

현재 HomePC의 실시간 보호는 켜져 있으나 `MAPSReporting=0`이고 클라우드 연결 검사가 실패했다. 클라우드 보호를 통과했다는 뜻이 아니다. Windows Sandbox와 Hyper-V도 꺼져 있어 깨끗한 가상 머신에서의 설치 검증은 완료하지 못했다. 새 본체/Setup에는 공개적으로 신뢰되는 코드 서명이 없다.

탐지 예외 등록, Defender 비활성화, 보안 정의 롤백, 격리 복원은 수행하지 않는다. 설정·기존 인증 상태와 운영 서비스는 보존한다.

## 실제 신고 PC 증거 수집

관리자 PowerShell에서:

```powershell
.\bench\Collect-DefenderEvidence.ps1 -Paths @('C:\실제경로\RemoteControl_Setup.exe','C:\실제경로\RemoteControl.exe') -Since '2026-10-10'
```

스크립트 실행 정책을 우회하지 않는다. 격리 파일이 없으면 해시를 추정하지 않는다. 출력의 `errors.json`을 함께 확인한다.

## 공식 참고 자료

- https://learn.microsoft.com/en-us/defender-endpoint/command-line-arguments-microsoft-defender-antivirus
- https://learn.microsoft.com/en-us/defender-xdr/developer-faq
- https://www.microsoft.com/en-us/wdsi/filesubmission
- https://github.com/modelcontextprotocol/typescript-sdk/security/advisories/GHSA-6qxp-vccf-f47h
- https://nodejs.org/dist/v24.22.0/SHASUMS256.txt
- https://api.github.com/repos/caddyserver/caddy/releases/tags/v2.11.7
