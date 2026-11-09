# v1.9.10 화면 전송 지연 개선 (LAN 1440p)

2026-11-09, Intel Core Ultra 7 155H / Intel Arc 내장 GPU + NVIDIA RTX 1000 Ada Laptop GPU, 2560×1440 모니터에서 측정·수정했다.

## 증상

같은 LAN에서 1440p PC를 제어할 때 화면이 뚝뚝 끊기고, 조작 뒤 반영이 늦고, 화질이 흐리고, 첫 화면·전환이 느리다는 보고. 뷰어의 `캡처` 칸이 영상(H.264)인지 DD(JPEG)인지는 확인되지 않았다.

## 측정 방법

추측으로 고르지 않도록 끝에서 끝까지 재는 장치를 먼저 만들었다. 두 장치 모두 화면의 정해진 위치에 **시간값 바코드**를 찍고, 브라우저가 실제로 그린 프레임에서 그 값을 읽어 "화면이 바뀐 순간 → 브라우저에 나온 순간"을 잰다(같은 PC의 시계). 마우스·키 입력이 서버에 도착하기까지의 시간은 포함하지 않는다.

| 장치 | 경로 | 화면 |
|---|---|---|
| `bench/Test-LiveView.ps1` | 실제 `Server.DoWebSocket` → `StreamLoop` → `ScreenSource` → 실제 `viewer.js` (headless Chrome) | 합성 60Hz 화면 (DesktopDup 대역). 실제 화면을 읽지 않고 입력도 넣지 않는다 |
| `video-helper` `TestVideoLatencyBench` | 제품 FFmpeg 인자·재시작 로직 → Pion → 실제 `media.js` `RemoteVideo` | 합성 NV12 화면을 FFmpeg 표준입력으로 넣는다 |

장면: **스크롤**(초당 360px, 화면 전체가 매 장 바뀜, 왼쪽 위·오른쪽 아래 두 곳 측정), **영상 창**(가운데 1280×720만 매 장 바뀜), **타이핑**(0.12초마다 한 글자).
회선: loopback(유선 LAN 상한), 측정 장치 안의 중계로 300Mbps/RTT 2ms, 100Mbps/RTT 4ms(무선 LAN 흉내). 중계는 고해상도 대기 타이머를 쓴다 — Node `setTimeout`은 Windows 기본 타이머에 묶여 RTT 4ms가 30ms 이상이 됐다.

## 찾은 원인

1. **대리 프로세스의 타이머 해상도가 15.6ms였다.** `timeBeginPeriod`를 부르지 않아 `Monitor.Wait(5ms)`가 실제로 p50 15.5ms 잤다. Windows 10 2004부터 해상도는 프로세스별이라 다른 프로그램이 올려도 소용없다. "캡처 → 남은 시간 대기 → 다음 vsync 대기"가 겹쳐 한 바퀴가 ~33ms가 됐다. 1440p 화면 전체가 바뀌어도 캡처·지문·압축은 장당 약 10ms(타일 512)인데 스크롤이 25fps에 묶였다.
2. **GPU 영상은 1440p를 1080p와 같은 2→6Mbps로 보냈다.** 낮게 시작해 8초마다 1.35배 올리는데, 올릴 때마다 FFmpeg를 다시 띄워 최대 700ms씩 화면이 멎었다(첫 30초에 세 번). 그동안 2~5Mbps라 오른쪽 아래 바코드가 초당 30번 중 16번만 읽혔다(뭉개짐).
3. **같은 LAN에서 GPU 영상이 붙지 못할 수 있었다.** 방화벽 프로그램 규칙은 본체 exe만 허용하고, UDP로 직접 받는 `video-helper`는 허용하지 않았다(측정 PC에 helper 규칙이 하나도 없었다). Chrome은 LAN 주소를 mDNS 이름으로 가려 helper가 먼저 보낼 수도 없으므로 ICE가 실패하고 JPEG로 돌아간다.
4. (이번에 바꾸지 않음) **NVENC가 드라이버 610 미만에서 실패한다.** 동봉 FFmpeg n9.0.2는 NVENC API 13.1을 요구한다. 측정 PC(드라이버 597.13, API 13.0)는 NVENC가 실패해 Intel QSV로 넘어갔다. 내장 GPU가 없는 NVIDIA PC는 JPEG로 돌아간다. NVIDIA 드라이버를 610 이상으로 올리면 해결된다.

