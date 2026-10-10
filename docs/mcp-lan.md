# RemoteControl LAN MCP · v1.8.0

공인 IP/DDNS를 통해 **웹 ChatGPT / Claude 원격 커넥터**에서 연결하려면 별도
[웹 MCP 중계 설치 안내](../web-mcp/README.md)를 사용합니다. 이 문서의 LAN 패키지는 로컬 stdio용입니다.

인터넷이 되는 **LLM PC**가 인터넷이 없는 **대상 PC**의 프로그램을 진단하고 수정본을 배포합니다.
대상 PC에서는 기존 RemoteControl ID/PW만 사용합니다. OAuth, GitHub 로그인, 외부 인증 서버,
모델 API 키, Node/Python 설치가 필요하지 않습니다. LLM 서비스 로그인은 LLM PC에서 합니다.

```text
Claude Desktop / Claude Code / Codex / 로컬 MCP 지원 LLM 앱
  └─ RemoteControl.exe --mcp-client (LLM PC, stdio)
       └─ LAN: 대상 IPv4:MCP포트 (기본 5901), Remote ID/PW
            └─ 대상 RemoteControl agent: 파일·명령·로그·화면
```

LLM PC에서 대상 IP로 통신할 수 있어야 합니다. 두 PC 사이에 경로가 없는 완전 분리망을
프로그램만으로 연결하지는 않습니다. 화면 접속 포트(기본 5900), SSH/Tailscale 외부망 설정과
MCP 포트는 별개입니다. MCP 포트를 인터넷에 공개하도록 자동 구성하지 않습니다.

## 1. 대상 PC 설정

1. `RemoteControl_Setup.exe`로 설치/업데이트합니다. 빌드만으로 실행 중인 서비스가 바뀌지는 않습니다.
2. 원격제어 창의 기본 설정에서 ID/PW를 적용합니다.
3. 설정 영역 아래 **LLM / MCP 설정**을 열고 **LAN MCP 사용**을 켭니다.
4. MCP 포트(예: 5901), **LLM PC의 IPv4** 또는 허용 대역, 파일 작업 폴더를 지정합니다.
   기본 폴더는 `C:\Users\Public\Documents\RemoteControlDeploy`입니다. 명령 실행과 화면 확인도 각각 선택합니다.
   배포한 프로그램이 원격제어 제거 때 함께 지워지지 않도록 설정/설치 폴더 밖을 기본값으로 씁니다.
5. 저장한 뒤 `MCP 대기 중 · 포트 ...` 상태를 확인합니다. 설정은 수 초 안에 반영됩니다.

MCP는 기본 꺼짐입니다. 설치 관리자의 방화벽 허용은 개인/도메인 프로필에서 적용됩니다.
회사 방화벽 정책이 별도로 있으면 해당 LAN 포트를 허용해야 합니다.
연결은 신뢰하는 LAN의 HTTP입니다. ID/PW와 데이터가 전송 구간에서 암호화되지는 않으므로
허용 IP를 LLM PC로 좁혀 사용합니다. 기존 Remote의 로그인 실패 제한이 적용됩니다.

파일 도구는 지정 폴더 밖과 junction/symlink를 거부합니다. **PowerShell은 이 폴더 제한의
샌드박스가 아닙니다.** 명령은 원격제어의 실행 계정 권한(서비스 모드에서는 SYSTEM)으로 실행됩니다.

## 2. Claude Desktop (Windows x64)

설치된 대상 PC의 `C:\Program Files\RemoteControl\plugins`에 있는
`RemoteControl_Claude.mcpb`를 LLM PC로 복사합니다. 설치 경로를 바꿨다면 그 경로의 `plugins`입니다.

Claude Desktop에서 **Settings → Extensions → Advanced settings → Install Extension**으로
이 파일을 수동 설치합니다. 대상 PC IPv4, MCP 포트, Remote ID/PW를 입력합니다.
비밀번호 항목은 sensitive 설정이며 Claude가 보관합니다. 대상 PC에 Claude를 설치하지 않습니다.

확장을 활성화한 대화에서 다음처럼 요청합니다.

> RemoteControl로 연결된 PC 이름과 실행 계정을 확인하고, 배포 폴더의 프로그램과 오류 로그를 확인해줘.

MCPB는 실행 파일을 포함하므로 별도 Node/npm 설치 없이 연결합니다.
회사 정책에서 로컬 확장을 막은 경우에는 관리자가 허용해야 합니다.

## 3. Codex / 로컬 MCP 지원 GPT 앱 / Claude Code

`RemoteControl_MCP.zip`을 LLM PC의 고정 폴더에 풀고 `remotecontrol-lan\Configure.cmd`를 실행합니다.
대상 IP, 포트, Remote ID/PW를 입력해 연결 시험 후 저장합니다. 기본 연결 이름은 `default`입니다.
비밀번호는 LLM PC의 현재 Windows 계정에 DPAPI로 암호화해
`%LOCALAPPDATA%\RemoteControl\mcp-clients\default.json`에 보관합니다.
다른 PC를 추가할 때는 다른 연결 이름으로 저장하고 `--profile` 인자를 바꿉니다.

설정 창의 **MCP JSON 복사**로 비밀번호 없는 stdio 설정을 복사할 수 있습니다.
예를 들어 압축을 `D:\Tools`에 풀었을 때:

```json
{
  "mcpServers": {
    "remotecontrol-lan": {
      "command": "D:\\Tools\\remotecontrol-lan\\server\\RemoteControl.exe",
      "args": ["--mcp-client", "--profile", "default"]
    }
  }
}
```

Claude Code 수동 등록:

