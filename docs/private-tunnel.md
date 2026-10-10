# 외부망 연결 설정 — private-tunnel / Tailscale

메인 창의 **외부망 → 연결 설정**에서 `private-tunnel (SSH)`, `Tailscale Funnel`, `사용 안 함 (LAN만)`을 선택합니다. 새 설치의 기본값은 private-tunnel입니다. 기존 Tailscale 키가 있는 설치는 이전 방식을 유지합니다. 방식 변경으로 Tailscale 키·장비 신원을 삭제하지 않습니다.

허브 주소와 포트가 아직 정해지지 않았다면 **자동 연결을 끄고 빈 설정을 저장**하세요. 값이 모두 준비된 뒤 자동 연결을 켜거나 메인 창에서 **외부망 열기**를 누릅니다. 설정 창의 자동 연결은 현재 연결 유지와 다음 실행 때 복원을 함께 지정합니다. 외부망 닫기는 재연결도 중지합니다.

## 전달 구조

```text
브라우저 ── HTTPS/WSS ── 허브의 HTTPS 프록시
                               │
                 [선택: Private Tunnel 허브 relay]
                               │
                    허브 127.0.0.1:<수신 포트>
                               │ SSH reverse
원격제어 PC ── Windows ssh.exe ─┘
     └─ 127.0.0.1:<원격제어 접속 포트>
```

`windows-private-tunnel/docs/프로그램_내장_터널링_원격제어_개발가이드.md`의 OpenSSH backend 방향을 적용했습니다. 원격제어 프로그램이 Windows OpenSSH 클라이언트를 자식 프로세스로 실행합니다. 원격제어 PC에 별도 Private Tunnel 앱을 실행해 같은 reverse 포트를 만들 필요는 없습니다. Private Tunnel의 관리 웹 API에 로그인하거나 프로필을 자동 등록하는 방식은 아닙니다.

SSH 터널은 TCP 전달 경로입니다. Tailscale Funnel처럼 공개 DNS·인증서·HTTPS 주소를 자동 발급하지 않습니다. 브라우저만 쓰는 접속을 유지하려면 **허브의 HTTPS 프록시, 도메인/인증서, 외부 접근 경로**를 별도로 준비해야 합니다. 기존 화면·입력·파일·클립보드·하위 PC 기능은 이 경로를 그대로 통과합니다.

## 설정 항목

| 화면 항목 | 의미 | settings.txt 키 |
|---|---|---|
| 연결 방식 | `private` / `tailscale` / `off` | `tunnel` |
| 자동 연결 | `1` 연결 유지 / `0` 중지 | `extauto` |
| SSH 허브 주소 | 도메인 또는 IP만 입력. URL·포트 제외 | `sshhost` |
| SSH 접속 포트 | 외부에서 허브 SSH에 접근하는 포트 | `sshport` |
| SSH 계정 | 허브의 터널 제공용 계정 | `sshuser` |
| 개인키 파일 | 이 PC의 로컬 절대 경로 | `sshkeyfile` |
| known_hosts 파일 | 검증한 **허브 서버 공개키** 파일의 절대 경로 | `sshknownhosts` |
| 허브 수신 포트 | reverse 터널이 허브 127.0.0.1에서 받을 포트 | `sshremoteport` |
| 브라우저 접속 주소 | `https://도메인[:포트]` | `sshurl` |

원격제어 프로그램의 기존 **접속 포트**가 SSH 전달 대상입니다. 바꾸면 터널도 새 포트로 연결합니다. SSH 접속 포트, 허브 수신 포트, 브라우저 HTTPS 포트는 서로 다른 용도입니다. 어느 포트도 여기서 임의로 정하지 않았습니다. 숫자 범위는 1~65535입니다. IPv6 허브 주소도 허용합니다.

브라우저 주소는 이 원격제어 PC 전용 origin이어야 합니다. `/remote` 같은 하위 경로, 인증 정보, 쿼리는 지원하지 않습니다. 로컬 검증용 `http://127.0.0.1:포트` / `http://localhost:포트`는 허용합니다. 공인 주소는 HTTPS를 사용합니다.

## SSH 준비

원격제어 PC에는 Windows 선택적 기능 **OpenSSH 클라이언트**가 있어야 합니다. `%SystemRoot%\System32\OpenSSH\ssh.exe`만 사용하며 PATH 검색·사용자 SSH 설정 파일·임의 실행 명령은 사용하지 않습니다. 설치기가 Windows 기능을 임의로 설치하지는 않습니다.

1. 허브에 OpenSSH 서버와 터널 제공 전용 계정을 준비합니다.
2. 이 PC용 SSH 공개키를 허브 계정의 `authorized_keys`에 등록합니다. 개인키는 이 PC에 보관합니다. 개인키 내용은 원격제어 설정이나 설치 파일에 담지 않습니다.
3. 허브 관리자가 확인한 서버 공개키로 `known_hosts` 파일을 만듭니다. 기본 SSH 포트 22는 `허브주소 키종류 공개키`, 다른 포트는 `[허브주소]:SSH접속포트 키종류 공개키` 형식입니다.
4. 허브 관리자가 `ssh-keygen -lf <서버공개키파일> -E sha256`으로 확인한 지문과 대조합니다. `ssh-keyscan` 결과만으로 서버를 신뢰하지 않습니다. Private Tunnel 화면의 `SHA256:...` 지문 문자열만으로는 OpenSSH known_hosts 파일을 만들 수 없으므로 실제 서버 공개키도 받아야 합니다.
5. 허브에서 `GatewayPorts no`와 해당 계정의 remote forwarding 허용, 지정한 포트의 `PermitListen`을 확인합니다. 같은 포트를 다른 Private Tunnel remote 프로필과 동시에 사용하지 않습니다.

