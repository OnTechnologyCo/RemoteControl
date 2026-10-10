# Setup v1.8.10 Tailscale 기본 443 및 HTTPS 중계 확인

`dist/RemoteControl_Setup.exe`: 2026-10-04 **19:06:51 KST**, 버전 **1.8.10.0**, **84,826,112 bytes**.
SHA-256: `EFEC64424815439942794D152857AF5508CDE62F3DEE25E34F68E7091548A1E9`.

Tailscale 자동 연결에서도 [기본 HTTPS 포트(443) 사용]을 지원한다. 화면·파일·화면 로그인은
기존 본체로, MCP와 OAuth 경로는 인증 중계로 분기하여 같은 tsnet 443 리스너를 쓴다.
자동 생성 URL·플러그인·알림에서는 `:443`을 생략한다. 저장된 8443/10000 선택과 수동 주소는 유지한다.

`HTTPS proxy required`는 실제 공개 Claude 로그인 페이지를 HTTP/3로 반복 요청해 **8회 중 2회** 재현했다.
DDNS Caddy에 HTTP/1.1·HTTP/2 및 `Alt-Svc: clear`를 적용한 뒤 같은 브라우저 프로필에서
**20회 모두 HTTP/2 200과 로그인 폼**을 확인했다. 프록시 인증 검사는 변경하지 않았다.
`artifacts/web-registration-diagnostic/browser-yuOcH0/responses.json`,
`artifacts/web-registration-diagnostic/browser-Y0QZiv/responses.json`.

이번에는 18:57에 실행 중인 **소유 Caddy 서비스만** 사전 검증·백업 후 수정했다.
공개 URL/포트/방화벽/홈페이지 루트 이동을 보존했고 Node OAuth 프로세스도 그대로 유지했다.
적용 기록: `artifacts/web-registration-diagnostic/http3-repair-20261004-185737.json`.
백업: `C:\ProgramData\RemoteControlWebMcp\before-http3-repair-20261004-185740.Caddyfile`.

검증 결과:

- 별도 임시 Tailscale 노드의 실제 공개 443에서 화면과 MCP가 같은 origin을 사용함을 확인했다.
  두 앱 각각 실제 브라우저 자동/수동 복귀, PKCE와 시험용 PC 정보 조회 통과.
  `artifacts/tailscale-public-test/run-pA29qd/verification.json`.
- `tsproxy/go test ./...`: 같은 443의 MCP/OAuth 경로 분기, 기존 화면 경로 유지, 프록시 인증·Host 검사 통과.
- `web-mcp/npm test`: Tailscale 443/8443/10000, 두 앱 OAuth, 인증서·브라우저 폼 등 **18개 통과**.
- `Test-TailscaleMcp.ps1`: 수동 설정 보존, 443 URL/플러그인 생성, 알림 등 **19개 통과**.
  `artifacts/tailscale-controller-test/048ad14b3a0b4d94ad8934048fb0240c`.
- 완성 Setup 내장 리소스/dist 해시 일치와 실제 패키지 배치 확인.
  `artifacts/setup-web-test/5361ea58b6ed4cf3b03cf90ef5ab71ff/verification.json`.
- 완성 Setup에서 Tailscale 443 선택/해제, 각 방식의 포트 복원, 나중 설정과 수동 주소 보존 확인.
  `artifacts/setup-optional-web-test/6122bf0053c74328badcad7911382dc4/verification.json`.
- 설치·제거·직접 입력 웹 설정·Tailscale 웹 설정의 DPI/폰트/화면 **40개 조건 통과**.
  `artifacts/setup-layout-test/2a23e1c45ee24b58bfe023aa2ce8e50c`.

Tailscale 시험은 시험용 PC만 노출하고 앱 callback은 시험 브라우저에서 가로챘다.
사용자의 실제 ChatGPT/Claude 계정 등록 완료를 대신 검증한 것은 아니다.
실행 중인 화면 Tailscale 노드, 수동 DDNS MCP 설정은 자동 방식으로 전환하지 않았고 텔레그램 실발송도 하지 않았다.

업데이트 후 [LLM / MCP 설정] → [웹 ChatGPT / Claude 설정] → [Tailscale 주소 자동 연결]에서
[기본 HTTPS 포트(443) 사용]을 선택하고 적용한다. 생성되는 `https://장비주소.ts.net/mcp`를 등록한다.

---

## 이전 Setup v1.8.8 기본 HTTPS 포트 확인

`dist/RemoteControl_Setup.exe`: 2026-10-04 **18:45:44 KST**, 버전 **1.8.8.0**, **84,822,016 bytes**.
SHA-256: `8A31B4FD6A588A32A1FA17FCAEAFD50AD929931FF5B2059F5D5C402D17897A27`.

