package main

import (
	"fmt"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"sync/atomic"
	"time"
)

type privateProxy struct {
	server   *http.Server
	listener net.Listener
	host     atomic.Value
}

func localURL(target string) (*url.URL, error) {
	host, port, err := net.SplitHostPort(target)
	p, e := strconv.Atoi(port)
	if err != nil || e != nil || host != "127.0.0.1" || p < 1024 || p > 65535 {
		return nil, fmt.Errorf("invalid connection settings: backend must be a loopback port")
	}
	return &url.URL{Scheme: "http", Host: target}, nil
}
func mcpPath(path string) bool {
	switch path {
	case "/files", "/files/open", "/files/api", "/files/client.js", "/files/download",
		"/mcp", "/authorize", "/connect", "/token", "/register", "/revoke",
		"/.well-known/oauth-authorization-server", "/.well-known/oauth-protected-resource", "/.well-known/oauth-protected-resource/mcp":
		return true
	}
	return false
}
func newPrivateProxy(screenTarget, mcpTarget, key string) (*privateProxy, error) {
	screen, err := localURL(screenTarget)
	if err != nil {
		return nil, err
	}
	mcp, err := localURL(mcpTarget)
	if err != nil {
		return nil, err
	}
	if screen.Host == mcp.Host || !regexp.MustCompile(`^[A-Za-z0-9_-]{43}$`).MatchString(key) {
		return nil, fmt.Errorf("invalid connection settings: invalid MCP proxy settings")
	}
	p := &privateProxy{}
	p.host.Store("")
	proxy := func(target *url.URL, authenticated bool) *httputil.ReverseProxy {
		return &httputil.ReverseProxy{Rewrite: func(pr *httputil.ProxyRequest) {
			pr.SetURL(target)
			pr.Out.Host = pr.In.Host
			// These authentication hints must never reach the screen backend.
			pr.Out.Header.Del("X-RC-Proxy-Key")
			pr.Out.Header.Del("X-RC-Client-IP")
			if authenticated {
				ip := pr.In.Header.Get("X-Forwarded-For")
				for _, name := range []string{"Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Forwarded-Port", "X-Forwarded-Prefix", "X-Real-IP"} {
					pr.Out.Header.Del(name)
				}
				pr.Out.Host = p.host.Load().(string)
				pr.Out.Header.Set("X-Forwarded-Proto", "https")
				pr.Out.Header.Set("X-RC-Proxy-Key", key)
				pr.Out.Header.Set("X-RC-Client-IP", ip)
			}
		}, FlushInterval: -1, ErrorHandler: func(w http.ResponseWriter, r *http.Request, e error) {
			http.Error(w, "Local gateway unavailable", http.StatusBadGateway)
		}}
	}
	screenProxy, mcpProxy := proxy(screen, false), proxy(mcp, true)
	p.server = &http.Server{ReadHeaderTimeout: 10 * time.Second, IdleTimeout: 90 * time.Second, MaxHeaderBytes: 16384, Handler: http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		host := p.host.Load().(string)
		if host == "" {
			http.Error(w, "Waiting for tunnel address", http.StatusServiceUnavailable)
			return
		}
		if !strings.EqualFold(r.Host, host) && !strings.EqualFold(r.Host, host+":443") {
			http.Error(w, "Host rejected", http.StatusBadRequest)
			return
		}
		if mcpPath(r.URL.Path) {
			// Only the pinned relay creates these values: it strips public proxy hints
			// and terminates TLS before sending the request over the SSH channel.
			if r.URL.EscapedPath() != r.URL.Path || len(r.Header.Values("X-Forwarded-Proto")) != 1 || r.Header.Get("X-Forwarded-Proto") != "https" || len(r.Header.Values("X-Forwarded-For")) != 1 || net.ParseIP(r.Header.Get("X-Forwarded-For")) == nil {
				http.Error(w, "HTTPS relay required", http.StatusForbidden)
				return
			}
			r.Body = http.MaxBytesReader(w, r.Body, 2*1024*1024)
			mcpProxy.ServeHTTP(w, r)
		} else {
			screenProxy.ServeHTTP(w, r)
		}
	})}
	p.listener, err = net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return nil, err
	}
	go p.server.Serve(p.listener)
	return p, nil
}
func (p *privateProxy) Close() { p.server.Close() }
