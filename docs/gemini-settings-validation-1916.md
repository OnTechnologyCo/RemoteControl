# Gemini 설정 MCP 도구 v1.9.16 검증 기록

검증일: 2026-11-17. [사용 안내](gemini-readonly-hints.md).

## 대상과 변경

- 프로그램/저장소: RemoteControl, https://github.com/OnTechnologyCo/RemoteControl.
- 기준 소스: v1.9.15 `51339e1e2c57c67f9c1243b3cc532276074d2f70`. 작업 브랜치 `feat/gemini-settings-tools-1916`, 작업 경로 `D:\Src\RemoteControl-Gemini-Hints-20261116`. 최종 소스는 v1.9.16 태그로 식별한다.
- 원래 `D:\Src\원격제어`의 미커밋 개발 작업은 보존했다. PhonePilot의 요청 문서만 읽고 다른 프로그램의 소스·키·실행 설정은 복사하지 않았다.
- 본체/Setup/Web MCP 버전 1.9.16. 기존 관리자 UI에 실제 MCP 조회/저장 도구, 영속 요청 기록, 부분 변경·복원과 오래된 UI의 저장 충돌 검사를 연결했다. 기본 카탈로그는 29개 도구이며 저장 도구만 사용자 지정이 금지된다.
- 원격 도구: `pc_get_gemini_settings`, `pc_set_gemini_settings`. 로컬 UI: **Web MCP → Gemini 커넥터 설정 → readOnlyHint**. 기존 `--mcp-settings` 전용 관리자 진입과 원격 서비스 비활성 상태에서의 편집을 유지한다.
- 기존 단일 RemoteControl 소유자 계정의 PC 전체 정책이다. 직접 인증된 연결, 확인한 `expected_hostname`, MCP 활성 상태와 저장 시 Windows 관리자 권한을 검사한다. 브릿지 경로에는 관리 도구를 추가하지 않는다. 저장값 적용은 인증 grant의 서버 보관 Google 콜백 등록 집합으로 판별한다. 특정 Google 제품의 신원 증명이 아니다.

## 구현상 보장과 한도

- 기존 Config의 프로세스 잠금·파일 잠금·원자적 교체를 사용한다. UI/원격 도구가 같은 검증/저장 코어를 통과한다. 부분 변경은 최신 맵에 합치고 UI의 전체 저장은 읽은 revision과 비교한다.
- 요청 ID와 인증 연결·자격 증명의 해시, 정규화한 변경의 해시, 적용된 맵과 revision을 같은 파일 교체에 기록한다. 토큰/비밀번호/원본 요청 ID는 새 기록에 포함하지 않는다.
- 같은 ID 재시도는 과거 결과를 반환하고 후속 변경을 되돌리지 않는다. 다른 payload는 충돌, 다른 연결의 조회는 not_found다. GET의 request_id를 생략하면 현재 설정을 읽는다. 과거 결과에는 current_revision/result_is_current가 있다.
- 기록은 256개/연결, 4096개/대상, JSON 8MiB로 제한한다. 자동 만료/삭제 없이 한도에서 신규 저장을 거부하여 오래된 요청 재실행을 방지한다. 한도와 관리자 점검 필요 사항을 도구 설명과 사용 안내에 명시했다.
- 유효 카탈로그를 저장 전에 구성하고 확정된 파일만 다음 요청에서 읽는다. 별도 비동기 런타임 적용 단계가 없다. 실제 도구 구현, 다른 세 어노테이션, OAuth 검사, 실행 권한, 전송/프로세스 소유권은 유지한다.
- 저장 도구의 readOnlyHint는 false 고정이다. 이전 파일의 보호 도구/삭제된 이름은 복원 때 무시하고 다음 저장에서 정리한다.

## 실행한 검증

| 검사 | 결과 / 증빙 |
|---|---|
| `bench/Test-GeminiSettings.ps1` | 64개 + 새 프로세스 재시작 1개 통과. `artifacts/gemini-settings-1916.log` |
| `bench/Test-Mcp.ps1` | 실제 Windows/OAuth 통합 42개 통과, 29개 도구. `artifacts/mcp-1916.log` |
| `cd web-mcp; npm.cmd test` | 80개 중 79 통과, 실패 0, 실제 SSH 환경 1개 건너뜀. `artifacts/web-all-1916.log` |
| 신규 HTTP 설정 도구 검사 | `web-mcp/test/gemini-hints.test.mjs`에 포함. 실제 native 처리기 + OAuth gateway의 tools/list/GET/SET/선택·전체 복원/요청 재시도/재시작/연결 격리/잘못된 입력 원자적 거부 |
| `bench/Test-SettingsWindow.ps1` | 13개 통과. 관리자 진입 거부, 중복 창 방지와 재열기. `artifacts/settings-window-1916.log` |
| 배포 본체 검사 | 일반 사용자 Save 호출이 관리자 경계에서 거부됨. 컴파일된 카탈로그 29개와 setter=false, 내장 web-tools.json 소스 해시 일치. `artifacts/production-check-1916.log` |
| 빌드 입력/출력 | 소스 257개 SHA-256 전부 일치. 본체/엔진 버전 1.9.16, 고정/버전 Setup 동일. `artifacts/delivery-1916.json` |
| `bench/Test-SetupWebMcp.ps1` | 엔진 내장 본체/ZIP 해시, 플러그인 배치/버전, 네 설치 경로의 실제 복사 블록과 Node 모듈 import, 설정 UI 통과. `artifacts/package-1916.log` |