직접 입력 웹 연결의 1024 이상 제한에서 443을 허용하고 [기본 HTTPS 포트(443) 사용]을 추가했다.
443 선택/직접 입력 시 URL에서 포트를 생략하며, 선택 해제 시 앞서 입력한 사용자 지정 포트를 복원한다.
직접 입력/Tailscale 모드 전환 시에도 443 선택을 보존한다. Tailscale 자동 MCP 포트는 8443/10000 그대로다.
동일 소유 Caddy의 기존 홈페이지 루트 이동과 MCP/OAuth 경로를 하나의 443 사이트에서 함께 구성한다.
다른 프로그램이 점유한 포트는 기존 사전 검사로 거부하며 인증서 자동 발급은 사용하지 않는다.

검증 결과:

- 완성된 Setup 내장 리소스/dist 해시 일치, 배포 파일 배치 및 `:443` 없는 플러그인 URL 확인.
  `artifacts/setup-web-test/e8cd730342b843c587eb468b70ceeabc/verification.json`.
- 실제 입력 칸의 443 입력, 체크 선택/해제, 사용자 지정 포트 복원, 모드 전환 보존 및 웹 설정 생략 확인.
  `artifacts/setup-optional-web-test/404ffcdd39054fec990c46106c11877b/verification.json`.
- 완성된 Setup의 DPI·텍스트 크기·작업 영역 30개 조건 통과. 기본 포트 선택 화면도 시각 확인.
  `artifacts/setup-layout-test/f936834a67a9425881c393b3923b383c`.
- 실제 생성 Caddy 설정을 시험용 포트로 옮겨 홈페이지 302, OAuth metadata 200, 인증 없는 MCP 401 확인.
  같은 443 사이트로 생성되며 인증서 자동 발급·추가 80 포트가 없음을 확인했다.
  `artifacts/web-port-test/883812efaa7e416dad0a917ecdf20d42/result.json`.
- `web-mcp/npm test`: 실제 브라우저 폼·두 앱 OAuth·PKCE·격리 MCP 등 16개 통과.

이번 빌드/시험에서 실행 중인 서비스, 설치본, 실제 포트와 방화벽은 변경하지 않았다.
18:43 읽기 전용 확인에서는 설치본 v1.8.7, 저장/수신 HTTPS 포트 1024였다.
59334 연결은 실패했고 443 `/mcp`는 기존 이동 안내 404, 1024 metadata는 200과 올바른 인증 안내를 반환했다.
이 기록은 사용자 계정에서 ChatGPT/Claude 등록이 성공했다는 의미가 아니다.

업데이트 후 [LLM / MCP 설정] → [웹 ChatGPT / Claude 설정]에서 [기본 HTTPS 포트(443) 사용]을 적용한다.
적용 성공 후 등록 URL은 `https://najhna.dyndns.org/mcp`다. 설치 폴더의 웹 플러그인 ZIP도 그 주소로 생성된다.

---

## 이전 Setup v1.8.7 웹 MCP·Tailscale·알림 확인

`dist/RemoteControl_Setup.exe`: 2026-10-04 **17:42:09 KST**, 버전 **1.8.7.0**, **84,820,480 bytes**.
SHA-256: `D4817ADB0E86B712CAA1E967D481E3F0871CC66CEDD1DF9549DD5773FF743D98`.

직접 입력 MCP는 화면 터널이 Tailscale이어도 변경하지 않는다. 직접 입력한 `.ts.net` 주소도
보존한다. 명시적으로 선택한 자동 연결만 주소·OAuth·플러그인을 갱신한다.
MCP 공개 응답을 확인한 뒤 텔레그램으로 주소를 자동 발송하며, 알림 창의 [접속 주소 보내기]로
현재 화면/MCP 주소와 포트를 직접 보낼 수 있다. 확인 전인 주소는 상태를 표시한다.

검증 결과:

- 완성된 Setup의 내장 리소스/dist 해시 일치, 실제 파일 배치, 새 Tailscale 실행 파일 포함 확인.
  `artifacts/setup-web-test/f02a121f1ce54c3696d4d16381f4e184/verification.json`.
- 웹 설정 생략, 기존 플러그인 유지, 수동/자동 선택, 수동 주소·포트 및 `.ts.net` 보존 확인.
  `artifacts/setup-optional-web-test/da63832309e54ae0964dc80a6d64811e/verification.json`.
- `Test-SetupLayout.ps1 -BuiltSetup`: DPI·폰트·작업 영역 **30개 조건 통과**.
  `artifacts/setup-layout-test/23a4721391b44362aecb8e6f76d40efc`.
