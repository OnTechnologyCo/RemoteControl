# 브릿지 MCP 중계 (v1.9.7)

메인 PC의 Web MCP 연결 하나로 브릿지에 등록된 하위 PC의 파일·명령·작업·화면 도구를 호출합니다. 하위 PC마다 공개 MCP 주소나 OAuth 연결을 만들 필요가 없습니다.

## 사용 조건

- 메인과 하위 PC 모두 브릿지 MCP를 포함한 v1.9.7 빌드로 업데이트합니다.
- 두 PC에서 **Web MCP 사용**을 켜고 원격제어 ID·비밀번호를 설정합니다. 하위 PC는 별도 HTTPS/터널 설정 없이 사용 설정만 켜면 됩니다.
- 메인의 기존 브릿지 목록에 하위 PC 주소, 화면 포트, 로그인 ID와 비밀번호를 저장합니다. 메인에서 해당 화면 포트로 연결할 수 있어야 합니다.
- ChatGPT/Claude/Codex는 메인의 기존 HTTPS/OAuth MCP 주소에 연결합니다.

## 도구 순서

1. `pc_list_bridge_pcs`로 등록된 PC의 `bridge_id`와 `target_revision`을 확인합니다.
2. `pc_bridge_list_tools`에 두 값을 넣어 하위 PC 도구의 입력 형식을 조회합니다.
3. `pc_bridge_call`에서 `tool_name: "pc_get_status"`를 호출해 하위 PC의 실제 hostname을 확인합니다.
4. 이후 호출마다 같은 ID·revision과 확인한 `expected_hostname`, 하위 도구 이름과 `arguments`를 전달합니다.

예시(목록과 상태에서 얻은 실제 값으로 대체):

```json
{
  "bridge_id": 1,
  "target_revision": "<목록에서 확인한 revision>",
  "expected_hostname": "<하위 PC 상태의 hostname>",
  "tool_name": "pc_list_directory",
  "arguments": {"path": "D:\\Work", "limit": 100}
}
```

일반 `pc_get_status`, `pc_read_file` 등의 직접 호출은 계속 메인 PC를 대상으로 합니다. 전역 선택 PC를 바꾸지 않습니다. 중계 응답의 `_meta.bridge_target`에 실제 대상 정보를 함께 돌려줍니다.

## 파일·작업과 장애 처리

- 파일 수정의 기존 SHA-256/백업 검증, 절대 경로 검사, 하위 Windows 계정의 권한을 유지합니다. 메인과 하위의 명령/화면 허용 설정을 모두 검사합니다.
- 프로세스 session_id와 파일 transfer_id는 대상 ID·revision 및 같은 MCP 로그인과 함께 보관합니다. 다른 PC나 다른 MCP 로그인에서는 사용할 수 없습니다.
- 파일/폴더는 `pc_begin_upload` → `pc_write_upload_chunk` → `pc_finish_upload`, 다운로드는 `pc_prepare_download` → `pc_get_transfer_status` → `pc_read_download_chunk`를 중계 호출합니다. 파일 내용은 직접 확인한 SHA-256으로 검증합니다.
- 중계 HTTP 세션 갱신과 메인 MCP 재시작 후에도 같은 인증의 작업/전송 소유권이 유지됩니다. 하위 PC 재시작 시 실행 중인 작업은 종료되지만 저장된 전송은 상태 조회 후 재개할 수 있습니다.
- 브릿지 주소·계정·비밀번호 등이 변경되면 이전 revision을 거부합니다. 목록과 상태를 다시 확인합니다.
- 인증 만료는 상태 확인부터 다시 호출합니다. 응답 유실은 `BRIDGE_RESULT_UNKNOWN`으로 알리며 명령을 자동 재전송하거나 메인 PC로 대신 실행하지 않습니다.
- `pc_open_file_transfer`의 브라우저 링크는 메인 PC 전용입니다. 하위의 브릿지로 한 번 더 연결하는 다단 중계는 지원하지 않습니다.

## 연결 경계

외부 MCP 진입점은 계속 메인의 HTTPS/OAuth `/mcp`입니다. 내부 McpHost는 127.0.0.1 바인딩을 유지합니다.

메인→하위 구간은 기존 브릿지의 인증된 화면 HTTP 연결을 사용하며 고정 `POST /bridge/mcp`만 처리합니다. 별도 MCP 포트를 LAN에 열지 않습니다. 브릿지 구간 자체에 TLS가 추가되는 것은 아니므로 기존처럼 신뢰할 수 있는 내부망/VPN에서 사용합니다. 하위는 원격제어 세션, 전용 헤더, MCP 사용 설정을 검사하고 브라우저 출처 요청을 거부합니다.

## 검증

`bench/Test-McpBridge.ps1`은 운영 설정과 분리한 메인/하위 2개 프로세스에 실제 Web OAuth 게이트웨이를 연결합니다. 대상 분리, 파일 수정·화면 응답·명령 실행, 권한 설정, 로그인별 소유권, 재시작 후 전송 재개, 구버전, 잘못된 인증, 브라우저 차단, 응답 유실 시 단일 실행을 검증합니다. 실제 서로 다른 PC의 네트워크와 설치 후 연결은 배포 후 확인해야 합니다.
