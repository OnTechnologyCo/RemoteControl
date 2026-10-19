## LAN IP / 인터넷 없는 내부망 (v1.8.18)

여러 랜카드의 기본·추가 IPv4를 등록하고 적용하면 **연결 스크립트** 창에서 사용할 IP를 선택해 Claude Code, VS Code, Cursor, Codex 설정을 복사할 수 있다. 전체 안내는 각 IP의 `/mcp`, `/authorize`, `/ca/RemoteControl-LAN-CA.crt` 주소와 실제 공개 CA PEM 파일 바이트의 SHA256을 포함한다. 기본 포트 443은 URL에서 생략하며, 다른 포트는 모든 주소에 반영한다.

각 IP는 자신의 OAuth issuer·resource와 로그인·전송 경로를 사용한다. IP를 추가하거나 기본 순서를 바꾸어도 기존 주소의 CA·인증 상태를 보존한다. 공개 CA는 정확한 파일 경로의 GET/HEAD 요청만 제공하며, 접속 허용 클라이언트 CIDR 검사를 따른다. 첫 CA 등록 전에는 HTTPS 다운로드도 신뢰되지 않을 수 있으므로 대상 PC의 `certificates` 폴더에서 공개 파일을 전달하고 표시된 SHA256을 대조한다.

설치 후 **LLM / MCP 설정 → LAN / 내부망 설정**에서 이 PC의 IPv4와 접속 허용 대역을 지정한다. 기본 HTTPS **443**이며 접속 주소는 `https://LAN_IP/mcp`이다. DDNS·터널·인터넷 인증 조회 없이 로컬 MCP 클라이언트가 DCR + PKCE로 로그인한다. 클라이언트 PC에는 대상 PC의 공개 CA를 최초 1회 등록한다. 기존 외부 DDNS WebMCP가 같은 443을 사용하는 경우 하나의 소유 HTTPS 서비스에서 도메인과 LAN IP를 분기한다.

[LAN 설정·인증서·클라이언트 사용법](../docs/webmcp-lan-offline.md). 외부 웹 ChatGPT/Claude 커넥터와 폐쇄망 로컬 MCP 클라이언트는 접속 가능한 네트워크가 다르며 클라우드 앱이 사설 IP에 직접 접속할 수 있다는 뜻은 아니다.

# 원격제어 웹 ChatGPT / Claude 연결



공인 IP와 DDNS가 있는 PC에서 `https://DDNS주소:59334/mcp`를 ChatGPT에 등록하고,

Claude에서도 같은 MCP 주소를 사용자 지정 커넥터에 등록한다.

연결 화면에서 **기존 원격제어 ID·비밀번호**로 로그인한다. 별도의 원격제어 인터넷 계정은 필요 없다.



이 파일이 들어 있는 ZIP은 **서버 설치 패키지**다. ChatGPT에 올리는 플러그인 ZIP과 다르다.

HTTPS 서버를 먼저 개통해야 한다. 기존 `RemoteControl_MCP.zip`은 로컬 stdio/LAN 연결용이다.



## 구성



```text

웹 ChatGPT → DDNS HTTPS 지정 포트 (기본 59334) → Caddy → 127.0.0.1:59332 웹 인증 중계

                                              ↓ 로그인 쿠키 / MCP

                                     127.0.0.1:기존 LAN MCP 포트

                                              ↓

                                       원격제어 대리 프로세스

```



웹 중계와 HTTPS는 LocalService로 실행한다. 도구는 기존 원격제어의 권한과 MCP 설정을 따른다.

LAN 파일 폴더 제한은 명령 실행 샌드박스가 아니다. 명령을 허용하면 원격 대리 프로세스의

Windows 권한으로 실행된다. 로그인 화면에 이 권한을 표시하고 연결 동의를 받는다.

화면·외부망 터널 포트에는 명령 API를 추가하지 않는다.



## 설치

### Tailscale 주소 자동 연결

