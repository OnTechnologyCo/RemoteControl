package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/pion/webrtc/v4"
)

type browserEvents struct {
	mu    sync.Mutex
	queue []map[string]any
}

func (e *browserEvents) Write(b []byte) (int, error) {
	var value map[string]string
	if err := json.Unmarshal(b, &value); err != nil {
		return 0, err
	}
	e.mu.Lock()
	defer e.mu.Unlock()
	if value["type"] == "answer" || value["type"] == "candidate" || value["type"] == "encoder" {
		e.queue = append(e.queue, map[string]any{"t": "rtc", "id": 1, "op": value["type"], "data": value["value"]})
	}
	return len(b), nil
}
func TestBrowserVideoAndFallback(t *testing.T) {
	browser, encoder := os.Getenv("RC_TEST_BROWSER"), os.Getenv("RC_VIDEO_TEST_FFMPEG")
	if browser == "" || encoder == "" {
		t.Skip("set RC_TEST_BROWSER and RC_VIDEO_TEST_FFMPEG for the opt-in browser test")
	}
	width, height := 1920, 1080
	if os.Getenv("RC_VIDEO_TEST_4K") == "1" {
		width, height = 3840, 2160
	}
	media, err := os.ReadFile(filepath.Join("..", "web", "media.js"))
	if err != nil {
		t.Fatal(err)
	}
	reader, input := io.Pipe()
	defer reader.Close()
	defer input.Close()
	events := &browserEvents{}
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
				prefix := []string{"-hide_banner", "-loglevel", "error", "-nostdin", "-re", "-f", "lavfi", "-i", fmt.Sprintf("testsrc2=size=%dx%d:rate=30", width, height)}
				return exec.CommandContext(ctx, encoder, append(prefix, args[start:]...)...)
			}
			return streamWithFactory(ctx, c, track, w, rate, factory)
		})
	}()
	result := make(chan map[string]float64, 1)
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
			_ = enc.Encode(command{Type: "start", SDP: m.Data, FFmpeg: encoder, Width: width, Height: height, OutputWidth: width, OutputHeight: height, Adapter: -1, Output: -1, FPS: 30, Bitrate: 6000000, InitialBitrate: 2000000})
		case "candidate":
			_ = enc.Encode(command{Type: "candidate", Candidate: m.Data})
		}
	})
	mux.HandleFunc("/stop", func(w http.ResponseWriter, r *http.Request) { _ = input.Close() })
	mux.HandleFunc("/result", func(w http.ResponseWriter, r *http.Request) {
		var v map[string]float64
		if json.NewDecoder(r.Body).Decode(&v) == nil {
			select {
			case result <- v:
			default:
			}
		}
	})
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/html")
		_, _ = io.WriteString(w, strings.ReplaceAll(`<!doctype html><html><body><div id="world"><img id="cursor"></div><script src="/media.js"></script><script>
var began=performance.now(), first=0, frames=0, stopped=0, finished=false, chain=Promise.resolve();
function report(data){if(finished)return;finished=true;fetch('/result',{method:'POST',body:JSON.stringify(data)});}
var transport=new RemoteVideo({world:document.getElementById('world'),cursor:document.getElementById('cursor'),view:function(){return 1;},
send:function(m){chain=chain.then(function(){return fetch('/signal',{method:'POST',body:JSON.stringify(m)});});
if(m.op==='ready'&&!first){first=performance.now()-began;setTimeout(function(){stopped=performance.now();fetch('/stop');},5000);}},
mode:function(mode){if(mode==='JPEG'&&stopped){report({firstMs:first,frames:frames,fallbackMs:performance.now()-stopped});}},
stats:function(bytes,count){frames+=count;}});
transport.start({rtc:1,w:1920,h:1080,ice:[]});
setInterval(function(){fetch('/events').then(function(r){return r.json();}).then(function(list){(list||[]).forEach(function(m){transport.handle(m);});});},30);
setTimeout(function(){report({firstMs:first,frames:frames,fallbackMs:-1});},20000);
</script></body></html>`, "w:1920,h:1080", fmt.Sprintf("w:%d,h:%d", width, height)))
	})
	server := httptest.NewServer(mux)
	defer server.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 25*time.Second)
	defer cancel()
	profile := t.TempDir()
	cmd := exec.CommandContext(ctx, browser, "--headless=new", "--no-first-run", "--no-default-browser-check", "--user-data-dir="+profile, server.URL)
	hideWindow(cmd)
	var log bytes.Buffer
	cmd.Stderr = &log
	if err = cmd.Start(); err != nil {
		t.Fatal(err)
	}
	defer func() { cancel(); _ = cmd.Wait() }()
	select {
	case v := <-result:
		if v["firstMs"] <= 0 || v["frames"] < 90 || v["fallbackMs"] < 0 || v["fallbackMs"] > 6000 {
			t.Fatalf("browser video/fallback failed: %v", v)
		}
		t.Logf("Browser displayed first frame %.0fms, decoded %.0f frames, fallback %.0fms", v["firstMs"], v["frames"], v["fallbackMs"])
	case <-ctx.Done():
		t.Fatalf("browser timeout: %s", log.String())
	}
	_ = input.Close()
	select {
	case <-done:
	case <-time.After(3 * time.Second):
		t.Fatal("helper survived browser teardown")
	}
}
