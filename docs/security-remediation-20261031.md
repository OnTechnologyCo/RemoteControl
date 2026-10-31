# RemoteControl 1.9.1 보안 수정·검증 보고

작성일: 2026-10-31. 작업 기준은 `main`의 `64fb3227047fdecc4ee20f57f3943c728b968bad`이다. 이 보고서는 SEC-01, SEC-06, DEP-01, DEP-03, QA-02에 한정한다. 실제 침해 여부나 전체 제품 보안을 보증하는 보고서가 아니다.

## 항목별 판정

| 항목 | 결과 | 남은 인수 범위 |
| --- | --- | --- |
| SEC-01 | 코드 수정 및 Windows 표준 사용자 격리 회귀 완료 | 독립 VM에서 구버전 설치 → 업데이트 → 실제 서비스 재시작, 대화형 UAC 인수는 미실행 |
| SEC-06 | 코드 수정 및 합성 토큰·실제 named pipe 격리 회귀 완료 | 실제 Telegram 발송·실사용 토큰 회전 및 전체 설치 인수는 미실행 |
| DEP-01 | 수정 완료. production npm audit 0건, 선택 웹 검사 35개 통과 | 이 검사는 해당 시점의 npm 공지 및 시험 범위에 한정 |
| DEP-03 | Windows x64 생산 경로 수정·재빌드·검사 완료 | 모듈 수준 OpenPGP 공지 1종은 미사용 패키지로 적용 대상 아님. Linux/Android 출시 경로는 이 저장소에 없음 |
| QA-02 | 수정 완료. 실제 Windows McpFixture를 먼저 생성하여 누락으로 실패했던 관련 검사를 실행 | 라이브 서비스 대신 격리 fixture 사용 |

SEC-01/06의 파일 권한 경계와 정상 동작은 검사했지만, 별도 시험 설치의 서비스 생명주기까지 포함한 모든 완료 조건을 충족했다고 주장하지 않는다.

## 원인과 변경

### SEC-01: 기계 정책은 관리자 경계 안에서만 변경

기존 `Config.EnsureWritable`과 설치기의 `PrepareDataDir`이 일반 사용자에게 공용 설정 쓰기를 허용했다. `ConfigProtection`을 설치기와 서비스가 공유하도록 바꾸고 다음 정책을 적용했다.

- `%ProgramData%\RemoteControl` 및 그 안의 설정·백업·임시 파일은 SYSTEM/Administrators만 수정한다. 기존 명시적/상속 쓰기 ACL을 교체하며 소유자도 관리자 그룹으로 설정한다.
- 설정 경로의 reparse point와 파일 hard link, 소유자·위험한 허용 ACL을 검증한다. 안전하지 않거나 읽을 수 없는 정책을 기본 비밀번호나 기본 허용 정책으로 대체하지 않는다.
- 인증·MCP·터널 정책은 기존 공용 파일에 보존한다. 실행 파일 옆 설정으로의 폴백은 제거했다. 일반 UI 창 위치는 `%LOCALAPPDATA%\RemoteControl\ui-settings.txt`에 저장하며 서비스는 이 파일을 읽지 않는다.
- 일반 트레이에서 정책 변경을 선택하면 `runas`로 `--admin-ui`를 연다. 이 진입점에서도 실제 관리자 토큰을 확인한다. 비밀번호를 명령줄로 전달하지 않는다. 상승된 창은 사용자 쓰기 가능한 UI 파일에 쓰지 않는다.
- 설치기의 파일 교체/토큰 이관 전에 ACL 이관을 완료한다. 서비스 시작과 에이전트 시작도 같은 보호 함수를 사용한다. 실패는 숨기지 않고 중단한다.

주요 파일: `src/ConfigProtection.cs`, `src/Config.cs`, `src/UiPreferences.cs`, `src/Program.cs`, `src/ServiceHost.cs`, `src/Agent.cs`, `Setup.cs`.

### SEC-06: 토큰은 서비스에 남기고 트레이는 정해진 동작만 요청

