# Gemini 커넥터 readOnlyHint 설정

2026-11-16. 대상 소스: `D:\Src\RemoteControl-Gemini-Hints-20261116`, `feat/gemini-readonly-hints-1915`. v1.9.14의 최신 설정 창 수정 위에 v1.9.15로 구현했다.
PhonePilot의 개발 요청서는 동작 계약만 참고했으며 PhonePilot 파일·인증정보는 변경하거나 복사하지 않았다.

## 사용 방법

1. 본체와 Web MCP 게이트웨이를 함께 갱신한다. 본체의 **Web MCP → Gemini 커넥터 설정**을 연다. 일반 사용자 창에서는 기존 `--mcp-settings` UAC 전용 창 경로를 사용한다.
2. 각 도구의 **서버 기본값**을 확인하고 **기본값 / true / false** 중 하나를 선택한다. 기본값과 다르면 화면에 표시되며 실제 부작용과 실행 권한은 유지된다.
3. **저장** 성공을 확인한다. 실행 중인 서버에는 기존 설정 갱신 주기에 따라 수 초 이내 반영된다. 서버가 꺼져 있어도 설정은 가능하며, 설정 화면 진입은 원격 접속을 활성화하지 않는다. 실제 이용 시 기존 Web MCP 사용 설정과 HTTPS 연결이 필요하다.
4. Gemini Apps Developer Mode의 **Tool schemas → Sync schema**를 실행하고 Raw schema의 `annotations.readOnlyHint`를 확인한다. 해당 기능이 없는 제품에서는 커넥터를 재연결한다.
5. Gemini Apps의 **Customize → Connectors → 해당 앱 → App actions**에서 필요한 액션의 **Always allow**를 선택한다. 화면을 다시 열고 새 대화에서도 선택값과 실제 실행 결과를 확인한다.
6. 원복하려면 **모두 기본값으로 → 저장**한다. 저장 전 취소·창 닫기는 변경을 버린다. 이전에 이미 저장한 변경은 취소로 되돌아가지 않는다.

이 안내의 제품 기준은 Gemini 웹·모바일 앱이다. Enterprise와 CLI의 권한 설정은 별도 제품 정책을 확인해야 한다. 실제 계정·제품별 메뉴 제공 여부는 아직 검증하지 않았다.

`readOnlyHint=true`는 표준상 환경을 변경하지 않는 조회를 뜻한다. 관리자 지정값이 실제 동작과 다르면 도구 설명에도 이를 명시한다. 서버는 Gemini의 App actions 권한을 저장하거나 강제하지 않는다. ‘항상 허용’ 메뉴 활성화·설정 유지·예약 실행 확인창 생략을 보장하지 않는다.

## 저장 및 연결 범위

