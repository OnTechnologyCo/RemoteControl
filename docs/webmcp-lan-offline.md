# LAN WebMCP 연결 설정·스크립트 복사 (v1.8.18)

대상 PC와 로컬 MCP 클라이언트가 통신할 수 있는 내부망에서 HTTPS IP 주소로 연결한다. DDNS, Tailscale 또는 SSH 터널 없이 사용할 수 있으며, 이미 설정한 외부 WebMCP와 함께 사용할 수 있다.

## 대상 PC에서 설정하기

1. v1.8.18 설치 파일로 업데이트한 뒤 **LLM / MCP 설정 → LAN / 내부망 설정**을 연다. 처음 설치할 때도 LAN WebMCP를 선택할 수 있다.
2. 기존 원격제어 ID·비밀번호, 잠금 화면 제어 서비스와 MCP 사용을 설정한다.
3. 랜카드 이름과 IPv4 주소를 확인하고 **기본 서버 IP**를 선택한다. 다른 랜카드나 같은 랜카드의 추가 IP로도 접속하려면 해당 주소를 추가한다. 서버에 실제로 할당된 IPv4 주소를 최대 32개까지 등록할 수 있다. NIC 목록을 새로 읽을 수 있으며, 저장된 IP가 현재 없어졌다면 적용 전에 확인하고 수정한다.
4. **접속 허용 클라이언트 IPv4 / 대역**을 지정한다. 이것은 서버 IP 목록과 별개다. 예를 들어 서버 주소가 `192.168.195.141`과 `10.10.0.20`이면, 접속할 PC에 맞게 `192.168.195.0/24,10.10.0.0/24` 등 필요한 범위를 허용한다. 기존에 직접 입력한 허용 대역은 보존된다.
5. HTTPS는 기본 443을 사용하고 별도 포트도 지정할 수 있다. 내부 중계 기본 59338은 loopback 전용이며 화면 포트·내부 MCP 포트와 달라야 한다.
6. **적용**이 성공하면 연결 스크립트 창에서 사용할 IP와 클라이언트 형식을 선택해 복사한다. 적용 전 편집한 값이나 적용에 실패한 값은 유효한 연결 안내로 복사하지 않는다. 저장된 설정을 다시 열어 연결 안내를 복사할 수도 있다.

외부 DDNS WebMCP가 이미 같은 HTTPS 포트를 사용하면 소유권을 확인한 HTTPS 서비스에서 도메인과 등록된 IP들을 분기한다. 다른 제품이 포트를 사용하면 사전 검사에서 알린다. LAN 기능을 해제할 때는 LAN 전용 작업·방화벽 규칙을 해제하고 CA 및 인증 상태는 보존한다.

## 복사되는 정보

IP를 선택하면 아래 주소에 동일한 IP와 설정한 HTTPS 포트가 반영된다. 기본 포트 443은 URL에서 생략한다.

```text
- MCP 주소: https://192.168.195.141/mcp
- 인증 주소: https://192.168.195.141/authorize
- 공개 CA: https://192.168.195.141/ca/RemoteControl-LAN-CA.crt
- 공개 CA SHA256: <이 PC에서 생성한 공개 CA 파일의 실제 64자리 SHA256>
```

인증 주소는 OAuth 클라이언트가 필요한 요청 값을 붙여 여는 주소다. 사용자는 MCP URL을 클라이언트에 등록한 뒤 클라이언트의 로그인 안내를 따른다. 등록 명령의 성공은 설정 저장을 뜻하며, 그 순간 모든 클라이언트가 자동으로 브라우저를 여는 것은 아니다.

**전체 안내 복사**에는 접속 정보, CA 정보와 클라이언트별 사용 방법이 포함된다. **클라이언트별 복사**는 해당 프로그램에 붙여 넣을 명령 또는 설정 본문을 제공한다. JSON 파일에는 안내 문장이나 Markdown 코드 펜스를 붙여 넣지 않는다. 기존 파일에 다른 서버가 있으면 해당 서버 항목을 병합한다.

## 클라이언트별 등록 예제

### Claude Code

```shell
claude mcp add --transport http remotecontrol-pth-web https://192.168.195.141/mcp
```

Claude Code에서 `/mcp`를 열어 해당 서버에 로그인한다. 현재 프로젝트에 대한 개인 등록이 기본이며, 모든 프로젝트에서 사용할 때는 등록 명령에 `--scope user`를 추가할 수 있다.

### VS Code — `.vscode/mcp.json`

```json
{
  "servers": {
    "remotecontrol-pth-web": {
      "type": "http",
      "url": "https://192.168.195.141/mcp"
    }
  }
}
```

사용자 전체 설정은 명령 팔레트의 **MCP: Open User Configuration**으로 연다. 서버를 시작하고 표시되는 OAuth 인증 안내를 따른다.

### Cursor — `.cursor/mcp.json`

```json
{
  "mcpServers": {
    "remotecontrol-pth-web": {
      "url": "https://192.168.195.141/mcp"
    }
  }
}
```

사용자 전체 설정은 `%USERPROFILE%\.cursor\mcp.json`에 저장한다. Cursor의 서버 연결 화면에서 인증한다.

### Codex

```shell
codex mcp add remotecontrol-pth-web --url https://192.168.195.141/mcp
codex mcp login remotecontrol-pth-web
```

