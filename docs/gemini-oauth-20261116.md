# Gemini 웹 MCP OAuth 호환 업데이트 (2026-11-16)

## 분석 범위와 원인

PhonePilot의 GEMINI-MCP-OAUTH-HANDOFF.md, GEMINI.md, MCP.md의 인증 설명,
OAUTH_RECOVERY.md와 VALIDATION-1.0.34.md를 참고하고 Google 공식 문서를 다시 확인했다.
변경 대상은 RemoteControl의 web-mcp이며 PhonePilot은 변경하지 않았다.

수정 전 격리 회귀 검사에서 Google 콜백 6개 DCR은 400, 1,400자 이상의
ChatGPT state는 승인 오류 리디렉션으로 거부되었다. 이는 호환성 결함의 재현이며
사용자의 실제 Google 요청을 캡처했다는 뜻은 아니다.

## 구현

- Google의 정확한 세 호스트 oauth-redirect.googleusercontent.com,
  oauth-redirect-sandbox.googleusercontent.com, oauth-redirect-test.googleusercontent.com의
  HTTPS /r/<id>, /a/<id>만 지원한다. ID는 영문·숫자·밑줄·하이픈이다.
- 한 클라이언트에 1~6개 고유 Google 콜백을 저장·반환한다.
  중복·다른 서비스 혼합·임의 포트·userinfo·query·fragment·역슬래시·인코딩 경로·제어문자는 거부한다.
  SDK가 URL을 정규화하기 전에 요청 원문도 검사한다.
- Google 승인 URI는 등록 문자열 전체와 정확히 일치하고, 코드 교환은 실제 코드 발급 URI에 묶인다.
  기존 로컬 클라이언트의 SDK loopback 포트 변경 허용은 유지한다.
- state는 해석하지 않고 UTF-8 기준 최대 8KiB를 그대로 반환한다. 전체 요청 헤더와
  authorize 본문은 32KiB로 제한한다. 인코딩으로 24KiB가 되는 state도 처리한다.
- 10분 승인 대기를 기존 AES-GCM 인증 저장소에 추가한다. state·CSRF·PKCE·callback·resource를
  재시작 후 복원하며, 기존 pending 필드가 없는 저장소도 읽는다.
  대기는 최대 128개, 전체 암호화 파일은 기존 2MiB 상한을 쓰기 시점에도 검사한다.
  저장 실패 시 승인 대기와 코드 교환을 다시 시도할 수 있다.
- client_secret_post(생략 시 기본값)로 인증하는 Google 클라이언트만 refresh token을 유지한다.
  매 갱신에는 새 access token을 발급한다. 공개 Google, ChatGPT, Claude, 로컬 클라이언트의
  refresh 회전·재사용 감지는 유지한다. 기존 연결 수명·회수 정책은 변경하지 않는다.
- 관리자 로그인·CSRF·동일 출처 폼·PKCE S256·resource/scope·연결별 작업 격리를 유지한다.
  로그인과 복귀 문서에는 Google (Gemini)를 표시한다.
- 기존 connection-events.jsonl의 register 단계에 고정 reason만 추가한다.
  accepted, callback_invalid, auth_method_unsupported, flow_unsupported, capacity_reached,
  storage_unavailable, metadata_invalid, rate_limited, request_rejected를 기록한다.
  시간·고정 단계·메서드·HTTP 상태·소요시간 외 요청 URL, client ID/secret,
  state, code, token, Cookie, 로그인 자격 증명은 기록하지 않는다.
  기존 256KiB 회전 로그와 .previous 보존을 사용한다.
- LAN HTTPS는 계속 loopback 클라이언트만 허용한다. Google CIMD, client_secret_basic,
  추가 Origin/CORS 허용은 실제 요청 근거 없이 확장하지 않았다.

## Gemini 연결 방법

1. 수정된 서버를 적용한 뒤 기존 공개 HTTPS /mcp 주소를 Gemini 사용자 지정 앱에 입력한다.
   기본 HTTPS 포트 443은 생략한다. Gemini API 키나 별도 MCP 포트는 필요하지 않다.
2. Dynamic Client Registration을 선택한다.
3. RemoteControl 로그인 화면에서 원격제어 계정으로 로그인하고 연결을 승인한다.
4. Gemini에 연결 완료가 표시된 뒤 pc_get_status로 대상 PC를 확인한다.

“자동으로 등록하지 못했습니다”와 client ID/secret 입력란은 관리자 로그인 전 DCR 오류다.
관리자 ID/비밀번호를 이 입력란에 넣지 않는다. 해당 시간대 register 단계와 reason,
Gemini의 리디렉션 URI 복사 결과를 확인한다. 비밀값을 채팅이나 로그로 복사하지 않는다.
자동 등록 응답·승인 페이지가 성공해도 token과 인증된 MCP 요청까지 성공해야 연결 완료다.

## 배포와 검증 범위

v1.9.13은 GitHub main의 v1.9.12 소스 위에 이 변경을 적용했다.
공식 빌드_setup.ps1 -NoKey로 본체·WebMCP·Inno 설치 파일을 함께 빌드한다.
설치 파일명은 RemoteControl_Setup_v1.9.13.exe이며 이후 빌드도 실제 본체 버전을 자동 반영한다.
원래 작업 폴더의 별도 미커밋 변경은 릴리즈에 섞지 않는다.

자동 검사·설치 패키지 검증·해시는 [검증 기록](gemini-oauth-validation-20261116.md)에 기록한다.
실제 Gemini 계정 연결은 별도 확인 사항이다. 브라우저 시험은 임시 HTTPS·합성 계정·Windows fixture를 사용하고
Google 콜백을 브라우저에서 가로채므로 실제 Google 계정이나 외부 Google 서버에 시험 코드를 보내지 않는다.

참고:
- https://developers.google.com/connected-apps/mcp/oauth
- https://developers.google.com/connected-apps/mcp/dev-mode/usage
