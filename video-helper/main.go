// video-helper has no control or HTTP listener. It receives one authenticated
// viewer's signaling over its parent's private pipes and emits encrypted SRTP.
package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/pion/rtp"
	"github.com/pion/webrtc/v4"
)

type command struct {
	Type                         string `json:"type"`
	SDP                          string `json:"sdp,omitempty"`
	Candidate                    string `json:"candidate,omitempty"`
	FFmpeg                       string `json:"ffmpeg,omitempty"`
	X, Y, Width, Height          int
	OutputWidth, OutputHeight    int
	Adapter, Output              int
	FPS, Bitrate, InitialBitrate int
	ICE                          []webrtc.ICEServer `json:"ice,omitempty"`
}

type writer struct {
	mu  sync.Mutex
	enc *json.Encoder
}

func (w *writer) send(v any)            { w.mu.Lock(); defer w.mu.Unlock(); _ = w.enc.Encode(v) }
func (w *writer) event(t, value string) { w.send(map[string]any{"type": t, "value": value}) }

func main() {
	w := &writer{enc: json.NewEncoder(os.Stdout)}
	if err := run(os.Stdin, w); err != nil {
		w.event("error", err.Error())
		os.Exit(1)
	}
}

func validate(c command) error {
	if c.Type != "start" || len(c.SDP) < 32 || len(c.SDP) > 128*1024 {
		return errors.New("invalid offer")
	}
	if !filepath.IsAbs(c.FFmpeg) || !strings.EqualFold(filepath.Ext(c.FFmpeg), ".exe") {
		return errors.New("invalid encoder path")
	}
	if c.Width < 16 || c.Height < 16 || c.Width > 8192 || c.Height > 8192 ||
		c.OutputWidth < 16 || c.OutputHeight < 16 || c.OutputWidth > c.Width || c.OutputHeight > c.Height ||
		c.FPS < 10 || c.FPS > 60 || c.Bitrate < 500000 || c.Bitrate > 30000000 ||
		c.Adapter < -1 || c.Adapter > 32 || c.Output < -1 || c.Output > 32 {
		return errors.New("invalid video geometry")
	}
	return nil
}

func run(input io.Reader, w *writer) error {
	return runWithStream(input, w, stream)
}
func runWithStream(input io.Reader, w *writer, streamer func(context.Context, command, *webrtc.TrackLocalStaticRTP, *writer, *atomic.Int64) error) error {
	scan := bufio.NewScanner(input)
	scan.Buffer(make([]byte, 4096), 256*1024)
	if !scan.Scan() {
		return errors.New("missing start")
	}
	// .NET Framework may emit its UTF-8 preamble when it opens redirected stdin.
	var c command
	if err := json.Unmarshal(bytes.TrimPrefix(scan.Bytes(), []byte{0xef, 0xbb, 0xbf}), &c); err != nil {
		return errors.New("invalid start JSON")
	}
	if err := validate(c); err != nil {
		return err
	}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	pc, err := webrtc.NewPeerConnection(webrtc.Configuration{ICEServers: c.ICE})
	if err != nil {
		return errors.New("peer initialization failed")
	}
	defer pc.Close()
	track, err := webrtc.NewTrackLocalStaticRTP(webrtc.RTPCodecCapability{
		MimeType: webrtc.MimeTypeH264, ClockRate: 90000, SDPFmtpLine: "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f",
	}, "desktop", "remotecontrol")
	if err != nil {
		return err
	}
	sender, err := pc.AddTrack(track)
	if err != nil {
		return err
	}
	// Drain RTCP to run the default NACK interceptor. A one-second IDR interval
	// bounds recovery after PLI/FIR without continuously restarting the encoder.
	go func() {
		b := make([]byte, 1500)
		for {
			if _, _, e := sender.Read(b); e != nil {
				return
			}
		}
	}()
	pc.OnICECandidate(func(candidate *webrtc.ICECandidate) {
		if candidate != nil {
			b, _ := json.Marshal(candidate.ToJSON())
			w.event("candidate", string(b))
		}
	})
	connected := make(chan struct{}, 1)
	pc.OnConnectionStateChange(func(state webrtc.PeerConnectionState) {
		w.event("state", state.String())
		if state == webrtc.PeerConnectionStateConnected {
			select {
			case connected <- struct{}{}:
			default:
			}
		}
		if state == webrtc.PeerConnectionStateFailed || state == webrtc.PeerConnectionStateClosed {
			cancel()
		}
	})
	if err = pc.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeOffer, SDP: c.SDP}); err != nil {
		return errors.New("offer rejected")
	}
	answer, err := pc.CreateAnswer(nil)
	if err != nil {
		return errors.New("answer failed")
	}
	if err = pc.SetLocalDescription(answer); err != nil {
		return errors.New("local description failed")
	}
	w.event("answer", answer.SDP)
	// Read commands concurrently so EOF always terminates capture, even while ICE
	// or a driver is blocked. Unknown commands cannot inject FFmpeg arguments.
	var desired atomic.Int64
	desired.Store(int64(clamp(c.InitialBitrate, 500000, c.Bitrate)))
	go func() {
		defer cancel()
		for scan.Scan() {
			var next command
			if json.Unmarshal(scan.Bytes(), &next) != nil {
				return
			}
			switch next.Type {
			case "candidate":
				var candidate webrtc.ICECandidateInit
				if len(next.Candidate) > 8192 || json.Unmarshal([]byte(next.Candidate), &candidate) != nil {
					return
				}
				if pc.AddICECandidate(candidate) != nil {
					return
				}
			case "rate":
				desired.Store(int64(clamp(next.Bitrate, 500000, c.Bitrate)))
			case "stop":
				return
			default:
				return
			}
		}
	}()
	select {
	case <-connected:
	case <-ctx.Done():
		return nil
	case <-time.After(15 * time.Second):
		return errors.New("ICE timeout")
	}
	return streamer(ctx, c, track, w, &desired)
}