LocalMachine DPAPI의 암호문을 다른 로컬 사용자가 읽을 수 있었던 것이 문제다. `credentials` 디렉터리와 파일의 읽기/쓰기 권한을 SYSTEM/Administrators로 제한했다. DPAPI 범위와 기존 암호문은 유지하며 반복 이관 시 바이트가 동일한지 검사했다.

`NotifyBroker`는 무작위 named pipe 이름을 보호된 공용 파일로 알린다. 로컬 대화형 사용자/관리자/SYSTEM만 허용하고 network logon은 거부한다. 허용 요청은 `status`와 `send-addresses`뿐이다. 토큰·수신자·메시지 본문을 클라이언트가 지정하거나 응답으로 받지 않는다. 전송에는 30초 간격 제한이 있다. 트레이의 상태 표시와 수동 주소 전송은 이 IPC를 이용한다. 알림 설정 변경은 관리자 창에서 한다.

실사용 토큰은 읽거나 복호화·출력·회전하지 않았다. 과거 버전에서 신뢰하지 않는 로컬 사용자가 암호문을 읽었을 가능성은 ACL 수정만으로 소급 제거되지 않는다. 그러한 접근이 있었거나 의심되면 운영자가 토큰 재발급을 검토해야 한다. 실제 유출 증거는 이번 작업에서 수집하지 않았다.

### DEP-01: 호환 업데이트와 압축 본문 거부

`package.json`과 lockfile을 함께 갱신했다. 직접 의존성은 MCP SDK 1.32.2, Express 5.3.0, express-rate-limit 8.7.2로 고정했다. 감사 대상 전이 패키지는 body-parser 2.4.0, zod 4.6.7, fast-uri 3.1.9, ip-address 10.7.5, qs 6.17.1이다.

OAuth 연결/등록/토큰/취소 경로의 JSON 및 URL-encoded 파서는 `inflate:false`를 사용한다. 압축 본문은 415로 거절한다. 격리 게이트웨이에 비정상 gzip/deflate/br 입력을 보낸 뒤 프로세스가 유지되고 정상 discovery/등록이 성공하는 회귀를 추가했다. 기존 클라이언트 흐름은 압축 없는 본문으로 통과했다. 압축 요청을 사용하던 외부 클라이언트는 압축을 해제해서 보내야 한다.

### DEP-03: 실제 빌드 경로에서 Go 1.27.3 강제

세 모듈의 최소 버전과 `build/Use-GoToolchain.ps1`을 Go 1.27.3으로 맞췄다. 빌드 스크립트는 `GOTOOLCHAIN=go1.27.3`, `GOOS=windows`, `GOARCH=amd64`, `CGO_ENABLED=0`을 명시하고, 압축 리소스를 만들기 전에 실제 바이너리의 `go version -m` 결과를 검증한다. 개발 PC의 전역 Go 설치는 변경하지 않았다.

영상 helper의 호출 가능한 DNS 파서 공지 GO-2026-5942도 해결했다. video-helper와 tsproxy의 x/net을 0.62.0, x/crypto를 0.58.0으로 맞추고 호환 전이 의존성을 갱신했다. 각 모듈의 Windows 빌드 조건에서 `go test ./...`와 `govulncheck -scan=symbol -show=verbose ./...`를 실행했다.

각 모듈의 호출 가능한 취약점은 0건이다. 남은 GO-2026-5932는 `golang.org/x/crypto/openpgp`의 유지보수 중단 공지로 수정 버전이 없다. 세 모듈의 `go list -deps ./...`에 이 패키지가 없음을 확인했다. 동일 공지를 세 개의 독립 취약점으로 합산하지 않는다. 이 결과를 Linux/Android의 실행 검사로 확대 해석하지 않는다.

## 실행한 검사

Windows 관리자/SYSTEM 시험 프로세스에서 프로젝트 하위 임시 디렉터리를 사용했다. 권한 거부 검사는 기존 대화형 세션의 비관리자 토큰을 impersonation하여 실행했다. 새 OS 계정을 만들거나 운영 서비스·운영 정책을 변경하지 않았다.