화면 연결에 Tailscale을 쓰더라도 **직접 입력한 MCP 주소는 자동으로 바꾸지 않는다.**
웹 설정의 기본 방식은 **주소 직접 입력 (자동 변경 안 함)**이다. 직접 입력한 `.ts.net` 주소도
동일하게 유지된다. 자동 연결은 아래 항목을 명시적으로 선택하고 적용한 경우에만 동작한다.

1. 원격제어의 외부망 연결에서 Tailscale을 설정하고, ID·비밀번호와 잠금 화면 제어 서비스를 켠다.
2. **LLM / MCP 설정 → 웹 ChatGPT / Claude 설정 → Tailscale 주소 자동 연결**을 선택해 적용한다.
   LAN MCP도 함께 켜진다. **기본 HTTPS 포트(443) 사용**을 선택하면 화면과 MCP가 같은 443을 쓴다.
   별도 공개 포트 **8443 또는 10000**도 선택할 수 있다. PC 내부 중계 포트는 기본 **59335**다.
   내부 포트는 다른 프로그램과 겹치지 않게 변경할 수 있다.
3. 주소가 생성되면 화면의 주소 칸과 MCP OAuth 주소가 자동 갱신된다. MCP 공개 응답을 확인하면
   설치 폴더의 `plugins/RemoteControl_WebPlugin.zip`을 생성/갱신한다.
4. 텔레그램 알림을 설정해 두면 화면 URL·포트와 MCP URL·포트를 자동으로 보낸다.
   **알림 설정 → 접속 주소 보내기**로 현재 주소를 직접 보낼 수도 있다. 접속 확인 전인 주소는 상태를 표시한다.
5. 기본 443 선택 시 ChatGPT/Claude에는 생성된 `https://장비.테일넷.ts.net/mcp`를 등록하고
   원격제어 ID·비밀번호로 로그인한다. 주소 자동 생성은 앱 계정의 최초 등록·로그인까지 대신하지 않는다.
   별도 포트를 선택했으면 `https://장비.테일넷.ts.net:8443/mcp`(또는 10000)를 사용한다.
   주소가 바뀌면 갱신된 플러그인/주소로 앱 연결도 다시 등록해야 한다.

공개 Tailscale Funnel을 사용하므로 웹 ChatGPT/Claude가 접속할 수 있다. 사설 Tailscale IP나 Serve만으로는
클라우드 앱에서 접속할 수 없다. 별도 공유기 포트 전달이나 PEM 선택은 필요 없고, Tailscale 계정의
Funnel 허용 정책이 적용된다. 기존 화면용 443, 직접 입력 DDNS HTTPS와 다른 서버 포트는 바꾸지 않는다.
직접 입력 모드와 자동 모드를 오갈 때 직접 입력 주소·포트를 보존한다.

별도 작업 `RemoteControlTailscaleMcp`는 LocalService로 실행한다. 프로그램은
`C:\Program Files\RemoteControlTailscaleMcp`, 설정은 `C:\ProgramData\RemoteControlTailscaleMcp`에 둔다.
인증 중계는 127.0.0.1에서만 수신하고 전용 터널의 인증된 프록시 머리글을 확인한다.
주소 전달 파일이 만료되거나 터널 주소가 바뀌면 기존 OAuth 세션을 닫는다. 텔레그램 발송 지연은
이 주소 전달과 독립적이며, 발송 성공한 주소만 중복 알림을 막는다.

### 주소 직접 입력

**권장: `dist/RemoteControl_Setup.exe` v1.8.10을 실행한다.** 설치 창에서
웹 연결은 선택 사항이며 기본으로 꺼져 있다. 주소·포트·인증서를 입력하지 않아도 본체를 설치할 수 있다.
나중에 원격제어의 **LLM / MCP 설정 → 웹 ChatGPT / Claude 설정**에서 DDNS 주소·HTTPS 포트·인증서를
지정하고 적용한다. 먼저 원격제어 ID·비밀번호, 잠금 화면 제어 서비스와 LAN MCP 사용을 설정하고 저장한다.
이미 준비된 PC는 설치 창의 `웹 ChatGPT / Claude 연결을 지금 설정`을 선택할 수도 있다.
Setup은 본체를 업데이트하고, 웹 중계 파일과 다음 업로드 ZIP을 자동으로 배치한다.
설치 창은 화면 배율과 Windows 텍스트 크기에 맞춰 줄바꿈한다. 화면이 작으면 본문을
스크롤하고, 설치·닫기 버튼은 하단에서 사용할 수 있다. 창 크기 조절과 최대화도 가능하다.