func clamp(v, lo, hi int) int {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}

type mode struct {
	codec string
	dda   bool
}

func modes(c command) []mode {
	var list []mode
	if c.Adapter >= 0 && c.Output >= 0 {
		list = append(list, mode{"h264_amf", true}, mode{"h264_nvenc", true})
	}
	return append(list, mode{"h264_amf", false}, mode{"h264_nvenc", false}, mode{"h264_qsv", false})
}
func arguments(c command, m mode, port, bitrate int) []string {
	s := strconv.Itoa
	args := []string{"-hide_banner", "-loglevel", "error", "-nostdin"}
	if m.dda {
		args = append(args, "-init_hw_device", "d3d11va=cap:"+s(c.Adapter), "-filter_hw_device", "cap", "-f", "lavfi", "-i",
			fmt.Sprintf("ddagrab=output_idx=%d:framerate=%d:draw_mouse=0:dup_frames=1", c.Output, c.FPS))
	} else {
		args = append(args, "-f", "gdigrab", "-framerate", s(c.FPS), "-draw_mouse", "0", "-offset_x", s(c.X), "-offset_y", s(c.Y), "-video_size", fmt.Sprintf("%dx%d", c.Width, c.Height), "-i", "desktop")
	}
	ow, oh := c.OutputWidth&^1, c.OutputHeight&^1
	if !m.dda || ow != c.Width || oh != c.Height {
		filter := fmt.Sprintf("scale=%d:%d:flags=fast_bilinear,format=nv12", ow, oh)
		if m.dda {
			filter = "hwdownload,format=bgra," + filter
		}
		args = append(args, "-vf", filter)
	}
	args = append(args, "-an", "-c:v", m.codec, "-flags", "-global_header", "-bsf:v", "dump_extra=freq=keyframe", "-b:v", s(bitrate), "-maxrate", s(bitrate), "-bufsize", s(bitrate/4), "-g", s(c.FPS), "-bf", "0")
	switch m.codec {
	case "h264_amf":
		args = append(args, "-usage", "ultralowlatency", "-quality", "speed", "-rc", "cbr", "-profile:v", "constrained_baseline", "-async_depth", "1", "-header_spacing", s(c.FPS))
	case "h264_nvenc":
		args = append(args, "-preset", "p1", "-tune", "ull", "-rc", "cbr", "-profile:v", "baseline", "-zerolatency", "1", "-rc-lookahead", "0", "-surfaces", "2")
	case "h264_qsv":
		args = append(args, "-preset", "veryfast", "-profile:v", "baseline", "-async_depth", "1", "-look_ahead", "0")
	}
	return append(args, "-flush_packets", "1", "-f", "rtp", "-payload_type", "96", fmt.Sprintf("rtp://127.0.0.1:%d?pkt_size=1200", port))
}

