package main

import (
	"bytes"
	"encoding/json"
	"io"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/pion/rtp"
	"github.com/pion/webrtc/v4"
)

func TestTimelineAcrossEncoderRestart(t *testing.T) {
	clock := timeline{}
	clock.restart()
	p := rtp.Packet{Header: rtp.Header{Timestamp: 0xfffffff0}}
	clock.rewrite(&p)
	first := p.Timestamp
	p = rtp.Packet{Header: rtp.Header{Timestamp: 0x00000ba8}}
	clock.rewrite(&p)
	if p.SequenceNumber != 1 || p.Timestamp-first != 3000 {
		t.Fatal("RTP clock wrap changed frame spacing")
	}
	stamp := p.Timestamp
	clock.restart()
	p = rtp.Packet{Header: rtp.Header{Timestamp: 12345}}
	clock.rewrite(&p)
	if p.SequenceNumber != 2 || p.Timestamp-stamp != 3000 {
		t.Fatal("encoder restart reset receiver timeline")
	}
}
func TestArgumentsAreFixedAndLowDelay(t *testing.T) {
	c := command{X: -1920, Y: 0, Width: 1920, Height: 1080, OutputWidth: 1440, OutputHeight: 810, FPS: 30, Adapter: 1, Output: 2}
	for _, m := range modes(c) {
		args := strings.Join(arguments(c, m, 12345, 2000000), " ")
		for _, need := range []string{"-bf 0", "-g 30", "-draw_mouse 0", "rtp://127.0.0.1:12345?pkt_size=1200"} {
			if m.dda && need == "-draw_mouse 0" {
				need = "draw_mouse=0"
			}
			if !strings.Contains(args, need) {
				t.Fatalf("missing %s in %s", need, args)
			}
		}
		if strings.Contains(args, "libx264") || strings.Contains(args, "-listen") {
			t.Fatal("unexpected software encoder or listener")
		}
	}
}
func TestHelperEOFClosesBeforeICEConnect(t *testing.T) {
	pc, err := webrtc.NewPeerConnection(webrtc.Configuration{})
	if err != nil {
		t.Fatal(err)
	}
	defer pc.Close()
	if _, err = pc.AddTransceiverFromKind(webrtc.RTPCodecTypeVideo, webrtc.RTPTransceiverInit{Direction: webrtc.RTPTransceiverDirectionRecvonly}); err != nil {
		t.Fatal(err)
	}
	offer, err := pc.CreateOffer(nil)
	if err != nil {
		t.Fatal(err)
	}
	if dest := os.Getenv("RC_VIDEO_TEST_OFFER"); dest != "" {
		if err = os.WriteFile(dest, []byte(offer.SDP), 0600); err != nil {
			t.Fatal(err)
		}
	}
	c := command{Type: "start", SDP: offer.SDP, FFmpeg: filepath.Join(t.TempDir(), "ffmpeg.exe"), Width: 1920, Height: 1080, OutputWidth: 1920, OutputHeight: 1080, Adapter: -1, Output: -1, FPS: 30, Bitrate: 6000000, InitialBitrate: 2000000}
	body, _ := json.Marshal(c)
	body = append(body, '\n')
	body = append([]byte{0xef, 0xbb, 0xbf}, body...) // .NET Framework redirected pipe
	done := make(chan error, 1)
	go func() { done <- run(bytes.NewReader(body), &writer{enc: json.NewEncoder(io.Discard)}) }()
	select {
	case err := <-done:
		if err != nil {
			t.Fatal(err)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("stdin EOF failed to cancel ICE")
	}
}