## 변경

- `src/TimerResolution.cs`: 화면 연결이 하나라도 있는 동안만 이 프로세스의 타이머를 1ms로 올린다. Windows 11의 "보이지 않는 창 프로세스 요청 무시" 절전도 이 프로세스에서만 끈다. 마지막 연결이 끝나면 되돌린다.
- `video-helper`: ICE가 고른 후보 쌍이 host/host(서로의 인터페이스 주소로 직접)이거나 사설·루프백 주소이면 **처음부터 상한으로** 보낸다. 정한 값을 `bitrate` 신호로 브라우저에 알려 브라우저의 전송률 조절도 같은 값에서 시작한다. 인터넷 너머는 기존처럼 낮게 시작한다.
- 해상도별 전송률 표를 `RtcSession.Bitrates`와 `media.js`에 같은 값으로 둔다.

  | 해상도 | 기존 시작 → 상한 | 변경 시작(인터넷) / 상한(LAN 시작값) |
  |---|---|---|
  | 1080p 이하 | 2 → 6Mbps | 2 / 8Mbps |
  | 1440p | 2 → 6Mbps | 3 / 12Mbps |
  | 4K | 4 → 12Mbps | 4 / 16Mbps |

- `media.js`: 전송률을 바꾼 뒤 5초 동안은 손실·지터 지연을 보고 다시 깎지 않는다. 재시작으로 멎은 흔적을 회선 문제로 읽고 또 깎으면 또 재시작하는 연쇄로 500kbps까지 떨어지는 것을 측정 중에 봤다.
- 방화벽: 첫 영상 연결 때 해시 이름의 helper 파일 하나에만 사설·도메인 망 UDP 수신을 허용하는 `원격제어 영상` 규칙을 만든다. 판이 바뀌면 경로를 바꾸고, 설치·제거 때 옛 규칙을 지운다. 공용망은 열지 않는다.

## 결과

### JPEG 경로 (1440p, 두 측정 위치 중 늦은 쪽)

| 회선 | 장면 | 수정 전 | 수정 후 |
|---|---|---|---|
| loopback | 스크롤 | 25.4fps, p50 39 / p95 63ms | **34.2fps, p50 30 / p95 35ms** |
| loopback | 영상 창 | 47.9fps, 21 / 39ms | **60fps, 14 / 20ms** |
| loopback | 타이핑 | 11 / 16ms | 11 / 17ms |
| 300Mbps, RTT 2ms | 스크롤 | 20.2fps, 51 / 61ms | 20fps, 51 / 63ms |
| 300Mbps, RTT 2ms | 영상 창 | 46.5fps, 27 / 41ms | 53.2fps, 30 / 46ms |
| 100Mbps, RTT 4ms | 스크롤 | 12.7fps, 80 / 91ms | 12.3fps, 80 / 104ms |
| 100Mbps, RTT 4ms | 영상 창 | 42.7fps, 41 / 47ms | 41.1fps, 44 / 48ms |
| 100Mbps, RTT 4ms | 타이핑 | 25 / 28ms | 23 / 27ms |

타이머 수정은 회선 여유가 있을 때 효과가 크다. 회선이 좁으면 전송량이 먼저 막아 차이가 없다(실행 간 편차 범위). JPEG로 1440p 전체 움직임을 보내면 장당 약 680KB(화질 70)이고 브라우저 디코딩이 장당 약 30ms라 loopback에서도 ~35fps·190Mbps가 한계다. **LAN에서 부드러운 움직임은 GPU 영상 경로가 맡아야 한다.**