`C:\Program Files\RemoteControl\plugins\RemoteControl_WebPlugin.zip`



웹용 ZIP은 입력한 DDNS 주소와 포트로 생성된다. 전용 HTTPS 포트 기본값은 59334다.
**기본 HTTPS 포트(443) 사용**을 체크하면 포트는 443으로 설정되고 등록 주소는
`https://DDNS주소/mcp`가 된다. 선택을 해제하면 앞서 입력한 전용 포트가 복원된다.
443을 직접 입력해도 같은 선택이 적용된다. Tailscale 자동 연결에서도 기본 443 선택을 지원한다.
이미 443을 사용하는 다른 프로그램이 있으면 중지하지 않고 적용을 거부한다. 이 프로그램이 관리하는
Caddy의 기존 홈페이지 루트 이동은 MCP와 같은 443에서 보존한다. 화면과 LAN MCP 포트는 바꾸지 않는다.
웹 HTTPS는 HTTP/1.1·HTTP/2를 사용한다. 일부 HTTP/3 재접속에서 발생하던 `HTTPS proxy required` 오류를
피하고 `Alt-Svc: clear`로 이전 HTTP/3 접속 정보를 해제한다. HTTPS 인증서·프록시 인증 검사는 그대로 유지한다.
웹 연결을 건너뛰면 나중 설정에 필요한 파일과 LAN 패키지만 배치한다. 신규 웹 ZIP은 나중에
웹 설정을 적용할 때 생성하며, 이미 있던 웹 ZIP과 실행 중인 웹 연결은 유지한다.
기존 웹 중계 업데이트는 같은 소유 경로를 확인하고 인증서·OAuth client 등록 정보를 보존한다.

다음 수동 절차는 Setup 대신 PowerShell로 설치할 경우에만 사용한다.



1. 대상 PC의 원격제어에서 LAN MCP와 비밀번호를 설정한다. 실제 MCP 포트를 확인한다.

2. DDNS가 이 PC의 공인 IP를 가리켜야 한다. 공유기에서 지정한 HTTPS 포트를 이 PC의 같은 번호로 전달한다.

   전용 포트 기본값은 **59334**다. 기본 HTTPS를 선택한 경우 **443**을 전달하고 주소에서 포트를 생략한다.
   기존의 유효한 인증서는 자동 재사용한다. 인증서 자동 발급을 위한 80/443 개방은 하지 않는다.
   인증서가 없으면 체인 포함 PEM 인증서와 개인 키를 선택한다. 인증서는 DNS 검증 등으로 별도 발급하고,

   만료 전에 갱신한 파일을 다시 설치한다. 사용자 지정 포트에서는 자동 발급/갱신을 하지 않는다.

   LAN MCP 포트와 59332는 외부에 전달하지 않는다. 주소는 반드시 `https://`로 입력한다.

3. 배포 ZIP을 풀고 관리자 PowerShell에서 실행한다. 이 PC의 확인된 MCP 포트는 59331이다.



   ```powershell

   .\Install-WebMcp.ps1 -PublicUrl 'https://najhna.dyndns.org:59334' -LanPort 59331

   ```



4. 외부망에서 다음 두 주소가 열리는지 확인한다. HTTPS 인증서 오류가 없어야 한다.



   - `https://najhna.dyndns.org:59334/.well-known/oauth-protected-resource`

   - `https://najhna.dyndns.org:59334/.well-known/oauth-authorization-server`



