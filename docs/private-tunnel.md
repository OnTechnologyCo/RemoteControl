# 외부망 연결 설정 — SSHTunnel / Tailscale (v1.8.17)

프라이빗 터널은 `D:\src\SSHTunnel` 1.5.0 이상의 **웹 터널 API**를 사용합니다. 원격제어의 로컬 HTTP 서버를 먼저 실행하고 SSH 연결 안에서 `protocol: http`로 개설을 요청합니다. 서버가 반환한 `https://<6자리 식별자>.<웹 기본 도메인>`을 메인 창·트레이·주소 알림에 표시합니다. 브라우저 주소에 별도 포트를 입력하지 않습니다.

v1.8.17의 PC·계정 구분 알림과 주소 유지 조건은 [터널 주소 유지와 알림](tunnel-address-notifications.md)을 참고하세요.

## 서버 준비

SSHTunnel 관리 화면의 **서버 설정**에서 자동 터널과 웹 터널을 활성화합니다. 웹 기본 도메인, 루프백 웹 게이트웨이(기본 `127.0.0.1:17894`), 공개 HTTPS 방식을 설정하고 서버를 재시작합니다. **접속 계정 → 자동 터널용 인증키 발급**에서 발급한 `user`, `accessKey`, `fingerprint`를 사용합니다. SSH 접속 주소·포트(기본 2222)는 서버 프로그램이 중계 서버에 접속하는 주소입니다.

운영 서버에는 `*.<웹 기본 도메인>` DNS, 해당 와일드카드 인증서와 Host를 보존하는 HTTPS 프록시가 필요합니다. 예를 들어 웹 기본 도메인이 `tunnel.ontechdev.com`이면 브라우저 URL은 `https://<랜덤키>.tunnel.ontechdev.com`입니다. 이것은 설정 예시이며 프로그램이 DNS·인증서·기존 80/443 서비스를 자동 변경하지 않습니다. 상세 서버 설정은 `D:\src\SSHTunnel\docs\WEB-TUNNELS.md`를 참조하세요.

## 원격제어 설정

메인 창 **외부망 → 연결 설정 → 프라이빗 터널 (SSHTunnel)**에서 아래 값을 입력합니다.

| 화면 항목 | 입력값 | 설정 키 |
|---|---|---|
| 서버 주소 | SSHTunnel SSH 도메인 또는 IP. URL·포트 제외 | `sshhost` |
| SSH 접속 포트 | 서버 SSH 포트. 신규 설정 기본 2222 | `sshport` |
| 터널 계정 | 발급받은 `user` | `sshuser` |
| 인증키 (AccessKey) | 발급받은 64자리 소문자 hex | `sshaccesskey` / `sshaccesskeym` (DPAPI) |
| 서버 지문 | 발급받은 `SHA256:...` 호스트 지문 | `sshfingerprint` |
| 터널 이름 | 관리 화면에서 구별할 이름. 기본 PC 이름 | `sshname` |

**외부망 연결 유지 · 다음 실행 때 자동 연결**을 켜고 저장하거나 메인 창의 **외부망 열기**를 누릅니다. 값이 아직 없으면 자동 연결을 끄고 초안을 저장할 수 있습니다. Tailscale과 LAN만 사용도 계속 선택할 수 있습니다.

이전 private-tunnel의 개인키 파일, known_hosts, 고정 reverse 수신 포트, 수동 브라우저 URL은 더 이상 입력하지 않습니다. 기존 설정 파일의 해당 값은 사용하지 않으며 새 인증키·지문 입력이 필요합니다. 저장된 Tailscale 선택과 키는 유지합니다.

## 전달 및 인증

브라우저 HTTPS/WSS 요청은 SSHTunnel 웹 게이트웨이에서 해당 터널의 SSH 채널을 거쳐 원격제어 PC의 `127.0.0.1:<접속 포트>`로 전달됩니다. 페이지·로그인 쿠키·화면 WebSocket·파일 업로드/다운로드·클립보드·하위 PC 요청이 같은 웹 origin을 사용합니다. 원격제어 접속 포트 변경 시 터널도 새 로컬 포트로 다시 엽니다.

원격제어 EXE에는 SSHTunnel Go SDK를 사용하는 연결 helper가 포함됩니다. 사용자 PC에 Go, Windows OpenSSH, 별도 SSH 키 파일을 설치하지 않습니다. 인증키는 사용자/SYSTEM 실행을 위해 DPAPI 사용자·컴퓨터 사본으로 저장하며 helper에는 stdin으로 전달합니다. 명령줄·환경변수·임시 연결 설정 파일에 인증키를 넣지 않습니다. 컴퓨터 범위 DPAPI 사본은 같은 PC의 읽기 권한이 있는 계정에서 복원될 수 있으므로 Windows 계정 접근을 신뢰하는 기존 설정 보안 모델을 따릅니다. helper는 해시를 확인하고 SYSTEM/관리자만 쓰는 실행 폴더에 배치합니다.

