# v1.9.0 화면 전송 개선

2026-10-23, HomePC AMD Ryzen 5 5625U / Radeon Graphics에서 구현·검증했다.

## 변경 사항

- DXGI의 dirty rect와 move rect 목적지를 함께 반영한다. 이동 목적지의 **새 GPU 픽셀**을 복사하므로 이전 CPU 이미지의 이동 순서에 의존하지 않는다. 길이/좌표가 잘못된 메타데이터는 전체 갱신으로 처리한다.
- JPEG 타일은 전송 예산에 따라 128 / 256 / 512 / 1024픽셀로 조절한다. 최소 3초 간격으로 변경하여 전체 재압축이 반복되지 않게 한다. 구형 브라우저의 128픽셀 협상은 유지한다.
- 5초마다 작은 RTT probe를 보내고 최근 1분의 최솟값을 기준으로 추가 대기를 계산한다. 자동 JPEG 화질 판단도 기본 왕복 지연을 제외한다. ACK 대기 중에도 커서 갱신은 계속한다.
- 브라우저 JPEG 디코딩은 Worker에서 최대 4개만 진행하며, 대기 중 새 버전이 도착한 타일은 디코딩 전에 폐기한다. 오래된 PC/화면의 완료 콜백과 ACK는 계속 차단한다.
- 로그인한 WebSocket에서만 SDP/ICE를 협상한다. Pion helper는 부모의 전용 파이프만 읽고 제어용 HTTP 포트를 열지 않는다. 화면은 DTLS/SRTP로 전송한다. 키보드·마우스는 기존 인증 연결을 사용한다.
- FFmpeg의 AMD AMF / NVIDIA NVENC / Intel QSV 하드웨어 H.264를 사용한다. 정확한 DXGI 출력이 확인되면 GPU 캡처를 먼저 시도하고, 실패하면 GDI 캡처와 GPU 인코딩을 시도한다. 지원하지 않는 GPU는 JPEG로 돌아간다.
- B 프레임·lookahead를 줄이고 1초 키프레임과 인밴드 SPS/PPS를 보낸다. 비트레이트 재시작 전 잔여 패킷을 버리고 RTP 번호·시각은 이어 간다. 수신 손실/지터가 늘면 비트레이트를 낮춘다.
- .NET Framework 파이프의 UTF-8 BOM을 처리하며, 실행 파일은 쓰기 가능한 설정 폴더와 분리된 보호 폴더에 해시를 확인하여 추출한다.
- 브라우저의 첫 프레임 표시 확인 전에는 JPEG를 계속 보낸다. 영상 정지, ICE 실패, 화면 변경, 잠금 화면, 숨겨진 탭, 연결 해제 시 영상 helper와 그 자식을 정리한다. 복귀할 JPEG는 전체 화면을 다시 갱신한다.

## 동작 및 설정

- 기본은 WebRTC 자동 시도 + JPEG 자동 복귀이다. 수신 PC에는 설치가 필요 없다.
- 최대 동시 GPU 영상 세션은 2개이며 나머지는 JPEG를 사용한다.
- `video=off`는 기존 설정 파일에서 영상 경로를 끈다. `videostun=off`는 외부 STUN 없이 같은 망의 후보만 사용한다. 기본 STUN은 `stun:stun.l.google.com:19302`이다.
- TURN 서버는 이 변경에서 구축하지 않는다. UDP 직접 연결이 제한되는 회선은 기존 터널의 JPEG를 사용한다. SSH/Tailscale 터널 주소와 로그인·장비 식별키는 변경하지 않는다.
- 영상 시작 시 1080p는 2 Mbps, 4K는 4 Mbps로 시작하며 자동 최대치는 각각 6/12 Mbps, 목표는 30fps이다. 수동 화질은 영상 비트레이트에도 적용한다.
- 하드웨어 영상은 YUV 4:2:0이므로 작은 색 글자 품질은 JPEG와 다를 수 있다. 화면에 표시되는 프레임 수와 외부 회선에서의 체감 지연은 별개다.
- `/video-notices`에서 포함된 라이선스를 읽을 수 있다. LGPL FFmpeg 아카이브와 실행 파일의 SHA-256, Go 모듈 버전·go.sum을 고정했다.

## 확인한 결과

테스트는 실제 입력 주입 없이 합성 화면 또는 격리된 loopback에서 수행했다.

| 검사 | 결과 |
| --- | --- |
| 입력·타일·메타데이터 C# 검사 | 41개 통과 |
| 실제 WebSocket 서버·4K 타일 전환 | 16개 통과 |
| 뷰어 입력·오래된 프레임·ACK 검사 | 19개 통과 |
| 업로드 기존 기능 검사 | 10개 통과 |
| JPEG Worker 대기열 / 영상 상태 전환 | 통과 |
| 실제 EXE 내장 helper / UTF-8 파이프 / 세션 종료 | 통과 |
| AMD H.264 → SRTP → 수신 → 실제 H.264 디코딩 | 1080p 180프레임, 4K 179프레임 통과; 전송률 변경 포함 |
| Chrome 155 실제 4K 영상 표시 | 최초 표시 1.93초; 약 5초 구간에서 134프레임 집계 |
| 영상 공급 중단 후 브라우저 자동 복귀 | 3.66초 |
| 4K 전체 JPEG 준비, 512픽셀 타일 | 평균 36.0ms / p95 37.2ms, 40타일, 2191KiB |
| Chrome 4K JPEG Worker 디코딩·canvas | 평균 54.0ms / p95 77.0ms, 32회 ACK 및 불투명 화면 확인 |

최초 표시 시간에는 ICE·인코더·디코더 시작이 포함된다. 이 값은 클릭 반응 지연이나 지속적인 화면 지연 측정값이 아니다. 외부 NAT/TURN, 실제 인터넷 손실 회선, NVIDIA/Intel GPU 및 모바일 실기기는 추가 실사용 검증 대상이다.

## 재현

```powershell
.\bench\Test-Control.ps1
.\bench\Test-StreamServer.ps1
node .\bench\ViewerControlTest.js
node .\bench\MediaTest.js
$env:RC_TEST_BROWSER='C:\Program Files\Google\Chrome\Application\chrome.exe'
.\bench\Test-4K.ps1 -TileSize 512
# 먼저 build/Build-Video.ps1로 해시 검증한 FFmpeg만 사용한다.
$env:RC_VIDEO_TEST_FFMPEG='<검증한 FFmpeg 절대 경로>'
$env:RC_VIDEO_TEST_4K='1'
Push-Location .\video-helper
go test -mod=readonly -count=1 -v .
Pop-Location
.\bench\Test-VideoSession.ps1
.\빌드_setup.ps1 -NoKey
```

`TestBrowserVideoAndFallback`와 `TestHardwareEncryptedLoopback`은 환경 변수를 설정했을 때만 실행하며 사용자 화면 대신 생성 패턴을 사용한다. `Test-VideoSession.ps1`은 내장 helper의 파이프·프로세스 종료만 확인하며 ICE를 연결하지 않아 실제 화면 캡처를 시작하지 않는다.

참고한 구조: [RustDesk](https://github.com/rustdesk/rustdesk)의 영상 QoS와 하드웨어 코덱 경로, [Sunshine](https://github.com/LizardByte/Sunshine)의 지연을 줄이는 영상 전송, [TigerVNC](https://github.com/TigerVNC/tigervnc)의 변화 영역 처리. 해당 구현을 복사하지 않고 현재 브라우저 프로토콜에 맞춰 작성했다.