5. ChatGPT에서 개발자 모드를 활성화하고 사용자 MCP/플러그인을 만든다. 계정·워크스페이스가

   사용자 MCP 연결을 지원해야 하며, 관리자가 기능을 제한했으면 허용이 필요하다.

   서버 주소는 `https://najhna.dyndns.org:59334/mcp`, 인증은 **OAuth**를 선택한다.

   CIMD를 우선 지원하며 DCR도 지원한다. ID·비밀번호는 서버가 보여 주는 연결 화면에 입력한다.

   플러그인 등록 폼의 OAuth client secret에 원격제어 비밀번호를 넣지 않는다.

6. 새 대화에서 연결을 선택하고 `pc_get_status`로 PC 이름을 확인한다.



### Claude 웹·Desktop·휴대폰의 원격 커넥터



Claude의 **Customize → Connectors → Add custom connector**에서 위와 같은 HTTPS MCP 주소를 입력한다.

OAuth client ID와 client secret은 비워 둔다. 선택 항목이 보이면 Claude의 게시된 신원(CIMD) 또는

자동 등록(DCR)을 사용한다. 원격제어 ID/비밀번호는 다음에 열리는 **Claude에 원격제어 연결** 화면에 입력한다.

로그인 후 **Claude로 돌아가기**를 거쳐 연결을 완료한다. 연결된 대화에서 PC 이름을 확인한다.



`RemoteControl_WebPlugin.zip`은 ChatGPT 플러그인용이다. Claude 원격 커넥터에는 주소를 등록한다.

`RemoteControl_Claude.mcpb`는 Windows Claude Desktop의 **로컬 LAN 확장**이며 원격 커넥터와 다르다.

현재 운영 주소와 새 Setup의 기본 포트가 다를 수 있으므로 실제 개통된 주소를 사용한다.

인증 중계를 재시작하면 GPT와 Claude 모두 다시 로그인해야 한다.



설치 위치는 `C:\Program Files\RemoteControlWebMcp`, 설정과 인증서 위치는

`C:\ProgramData\RemoteControlWebMcp`다. 프로그램은 관리자/SYSTEM만 변경할 수 있고,

상태 폴더는 LocalService도 쓸 수 있다. 비밀번호는 저장하지 않는다.

기존 원격제어 서비스, LAN 연결, HomePC 잔여 백업은 수정하지 않는다.



서비스 `RemoteControlWebHttps`와 예약 작업 `RemoteControlWebMcp`가 자동 시작된다.

설치 소유 정보와 서비스/예약 작업 경로가 일치할 때만 업데이트한다. 다른 프로그램의 설치 흔적이면 멈춘다.

설치 프로그램 실행과 실제 Windows 자동 실행은 관리자 권한으로 배포할 때 최종 확인한다.



## 플러그인 ZIP



Setup으로 설치하면 `plugins\RemoteControl_WebPlugin.zip`이 자동 생성된다.

이 파일이 웹 업로드 대상이다. `RemoteControl_MCP.zip`은 기존 LAN/stdio용이며,

`RemoteControl_WebMCP.zip`은 서버 배포용이다. 이름이 비슷해도 서로 바꿔서 올리지 않는다.

외부 HTTPS 개통과 ChatGPT 로그인을 확인한 뒤 사용한다.



HTTPS 개통 후에 검증된 주소를 담은 별도 플러그인 ZIP을 만들 수 있다.



```powershell

.\Export-Plugin.ps1 -PublicUrl 'https://najhna.dyndns.org:59334' -Destination 'D:\Src\원격제어\dist\RemoteControl_WebPlugin.zip'

```



이 스크립트는 HTTPS/OAuth metadata를 검사한 뒤 패키지를 만든다. ChatGPT 계정에 자동 업로드하거나

존재하지 않는 app ID를 채우지 않는다. ZIP만 설치해도 PC의 서버가 켜지거나 포트가 열리는 것은 아니다.

ChatGPT 화면에서 직접 MCP 주소를 등록하면 ZIP 없이도 사용할 수 있다.



## 인증 수명과 중지



- v1.8.4는 ChatGPT만 허용하던 OAuth callback/CIMD 제한에 Claude의 정확한 공식 주소를 추가한다.

  임의 URL, 비슷한 도메인, 다른 callback 경로는 계속 거부한다. 두 호스트의 인증 코드와 토큰은 서로 사용할 수 없다.

  로그인·성공 화면에는 연결을 시작한 앱 이름을 표시한다. Claude의 LAN 로그인 만료는 HTTP 401로 반환한다.



