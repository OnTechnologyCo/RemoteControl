// 원격제어는 C# 5 단일 EXE다. SSH 확장 API는 SSHTunnel의 기존 Go SDK에 맡긴다.
package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/url"
	"os"
	"os/signal"
	"strings"

	"ontech/sshtunnel/pkg/tunnel"
)

type config struct {
	Server      string `json:"server"`
	User        string `json:"user"`
	AccessKey   string `json:"accessKey"`
	Fingerprint string `json:"fingerprint"`
	Target      string `json:"target"`
	Name        string `json:"name"`
	McpTarget   string `json:"mcpTarget,omitempty"`
	McpProxyKey string `json:"mcpProxyKey,omitempty"`
}
type event struct {
	Event     string `json:"event"`
	URL       string `json:"url,omitempty"`
	Message   string `json:"message,omitempty"`
	Permanent bool   `json:"permanent,omitempty"`
}

func permanent(err error) bool {
	text := strings.ToLower(err.Error())
	for _, part := range []string{"fingerprint mismatch", "unable to authenticate", "invalid web endpoint", "invalid tunnel endpoint", "user and a", "fingerprint is required", "invalid relay address", "target must", "비활성화", "계정 권한", "웹 터널 설정", "터널 키는"} {
		if strings.Contains(text, part) {
			return true
		}
	}
	return false
}

func run(ctx context.Context, input io.Reader, output io.Writer) error {
	var cfg config
	reader := bufio.NewReader(io.LimitReader(input, 16385))
	// .NET Framework의 stdin StreamWriter는 BaseStream 접근 때도 UTF-8 BOM을 먼저 쓴다.
	// 실제 프로세스 연결에서 확인한 호환 처리이며 JSON 본문과 인증키는 그대로 유지한다.
	if prefix, _ := reader.Peek(3); bytes.Equal(prefix, []byte{0xef, 0xbb, 0xbf}) {
		_, _ = reader.Discard(3)
	}
	d := json.NewDecoder(reader)
	d.DisallowUnknownFields()
	if err := d.Decode(&cfg); err != nil {
		return fmt.Errorf("invalid connection settings: %w", err)
	}
	if err := d.Decode(new(any)); err != io.EOF {
		return errors.New("invalid connection settings: trailing data")
	}
	var shared *privateProxy
	target := cfg.Target
	if cfg.McpTarget != "" || cfg.McpProxyKey != "" {
		var err error
		shared, err = newPrivateProxy(cfg.Target, cfg.McpTarget, cfg.McpProxyKey)
		if err != nil {
			return err
		}
		defer shared.Close()
		target = shared.listener.Addr().String()
	}
	session, err := tunnel.Open(ctx, tunnel.Options{Protocol: "http", Server: cfg.Server, User: cfg.User, AccessKey: cfg.AccessKey,
		// MCP 프록시는 시작마다 임시 포트를 쓰므로 주소 신원은 포트나 표시 이름과 분리한다.
		Fingerprint: cfg.Fingerprint, Target: target, Name: cfg.Name, Identity: "remotecontrol", MaxConnections: 64})
	if err != nil {
		return err
	}
	defer session.Close()
	cfg.AccessKey = ""
	endpoint := session.Endpoint().URL
	u, err := url.Parse(endpoint)
	if err != nil || u == nil || u.Scheme != "https" || u.Port() != "" {
		return errors.New("invalid web endpoint: a port-free HTTPS URL is required")
	}
	if shared != nil {
		shared.host.Store(u.Host)
	}
	encoder := json.NewEncoder(output)
	if err := encoder.Encode(event{Event: "ready", URL: endpoint}); err != nil {
		return err
	}
	if err := session.Wait(); err != nil && ctx.Err() == nil {
		return err
	}
	return nil
}

func main() {
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt)
	defer cancel()
	if err := run(ctx, os.Stdin, os.Stdout); err != nil {
		_ = json.NewEncoder(os.Stdout).Encode(event{Event: "error", Message: err.Error(), Permanent: permanent(err) || strings.HasPrefix(err.Error(), "invalid connection settings")})
		os.Exit(1)
	}
}