- `Test-TailscaleMcp.ps1`: 실제 컨트롤러·설정·플러그인·알림 UI 코드 **16개 검사 통과**.
  지연 완료의 수동 설정 덮어쓰기 차단, 알림 재시도/중복 방지, 전송 중 heartbeat 유지와 주소 버튼 확인.
  `artifacts/tailscale-controller-test/6894edfd1a5242e89f1e372f1725107a`.
- `Test-Config.ps1`: 설정 동시 저장·오류 복구 **7개 검사 통과**.
- `Test-SshTunnel.ps1`: 실제 SSH와 기존 터널 제어 **29개 검사 통과**.
- `tsproxy/go test ./...`: 별도 MCP 프록시 헤더·Host·loopback 제한 통과.
- `web-mcp/npm test`: OAuth·Caddy·브라우저 폼·격리 MCP 등 **16개 시험 통과**.
- `Test-TailscalePublic.ps1`: 별도 임시 Tailscale 노드의 실제 공개 HTTPS로 화면과 MCP 확인.
  ChatGPT/Claude 각각 실제 브라우저 자동/수동 복귀·PKCE·가짜 PC 조회 통과.
  `artifacts/tailscale-public-test/run-26yCRP/verification.json`.

기존 설치본·서비스·공유기·다른 서버 포트는 변경하지 않았다. 텔레그램 실발송,
사용자 ChatGPT/Claude 계정 등록, 새 LocalService 작업의 실제 설치는 실행하지 않았다.
공개 OAuth 시험은 callback을 시험 브라우저에서 가로채 복귀를 확인했고 실제 PC 대신 시험용 정보를 반환했다.

위 Setup으로 업데이트한 후 [LLM / MCP 설정] → [웹 ChatGPT / Claude 설정]에서 원하는 방식을 적용한다.
Tailscale 자동 방식은 공개 포트 8443/10000, 내부 중계 포트 기본 59335를 쓴다. 최초 앱 등록·로그인은
사용자가 진행한다. 생성된 웹 ZIP은 설치 폴더의 `plugins/RemoteControl_WebPlugin.zip`에 있다.

---

## 이전 v1.8.0 검증 기록 (현재 설치 안내가 아님)

`dist/RemoteControl_Setup.exe`를 2026-10-04 12:19:09에 다시 빌드했다.
파일 버전은 **1.8.0.0**, 크기는 **85,296,128 bytes**다.
SHA-256: `4D59CF6A174F2EB57C66B1A0EE5DFCB62AE5072B7EF2D6ED168356A81748E50C`.

기존 설치 순서에 웹 중계 리소스를 포함했고, 설치 시 `plugins/RemoteControl_WebPlugin.zip`을
설치 창의 DDNS 주소로 생성한다. 별도 스크립트를 실행하지 않아도 Setup의 웹 연결 선택으로
HTTPS 서비스와 자동 실행 설치를 호출한다. 공개 443의 라우터 연결은 별도로 필요하다.

`bench/Test-SetupWebMcp.ps1`이 **완성된 EXE 자체**를 읽어 다음을 확인했다.

- 내장 본체, LAN ZIP, Claude MCPB, 웹 서버 ZIP의 SHA-256이 dist 파일과 일치한다.
- 실제 설치 때 쓰는 패키지 배치 함수를 격리 폴더에서 실행해 업로드 ZIP과 웹 실행 파일을 확인했다.
- 웹 플러그인 이름/버전, `streamable-http`, `https://najhna.dyndns.org/mcp`를 확인했다.
- DDNS 변경 시 ZIP의 주소도 변경된다. 잘못된 주소 5종을 거부한다.
- Setup 창에 웹 연결 선택과 주소 입력 컨트롤이 있다.
- 빌드 후 웹 플러그인 ZIP 생성도 같은 Setup 코드로 수행한다.

LAN 실통신 시험 35개와 웹 인증/HTTPS 시험 13개가 통과했다.
시험은 실제 설치, Windows 서비스 등록, 방화벽 변경을 실행하지 않았다.
공개 HTTPS 인증서 발급과 사용자 ChatGPT 계정 연결은 Setup 실행 후 확인할 항목이다.

사용자는 `RemoteControl_Setup.exe`에서 웹 연결을 선택하고 설치한 뒤
`C:\Program Files\RemoteControl\plugins\RemoteControl_WebPlugin.zip`을 웹 플러그인 업로드에 사용한다.
설치 위치를 바꿨으면 해당 설치 폴더의 `plugins`를 사용한다.