예시 정책은 아래와 같습니다. `<...>`를 확정한 값으로 바꾸고 기존 설정에 맞게 병합합니다. 이 프로그램은 허브 설정을 변경하지 않습니다.

```text
Match User <터널제공계정>
    AuthenticationMethods publickey
    PasswordAuthentication no
    AllowTcpForwarding remote
    GatewayPorts no
    PermitListen 127.0.0.1:<허브수신포트>
    PermitOpen none
    MaxSessions 0
Match all
```

프로그램은 reverse 수신을 127.0.0.1로 요청합니다. 서버가 `GatewayPorts yes`로 이를 덮어쓰면 전체 인터페이스에 노출될 수 있으므로 허브 정책 확인이 필요합니다.

로그인 프롬프트 없이 동작하는 전용 키 인증을 사용합니다. 키 암호를 대화상자로 묻거나 ssh-agent의 키를 가져오지 않습니다. 무인 실행용 키는 파일 ACL로 보호해야 합니다. **서비스 모드는 SYSTEM**, 창 단독 모드는 현재 사용자가 ssh.exe를 실행하므로 해당 실행 계정에서 읽을 수 있고 OpenSSH의 개인키 소유권/ACL 검사도 통과하는 키를 지정하세요. 사용자 프로필 키를 그대로 지정하면 서비스에서 실패할 수 있습니다. 프로그램이 키 권한을 자동 완화하지 않습니다.

## 허브의 브라우저 중계

HTTPS 프록시 upstream을 허브 `127.0.0.1:<허브수신포트>`로 연결합니다. Private Tunnel 허브의 Open/Close로 차단하려면 별도 relay를 만들고 **HTTPS 프록시 → relay → reverse 포트** 순서로 연결하세요. reverse 포트로 직접 연결하면 허브 앱의 relay Close를 거치지 않습니다.

프록시에는 WebSocket Upgrade, 장시간 연결, 스트리밍 업로드·다운로드가 필요합니다. 인증 쿠키·Content-Type·Range를 전달하고 응답을 파일 전체 크기만큼 버퍼링하지 않도록 구성합니다. 브라우저에는 외부 HTTPS 주소만 알려 주며 SSH 포트·reverse 포트를 브라우저 주소로 쓰지 않습니다. 별도의 `/ping` 응답을 프록시에서 만들지 말고 원격제어 서버까지 전달합니다.

## 상태와 재연결

- `연결 중`: SSH 서버의 reverse forwarding 승인까지 기다립니다. 창에서 연결 취소가 가능합니다.
- `터널 연결됨 · 브라우저 주소 확인 중`: SSH 수신 포트는 준비됐고 외부 URL 검증은 대기 중입니다.
- `브라우저 접속 확인됨`: 외부 URL의 `/ping`이 HTTP 200과 정확한 `ok`를 반환했습니다. 이때만 주소 알림을 처리합니다.
- 인증 거부·서버 키 불일치·허브 포트 충돌·개인키 문제: 재시도를 멈춥니다. 원인을 고친 뒤 **연결 설정 → 저장** 또는 닫기 후 열기로 재시도합니다.
- 일시적인 연결 실패·SSH 프로세스 종료: 기존 지킴이가 제한된 간격으로 재연결합니다. private-tunnel 모드에서는 Tailscale 관제 서버에 의존하지 않습니다.

실행 중 주소·포트·방식을 저장하면 현재 외부 TCP 연결은 종료됩니다. 기존 LAN 서버는 유지됩니다. 중지 상태에서 편집만 하면 자동으로 시작하지 않습니다. 서비스 모드에서는 약 2초 안에 변경을 읽고, 연결 지킴이는 5초 주기로 동작합니다. 창 모드는 프로그램을 다음에 실행할 때, 설치한 서비스 모드는 서비스가 시작될 때 저장한 연결 의도를 복원합니다.

## 빌드와 검증

실행 중인 기존 프로그램을 중지하지 않고 `dist`에 빌드합니다. 경로와 파일명은 고정이며, setup 빌드는 본체도 함께 갱신합니다. 자세한 사용법은 [프로젝트 안내](../README.md)를 참고하세요.

```powershell
.\빌드_setup.ps1  # 본체와 설치 파일을 모두 dist에 생성
powershell -NoProfile -ExecutionPolicy Bypass -File bench\Test-SshTunnel.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File bench\Test-Config.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File bench\Test-TunnelForm.ps1
```

2026-09-23 로컬 시험: 실제 Windows OpenSSH + 일회용 Go SSH 허브에서 HTTP 왕복, 16MiB 바이트 일치, WebSocket 핸드셰이크/양방향 프레임, EOF 뒤 응답, 포트 변경·충돌, 호스트 키 불일치, 인증 실패, 자동 재연결·수동 중지·연결 중 취소를 검증했습니다. Tailscale 선택 분기는 시험 대역으로 검증하며 실제 계정·장비 신원은 건드리지 않습니다. 설정 저장은 두 프로세스 동시 저장과 저장 실패 후 복구를 별도로 검사합니다.

로컬 측정은 SSH 준비 약 150ms, 16MiB 업로드와 반환 약 320ms였습니다. **실제 외부 회선 지연이나 Tailscale 대비 성능 수치는 아닙니다.** 허브 주소·포트가 정해진 뒤 HTTPS 프록시, 실제 계정/SYSTEM 키 권한, 외부 회선과 재부팅 복원을 확인해야 합니다.

참조: [OpenSSH 옵션](https://man.openbsd.org/ssh_config), [Windows SSH 키 관리](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_keymanagement), `D:\Src\windows-private-tunnel\docs\quickstart.md`.