네이티브 시험은 생성된 어셈블리에서만 저장 경로와 ACL 경계를 대역으로 바꾼다. 실제 Config 파일 잠금·원자적 교체를 그대로 실행하고 두 별도 프로세스의 동시 patch, Windows 관리자 거부, 저장 실패 시 정책과 기록 동시 보존, UI 충돌과 재조회, 보호된 도구, 기록 한도를 확인했다. 실제 관리자 계정의 운영 설정 저장 시험으로 간주하지 않는다.

숨겨진 WinForms를 820×610 및 680×480으로 렌더링했고 저장/취소/복원/재조회 버튼이 화면 안에 있음을 검사했다. 820×610 렌더를 시각적으로 확인했다. 이 합성 UI 검사는 원격 화면을 깨우거나 잠금을 해제하지 않는다.

HTTP 시험은 공개/비밀 Google OAuth 클라이언트와 ChatGPT/Claude/loopback의 기본값 격리, Origin/User-Agent/표시 이름 위장 거부, 나머지 annotations와 스키마 보존을 확인한다. 실제 Google 계정의 OAuth/스키마 동기화 결과를 의미하지 않는다.

처음 기존 MCP 시험에서 환경의 FORCE_COLOR/NO_COLOR 경고가 PowerShell 오류로 처리되었다. 시험 프로세스의 FORCE_COLOR만 해제해 다시 실행했고 42개가 통과했다. 제품의 정책이나 테스트 단언을 완화하지 않았다.

## 빌드·배포와 실제 적용 경계

공식 `빌드_setup.ps1 -NoKey`를 사용한다. Go 1.27.3, npm lockfile, FFmpeg/VDD/Node/Caddy 고정 해시, 발급자 서명 검증과 기존 Inno AppId를 유지한다. Git trust는 빌드 프로세스에 지정한 저장소 세 곳으로 한정하며 전역 설정을 바꾸지 않는다.

공식 빌드는 2026-11-17 09:42 KST 완료했다(`artifacts/build-1916.log`). 본체와 설치 파일은 기존 배포처럼 **NotSigned**이며 새 코드 서명은 하지 않았다. 포함 Node 실행 파일의 **OpenJS Foundation 서명은 Valid**다. 소스 fingerprint는 `CC3BA1FE7527BD80572A64727270B4BBC09B9C53A6D958803A03BD03FD4E99E0`이다. 빌드는 커밋 전 확정한 소스에서 수행했으며 manifest의 소스 해시로 최종 커밋 파일과 일치 여부를 검증한다.

| 배포 파일 | 크기(bytes) | SHA-256 |
|---|---:|---|
| RemoteControl_Setup_v1.9.16.exe | 132309736 | `51f658854fb025c56c744a7a223b6bf3afbe2fee2a745193e3fc480dbcbfb4d8` |
| RemoteControl_WebMCP.zip | 58532313 | `c75c2cc4a5aa0f038f66eda6f2bab06200cb2ca5c823b535629f3c82801cbeb5` |
| RemoteControl.build.json | 78953 | `80edb42484a7172820dab95098540d9bae3c8769608390700417fef0deba7dc5` |

GitHub 배포 파일과 SHA256SUMS는 [v1.9.16 릴리즈](https://github.com/OnTechnologyCo/RemoteControl/releases/tag/v1.9.16)에서 제공한다. 검증 로그는 로컬 artifacts에 보관하며 릴리즈에 실제 설정/인증 데이터를 포함하지 않는다.

- 실제 연결 대상은 작업 시작 시 `pc_get_status`로 확인했으며 실행 중 버전은 **1.9.15**, 실행 계정은 SYSTEM이었다. 대상 hostname 등 원격 상태는 로컬 증빙으로 유지한다.
- 이번 범위는 코드 수정과 사용자가 요청한 GitHub 소스/릴리즈 업로드다. 운영 서버 설치·서비스 재시작·정책 변경은 수행하지 않는다. 업로드가 실행 중 서버 갱신을 의미하지 않는다.
- 실제 Gemini 계정에서 두 도구 노출, Sync schema 반영, App actions의 항상 허용 저장/새 대화 유지/실제 호출은 **미검증**이다. 설치 후 실행 버전 → 도구 동기화 → 설정 저장 → 스키마 재동기화 → 앱 권한 확인 순으로 별도 확인해야 한다.
- 실제 SSH 통합 1개는 RC_SSH_FIXTURE가 없어 건너뛰었다. 관리자 환경이 필요한 전체 브릿지/LAN 설치 시험은 이번 일반 권한 환경에서 실행하지 않았다. 운영 설치나 실제 드라이버 시험의 성공으로 주장하지 않는다.
