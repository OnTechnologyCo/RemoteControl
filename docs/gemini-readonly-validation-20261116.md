# Gemini readOnlyHint v1.9.15 검증 기록

검증일: 2026-11-16. [사용 안내와 설계](gemini-readonly-hints.md).

## 소스와 작업 범위

- 최신 v1.9.14 커밋 `5e1f0f2`에서 `feat/gemini-readonly-hints-1915` 브랜치와 `D:\Src\RemoteControl-Gemini-Hints-20261116` 작업 트리를 생성했다. 빌드 검증 시점에는 미커밋 상태였다. GitHub 공개 시 v1.9.15 태그에 이 소스를 연결한다.
- 원래 `D:\Src\원격제어`는 v1.9.9 기반의 미커밋 개발 작업이 있어 최종 배포 소스로 사용하지 않았다. 원래 소스 변경은 작업 시작 상태로 보존하고, 완성된 버전별 배포 파일만 그 폴더의 `dist`에도 복사했다.
- v1.9.14의 MCP 전용 관리자 진입점·중복 창 방지·DPI 초기화·아이콘과 v1.9.10~12의 화면/세션 수정을 유지했다. 본체·Setup·Web MCP package/lockfile 버전은 1.9.15다.
- 기존 27개 도구의 네 어노테이션은 [기본값 기록](gemini-default-annotations-1915.json)에 남겼다. 이 파일은 감사 자료이며 UI나 실행 카탈로그로 사용하지 않는다.
- 운영 서비스·예약 작업·정책·자격증명·터널 신원을 변경하지 않았다. PhonePilot은 개발 요청 문서만 읽었으며 프로그램·키·설정은 복사하지 않았다.

## 로컬 빌드 검증 시점의 완료 상태

| 항목 | 상태 |
|---|---|
| 소스 구현 | 완료: 관리자 UI, 저장/취소/복원, Google 인증 범위, 메타데이터 표시, 패키지 포함 |
| 테스트 | 아래 핵심 회귀 통과. 실제 SSH 1개 건너뜀, 관리자 환경이 필요한 별도 시험 2개 미완료 |
| 빌드·서명 | 공식 빌드 완료. 기존 AppId/assembly identity 유지. 본체·설치 파일은 기존과 같이 NotSigned, 새 코드 서명 없음. Node 발급자 서명 Valid |
| 업로드·배포 | 버전별 로컬 산출물 생성·해시 검증 완료. GitHub 업로드·릴리스·운영 배포 미실행 |
| 실제 Gemini 검증 | 미실행. 실제 계정 Sync schema, Always allow 저장/재접속 유지/대표 호출/예약 실행은 별도 확인 필요 |
| 실제 기기 설치 | 미실행. 설치 프로그램을 운영 기기에 실행하지 않음 |

