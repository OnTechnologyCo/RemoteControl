# Claude Code OAuth loopback callback 수정 (v1.8.20)

## 원인과 범위
외부 HTTPS WebMCP의 callbackName(uri, lan=false)가 공식 웹 콜백 목록만 반환해,
Claude Code의 http://localhost:<port>/callback DCR 요청을 invalid_client_metadata로 거절했다.
미등록/유효하지 않은 client_id로 다음 인증 요청을 보내면 SDK에서 Invalid client_id를 반환한다.
LAN 모드에는 루프백 지원이 이미 있었으나 외부 모드와 공유되지 않았다.

## 수정
- 외부 모드: 기존 ChatGPT/Claude 웹 콜백 + HTTP localhost/127.0.0.1/[::1] 루프백 허용.
- LAN 모드: 기존과 같이 루프백만 허용하며 인터넷 CIMD 조회 금지.
- 포트는 1024~65535. 임의 도메인, 사설 IP, 사용자정보, 쿼리, fragment, 제어 문자 및 변형 IP 표기 거부.
- SDK의 RFC 8252 루프백 포트 변경 허용 유지. 호스트/경로는 등록값 검증, 토큰 교환은 실제 인증 요청 redirect_uri와 정확히 결합.
- S256 PKCE, state, 로그인/승인, CSRF, client_id/resource 검증, 일회용 코드, refresh 회전 유지.
- 사용자가 주장한 client_name을 신뢰하지 않고 로컬 MCP 클라이언트로 표시.
- CA URL/SHA256, 다중 IP issuer 격리, 기존 인증 저장소, 터널 주소 및 Defender 조사 변경은 보존.

## 검증 파일
- web-mcp/test/native-oauth.test.mjs: 등록, 세 가지 loopback 호스트, 외부/LAN 6개 흐름,
  콜백/PKCE/resource/client_id 위조 거부, 취소, 코드 재사용 거부, 재시작 후 토큰 갱신.
- web-mcp/test/browser-login.test.mjs: 실제 Chrome 격리 프로필과 HTTPS fixture에서
  기존 웹 두 클라이언트 및 native localhost DCR의 승인/취소/수동 복귀, MCP 도구 호출.
- 상세 실행 로그: C:\Users\Public\Documents\RemoteControlDeploy\OAuthLoopback-1820

## 배포 주의
소스 수정이나 새 설치 파일 생성만으로 다른 장비 PC의 설치본은 바뀌지 않는다.
오류가 발생하는 장비 PC의 WebMCP gateway 및 설치 패키지를 업데이트하고 해당 중계를 재시작해야 한다.
CA/개인키/clients.json/data/auth-state.bin/프록시 키를 삭제하거나 초기화하지 않는다.
이 변경은 Defender 탐지의 오탐 확정 또는 해소를 뜻하지 않는다. 백신 보호를 해제하지 않는다.

## 기준 문서
- https://code.claude.com/docs/en/mcp (Use a fixed OAuth callback port)
- https://www.rfc-editor.org/rfc/rfc8252.html#section-7.3