// FFmpeg restarts for a material bitrate change. Preserve the outgoing RTP
// sequence/timeline across a new encoder SSRC; its first frame is always an IDR.
type timeline struct {
	sequence              uint16
	stamp, base, previous uint32
	first                 bool
}

func (t *timeline) restart() { t.first = true; t.base = t.stamp + 3000 }
func (t *timeline) rewrite(p *rtp.Packet) {
	if t.first {
		t.previous = p.Timestamp
		t.first = false
	}
	t.stamp = t.base + (p.Timestamp - t.previous)
	p.SequenceNumber = t.sequence
	t.sequence++
	p.Timestamp = t.stamp
}

func stream(ctx context.Context, c command, track *webrtc.TrackLocalStaticRTP, w *writer, desired *atomic.Int64) error {
	return streamWithFactory(ctx, c, track, w, desired, func(ctx context.Context, args []string) *exec.Cmd { return exec.CommandContext(ctx, c.FFmpeg, args...) })
}
func streamWithFactory(ctx context.Context, c command, track *webrtc.TrackLocalStaticRTP, w *writer, desired *atomic.Int64, factory func(context.Context, []string) *exec.Cmd) error {
	udp, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		return errors.New("RTP socket failed")
	}
	defer udp.Close()
	_ = udp.SetReadBuffer(256 * 1024)
	go func() { <-ctx.Done(); _ = udp.Close() }()
	port := udp.LocalAddr().(*net.UDPAddr).Port
	clock := timeline{}
	available := modes(c)
	index := 0
	for index < len(available) && ctx.Err() == nil {
		bitrate := int(desired.Load())
		m := available[index]
		childCtx, stop := context.WithCancel(ctx)
		cmd := factory(childCtx, arguments(c, m, port, bitrate))
		hideWindow(cmd)
		// Logs never include local paths, SDP, or ICE credentials in browser messages.
		cmd.Stderr = io.Discard
		cmd.Stdout = io.Discard
		if err = cmd.Start(); err != nil {
			stop()
			index++
			continue
		}
		done := make(chan error, 1)
		go func() { done <- cmd.Wait() }()
		clock.restart()
		started := time.Now()
		lastPacket := started
		received := false
		restart := false
		exited := false
		buf := make([]byte, 2048)
		for ctx.Err() == nil {
			select {
			case <-done:
				exited = true
			default:
			}
			if exited {
				break
			}
			_ = udp.SetReadDeadline(time.Now().Add(200 * time.Millisecond))
			n, _, readErr := udp.ReadFromUDP(buf)
			if readErr == nil {
				var packet rtp.Packet
				if packet.Unmarshal(buf[:n]) == nil {
					lastPacket = time.Now()
					if !received {
						received = true
						w.event("encoder", m.codec+map[bool]string{true: " / DXGI", false: " / GDI"}[m.dda])
					}
					clock.rewrite(&packet)
					if track.WriteRTP(&packet) != nil {
						break
					}
				}
			}
			if time.Since(lastPacket) > 4*time.Second {
				break
			}
			target := int(desired.Load())
			if received && ((time.Since(started) > 2*time.Second && target < bitrate*3/4) || (time.Since(started) > 8*time.Second && target > bitrate*5/4)) {
				restart = true
				break
			}
		}
		stop()
		if !exited {
			<-done
		}
		if ctx.Err() != nil {
			return nil
		}
		// Discard only packets from the stopped encoder before adopting its successor's
		// RTP clock. Otherwise queued old timestamps can corrupt a bitrate restart.
		for i := 0; i < 256; i++ {
			_ = udp.SetReadDeadline(time.Now().Add(time.Millisecond))
			if _, _, e := udp.ReadFromUDP(buf); e != nil {
				break
			}
		}
		if !restart {
			index++
		}
	}
	if ctx.Err() != nil {
		return nil
	}
	return errors.New("hardware capture unavailable")
}
