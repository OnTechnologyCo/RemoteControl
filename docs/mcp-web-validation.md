# 웹 MCP 중계 검증 · 2026-10-04

원격제어 본체 v1.7.0 위에 별도 `remotecontrol-web-mcp` v1.0.0 중계를 준비했다.
기존 LAN/stdio 패키지와 실행 중인 서비스는 변경하지 않았다.

## 완료

- `bench/Test-Mcp.ps1`: 기존 LAN/stdio 실통신 시험 35개 통과.
- `web-mcp`의 `npm test`: 13개 테스트 통과(격리 통합 시험의 하위 테스트 포함).
  실제 `McpHost` fixture와 공식 MCP SDK 1.30.1을 사용했다.
- 프록시 인증, Host/Origin 검사, 미인증 MCP 거부, 고정 ChatGPT callback,
  공개 metadata, CIMD/DCR, 로그인 CSRF, 잘못된 비밀번호, 취소, PKCE,
  코드 만료/재사용, audience/redirect 검사, refresh 회전/재사용 차단,
  토큰 수명, 다른 client의 취소 거부, 비밀번호 변경 후 차단을 확인했다.
- 공식 SDK로 초기 연결·도구 목록·상태 조회·원격 파일 쓰기·읽기에 성공했다.
  이 PC loopback에서 한 묶음 약 100ms. 인터넷 지연이나 화면 전송 성능 측정값이 아니다.
- 명령 응답 유실을 주입해 실행 요청을 한 번만 보내는 것을 확인했다.
- 실제 Caddy 2.11.7로 loopback HTTPS와 중계를 시험했다. 시험 CA를 Windows
  신뢰 저장소에 설치하지 않았다. 운영 설정은 Caddy로 변환 검사해 443 사용,
  HTTP challenge 및 HTTP redirect 비활성화를 확인했다.
- 고정 의존성의 npm audit: 취약점 보고 0개. Caddy 배포 ZIP은 공식 SHA-512
  목록과 일치 확인. Node 실행 파일의 Authenticode 서명을 확인했다.
- 설치·중지·플러그인 내보내기·빌드 PowerShell 파일의 구문 검사 통과.

## 배포 후 확인할 항목

- Windows LocalService 예약 작업과 HTTPS 서비스 등록·시작, 재부팅 자동 실행.
- DDNS `najhna.dyndns.org`에서 이 PC까지 TCP 443 도달 여부와 공개 인증서 발급/갱신.
- 사용자 ChatGPT 계정에서 OAuth 연결, 도구 발견, 실제 도구 호출.
- 등록한 뒤 연결 해제/재연결 동작과 연결 만료 시 재로그인 UI.

현재 확인한 라이브 상태: `RemoteControlSvc` 실행 중, 화면 포트 59330,
LAN MCP 포트 59331. 80은 GoClaw가 사용한다. 웹 중계 설치 시 80을 건드리지 않으며
443만 사용한다. 빌드·시험 중 서비스 설치, 방화벽 추가, 공개 인증서 발급,
ChatGPT 계정 등록은 수행하지 않았다.

배포 패키지: `dist/RemoteControl_WebMCP.zip`. ZIP에는 실행에 필요한 Node/Caddy와
중계 프로그램, 의존성, 설치/중지/내보내기 스크립트가 포함된다. 비밀번호·토큰·개인
인증서는 포함하지 않는다. 웹 플러그인 ZIP은 공개 HTTPS 검사 후 별도로 생성한다.