- v1.8.3은 로그인 성공 화면과 수동 “ChatGPT로 돌아가기” 버튼을 추가한다. 기존 폼에서 외부로

  바로 303 이동하던 경로를 새 문서로 나눴다. 브라우저에서 자동/수동 복귀와 callback 뒤 추가 이동을 시험한다.

  `connection-events.jsonl`에는 시각·단계·HTTP 상태·소요 시간만 기록한다. 비밀번호/토큰/주소는 기록하지 않는다.

- HTTPS 포트는 Setup에서 지정한다. 주소·방화벽·생성되는 플러그인 ZIP에 같은 번호가 반영된다.

  설치 전에 포트 점유와 인증서를 검사하므로 충돌하면 기존 서비스를 중지하기 전에 중단한다.

  기존 홈페이지 입구의 루트 이동을 보존한다. 같은 포트를 선택하면 `/` 이동과 MCP 경로를 함께 제공한다.
  포트를 변경한 뒤에는 공유기 전달 설정과 ChatGPT 플러그인 주소도 변경해야 한다.



- v1.8.2는 같은 DDNS의 다른 HTTP 서버까지 HTTPS로 강제하던 HSTS 설정을 해제한다.

  브라우저에 이미 저장된 정책은 HTTPS 응답의 `max-age=0`으로 지운다. 업데이트 후 문제가

  생긴 브라우저에서 웹 MCP의 HTTPS 주소를 한 번 연 다음 기존 HTTP 주소로 다시 접속한다.

  포트 번호, 기존 서버와 방화벽 규칙은 변경하지 않는다. MCP는 계속 HTTPS로만 제공한다.



- v1.8.1은 웹·폰 로그인 시 `Origin rejected`가 발생하는 브라우저 폼 오류를 수정한다.

  `Referrer-Policy: same-origin`으로 같은 사이트 POST의 Origin을 보존하고, CSP에 정확한

  ChatGPT callback을 허용한다. 다른 출처와 `Origin: null`, 잘못된 CSRF는 계속 거부한다.

  기존 웹 연결은 업데이트 후 ChatGPT에서 다시 로그인한다. MCP 주소는 그대로다.



- PKCE S256, 일회용 코드(5분), 로그인 폼(10분), access token(1시간), refresh token 회전을 사용한다.

- v1.8.13부터 로그인 인증은 원격제어·웹 중계 재시작과 일시적인 통신 장애 뒤에도 유지한다.
  access token은 1시간이며 refresh token은 회전한다. 통신 실패·5xx 때 기존 refresh token을 보존한다.
- 앱 연결 해제·로그아웃·원격제어 ID/비밀번호 변경 시 기존 인증을 무효화한다.
  공개 MCP 주소가 바뀌면 새 주소에 다시 로그인한다. 화면 브라우저 세션은 기존 8시간 한도를 유지한다.
- 비밀번호는 저장하지 않는다. LAN 인증은 `C:\ProgramData\RemoteControl\mcp-sessions.bin`에 Windows DPAPI로,
  웹 인증 쿠키·access/refresh token은 상태 폴더의 `auth-state.bin`에 AES-256-GCM으로 암호화한다.
  암호화 키는 기존 ACL로 보호된 gateway.json의 프록시 키에서 파생하며 상태는 공개 주소에 결합한다.
  OAuth client 등록 정보와 DCR client secret은 기존 보호된 상태 폴더에 보관한다.
- 업데이트 전에 메모리에만 있던 인증은 복원할 수 없어 v1.8.13 설치 후 처음 한 번 다시 로그인한다.
  원격제어 본체와 웹 중계 둘 다 업데이트해야 이 정책이 적용된다.
- 명령 응답이 유실되면 실행 여부가 불확실하다고 반환하고 재전송하지 않는다.