서버 지문이 다르거나 인증을 거부하면 재시도를 멈춥니다. 수정한 값을 다시 저장하면 오류를 해제합니다. 네트워크 장애 또는 터널 종료는 기존 연결 지킴이가 제한된 간격으로 재접속합니다. SSHTunnel 1.5.0 이상에서는 고정 서비스 식별자와 저장된 식별키를 사용하므로 재접속·프로세스 재시작·로컬 포트 및 표시 이름 변경 뒤에도 같은 URL을 사용합니다. 식별키는 실행 계정의 AppData\Roaming\OnTech\SSHTunnel\identities에 보관되므로 삭제하지 마세요. 구형 클라이언트에서 최초 전환할 때는 저장된 식별키가 없어 주소가 한 번 변경됩니다. **외부망 닫기**는 자동 재접속도 중지합니다.

주소 알림은 외부 URL의 `/ping`이 실제 원격제어에서 `ok`를 반환한 후 처리합니다. 주소 발급 성공과 공개 DNS·HTTPS 연결 성공은 서로 다른 상태입니다. 랜덤 주소는 로그인 자격 증명이 아니며 기존 Remote ID/PW 로그인이 그대로 필요합니다.

SSHTunnel 웹 게이트웨이의 업로드 제한은 기본 128MiB, 서버 설정으로 최대 2048MiB입니다. 앞단 프록시에 더 작은 제한이 있으면 그 제한을 적용받습니다. 다운로드 응답은 스트리밍됩니다.

## 빌드와 검증

SDK 소스 `SSHTunnel` 폴더를 원격제어 프로젝트와 같은 상위 폴더에 둡니다. Go 1.26 이상이 필요합니다. `sshtunnel-client/go.mod`는 `../../SSHTunnel`을 참조합니다.

```powershell
.\빌드.ps1                       # SSHTunnel helper도 새로 빌드·임베드
.\빌드_setup.ps1                 # 본체·MCP 패키지·setup (설치하지 않음)
powershell -NoProfile -ExecutionPolicy Bypass -File bench\Test-SshTunnel.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File bench\Test-TunnelForm.ps1
```

`bench/sshtunnel-fixture`는 실제 SSHTunnel 서버 코드를 임시 데이터 폴더, 일회용 계정·호스트 키, 루프백 포트로 실행합니다. 제품 C# 연결 코드와 내장 Go helper로 주소 발급, HTTP, 16MiB 바이트 왕복, WebSocket, 재시작·로컬 포트 변경 후 주소 유지, 지문·계정 거부, 자동 재접속, 수동 중지와 연결 중 취소를 검사합니다. 운영 SSHTunnel 계정·DNS·HTTPS 프록시를 변경하지 않습니다. 로컬 시험은 공개 회선의 속도나 실제 도메인 개통을 증명하지 않습니다.

## v1.8.11 검증 결과 (2026-10-11, HomePC_AMD)

- 실제 SSHTunnel 서버 코드와 제품 연결 코드의 통합 검사 **28개 통과**: 랜덤 HTTPS 주소 발급, HTTP, 16MiB 전송 일치, WebSocket 양방향 통신, 종료한 주소 폐기, 재접속 시 새 주소, 인증·지문 오류 차단, 자동 재접속과 수동 중지, 연결 중 취소.
- 설정 화면 검사 **4개 통과**: 미완성 설정 저장, 인증키 마스킹·DPAPI 저장, 두 방식의 인증키 보존, 기존 Tailscale 자동 선택. 실제 화면도 이미지로 확인했습니다.
- Go helper와 실제 서버 시험용 fixture의 `go vet ./...` 통과.
- 본체와 Setup **1.8.11.0 빌드 완료**. 내장 SSHTunnel helper의 SHA-256이 배포 helper와 일치하며 Setup의 내장 본체·MCP 패키지들도 배포 파일과 일치합니다.
- 기존 Setup 웹 패키지 검사와 선택 설치 검사 통과. 실행 중인 서비스는 설치·재시작하지 않았습니다. 운영 DNS·인증서·공개 HTTPS 접속은 이 로컬 통합 검사에 포함되지 않습니다.

설치 파일: `D:\src\원격제어\dist\RemoteControl_Setup.exe` (86.39MiB). 상세 검증 기록: `D:\src\원격제어\artifacts\sshtunnel-validation-1.8.11.json`. 변경 전 소스·배포 파일 백업: `D:\src\원격제어\artifacts\sshtunnel-backup-20261011-010424`.
