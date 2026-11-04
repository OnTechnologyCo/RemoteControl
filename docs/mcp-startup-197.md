# v1.9.7 Web MCP 전환 및 자동 시작

기본 MCP 연결은 HTTPS/OAuth Web MCP로 통일했다. 기존 직접 LAN HTTP 접속, stdio 연결기, 연결기 설정 창, 구형 MCP ZIP/MCPB 패키지를 제품에서 제거했다. Web 게이트웨이에 필요한 내부 처리기는 127.0.0.1에서만 수신하며 외부 접속을 받지 않는다. 기존 OAuth 상태와 Web 연결 설정은 유지한다.

## 시작 동작

- MCP 사용 설정이 켜져 있으면 화면 초기화와 화면 포트 바인딩보다 먼저 내부 처리기를 시작한다. 화면 포트가 사용 중이어도 MCP 시작을 막지 않는다.
- 관리자/SYSTEM 프로세스는 저장된 Web 모드에 해당하는 기존 예약 작업과 HTTPS 서비스를 즉시 확인하고, 이후 15초마다 중지 상태를 복구한다. 실행 중·대기 중·사용 안 함 작업을 중복 실행하거나 새로 등록하지 않는다.
- Private/Tailscale 주소 heartbeat를 화면/UI 타이머와 분리했다. 터널 감시도 활성화 시 즉시 깨운다. 실제 외부 연결 완료 시간에는 네트워크·터널 준비 시간이 추가된다.
- 내부 포트 충돌은 기존 2초 재시도로 복구한다. 사용 안 함·비밀번호 미설정 시 내부 포트를 열지 않는다.
- 설치 시 구형 RemoteControl_MCP.zip, RemoteControl_Claude.mcpb 두 패키지만 정리한다. Web 패키지와 사용자 파일은 보존한다.

## 검증 (2026-11-04)

- MCP 자동 시작: 25개 통과. 화면 포트 충돌, loopback 수신, 중복 시작, 설정 토글, 비밀번호 미설정, 포트 충돌 후 복구, 예약 작업 상태, 모드 선택/공유 HTTPS, 터널 감시 재활성화.
- 실제 Windows 내부 처리기와 OAuth Web 게이트웨이를 연결한 통합 테스트: 42개 통과. 파일·폴더, 해시, 4MiB 업로드, 다운로드, 명령 실행·중지, Windows ACL, 비밀번호 변경 후 권한 취소.
- Web OAuth/HTTPS/파일전송 회귀 테스트: 57개 통과, 실패·생략 없음. 공식 MCP SDK, 인증 재시작 보존, PKCE, 브라우저 전송, 20MiB 청크/재개, 내부망 HTTPS 포함.
- Web 전용 전환: 34개 통과. 구형 클래스/CLI/메뉴/패키지 제거, 새 UI 범위, 업그레이드의 구형 패키지 정리 및 사용자 파일 보존.
- 시작 알림 회귀 12개, 설치 업그레이드 27개, LAN 연결 회귀 192개 통과.
- 로컬 격리 시험에서 화면 포트가 막힌 상태에서도 MCP 초기 응답은 약 80ms였고, 내부 포트 충돌 해제 후 복구는 약 2초였다. 인터넷 연결 완료 시간을 뜻하지 않는다.

빌드: dist/RemoteControl.exe 및 dist/RemoteControl_Setup.exe, 버전 1.9.7. 운영 서비스(현재 1.9.6)에 설치하거나 재부팅하지 않았다. 적용 후 실제 부팅 및 외부 접속 확인은 별도 필요하다.

## 제거한 파일

- src/McpClient.cs
- 빌드_mcp.ps1
- plugins/remotecontrol-lan/
- plugins/claude-mcpb/
- docs/mcp-lan.md
- docs/mcp-validation.md
- dist/RemoteControl_MCP.zip
- dist/RemoteControl_Claude.mcpb

원본은 artifacts/legacy-mcp-removed-b762a6173d5c4aefb0f4f26aef1cd94c에 보관했다. 배포·컴파일 대상에서는 제외했다.