### GPU 영상 경로 (1440p 30fps 스크롤, loopback, h264_qsv)

| 항목 | 수정 전 (2Mbps 시작, 상한 6Mbps) | 수정 후 (LAN, 12Mbps 시작) |
|---|---|---|
| 오른쪽 아래 바코드 판독 | 16.2fps / 30fps | **29.8fps / 30fps** |
| 지연 p50 / p95 (늦은 쪽) | 73 / 218ms | **70 / 88ms** |
| 150ms 넘는 정지 | 3번, 최대 700ms | **0번 (최대 간격 81ms)** |
| 인코더 재시작 (첫 40초) | 3번 | 0번 |
| 평균 전송량 | 4.6Mbps | 11.4Mbps |

같은 설정에서 타이핑은 p50 58 / p95 76ms다. 작은 변화의 지연은 JPEG(11~23ms)가 더 짧다 — 인코더·지터 버퍼·디코더를 거치는 영상 경로의 구조적 비용이며 이번에 바꾸지 않았다.

## 재고 넣지 않은 것

| 시도 | 결과 | 판단 |
|---|---|---|
| 두 묶음이 나가 있는 동안 다음 화면을 미리 압축 | 100Mbps 스크롤 12→16fps, 하지만 지연 p50 80→109ms | 지연이 합격 기준이라 넣지 않음 |
| 1440p 상한 16·24Mbps | 16Mbps는 10Mbps와 지연이 같았고(p50 49~59ms), 24Mbps는 지터 443ms로 무너짐 | 여유를 두고 12Mbps |
| 1440p 60fps 영상 | 16Mbps에서 실제 37fps, 지연 이득 없음 | 30fps 유지 |
| helper RTP 수신 버퍼 256KB → 4MB | 12Mbps에서 FFmpeg→helper 구간 손실이 둘 다 0 | 256KB 유지 |
| QSV를 GDI 대신 DXGI 캡처로 | 1440p 30fps에서 GDI 캡처도 27~28fps | 바꾸지 않음 |

## 한계

- loopback과 headless Chrome(소프트웨어 디코딩)에서 쟀다. 실제 다른 PC의 브라우저, 하드웨어 디코딩, 무선 손실·흔들림은 포함하지 않는다.
- **방화벽 규칙으로 다른 PC에서 GPU 영상이 붙는지는 이 PC 하나로 검증하지 못했다.** 같은 PC 안의 연결은 방화벽을 거치지 않는다. 업데이트 뒤 다른 LAN PC에서 접속해 `캡처` 칸이 `h264_… / DXGI` 또는 `/ GDI`로 바뀌는지 확인해야 한다.
- 4K 영상은 측정 장치의 소프트웨어 디코더 한계로 15.6fps에 머물러 상한 비교만 했다(12·16Mbps 차이 없음).

## 재현

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File bench\Test-LiveView.ps1                      # loopback, 세 장면
powershell -NoProfile -ExecutionPolicy Bypass -File bench\Test-LiveView.ps1 -Mbps 100 -Rtt 4 -NoBuild
# -Width 는 이 PC 모니터 중 하나의 가로 크기여야 한다(기본 2560). 결과: artifacts\live-view\result-*.json

$env:RC_VIDEO_BENCH='1'
$env:RC_TEST_BROWSER='C:\Program Files\Google\Chrome\Application\chrome.exe'
$env:RC_VIDEO_TEST_FFMPEG='<build/Build-Video.ps1 로 해시 검증한 ffmpeg.exe>'
$env:RC_VIDEO_BENCH_INITIAL='3000000'; $env:RC_VIDEO_BENCH_MAX='12000000'   # RtcSession.Bitrates 1440p
Push-Location video-helper; go test -mod=readonly -count=1 -run TestVideoLatencyBench -v .; Pop-Location
```