| 명령/절차 | 실제 결과 |
| --- | --- |
| `powershell -File bench/Test-SecurityPolicy.ps1` | 22개 통과. 수정 전 일반 사용자 설정 쓰기와 합성 DPAPI 토큰 복호화 재현, 수정 후 쓰기/교체/삭제/이름 변경/상위 폴더 교체 및 토큰 읽기 거부. 기존 로그인·관리자 변경·일반 UI 저장·IPC 성공 |
| `powershell -File bench/Test-Config.ps1` | 7개 통과. 동시 저장·실패 롤백·백업·잘못된 입력·읽기 실패 처리 |
| `powershell -File bench/Test-NotifyMessages.ps1` | 15개 통과. 기존 메시지 내용 회귀; 실제 발송 없음 |
| `powershell -File bench/Test-Mcp.ps1` | 세션 검사 6개와 통합 검사 34개 통과. Windows fixture 및 25개 도구, 합성 화면·격리 파일/프로세스 사용 |
| `npm ci`, `npm audit --omit=dev` (`web-mcp`) | 설치 성공, production 취약점 0건 |
| `node --test test/native-oauth.test.mjs test/gateway.test.mjs test/transfers.test.mjs test/browser-login.test.mjs` (`web-mcp`) | 35개 통과, 실패 0, skip 0. PKCE·refresh·revocation·브라우저 HTTPS 로그인·20 MiB 바이너리 전송 및 압축 본문 거부 |
| `powershell -File bench/Test-TelegramSetup.ps1` | 27개 통과. 합성 입력/DPAPI/기존 값 보존/권한 거부/입력란 마스킹 |
| `powershell -File bench/Test-PrivateMcp.ps1` | 21개 통과 |
| `powershell -File bench/Test-TailscaleMcp.ps1` | 19개 통과 |
| `powershell -File bench/Test-BuildSecurity.ps1` | 합성 토큰 이관 4개, 빌드 입력 검증 5개, 드라이버 디렉터리 정책 2개 통과. 구버전 프로그램/드라이버 실행 없음 |
| `powershell -File bench/Test-StartupQuiet.ps1` | 최종 1.9.1 본체에서 12개 통과 |
| `powershell -File bench/Test-SetupWebMcp.ps1` | 최종 설치 엔진과 dist 리소스 해시 일치, 격리 패키지 배치, 3개 설치 경로의 런타임 import, 플러그인 URL/버전 및 설치 UI 검사 통과. Setup Main/Install 및 서비스 실행 없음 |
| 토큰 내보내기 스크립트의 공유 C# 의존성만 `Add-Type` 컴파일 | 성공. 실사용 토큰 읽기/내보내기는 실행하지 않음 |
| 각 Go 모듈의 `go test ./...` | video-helper, sshtunnel-client, tsproxy 모두 통과 |
| 각 Go 모듈의 `govulncheck -scan=symbol -show=verbose ./...` | 호출 경로 및 import 패키지 취약점 0건, 위 OpenPGP 모듈 공지 1종만 남음 |

검사 수를 전체 제품 커버리지로 환산하지 않는다. 위 명령은 해당 시험 환경에서 실행한 기록이며 GPU 실장비 성능, 실제 WAN 연결, 실제 Telegram API 수신, 독립 VM 설치/업데이트/서비스 재시작, 사람이 승인하는 UAC 흐름은 이번 검사의 통과 수에 포함하지 않았다.

## 업그레이드와 산출물

기존 설정 값·비밀번호 해시를 유지하고 ACL만 회수한다. 손상된 자격 증명은 새 값으로 덮거나 미등록으로 간주하지 않는다. 반복 권한 이관 후 기존 비밀번호 로그인과 합성 토큰의 동일 암호문이 유지되었다. 실제 설치된 사용자 데이터로 시험하지 않았다.