```powershell
claude mcp add --scope user remotecontrol-lan -- "D:\Tools\remotecontrol-lan\server\RemoteControl.exe" --mcp-client --profile default
claude mcp list
```

Codex 수동 등록:

```powershell
codex mcp add remotecontrol-lan -- "D:\Tools\remotecontrol-lan\server\RemoteControl.exe" --mcp-client --profile default
```

ZIP에는 Codex/Agent Plugins용 `plugin.json`, `mcp.json`, 호환 `.codex-plugin/plugin.json`,
`.mcp.json`과 작업 skill도 있습니다. 로컬 플러그인 등록을 지원하는 앱에서 수동으로 추가할 수 있습니다.
구버전 앱이 `${PLUGIN_ROOT}`를 처리하지 못하면 위의 절대 경로 stdio 등록을 사용합니다.
플러그인과 동일 MCP를 동시에 별도 등록하면 도구가 두 벌로 보이므로 한 방식만 사용합니다.
설치 프로그램은 사용자의 Claude/Codex 설정이나 마켓플레이스를 자동 수정하지 않습니다.

**웹 ChatGPT의 사용자 지정 GPT에 ZIP을 지식 파일로 올리는 것만으로는 로컬 exe나 MCP가
실행되지 않습니다.** 웹에서 사설 IP로 바로 접속하는 구성도 아닙니다. 이 패키지는 Windows에서
로컬 stdio MCP를 실행하는 클라이언트용입니다. 웹 전용 앱에서 쓰려면 인터넷 가능한 LLM PC에
별도의 지원되는 연결 경로(예: 공급사의 private MCP tunnel)가 필요하고, 공급사 인증/정책이 따릅니다.
그 외부 연결은 이 LAN 패키지에서 자동 생성하지 않습니다.

## 4. 수정 → 배포 → 확인

1. `pc_get_status`: 대상 hostname과 실행 계정을 먼저 확인합니다.
2. `pc_list_processes`, `pc_read_event_log`, `pc_read_file`: 실제 오류와 실행 파일/로그 위치를 확인합니다.
3. LLM PC의 원본 프로젝트를 수정하고 LLM PC의 컴파일러로 빌드합니다.
4. `pc_create_directory`, `pc_upload_file`: 빌드 결과를 대상 작업 폴더에 전송합니다.
5. `pc_get_file_info`: SHA-256을 대조합니다. 이미 있는 파일 교체에는 현재 `expected_sha256`이
   필요하며 `.mcp-backup-...` 원본을 남깁니다. 실행 중인 exe는 먼저 정확한 프로세스/서비스를 확인합니다.
6. `pc_start_process`로 설치/검증 명령을 실행하거나 `pc_launch_program(visible=true)`로 화면에 실행합니다.
7. `pc_read_process_output`, 로그, `pc_capture_screen`으로 결과를 확인합니다.

예: 원본 `D:\Project\dist\App.exe`는 **LLM PC 경로**, 대상
`C:\Users\Public\Documents\RemoteControlDeploy\App.exe`는 **대상 PC 경로**입니다.
업로드/다운로드는 파일 스트림으로 전송하므로 바이너리를 LLM 메시지에 넣지 않습니다.
업로드 한 파일은 최대 4GiB입니다. 여러 파일/폴더는 도구를 반복하거나 로컬에서 ZIP으로 묶어
한 번 전송한 뒤 대상에서 `Expand-Archive`로 풉니다. 기존 브라우저 다중 파일/폴더 업로드도 유지됩니다.

MCP 작업은 동시에 8개, 출력 보관은 작업당 최근 128Ki 문자, 최대 실행 시간은 1시간입니다.
MCP 서버가 종료되면 소유한 작업과 자손 프로세스도 끝납니다. 지속 운영 프로그램은 정상적인
Windows 서비스 설치 절차를 사용하세요. 단순 LLM 클라이언트 재접속은 작업을 새로 만들지 않습니다.
`pc_stop_process`는 MCP가 만든 지정 작업만 종료합니다.

이 기능은 VS 디버거의 중단점/변수 추적 프로토콜을 구현한 것은 아닙니다.
빌드·배포·실행·로그·화면 확인 과정을 LLM 도구로 연결합니다.

## 배포와 검증

- `빌드.ps1`: `dist\RemoteControl.exe`
- `빌드_mcp.ps1`: 현재 dist 본체를 `dist\RemoteControl_MCP.zip`, `dist\RemoteControl_Claude.mcpb`로 패키징
- `빌드_setup.ps1`: 위 작업을 모두 실행하고 `dist\RemoteControl_Setup.exe`에 패키지 포함
- 패키지에 대상 IP, ID/PW, 사용자 설정 파일은 넣지 않습니다.
- `bench\Test-Mcp.ps1`: 격리 서버와 공식 MCP SDK로 로그인·프로토콜·전송·파일 보호·명령을 시험합니다.
  SDK 위치는 `RC_MCP_SDK`로 지정 가능하며 기본값은 참고 프로젝트 MCP_LocalPC의 설치본입니다.
- 자동 검증은 테스트 폴더만 사용합니다. 실제 대상 PC 설치와 사용자의 Claude Desktop 확장 등록은 별도입니다.
- v1.7.0의 시험 결과와 실제 확인 범위는 [검증 기록](mcp-validation.md)에 있습니다.

공식 참고: [Claude 로컬 MCP 확장](https://support.claude.com/en/articles/10949351-getting-started-with-local-mcp-servers-on-claude-desktop),
[MCPB 형식](https://github.com/modelcontextprotocol/mcpb/blob/main/MANIFEST.md),
[Codex MCP](https://developers.openai.com/codex/mcp/),
[OpenAI 플러그인](https://developers.openai.com/plugins/build/plugins),
[Private MCP 연결](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels).