직접 설정할 때는 `%USERPROFILE%\.codex\config.toml`에 다음 항목을 넣는다.

```toml
[mcp_servers.remotecontrol-pth-web]
url = "https://192.168.195.141/mcp"
```

서로 다른 IP를 별도 서버 항목으로 동시에 등록하려면 이름을 구분한다. 같은 서버 이름으로 등록 명령을 반복 실행할 때는 각 클라이언트의 기존 설정을 확인한다.

## 공개 CA 등록과 SHA256 확인

공개 CA는 해당 대상 PC의 LAN HTTPS 인증서를 신뢰하는 데 사용한다. 같은 대상 PC의 여러 IP는 동일한 공개 CA를 사용하므로 IP를 추가할 때마다 새 CA를 설치할 필요가 없다.

대상 PC의 **LAN 공개 CA 폴더**에는 다음 파일이 있다.

- `RemoteControl-LAN-CA.crt`: PEM 형식의 공개 CA 인증서.
- `SHA256.txt`: 위 파일의 바이트를 SHA256으로 해시한 값.
- `Import-LanWebMcpCertificate.ps1`: 지정한 SHA256과 일치하는 공개 CA만 현재 Windows 사용자의 신뢰 저장소에 등록하는 스크립트.

기본 경로는 `C:\ProgramData\RemoteControlLanWebMcp\certificates`다. 처음 연결하는 클라이언트는 아직 이 CA를 신뢰하지 않으므로 HTTPS 다운로드에도 인증서 오류가 날 수 있다. 최초 등록은 대상 PC의 공개 CA 폴더에서 위 파일들을 전달하고, 대상 PC의 연결 안내에서 확인한 SHA256으로 검증한다. 이미 CA를 신뢰하는 PC에서는 공개 CA URL로도 같은 파일을 받을 수 있다.

```powershell
Get-FileHash -Algorithm SHA256 .\RemoteControl-LAN-CA.crt
.\Import-LanWebMcpCertificate.ps1 -ExpectedSha256 "대상PC에서확인한64자리SHA256"
```

표시 값은 **PEM 파일 바이트의 SHA256**이다. 인증서 관리 화면의 SHA1 thumbprint나 DER 데이터의 SHA256 지문과 비교하지 않는다. 다운로드 응답도 같은 PEM 바이트를 제공한다. CA 다운로드는 정확한 인증서 경로의 GET/HEAD 요청만 지원하며, 허용된 클라이언트 네트워크에서 접근할 수 있다.

별도 CA 파일을 사용하는 클라이언트는 시작 전에 해당 PEM 파일의 절대 경로를 지정한다.

```powershell
# Claude Code
$env:NODE_EXTRA_CA_CERTS = 'C:\MCP\RemoteControl-LAN-CA.crt'
claude

# Codex
$env:CODEX_CA_CERTIFICATE = 'C:\MCP\RemoteControl-LAN-CA.crt'
codex mcp login remotecontrol-pth-web
```

VS Code 등 Windows 신뢰 저장소를 사용하는 클라이언트는 CA 등록 후 재시작한다. 복사된 JSON에 임의의 인증서 검증 해제 옵션을 추가할 필요가 없다.

## 여러 IP의 인증 동작

등록한 각 IP는 자신의 HTTPS 주소로 OAuth discovery, issuer, resource, 로그인·토큰·파일 전송 주소를 제공한다. 다른 IP에 고정된 인증 주소로 이동하지 않는다. 각 주소의 OAuth 등록·토큰은 구분되며, 기존에 등록한 IP를 유지하면서 IP를 추가하거나 기본 IP 순서를 바꾸어도 기존 주소의 인증 상태를 보존한다. 처음 쓰는 새 IP에는 클라이언트 등록과 로그인이 필요하다.

LAN OAuth는 DCR, PKCE와 로컬 callback을 지원한다. callback은 `http://127.0.0.1:포트/...`, `http://localhost:포트/...`, `http://[::1]:포트/...`만 허용하며, 클라이언트 이름만으로 신뢰하지 않는다. 로그인 후 `pc_get_status`로 대상 PC 이름을 확인한다.

대상 PC의 DHCP 주소가 바뀌면 현재 할당된 IP로 LAN 설정을 다시 적용하고 클라이언트 URL을 수정한다. CA는 재사용한다. 이 기능은 LAN 주소를 고정 IP로 바꾸거나 DHCP 서버의 예약 설정을 변경하지 않는다.

## 범위와 공식 문서

대상 PC의 MCP 서버·인증·파일 전송은 내부망에서 동작한다. 완전한 폐쇄망에서는 내부망에서 실행할 수 있는 MCP 클라이언트와 모델이 필요하다. 클라우드 ChatGPT/Claude 서버는 사설 LAN IP에 직접 접속할 수 없고, 클라우드 모델 자체에는 인터넷 연결이 필요하다.

- [Claude Code MCP](https://code.claude.com/docs/en/mcp)
- [Claude Code 네트워크·CA 설정](https://code.claude.com/docs/en/network-config)
- [VS Code MCP 설정](https://code.visualstudio.com/docs/agents/reference/mcp-configuration)
- [Cursor MCP 설정](https://cursor.com/docs/mcp)
- [Codex MCP 설정](https://learn.chatgpt.com/docs/extend/mcp?surface=cli)
- [Codex 인증·CA 설정](https://learn.chatgpt.com/docs/auth)
