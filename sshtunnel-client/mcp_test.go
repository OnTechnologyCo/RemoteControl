package main

import (
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestPrivateRoutes(t *testing.T) {
	mcp := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("X-RC-Proxy-Key") != strings.Repeat("A", 43) || r.Header.Get("X-RC-Client-IP") != "198.51.100.2" || r.Header.Get("X-Forwarded-Proto") != "https" || r.Header.Get("Forwarded") != "" || r.Header.Get("X-Forwarded-For") != "" {
			t.Error("untrusted hints or missing relay authentication")
		}
		w.Write([]byte("mcp"))
	}))
	defer mcp.Close()
	screen := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("X-RC-Proxy-Key") != "" || r.Header.Get("X-RC-Client-IP") != "" {
			t.Error("MCP key leaked to screen")
		}
		w.Write([]byte("screen"))
	}))
	defer screen.Close()
	p, err := newPrivateProxy(strings.TrimPrefix(screen.URL, "http://"), strings.TrimPrefix(mcp.URL, "http://"), strings.Repeat("A", 43))
	if err != nil {
		t.Fatal(err)
	}
	defer p.Close()
	p.host.Store("abcd.tunnel.example.test")
	send := func(path, host, proto, ip string) (int, string) {
		r, _ := http.NewRequest("GET", "http://"+p.listener.Addr().String()+path, nil)
		r.Host = host
		r.Header.Set("X-Forwarded-Proto", proto)
		r.Header.Set("X-Forwarded-For", ip)
		r.Header.Set("X-RC-Proxy-Key", "forged")
		r.Header.Set("X-RC-Client-IP", "spoofed")
		r.Header.Set("Forwarded", "for=evil")
		resp, e := http.DefaultClient.Do(r)
		if e != nil {
			t.Fatal(e)
		}
		defer resp.Body.Close()
		b, _ := io.ReadAll(resp.Body)
		return resp.StatusCode, string(b)
	}
	for _, path := range []string{"/mcp", "/authorize", "/connect", "/register", "/token", "/revoke", "/.well-known/oauth-protected-resource/mcp", "/.well-known/oauth-authorization-server", "/files", "/files/open", "/files/api", "/files/download", "/files/client.js"} {
		status, body := send(path, "abcd.tunnel.example.test", "https", "198.51.100.2")
		if status != 200 || body != "mcp" {
			t.Fatal(path, status, body)
		}
	}
	for _, path := range []string{"/", "/login", "/ping", "/ws", "/files/extra", "/mcp/extra"} {
		status, body := send(path, "abcd.tunnel.example.test", "https", "198.51.100.2")
		if status != 200 || body != "screen" {
			t.Fatal(path, status, body)
		}
	}
	for _, bad := range []struct{ host, proto, ip string }{{"evil.test", "https", "198.51.100.2"}, {"abcd.tunnel.example.test", "http", "198.51.100.2"}, {"abcd.tunnel.example.test", "https", "evil"}, {"abcd.tunnel.example.test", "https", "198.51.100.2, 1.2.3.4"}} {
		status, _ := send("/mcp", bad.host, bad.proto, bad.ip)
		if status < 400 {
			t.Fatal("accepted forged relay", bad)
		}
	}
	status, _ := send("/m%63p", "abcd.tunnel.example.test", "https", "198.51.100.2")
	if status != 403 {
		t.Fatal("encoded MCP path accepted", status)
	}
}
func TestPrivateTargets(t *testing.T) {
	for _, target := range []string{"evil.test:59336", "127.0.0.1:80", "127.0.0.1:0", "127.0.0.1:70000", "http://127.0.0.1:59336"} {
		if _, err := newPrivateProxy("127.0.0.1:5900", target, strings.Repeat("A", 43)); err == nil {
			t.Fatal(target)
		}
	}
}