- 기존 보호된 `ProgramData\RemoteControl\settings.txt`의 `mcp_gemini_readonly_hints`에 도구 이름과 JSON boolean 맵만 저장한다. 설정 파일을 직접 수정하는 방식은 권장하지 않는다. 기존 관리자 ACL·설정 파일 잠금·원자적 저장을 사용한다.
- 키 미존재와 `{}`는 서버 기본값이다. `false`도 명시적인 사용자 설정으로 보존한다. 알 수 없는 이름·boolean 외 값·UTF-8 16KiB 초과 요청은 전체 거부한다.
- 파일 저장 전 실행 맵을 바꾸지 않는다. 저장 실패는 실패로 표시하고 마지막 저장값을 유지한다. 서버는 다음 설정 읽기에서 완전한 문자열 스냅샷 하나를 사용하므로 별도 비동기 반영 명령의 성공을 과장하지 않는다. 적용 확인은 Gemini의 Raw schema로 수행한다.
- 도구가 삭제·이름 변경된 뒤의 이전 키는 복원 시 무시한다. 다음 저장 시 현재 도구만 남는다. 손상된 설정은 UI에 읽기 실패를 표시하고 저장을 비활성화한다. **모두 기본값으로 → 저장**으로 복구할 수 있다.
- 본체 도구 카탈로그는 `McpTools.List()`에서, 웹 전용 전송 도구는 본체에 내장하고 게이트웨이도 읽는 `web-mcp/web-tools.json`에서 가져온다. UI용 수동 도구 목록을 두지 않는다.
- HTTPS/OAuth 인증을 통과한 grant의 client ID로 서버가 보관한 등록 정보를 읽는다. 등록된 모든 redirect URI가 기존 검증된 Google 콜백 집합일 때만 적용한다. 공개 클라이언트와 `client_secret_post` 클라이언트에 동일하게 적용한다.
- Google 콜백 범위는 여러 Google 제품이 공유할 수 있다. 특정 Gemini 제품의 신원을 증명한다는 의미가 아니다. Origin, User-Agent, client_name, initialize.clientInfo는 범위 판별에 사용하지 않는다.
- ChatGPT·Claude·식별되지 않은 연결·loopback 클라이언트와 LAN 연결에는 기본값을 유지한다. 하위 PC의 중첩 도구 설명은 변경하지 않으며, 메인 카탈로그의 `pc_bridge_call` 등 자체 도구만 선택 대상이다.

## 서버 경로

인증된 loopback 내부 처리기의 `tools/list`는 저장값 스냅샷을 내부 메타데이터로 게이트웨이에 전달한다. 게이트웨이는 이 내부 필드를 외부 응답에서 제거한 뒤 인증된 등록 범위에만 덮어쓰기를 적용한다. 공유 카탈로그와 도구별 annotations·_meta·inputSchema는 분리된 복사본이다.

명시적으로 지정한 도구에는 다음 서버 전용 메타데이터를 추가한다.

```json
{
  "_meta": {
    "remotecontrol/defaultReadOnlyHint": true,
    "remotecontrol/readOnlyHintOverride": true
  }
}
```

이 키들은 표준 권한 필드가 아니다. `destructiveHint`, `idempotentHint`, `openWorldHint`, 도구 이름, 인자 스키마, OAuth securitySchemes, 실제 도구 구현은 변경하지 않는다. `listChanged:false`를 유지한다. 원격 설정 변경용 MCP 도구는 추가하지 않는다. 명령·화면 활성화 설정, 파일 SHA 검증, 프로세스·전송 소유권과 브릿지 정책도 유지한다.

## 검증

- `bench/Test-GeminiSettings.ps1`: 실제 Config의 잠금·원자적 저장·재시작, 입력 검증, 동시성, 저장 실패, WinForms 저장·취소·복원과 두 크기의 렌더링. 생성된 시험 어셈블리에서만 경로와 관리자 ACL 경계를 대역으로 바꾸므로 관리자 설치 시험과 구분한다.
- `web-mcp/test/gemini-hints.test.mjs`: 별도 Windows 처리기와 실제 OAuth 게이트웨이를 HTTP로 연결하여 공개/비밀 Google 연결의 반영, 다른 연결 격리, 재시작, 이름·헤더 위장, 동시 조회, 실행 제한 보존을 검사한다. 합성 자격증명과 임시 포트를 사용한다.
- 기존 MCP 통합과 Web MCP 전체 회귀, 설치 패키지 복사·리소스 해시 검사를 함께 실행한다. 자세한 결과는 [검증 기록](gemini-readonly-validation-20261116.md)에 기록한다.

## 공식 문서 확인

2026-11-16 확인: [MCP ToolAnnotations](https://modelcontextprotocol.io/specification/2025-11-25/schema#toolannotations), [Google MCP integration standards](https://developers.google.com/connected-apps/mcp/server-optimization), [Gemini Developer Mode schema sync](https://developers.google.com/connected-apps/mcp/dev-mode/usage), [Gemini Apps 액션 권한](https://support.google.com/gemini/answer/18766567?hl=en).
