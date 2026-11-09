package main

// GPU 영상 경로를 끝에서 끝까지 잰다: 합성 화면(시간값 바코드) → 제품 FFmpeg 인자/재시작 로직 →
// Pion → 실제 web/media.js 의 RemoteVideo → 브라우저가 내보낸 프레임에서 바코드를 읽어 지연을 계산한다.
// JPEG 쪽 bench/LiveViewBench 와 같은 바코드 배치라 두 경로를 같은 기준으로 비교할 수 있다.
//
// 실제 화면은 캡처하지 않는다(입력을 rawvideo 파이프로 바꾼다). 명시적으로 켤 때만 돈다:
//   RC_VIDEO_BENCH=1 RC_TEST_BROWSER=<chrome> RC_VIDEO_TEST_FFMPEG=<검증한 ffmpeg>
//   RC_VIDEO_BENCH_W/H/FPS/SECS/SCENE, RC_VIDEO_BENCH_INITIAL/MAX (C# RtcSession 과 같은 값)

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"math/rand"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/pion/webrtc/v4"
)

func benchInt(name string, def int) int {
	if v, err := strconv.Atoi(os.Getenv(name)); err == nil {
		return v
	}
	return def
}

const barBlock, barCols, barRows, barInset = 14, 8, 4, 8

type syntheticScreen struct {
	mu    sync.Mutex // 인코더 재시작 순간 이전/다음 공급자가 잠깐 겹칠 수 있다
	w, h  int
	scene string
	page  []byte // Y plane, w x 3h
	frame []byte // NV12
	start time.Time
	typed int
}

func newSyntheticScreen(w, h int, scene string) *syntheticScreen {
	s := &syntheticScreen{w: w, h: h, scene: scene, page: make([]byte, w*h*3), frame: make([]byte, w*h*3/2), start: time.Now()}
	rnd := rand.New(rand.NewSource(7))
	for i := range s.page {
		s.page[i] = 235
	}
	// 문서 같은 화면: 글자 줄(가는 획), 회색 사진 상자. 글자 위주라 실제 화면처럼 압축이 어렵다.
	for y := 20; y < h*3-40; {
		if bh := 160 + rnd.Intn(80); rnd.Intn(8) == 0 && y+bh < h*3 {
			bw := 300 + rnd.Intn(400)
			x0 := 40 + rnd.Intn(w-bw-80)
			base := 60 + rnd.Intn(120)
			for yy := 0; yy < bh; yy++ {
				for xx := 0; xx < bw; xx++ {
					s.page[(y+yy)*w+x0+xx] = byte(base + (xx*80/bw+yy*40/bh)%110)
				}
			}
			y += bh + 16
			continue
		}
		for x := 40; x < w-60; {
			word := 3 + rnd.Intn(7)
			for c := 0; c < word && x < w-60; c++ {
				for yy := 0; yy < 12; yy++ {
					for xx := 0; xx < 7; xx++ {
						if xx == 1 || yy == 2 || (xx+yy+c)%5 == 0 && rnd.Intn(3) == 0 {
							s.page[(y+yy)*w+x+xx] = 30
						}
					}
				}
				x += 8
			}
			x += 8
		}
		y += 24
	}
	for i := w * h; i < len(s.frame); i++ {
		s.frame[i] = 128
	}
	return s
}

func barMarkers(w, h int, scene string) [][2]int {
	if scene == "typing" {
		return [][2]int{{barInset, 256 + barInset}}
	}
	return [][2]int{{barInset, barInset}, {w/128*128 - 128 + barInset, h/128*128 - 128 + barInset}}
}

func unixMs32() uint32 { return uint32(time.Now().UnixMilli()) }

// render 는 지금 시각의 화면을 NV12 로 채우고 바코드에 시각을 찍은 뒤 out 에 쓴다.
func (s *syntheticScreen) writeTo(out io.Writer) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	_, err := out.Write(s.render())
	return err
}

