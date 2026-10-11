# LAN IP / 폐쇄망 WebMCP (v1.8.15)

대상 PC와 로컬 MCP 클라이언트가 같은 내부망에 있으면 DDNS, Tailscale, SSH 터널, 인터넷 없이 HTTPS WebMCP를 사용한다. 외부 WebMCP와 동시에 켤 수 있으며 외부 주소와 터널 설정을 보존한다. 외부 DDNS WebMCP가 이미 443을 사용하면 소유권을 확인한 동일 HTTPS 서비스에서 도메인·LAN IP를 분기한다. 다른 제품이 443을 점유하면 중지하지 않고 사전 검사에서 알린다.

1. 대상 PC에 v1.8.15 Setup을 설치한다. 설치 시 **LAN IP 직접 연결 / 인터넷 없는 내부망 WebMCP 설정**을 선택하거나, 설치 후 **LLM / MCP 설정 → LAN / 내부망 설정**을 연다.
2. 기존 원격제어 ID·비밀번호, 잠금 화면 제어 서비스와 MCP 사용을 켠다. 대상 PC의 고정 LAN IPv4, HTTPS 포트(기본 **443**), 접속 허용 클라이언트 IPv4/CIDR을 지정한다. 내부 중계 기본 59338은 loopback 전용이며 화면/내부 MCP 포트와 다르다.
3. 적용하면 `https://대상PC_IP/mcp`가 준비된다. **LAN MCP 주소 복사**로 실제 주소를 복사한다. 예: `https://192.168.0.10/mcp`.
4. **LAN 공개 CA 폴더**에서 `RemoteControl-LAN-CA.crt`, `Import-LanWebMcpCertificate.ps1`, `SHA256.txt`를 클라이언트 PC로 복사한다. 대상 PC에서 표시한 SHA256과 대조한 뒤 클라이언트에서 실행한다:

   ```powershell
   .\Import-LanWebMcpCertificate.ps1 -ExpectedSha256 "대상PC에서확인한64자리SHA256"
   ```

   Windows 현재 사용자 인증서 저장소에 공개 CA만 등록된다. 관리자 권한은 필요 없다. Node 기반 클라이언트는 시작 전 공개 CA 파일의 절대 경로를 지정한다:

   ```powershell
   $env:NODE_EXTRA_CA_CERTS='C:\MCP\RemoteControl-LAN-CA.crt'
   # 이 환경에서 로컬 MCP 클라이언트를 시작한다.
   ```

5. HTTPS Streamable HTTP, OAuth 동적 클라이언트 등록(DCR), PKCE와 로컬 loopback callback을 지원하는 MCP 클라이언트에 URL을 등록한다. 브라우저가 열리면 기존 원격제어 ID·비밀번호로 로그인한다. LAN callback은 `http://127.0.0.1:포트/...`, `http://localhost:포트/...`, `http://[::1]:포트/...`만 허용한다. 클라이언트 이름은 신뢰하지 않으며 비밀번호를 클라이언트에 전달하지 않는다.
6. `pc_get_status`로 대상 PC 이름을 확인한 뒤 파일·폴더 전송, 명령 실행, 화면 조회를 사용한다. 파일 선택 화면과 다운로드도 같은 LAN HTTPS 주소에서 열린다. 파일 도구의 작업 폴더와 명령/화면 권한은 기존 MCP 설정을 따른다.

CA는 대상 PC에서 인터넷 없이 생성되고 재설치 시 보존된다. 인증서는 IP SAN을 포함하며 자동 갱신한다. 대상 PC IP를 바꾸면 LAN 설정을 다시 적용하고 클라이언트 URL을 수정해 로그인한다. 외부 도메인 인증서나 TLS 검증 해제는 필요 없다. 인증서 개인 키와 프록시 키는 클라이언트/설치 ZIP에 포함되지 않는다.

LAN 설정을 끄고 적용하면 LAN 전용 예약 작업과 방화벽 규칙만 해제한다. CA와 인증 상태는 재사용을 위해 보존되며 외부 WebMCP는 계속 동작한다.

**범위:** 대상 PC의 MCP 서버·인증·파일 전송에 인터넷이 필요 없다는 뜻이다. 완전한 폐쇄망에서는 로컬 실행 MCP 클라이언트와 로컬 모델을 사용한다. 클라우드 ChatGPT/Claude 서버는 사설 LAN IP에 직접 접속하지 못하며 클라우드 모델 자체에는 인터넷 연결이 필요하다.
