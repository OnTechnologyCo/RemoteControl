# MCP v1.9.2 검증 · 2026-10-31

작업공간은 기본 위치이며 접근 허용 목록이 아닙니다. 아래 과거 v1.7.0의 폴더 이탈 차단 기록은 현재 동작에 적용되지 않습니다. 운영 서비스·설정은 변경하지 않고 Windows 격리 fixture와 가짜 로그인 정보를 사용했습니다.

- 실제 배포 `dist/RemoteControl.exe --mcp-client`로 LAN/stdio 통합 42개 통과: 작업공간 밖 파일 읽기·쓰기·편집·검색, 4MiB 스트리밍 전송, 실행 파일 업로드·실행, 명시적 cwd와 첫 작업공간의 기본 cwd.
- 상태의 `workspace_directories`·`default_working_directory`·`workspaces_restrict_access:false`, LAN/웹 initialize 안내와 도구 설명.
- 합성 파일의 실제 Windows ACL 접근 거부(`ACCESS_DENIED`), 없는 파일(`NOT_FOUND`), 상대 경로·junction(`INVALID_ARGUMENT`), 꺼진 화면 기능(`CAPABILITY_DISABLED`) 구분.
- 세션 저장/재시작 검사 6개 통과. 웹 OAuth·LAN 다중 IP·전송/실제 HTTPS 브라우저 검사 36개 통과 (실패·건너뛰기 0).
- 웹 전송은 작업공간 밖 파일 업로드/다운로드와 브라우저 업로드/폴더 다운로드를 검증했습니다. 덮어쓰기 해시·백업·전송 소유권·CSRF·경로 이탈 검증도 통과했습니다.

- 시작 동작 회귀 검사 12개 통과. v1.9.2.0 Inno 설치 파일의 내장 리소스 해시, 실제 설치 복사 블록의 3종 런타임 import, 플러그인 배치·URL 및 설치 UI 검증 통과. 운영 설치는 수행하지 않았습니다.
- MCP 설정 설명의 글자 영역은 Windows 기본 글꼴 기준 45px이며 지정한 68px 안에 표시됩니다.

업데이트 설치 후 MCP를 다시 연결해야 새 서버 동작과 에이전트 안내가 적용됩니다. 기존 `mcp_roots` 저장 키와 설정값은 유지합니다.

---

# LAN MCP v1.7.0 검증 · 2026-09-29

실행 중인 설치본/서비스/설정은 수정하지 않았다. 격리 fixture도 제품과 같은 Windows GUI
실행 파일 형식으로 컴파일했고, 클라이언트는 실제 `dist\RemoteControl.exe --mcp-client`를 사용했다.

`bench\Test-Mcp.ps1` 및 `bench\McpIntegrationTest.js`: **35개 통과**.

- 공식 MCP SDK 1.30.1: Streamable HTTP 및 stdio 초기화·도구 조회, 18개 도구.
- 기존 Remote ID/PW 방식 로그인, 잘못된 비밀번호, 미인증 요청, Origin 제한.
- 인증 무효화 후 한 번 재로그인, 반복 연결에서 작업 프로세스가 늘지 않음.
- 한글 파일 쓰기/읽기/수정, SHA-256 사전 조건, 원본 백업, 누락 인자 거부.
- 허용 폴더 이탈과 junction 차단, 제한된 파일 검색.
- 4MiB 바이너리 스트리밍 업로드/다운로드 및 SHA-256 일치, 무단 덮어쓰기 거부.
- 설정에 따른 명령·화면 기능 차단, 합성 화면의 MCP JPEG 응답.
- PowerShell 출력·종료 코드, 실행 중 출력 읽기, 줄바꿈 없는 대량 출력 제한.
- 지정 작업 종료 및 자손 정리. 프로세스 이름 일괄 종료는 사용하지 않음.
- 컴파일된 진단 exe 전송 → 실행 → 종료 코드 및 대상 결과 파일 확인.
- 요청 ID 없는 변경 호출 거부, 감사 로그에 비밀번호 미포함.
- **설치된 실제 Claude Code의 `mcp list`에서 Connected 확인**.
  `CLAUDE_CONFIG_DIR`를 시험 폴더로 지정하여 사용자 설정과 분리했고 모델 요청은 보내지 않았다.

패키지 검증:

- Claude MCPB의 공식 v0.3 JSON Schema 통과.
- Plugin Creator의 `validate_plugin.py` 통과.
- ZIP/MCPB 압축 무결성, 포함 exe와 dist exe의 동일성, 사용 안내 포함 확인.
- setup에 내장된 exe·ZIP·MCPB의 SHA-256이 각각 dist 파일과 일치.
- 사용자 IP/ID/PW 설정 파일은 패키지에 포함하지 않음. 기본 setup에 preset.txt 없음.

기존 기능 회귀: Control 33개, 실제 화면 WebSocket 서버 16개, 브라우저 제어 19개,
브라우저 다중 업로드 10개 및 ViewerLint 통과.

검증 범위: 이 PC의 격리 서버/프로세스와 합성 화면을 사용했다. 실제 오프라인 대상 PC에
설치하거나, 사용자 Claude Desktop에 확장을 등록하거나, LLM 모델에게 실제 프로그램 수정을
의뢰하는 시험은 하지 않았다. Claude Desktop 확장은 수동 등록이 필요하다.

배포 exe 시험에서 콘솔 없는 winexe의 `Console.InputEncoding` 예외를 발견했다.
상속된 표준 입출력 파이프를 직접 UTF-8 reader/writer로 열도록 고친 후 배포 exe와 Claude Code
연결 시험을 통과했다. 콘솔형 시험 exe만으로 호환성을 판단하지 않는다.