func (s *syntheticScreen) render() []byte {
	w, h := s.w, s.h
	elapsed := time.Since(s.start).Milliseconds()
	if s.scene == "scroll" || s.typed == 0 {
		offset := 0
		if s.scene == "scroll" {
			offset = int(elapsed*360/1000) % (h * 2) // 초당 360px, FPS 와 무관한 같은 속도
		}
		copy(s.frame[:w*h], s.page[offset*w:(offset+h)*w])
	}
	if s.scene == "typing" {
		key := int(elapsed / 120)
		for ; s.typed <= key; s.typed++ {
			x, y := 160+(s.typed%160)*12, 272+(s.typed/160%20)*26
			for yy := 0; yy < 14; yy++ {
				for xx := 0; xx < 9; xx++ {
					v := byte(235)
					if xx == 2 || yy == 7 {
						v = 30
					}
					s.frame[(y+yy)*w+x+xx] = v
				}
			}
		}
	}
	stamp := unixMs32()
	for _, m := range barMarkers(w, h, s.scene) {
		for yy := 0; yy < barRows*barBlock; yy++ {
			for xx := 0; xx < barCols*barBlock; xx++ {
				bit := (yy/barBlock)*barCols + xx/barBlock
				v := byte(235)
				if stamp>>bit&1 != 0 {
					v = 16
				}
				s.frame[(m[1]+yy)*w+m[0]+xx] = v
			}
		}
	}
	return s.frame
}