GitHub 공개 상태는 [v1.9.15 릴리스](https://github.com/OnTechnologyCo/RemoteControl/releases/tag/v1.9.15)에서 확인한다. 아래 artifacts 로그 링크는 로컬 작업 트리의 검증 자료이며 저장소에는 포함하지 않는다. 운영 설치와 실제 Gemini 검증 여부는 GitHub 공개와 별개다.

## 통과한 검사

| 검사 | 결과 및 증거 |
|---|---|
| 네이티브 설정·UI | 29개 + 새 프로세스 재시작 1개, 총 30개. [로그](../artifacts/gemini-settings-tests.log) |
| 실제 본체의 일반 사용자 저장 거부 | 관리자 검사에서 저장 전 거부. [로그](../artifacts/gemini-hints-production-denial.log) |
| 기존 MCP 통합 | 42개, 27개 웹 도구. [로그](../artifacts/gemini-hints-mcp-test.log) |
| Web MCP 전체 | 80개 중 79 통과, 실패 0, 건너뜀 1. [로그](../artifacts/gemini-hints-all-web-tests.log) |
| MCP 전용 설정 창 회귀 | 13개. 관리자 진입 거부·별도 프로세스 중복 방지/활성화·종료·재열기. [로그](../artifacts/gemini-hints-settings-window.log) |
| 합성 입력/화면 fixture 준비 | 41개 Control 검사 통과. 네이티브 설정 로그 앞부분에 기록 |
| 설치 패키지 | 본체·WebMCP 내장 해시, 플러그인 배치, 웹·Tailscale·프라이빗·LAN 네 설치 경로의 실제 복사 블록 실행 및 Node import 통과. [로그](../artifacts/gemini-hints-package-tests.log) |
| ZIP·본체 카탈로그 | 11개 주요 웹 파일 소스 해시 일치, 런타임 설정·인증 상태 미포함, 내장 web-tools.json과 게이트웨이 JSON 동일. [ZIP 검사](../artifacts/gemini-hints-zip-verification.log), [본체 검사](../artifacts/gemini-hints-native-resource.log) |
| 최종 파일 | 모든 manifest 소스 해시, 버전 Setup/고정 이름 Setup 동일, 사용자 작업 폴더 사본 SHA-256 동일. [배포 증빙](../artifacts/gemini-hints-delivery-verification.json) |

새 HTTP 검사는 실제 Windows 내부 처리기와 OAuth 게이트웨이를 임시 포트로 연결한다. Google 공개/비밀 클라이언트의 true/false, 선택하지 않은 도구·다른 어노테이션·인자 스키마 보존, ChatGPT/Claude/loopback 격리, 이름·헤더 위장, 혼합 콜백 제외, 저장 거부, 프로세스/게이트웨이 재시작, 동시 수정/조회, 원격 설정 변경 도구 부재와 명령 비활성화 정책을 확인했다.

네이티브 설정 시험은 생성된 시험 어셈블리에서 Config 경로와 ACL 경계만 대역으로 바꾼다. 실제 Config의 파일 잠금·원자적 교체·실패 시 재로딩 코드를 실행한다. 관리자 권한을 가진 실제 사용자로 저장한 시험과 구분한다. 기본값 복원은 저장 전까지 반영되지 않으며 취소·실패 시 마지막 저장값을 유지함을 확인했다. 숨겨진 WinForms를 820×610, 680×480에서 렌더링하고 최소 크기의 표·설명·버튼 배치를 시각적으로 확인했다.

기존 브라우저 시험의 Google 콜백은 가로채기로 처리한다. 실제 Google 계정에서 OAuth를 승인한 결과가 아니다.

## 미완료·제외한 검사

- `actual SSHTunnel shared screen/OAuth/file routes and real HTTPS browser login`: 명시적 `RC_SSH_FIXTURE` 환경이 없어 건너뛰었다. 실제 터널 통합 성공으로 집계하지 않았다.
- `bench/Test-McpBridge.ps1`: 일반 권한 환경에서 fixture 프로세스가 기동 중 종료되어 실패했다. 브릿지 회귀 통과로 집계하지 않았다. 해당 fixture는 보호된 Config를 준비하는 관리자 환경이 필요하다.
- `bench/Test-LanWebInstall.ps1`: 격리 폴더의 관리자/LocalService ACL 적용 후 `ProgramData\RemoteControlLanWebMcp\data` 생성에서 Access denied로 실패했다. 전체 LAN 설치 회귀는 미완료다. 네 설치 경로의 파일 복사 및 모듈 로딩은 별도 패키지 검사에서 통과했다.
- 최초 패키지 검사에서 소스 줄바꿈 정리 전 gateway가 포함된 해시 차이를 검출했다. 입력을 확정한 뒤 공식 Setup을 재빌드하고 최종 패키지 검사를 재실행해 일치를 확인했다.
- 관리자 권한 확보나 운영 서비스 재설치로 시험 환경을 변경하지 않았다. 실제 Gemini의 권한 유지와 예약 실행 확인창도 검증하지 않았다.

## 배포 파일

공식 `빌드_setup.ps1 -NoKey`로 생성했다. 고정 Go 1.27.3, 모듈 검증, FFmpeg/VDD/Node/Caddy 고정 해시 및 기존 서명 검증을 유지한다. 최초 빌드의 Git 소유권 오류는 해당 빌드 프로세스의 명시적 safe.directory만 지정해 해결했으며 전역 Git 설정은 바꾸지 않았다.

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| RemoteControl_Setup_v1.9.15.exe | 132,305,416 bytes | `8de880fc119c97c26e469408f3c0a8a5b246eb6289ba1d9ae4b2d9ed484bab7c` |
| RemoteControl_WebMCP_v1.9.15.zip | 58,532,260 bytes | `730b225d98ff89a1ecc2e6a558805a3ac041f5dbf1d89e597204739f0eb1d723` |
| RemoteControl_v1.9.15.build.json | 78,953 bytes | `f02ab8142faa9866b0fb8d0ad14aa71362961d1e2060820e679b4946194520fc` |

세 파일과 각각의 `.sha256`은 `D:\Src\원격제어\dist`에도 동일하게 복사했다. WebMCP ZIP은 서버 배포용이며 Gemini에 업로드하는 플러그인이 아니다. 이 기능은 본체의 관리자 UI·저장소도 추가하므로 본체와 Web MCP를 함께 갱신해야 한다.

소스 차이는 기존 Windows 줄바꿈을 허용한 `git -c core.whitespace=cr-at-eol diff --check`로 확인했다. 빌드 증빙은 최신 작업 트리의 소스에 대응한다.
