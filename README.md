# RemoteControl v1.8.10

[한국어](README.md) | [English](README.en.md)

인터넷 없는 LAN PC를 Claude·로컬 MCP 지원 GPT 앱에서 진단하고 프로그램을 배포할 수 있습니다. **설정 → LLM / MCP 설정**에서 포트·허용 IP·작업 폴더를 지정하며, 기존 Remote ID/PW로 로그인합니다. [MCP 설치·연결·사용법](docs/mcp-lan.md)을 참조하세요.

4K 화면의 타일 준비·JPEG 압축·브라우저 디코딩과 전송 대기를 개선했습니다. 빠른 연결에서는 큰 타일과 전송 묶음을 사용하고, 수신이 밀리면 작은 타일로 돌아갑니다. 해상도는 유지합니다. 측정 조건과 한계는 [4K 성능 기록](docs/4k-performance.md)을 참조하세요.

프로젝트 폴더는 **`D:\Src\원격제어`**입니다. 안쪽에 같은 이름의 프로젝트 폴더를 만들지 않습니다.

## 빌드와 설치 파일

배포 결과는 항상 **`D:\Src\원격제어\dist`** 한 곳에 생성합니다.

| 파일 | 용도 |
|---|---|
| `dist\RemoteControl.exe` | 프로그램 본체 |
| `dist\RemoteControl_Setup.exe` | 공유할 PC 설치·업데이트용 setup |
| `dist\RemoteControl_MCP.zip` | LLM PC의 로컬 MCP/플러그인 수동 등록 패키지 |
| `dist\RemoteControl_Claude.mcpb` | Claude Desktop Windows 확장 |

프로젝트 폴더에서 아래 명령 하나로 본체와 setup을 함께 갱신합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\빌드_setup.ps1
```

본체만 만들 때는 `빌드.ps1`을 사용합니다. setup 빌드는 항상 현재 소스를 먼저 빌드한 뒤 그 본체를 포함하며, 본체와 setup의 버전이 다르면 실패합니다. 어느 폴더에서 스크립트를 실행해도 출력은 프로젝트의 `dist`로 고정합니다.

기본 setup에는 Tailscale 인증 키와 알림 받을 chat_id를 넣지 않습니다. 기존 키를 포함할 때만 `-IncludeTailscaleKey`를 명시합니다. `-NoKey`는 이전 명령 호환용이며 기본 동작과 같습니다. 이 저장소의 봇 토큰은 실행 환경 변수 `REMOTE_CONTROL_TELEGRAM_BOT_TOKEN`으로 별도 제공하며 소스·설치 파일에 내장하지 않습니다.

빌드는 설치된 서비스를 중지하지 않으며 관리자 권한도 요구하지 않습니다. `dist`의 파일을 직접 실행 중이어서 덮어쓸 수 없으면 그 실행본을 종료한 뒤 다시 빌드하세요.

실제 업데이트는 **`dist\RemoteControl_Setup.exe`**를 실행해 진행합니다. 설치 시 관리자 권한을 요청하고 원격제어 프로그램·서비스를 다시 시작합니다. 빌드만으로 `C:\Program Files\RemoteControl`의 설치본이 업데이트되지는 않습니다. 기존 프로그램이 떠 있으면 새 exe를 실행해도 중복 실행 방지 때문에 기존 창이 열릴 수 있습니다.

## 변경 내용

- [오프라인 LAN MCP: Claude·Codex 연결 및 프로그램 배포](docs/mcp-lan.md)
- [4K 화면 성능 개선과 측정값](docs/4k-performance.md)
- [다중 파일·폴더 올리기](docs/file-upload.md)
- [외부망 연결 설정: private-tunnel / Tailscale 선택](docs/private-tunnel.md)
- [끊김 시 우클릭 문제 수정과 느린 회선 대응](docs/control-latency.md)

새 버전의 **외부망 → 연결 설정**에서 연결 방식과 SSH 주소·포트를 지정합니다. 아직 값이 정해지지 않았다면 자동 연결을 끄고 빈 설정을 저장할 수 있습니다.

`src`, `web`, `bench`, `docs`, `vdd`는 원본 및 시험 코드입니다. `artifacts`는 시험 결과 전용이며 배포 파일을 두지 않습니다.


## GitHub 비공개 업로드 사본

이 저장소는 기존 로컬 프로젝트에서 소스·문서·아이콘·빌드 스크립트·시험 코드만 추린 사본입니다. 실제 설정·키·접속 로그·브라우저 프로필·시험 결과·컴파일된 배포물은 제외했습니다. 기존 원본과 실행 중인 서비스는 변경하지 않았습니다.

소스에 내장되어 있던 텔레그램 봇 토큰은 제외했습니다. 이 사본의 `Notify.BotToken`은 `REMOTE_CONTROL_TELEGRAM_BOT_TOKEN` 환경 변수에서 값을 읽습니다. 값이 없으면 봇 알림을 사용하지 않습니다. 직접 실행하는 프로그램은 실행 전에 해당 환경에 값을 제공하고, 서비스·대리 프로세스는 각 실행 환경에서 값을 받을 수 있어야 합니다. 사용자 셸에만 설정해도 이미 실행 중인 서비스에는 반영되지 않습니다. 실제 값은 Git에 올리지 마세요.

`vdd`에는 드라이버 메타데이터만 포함합니다. 가상 디스플레이 설치를 사용하려면 일치하는 서명 드라이버 파일을 별도로 준비해야 합니다. `AGENTS.md`와 문서의 과거 측정·운영 기록은 이번 업로드에서 재측정한 결과가 아닙니다.