func TestVideoLatencyBench(t *testing.T) {
	browser, encoder := os.Getenv("RC_TEST_BROWSER"), os.Getenv("RC_VIDEO_TEST_FFMPEG")
	if os.Getenv("RC_VIDEO_BENCH") != "1" || browser == "" || encoder == "" {
		t.Skip("set RC_VIDEO_BENCH=1, RC_TEST_BROWSER and RC_VIDEO_TEST_FFMPEG for the opt-in video latency bench")
	}
	width, height, fps := benchInt("RC_VIDEO_BENCH_W", 2560), benchInt("RC_VIDEO_BENCH_H", 1440), benchInt("RC_VIDEO_BENCH_FPS", 30)
	initial, maximum := benchInt("RC_VIDEO_BENCH_INITIAL", 2000000), benchInt("RC_VIDEO_BENCH_MAX", 6000000)
	secs, scene := benchInt("RC_VIDEO_BENCH_SECS", 40), os.Getenv("RC_VIDEO_BENCH_SCENE")
	if scene == "" {
		scene = "scroll"
	}
	media, err := os.ReadFile(filepath.Join("..", "web", "media.js"))
	if err != nil {
		t.Fatal(err)
	}
	reader, input := io.Pipe()
	defer input.Close()
	events := &browserEvents{}
	var starts atomic.Int32
	var startLog sync.Mutex
	var startTimes []string
	began := time.Now()
	// 화면은 한 번만 만든다. 재시작마다 만들면 그 시간이 재시작 정지로 잘못 잡힌다.
	screen := newSyntheticScreen(width, height, scene)
	done := make(chan error, 1)
	go func() {
		done <- runWithStream(reader, &writer{enc: json.NewEncoder(events)}, func(ctx context.Context, c command, track *webrtc.TrackLocalStaticRTP, w *writer, rate *atomic.Int64) error {
			factory := func(ctx context.Context, args []string) *exec.Cmd {
				start := 0
				for i, v := range args {
					if v == "-an" {
						start = i
						break
					}
				}
				for i, v := range args {
					if v == "-b:v" && i+1 < len(args) {
						startLog.Lock()
						startTimes = append(startTimes, fmt.Sprintf("%.1fs %s %s", time.Since(began).Seconds(), args[start+2], args[i+1]))
						startLog.Unlock()
					}
				}
				starts.Add(1)
				prefix := []string{"-hide_banner", "-loglevel", "error", "-nostdin", "-f", "rawvideo", "-pix_fmt", "nv12",
					"-s", fmt.Sprintf("%dx%d", width, height), "-framerate", strconv.Itoa(fps), "-i", "pipe:0"}
				cmd := exec.CommandContext(ctx, encoder, append(prefix, args[start:]...)...)
				stdin, perr := cmd.StdinPipe()
				if perr == nil {
					// 실제 캡처처럼 정해진 간격으로 한 장씩 넣는다. 인코더가 밀리면 쓰기가 막혀 그대로 드러난다.
					go func() {
						tick := time.NewTicker(time.Second / time.Duration(fps))
						defer tick.Stop()
						defer stdin.Close()
						for {
							select {
							case <-ctx.Done():
								return
							case <-tick.C:
								if screen.writeTo(stdin) != nil {
									return
								}
							}
						}
					}()
				}
				return cmd
			}
			return streamWithFactory(ctx, c, track, w, rate, factory)
		})
	}()
	result := make(chan map[string]any, 1)
	var commands sync.Mutex
	mux := http.NewServeMux()
	mux.HandleFunc("/media.js", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/javascript")
		_, _ = w.Write(media)
	})
	mux.HandleFunc("/events", func(w http.ResponseWriter, r *http.Request) {
		events.mu.Lock()
		defer events.mu.Unlock()
		_ = json.NewEncoder(w).Encode(events.queue)
		events.queue = nil
	})
	mux.HandleFunc("/signal", func(w http.ResponseWriter, r *http.Request) {
		var m struct{ Op, Data string }
		if json.NewDecoder(io.LimitReader(r.Body, 256*1024)).Decode(&m) != nil {
			http.Error(w, "bad signal", 400)
			return
		}
		commands.Lock()
		defer commands.Unlock()
		enc := json.NewEncoder(input)
		switch m.Op {
		case "offer":
			_ = enc.Encode(command{Type: "start", SDP: m.Data, FFmpeg: encoder, Width: width, Height: height, OutputWidth: width, OutputHeight: height,
				Adapter: -1, Output: -1, FPS: fps, Bitrate: maximum, InitialBitrate: initial})
		case "candidate":
			_ = enc.Encode(command{Type: "candidate", Candidate: m.Data})
		case "ready", "health":
			// 제품 Server.cs 와 같은 범위로 자른 뒤 helper 에 전달한다.
			if rate, e := strconv.Atoi(m.Data); e == nil {
				_ = enc.Encode(command{Type: "rate", Bitrate: max(500000, min(maximum, rate))})
			}
		}
	})
	mux.HandleFunc("/result", func(w http.ResponseWriter, r *http.Request) {
		var v map[string]any
		if json.NewDecoder(r.Body).Decode(&v) == nil {
			select {
			case result <- v:
			default:
			}
		}
	})
	page := `<!doctype html><html><body style="margin:0"><div id="world" style="position:relative"><img id="cursor"></div><script src="/media.js"></script><script>
var W=%d,H=%d,SCENE=%q,SECS=%d,BLOCK=14,COLS=8,ROWS=4,INSET=8,BW=COLS*BLOCK,BH=ROWS*BLOCK;
var markers=SCENE==='typing'?[[INSET,256+INSET]]:[[INSET,INSET],[Math.floor(W/128)*128-128+INSET,Math.floor(H/128)*128-128+INSET]];
var chain=Promise.resolve(),first=0,began=performance.now(),lat=markers.map(function(){return[];}),seen=[],frames=[],bytes=0,rates=[],mode='';
var scratch=document.createElement('canvas');scratch.width=BW;scratch.height=BH;var sx=scratch.getContext('2d',{willReadFrequently:true});
function decode(d){var v=0;for(var b=0;b<32;b++){var r=(b/COLS)|0,c=b%%COLS,i=(((r*BLOCK+7)*BW)+c*BLOCK+7)*4;if(d[i+1]<128)v=(v|(1<<b))>>>0;}return v>>>0;}
function stats(a){if(!a.length)return null;var s=a.slice().sort(function(x,y){return x-y;});var p=function(q){return s[Math.min(s.length-1,Math.floor(q*(s.length-1)))];};
 return{n:a.length,mean:+(a.reduce(function(x,y){return x+y;},0)/a.length).toFixed(1),p50:p(.5),p95:p(.95),max:s[s.length-1]};}
var peers=[],net=[],prev=null,Native=RTCPeerConnection;
RTCPeerConnection=function(c){var p=new Native(c);peers.push(p);return p;};RTCPeerConnection.prototype=Native.prototype;
// media.js 와 같은 계산(초당 손실률, 프레임당 지터 버퍼 지연)을 따로 기록해 전송률을 깎은 원인을 본다.
setInterval(function(){var p=peers[peers.length-1];if(!p)return;p.getStats().then(function(rs){rs.forEach(function(r){
 if(r.type!=='inbound-rtp'||r.kind!=='video')return;
 if(prev){var pk=Math.max(1,r.packetsReceived-prev.packetsReceived),em=(r.jitterBufferEmittedCount||0)-(prev.jitterBufferEmittedCount||0);
  net.push([Math.round((performance.now()-began)/1000),+((r.packetsLost-prev.packetsLost)/pk*100).toFixed(1),em>0?Math.round((r.jitterBufferDelay-prev.jitterBufferDelay)/em*1000):-1,(r.framesDecoded||0)-(prev.framesDecoded||0),(r.framesDropped||0)-(prev.framesDropped||0),(r.nackCount||0)-(prev.nackCount||0),(r.pliCount||0)-(prev.pliCount||0)]);}
 prev=r;});});},1000);
var transport=new RemoteVideo({world:document.getElementById('world'),cursor:document.getElementById('cursor'),view:function(){return 1;},
 send:function(m){if(m.op==='health'||m.op==='ready')rates.push([Math.round((performance.now()-began)/1000),+m.data]);
  chain=chain.then(function(){return fetch('/signal',{method:'POST',body:JSON.stringify(m)});});},
 mode:function(m){mode=m;},stats:function(b,c){if(first)bytes+=b;}});
transport.start({rtc:1,w:W,h:H,ice:[]});
setInterval(function(){fetch('/events').then(function(r){return r.json();}).then(function(list){(list||[]).forEach(function(m){transport.handle(m);});});},30);
var watched=null;
setInterval(function(){var v=document.querySelector('video');if(!v||v===watched||!v.requestVideoFrameCallback)return;watched=v;
 (function frame(now,meta){if(meta&&v.videoWidth){if(!first)first=performance.now();frames.push(performance.now());
  for(var k=0;k<markers.length;k++){sx.drawImage(v,markers[k][0],markers[k][1],BW,BH,0,0,BW,BH);var val=decode(sx.getImageData(0,0,BW,BH).data);
   if(val!==seen[k]){seen[k]=val;var ms=((Date.now()%%4294967296)-val+4294967296)%%4294967296;if(ms<10000)lat[k].push(ms);}}}
  v.requestVideoFrameCallback(frame);})();},100);
setTimeout(function(){
 var gaps=[];for(var i=1;i<frames.length;i++)gaps.push(frames[i]-frames[i-1]);
 var span=frames.length>1?(frames[frames.length-1]-frames[0])/1000:1;
 fetch('/result',{method:'POST',body:JSON.stringify({firstMs:first?first-began:-1,fps:+(frames.length/span).toFixed(1),
  markers:lat.map(function(a){var s=stats(a)||{};s.fps=+(a.length/span).toFixed(1);return s;}),
  gaps:stats(gaps),stalls:gaps.filter(function(g){return g>150;}).length,net:net.slice(0,40),mbps:+(bytes*8/span/1e6).toFixed(2),rates:rates.slice(-60),mode:mode})});
},SECS*1000);
</script></body></html>`
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/html")
		_, _ = fmt.Fprintf(w, page, width, height, scene, secs)
	})
	server := httptest.NewServer(mux)
	defer server.Close()
	ctx, cancel := context.WithTimeout(context.Background(), time.Duration(secs+30)*time.Second)
	defer cancel()
	cmd := exec.CommandContext(ctx, browser, "--headless=new", "--no-first-run", "--no-default-browser-check", "--disable-background-timer-throttling",
		"--autoplay-policy=no-user-gesture-required", "--user-data-dir="+t.TempDir(), server.URL)
	hideWindow(cmd)
	if err = cmd.Start(); err != nil {
		t.Fatal(err)
	}
	defer func() { cancel(); _ = cmd.Wait() }()
	select {
	case v := <-result:
		out, _ := json.Marshal(v)
		startLog.Lock()
		t.Logf("VIDEO %dx%d@%d %s initial %d max %d: %s", width, height, fps, scene, initial, maximum, out)
		t.Logf("ENCODER STARTS %d: %v", starts.Load(), startTimes)
		startLog.Unlock()
	case <-ctx.Done():
		t.Fatal("bench timeout")
	}
	_ = input.Close()
	select {
	case <-done:
	case <-time.After(5 * time.Second):
	}
}