- 웹 연결은 로컬 PC 파일 경로를 사용하는 `pc_upload_file` / `pc_download_file`을 제공하지 않는다.

  원격 파일 도구와 명령·로그·화면 조회는 LAN 서버에서 허용한 범위로 사용할 수 있다.



관리자 PowerShell에서 설치 폴더의 `Disable-WebMcp.ps1`을 실행하면 웹 자동 실행과 방화벽 규칙을

끄고, 파일과 인증서는 보존한다. 프로세스를 이름으로 일괄 종료하지 않는다.



## 개발·시험



`npm test`에는 GPT/Claude 각각 실제 Chrome 폼 제출 → OAuth callback → 토큰 발급 → 실제 LAN fixture의

`pc_get_status`까지 확인하는 회귀 시험이 포함된다. `RC_BROWSER`로 Edge/Chrome 실행 파일을

지정할 수 있다. 별도 브라우저 프로필과 loopback HTTPS를 사용하며 Windows 인증서 저장소와

실제 ChatGPT 계정은 변경하지 않는다. 헤더를 수동으로 넣는 HTTP 시험만으로 로그인 성공을

판정하지 않는다.



```powershell

.\bench\Test-Mcp.ps1

Push-Location .\web-mcp

npm.cmd ci --ignore-scripts

npm.cmd test

Pop-Location

.\빌드_webmcp.ps1

```



시험은 별도 임시 폴더의 실제 `McpHost` fixture를 사용한다. 현재 서비스/설정/비밀번호는 바꾸지 않는다.

공식 SDK의 OAuth 라우터와 MCP 클라이언트를 사용한다. HTTPS 시험도 loopback 임시 포트만 사용하며

시험 인증서를 Windows 신뢰 저장소에 설치하지 않는다.



새 설치는 지정 포트의 수동 인증서만 사용한다. 공개 포트, DNS와 인증서 신뢰는 배포 후 확인해야 한다.

최종 ChatGPT 연결 성공 여부는 사용자 계정에서 로그인한 뒤 확인한다.



공식 문서: [OpenAI 인증](https://developers.openai.com/plugins/build/auth),

[ChatGPT 연결](https://developers.openai.com/plugins/deploy/connect-chatgpt),

[Claude 인증](https://claude.com/docs/connectors/building/authentication),

[Caddy 실행](https://caddyserver.com/docs/running).


## WebMCP 공통 파일 전송 (v1.8.13)

로컬·외부에서 같은 HTTPS `/mcp` 주소를 사용합니다. `pc_open_file_transfer` 링크의 브라우저 화면에서 파일·폴더 업로드와 파일·폴더 ZIP 다운로드를 사용합니다. 직접 MCP 조각 전송도 제공하며 별도 stdio 연결기 설치는 필요 없습니다. [전체 전송 안내](../docs/webmcp-file-transfer.md).


## 프라이빗 터널 자동 MCP (1.8.14)

원격제어 → 외부망 연결 설정에서 프라이빗 터널의 발급 계정·키·지문을 저장하고 연결합니다. LLM / MCP 설정 → 웹 ChatGPT / Claude 설정 → 프라이빗 터널 주소 자동 연결 → 적용을 선택하면 발급된 `https://랜덤주소/mcp`를 사용합니다. 화면, OAuth, 파일/폴더 전송이 같은 HTTPS 주소를 공유합니다. 원격제어 ID·비밀번호, 잠금 화면 서비스가 필요하며 LAN MCP는 자동 활성화됩니다. 받는 PC에 로컬 연결기를 설치할 필요가 없습니다.

이 모드는 별도 인증 중계 작업 `RemoteControlPrivateMcp`만 설치합니다. SSHTunnel 서버의 공개 HTTPS 개통은 필요하며 별도 서버 작업 범위입니다. 직접 입력 DDNS 주소는 보존됩니다. 터널 재연결 때 새 주소가 발급되므로 ChatGPT/Claude에 새 `/mcp` 주소를 등록하고 다시 로그인하세요. 단순 인증 중계/본체 재시작 후 같은 URL이 유지되면 저장된 인증을 복원합니다.