본체/설치 엔진/플러그인 버전은 1.9.1이다. 공개 설치물은 Inno Setup 마법사이며, 내부 설치 엔진과 세 Go helper 및 WebMCP 패키지를 다시 빌드한다. 빌드는 서비스 설치/업데이트를 실행하지 않는다.

`빌드_setup.ps1 -NoKey -InnoCompiler <ISCC.exe>`가 2026-10-31 17:37 KST에 성공했다. 산출물은 프로젝트의 `dist`에 있다. 설치 엔진의 `ContainsPreset`은 false이며, 기존 빌드 검사도 본체에 Telegram 토큰 상수가 남지 않았음을 확인했다. 별도 코드 서명/백신 판정 작업을 수행했다는 의미는 아니다.

| 산출물 | 크기(바이트) | SHA-256 |
| --- | ---: | --- |
| `RemoteControl_Setup.exe` | 278306487 | `A226FC99B4985160CDC190106B8F4AF382FEFE8BFE775437AC6F7F17E02B8DE2` |
| `RemoteControl_SetupEngine.exe` | 278448640 | `77E54E11804C63BF4C2E5ECCDA5B9B7A28BCBEDE2153E5F3C42183F1C25C05A5` |
| `RemoteControl.exe` | 73454592 | `C9805602E5A766CE1DDC3256038F8C5E776A9835CA1891C88C8C552393D31829` |
| `RemoteControl_WebMCP.zip` | 58528054 | `D4A73D1365D2171310E83B42791BC2EEB6F09C3F7194B228548C88A18A7F3A43` |

본체에 들어간 각 gzip 리소스를 메모리에서 해제하여 아래 실제 Go 실행 파일과 SHA-256을 비교했다. 세 파일 모두 일치하고, `go version -m`은 **go1.27.3 / windows / amd64**를 확인했다. 설치 엔진에 포함된 본체도 위 dist 본체와 일치한다.

| 포함 helper | SHA-256 |
| --- | --- |
| `tsproxy.exe` | `F058B526F60D04086AB4DCFD940F34027BC43D9D1516D2EB6098DECFBBFFEB71` |
| `sshtunnel-client.exe` | `58699DCCE5670248DBC98803B22287D1D00225958BB044D556F1536A3B445FE7` |
| `video-helper.exe` | `A45132CA0F24FA99C8A531987713451C331913EBDE65BA40C9B50CFD00961A6E` |

빌드 provenance는 `dist/RemoteControl.build.json`에 보존했다. 생산 소스 fingerprint는 `1D05AC20901F099656CB5797ABA5B580A5AF3B459477FD6881DC16784E6A43B5`이다. SSHTunnel SDK는 기존 `../../SSHTunnel` 소스를 사용하는 경로를 유지하며 해당 소스 해시도 manifest에 기록한다.

운영 적용 상태: 실행 중인 서비스는 기존 버전이다. 새 설치 파일을 실행하거나 서비스를 재시작하지 않았다. 이 보고서와 함께 게시되는 소스 버전은 `v1.9.1`이며 GitHub 릴리스의 설치 파일은 위 SHA-256으로 확인할 수 있다. 소스·설치 파일 공개와 실제 PC의 설치·운영 서비스 업데이트는 별개다.

## 공급자 근거

- [body-parser GHSA-27r3-jxmh-p828](https://github.com/expressjs/body-parser/security/advisories/GHSA-27r3-jxmh-p828)
- [Go 다운로드 및 패치 버전](https://go.dev/dl/)
- [GO-2026-6809](https://pkg.go.dev/vuln/GO-2026-6809)
- [GO-2026-5942](https://pkg.go.dev/vuln/GO-2026-5942)
- [GO-2026-6866](https://pkg.go.dev/vuln/GO-2026-6866), [GO-2026-6860](https://pkg.go.dev/vuln/GO-2026-6860)
- [미사용 OpenPGP 공지 GO-2026-5932](https://pkg.go.dev/vuln/GO-2026-5932)
