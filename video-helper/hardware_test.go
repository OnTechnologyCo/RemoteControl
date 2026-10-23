package main

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/pion/webrtc/v4"
	"github.com/pion/webrtc/v4/pkg/media/h264writer"
)

// Explicit opt-in uses a generated pattern, never a user's desktop or input.
// Covers hardware encode -> RTP -> DTLS/SRTP -> receive -> H.264 decode.
func TestHardwareEncryptedLoopback(t *testing.T) {
	encoder := os.Getenv("RC_VIDEO_TEST_FFMPEG")
	if encoder == "" {
		t.Skip("set RC_VIDEO_TEST_FFMPEG to the verified LGPL binary")
	}
	width, height := 1920, 1080
	if os.Getenv("RC_VIDEO_TEST_4K") == "1" {
		width, height = 3840, 2160
	}
	tx, err := webrtc.NewPeerConnection(webrtc.Configuration{})
	if err != nil {
		t.Fatal(err)
	}
	defer tx.Close()
	rx, err := webrtc.NewPeerConnection(webrtc.Configuration{})
	if err != nil {
		t.Fatal(err)
	}
	defer rx.Close()
	track, err := webrtc.NewTrackLocalStaticRTP(webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeH264, ClockRate: 90000, SDPFmtpLine: "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f"}, "video", "test")
	if err != nil {
		t.Fatal(err)
	}
	sender, err := tx.AddTrack(track)
	if err != nil {
		t.Fatal(err)
	}
	go func() {
		b := make([]byte, 1500)
		for {
			if _, _, e := sender.Read(b); e != nil {
				return
			}
		}
	}()
	file := filepath.Join(t.TempDir(), "pattern.h264")
	output, err := h264writer.New(file)
	if err != nil {
		t.Fatal(err)
	}
	var frames atomic.Int64
	var headers atomic.Int64
	first := make(chan struct{}, 1)
	reading := make(chan struct{})
	rx.OnTrack(func(track *webrtc.TrackRemote, _ *webrtc.RTPReceiver) {
		defer close(reading)
		defer output.Close()
		for {
			p, _, e := track.ReadRTP()
			if e != nil {
				return
			}
			if len(p.Payload) > 0 && (p.Payload[0]&31 == 7 || p.Payload[0]&31 == 24) {
				headers.Add(1)
			}
			if e = output.WriteRTP(p); e != nil {
				return
			}
			if p.Marker {
				frames.Add(1)
				select {
				case first <- struct{}{}:
				default:
				}
			}
		}
	})
	offer, err := tx.CreateOffer(nil)
	if err != nil {
		t.Fatal(err)
	}
	gather := webrtc.GatheringCompletePromise(tx)
	if err = tx.SetLocalDescription(offer); err != nil {
		t.Fatal(err)
	}
	<-gather
	if err = rx.SetRemoteDescription(*tx.LocalDescription()); err != nil {
		t.Fatal(err)
	}
	answer, err := rx.CreateAnswer(nil)
	if err != nil {
		t.Fatal(err)
	}
	gather = webrtc.GatheringCompletePromise(rx)
	if err = rx.SetLocalDescription(answer); err != nil {
		t.Fatal(err)
	}
	<-gather
	if err = tx.SetRemoteDescription(*rx.LocalDescription()); err != nil {
		t.Fatal(err)
	}
	deadline := time.Now().Add(8 * time.Second)
	for tx.ConnectionState() != webrtc.PeerConnectionStateConnected && time.Now().Before(deadline) {
		time.Sleep(10 * time.Millisecond)
	}
	if tx.ConnectionState() != webrtc.PeerConnectionStateConnected {
		t.Fatal("DTLS connection timeout")
	}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	config := command{FFmpeg: encoder, Width: width, Height: height, OutputWidth: width, OutputHeight: height, FPS: 30, Bitrate: 4000000, Adapter: -1, Output: -1}
	var rate atomic.Int64
	rate.Store(2000000)
	factory := func(ctx context.Context, args []string) *exec.Cmd {
		start := 0
		for i, v := range args {
			if v == "-an" {
				start = i
				break
			}
		}
		fixed := []string{"-hide_banner", "-loglevel", "error", "-nostdin", "-re", "-f", "lavfi", "-i", fmt.Sprintf("testsrc2=size=%dx%d:rate=30", width, height)}
		return exec.CommandContext(ctx, encoder, append(fixed, args[start:]...)...)
	}
	done := make(chan error, 1)
	started := time.Now()
	go func() {
		done <- streamWithFactory(ctx, config, track, &writer{enc: json.NewEncoder(io.Discard)}, &rate, factory)
	}()
	select {
	case <-first:
		t.Logf("first encrypted %dx%d frame in %s", width, height, time.Since(started))
	case <-time.After(8 * time.Second):
		t.Fatal("no hardware RTP frames")
	}
	time.Sleep(2500 * time.Millisecond)
	// Exercise the live bitrate restart and continuous RTP sequence/timestamps.
	rate.Store(1000000)
	time.Sleep(2500 * time.Millisecond)
	cancel()
	select {
	case <-done:
	case <-time.After(3 * time.Second):
		t.Fatal("encoder did not exit")
	}
	_ = rx.Close()
	select {
	case <-reading:
	case <-time.After(3 * time.Second):
		t.Fatal("receiver did not exit")
	}
	if frames.Load() < 100 {
		t.Fatalf("too few delivered frames: %d", frames.Load())
	}
	t.Logf("RTP frames=%d header packets=%d", frames.Load(), headers.Load())
	decode := exec.Command(encoder, "-hide_banner", "-v", "error", "-xerror", "-i", file, "-f", "null", "-")
	hideWindow(decode)
	log, err := decode.CombinedOutput()
	if err != nil || strings.Contains(string(log), "error") {
		t.Fatalf("H.264 decode failed: %v %s", err, log)
	}
	t.Logf("%d frames encrypted, received, and decoded; includes live bitrate change", frames.Load())
}
