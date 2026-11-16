# v1.9.13 Gemini OAuth 및 Setup 검증 — 2026-11-16

## 릴리즈 범위

GitHub main의 v1.9.12(d76b578)에서 별도 작업 트리를 만들고 Gemini OAuth 변경만 적용했다.
본체·Setup·web-mcp 버전은 1.9.13으로 맞췄다. 원래 작업 폴더의 별도 미커밋 변경은 포함하지 않았다.
PhonePilot은 문서만 참고했다.

공식 빌드_setup.ps1 -NoKey로 본체, WebMCP, Inno Setup을 함께 빌드했다.
설치 파일명은 RemoteControl_Setup_v1.9.13.exe이며 실제 본체 버전에서 자동 생성된다.
기존 검사 호환용 RemoteControl_Setup.exe는 동일 바이트다.

## 검사 결과

| 검사 | 결과 |
|---|---|
| web-mcp npm test | 78개 중 77 통과, 실패 0, 명시적 SSH 환경이 필요한 1개 건너뜀 |
| bench/Test-Mcp.ps1 | 공식 SDK/Windows fixture 통합 42개 통과 |
| bench/Test-BuildSecurity.ps1 | 자격 증명 이전 4개, 입력 검증 5개, 드라이버 디렉터리 정책 2개 통과 |
| bench/Test-SecurityPolicy.ps1 | 비관리자 권한·DPAPI·IPC 22개 통과 |
| bench/Test-TelegramSetup.ps1 | 합성 토큰·선택 입력·ACL·DPAPI·원자적 교체 27개 통과 |
| bench/Test-InstallerUpgrade.ps1 -BuiltSetup | 실제 설치 엔진 추출·구버전 파일 교체·설정 보존 27개 통과 |
| bench/Test-SetupLayout.ps1 -BuiltSetup | 배율·글꼴·화면 크기 조건 60개 통과 |
| bench/Test-SetupOptionalWeb.ps1 | 선택하지 않은 웹 설정·기존 수동 주소/포트·패키지 보존 통과 |
| bench/Test-WebOnly.ps1 | 구형 연결 제거·패키지/UI·기존 사용자 패키지 보존 34개 통과 |

처음 새 작업 폴더에서는 Caddy 시험 실행 파일이 없어 HTTPS 검사 일부가 실패했다.
필수 파일을 준비한 뒤 전체 npm test를 다시 실행해 위 결과를 확인했다.
보안 검사의 첫 실행은 합성 IPC의 일시적 파일 공유 오류로 실패했으며 동일 검사 재실행에서 22개가 통과했다.
SetupOptionalWeb은 설치 엔진 생성 전 실행에서 파일 부재로 실패했고, 빌드 완료 후 통과했다.
실패한 실행이나 실행하지 않은 항목을 통과로 계산하지 않았다.

Google 브라우저 시험은 격리 Chrome + Caddy HTTPS + 실제 Windows fixture로 로그인·취소·자동/수동 복귀·pc_get_status·refresh를 검사했다.
Google 콜백은 브라우저에서 가로챘으므로 실제 Gemini 계정 연결 성공을 뜻하지 않는다.
실제 계정 승인, 외부 SSH 터널 통합, 운영 PC에서의 설치 실행은 별도 확인 사항이다.

## 산출물 및 무결성

[릴리즈 다운로드](https://github.com/OnTechnologyCo/RemoteControl/releases/tag/v1.9.13)

| 파일 | SHA-256 |
|---|---|
| RemoteControl_Setup_v1.9.13.exe | f0bd53dedd348fde29e436324ef4902a66cc12445e22ab923ef56c7bc62287ca |
| RemoteControl_WebMCP.zip | e29ff07b6a8c5f6477599c006fb0156458757fe9043e284cce28d158594c80b2 |

- Setup 크기: 132,298,406 바이트. 본체와 Inno 설치 파일의 숫자 버전은 모두 1.9.13.0이다.
- 내장 본체·WebMCP 리소스 해시는 실제 빌드 산출물과 일치한다.
- ZIP 안의 gateway/auth-state/server/tailscale-server/README/package/lockfile 해시는 릴리즈 소스와 일치한다.
- RemoteControl.build.json의 모든 소스·출력 해시를 다시 검증했다. ContainsPreset=false다.
- Node/Caddy 고정 해시, Node 발급자 서명, VDD 파일/서명, Go 1.27.3 및 lockfile 검증을 유지했다.
- 공개 설치 파일에는 실제 로그인 자격 증명·텔레그램 토큰·Tailscale 키·알림 수신자를 포함하지 않았다.
- Setup 자체의 새 코드 서명은 추가하지 않았다. 빌드 증빙은 독립적인 백신 판정을 대신하지 않는다.
- CRLF를 정상 줄바꿈으로 처리한 git diff --check를 통과했다.

[Gemini 연결 방법과 인증 경계](gemini-oauth-20261116.md)
