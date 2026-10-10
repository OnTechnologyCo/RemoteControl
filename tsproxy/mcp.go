package main

import (
	"context"
	"fmt"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
	"regexp"
	"strconv"
	"strings"
	"tailscale.com/tsnet"
	"time"
)

func mcpProxy(target, host, key string) (http.Handler, error) {
	u, err := url.Parse(target)
	if err != nil || u.Scheme != "http" || u.Hostname() != "127.0.0.1" || u.User != nil || u.Path != "" || u.RawQuery != "" || u.Fragment != "" {
		return nil, fmt.Errorf("MCP backend must be a loopback HTTP port")
	}
	port, err := strconv.Atoi(u.Port())
	if err != nil || port < 1024 || port > 65535 || !regexp.MustCompile(`^[A-Za-z0-9_-]{43}$`).MatchString(key) {
		return nil, fmt.Errorf("invalid MCP proxy settings")
	}
	proxy := &httputil.ReverseProxy{Rewrite: func(pr *httputil.ProxyRequest) {
		pr.SetURL(u)
		pr.Out.Host = host
		for _, name := range []string{"Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Real-IP", "X-RC-Proxy-Key", "X-RC-Client-IP"} {
			pr.Out.Header.Del(name)
		}
		pr.Out.Header.Set("X-Forwarded-Proto", "https")
		pr.Out.Header.Set("X-RC-Proxy-Key", key)
		ip, _ := pr.In.Context().Value(clientAddrKey{}).(string)
		if net.ParseIP(ip) == nil {
			ip, _, _ = net.SplitHostPort(pr.In.RemoteAddr)
		}
		if net.ParseIP(ip) != nil {
			pr.Out.Header.Set("X-RC-Client-IP", ip)
		}
	}, FlushInterval: -1, ErrorHandler: func(w http.ResponseWriter, r *http.Request, e error) {
		http.Error(w, "MCP gateway unavailable", http.StatusBadGateway)
	}}
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if !strings.EqualFold(r.Host, host) && !(strings.IndexByte(host, ':') < 0 && strings.EqualFold(r.Host, host+":443")) {
			http.Error(w, "Host rejected", http.StatusBadRequest)
			return
		}
		r.Body = http.MaxBytesReader(w, r.Body, 2*1024*1024)
		proxy.ServeHTTP(w, r)
	}), nil
}

// Share the existing TLS listener without forwarding screen/login/file requests to MCP.
func sharedMCP(screen http.Handler, target, host, key string) (http.Handler, error) {
	mcp, err := mcpProxy(target, host, key)
	if err != nil {
		return nil, err
	}
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/files", "/files/open", "/files/api", "/files/client.js", "/files/download",
			"/mcp", "/authorize", "/connect", "/token", "/register", "/revoke",
			"/.well-known/oauth-authorization-server", "/.well-known/oauth-protected-resource", "/.well-known/oauth-protected-resource/mcp":
			mcp.ServeHTTP(w, r)
		default:
			screen.ServeHTTP(w, r)
		}
	}), nil
}

func serveMCP(s *tsnet.Server, name string) {
	backend := os.Getenv("RC_MCP_BACKEND")
	if backend == "" {
		return
	}
	port := os.Getenv("RC_MCP_PUBLIC_PORT")
	if port != "8443" && port != "10000" {
		fmt.Println("MCP_ERROR=Unsupported MCP Funnel port")
		return
	}
	host := net.JoinHostPort(name, port)
	handler, err := mcpProxy(backend, host, os.Getenv("RC_MCP_PROXY_KEY"))
	if err != nil {
		fmt.Println("MCP_ERROR=Invalid MCP proxy settings")
		return
	}
	// Funnel failure must not terminate the existing screen listener or reset its identity.
	for {
		ln, err := s.ListenFunnel("tcp", ":"+port)
		if err != nil {
			fmt.Println("MCP_ERROR=MCP Funnel open failed; retrying")
			time.Sleep(30 * time.Second)
			continue
		}
		server := &http.Server{Handler: handler, ReadHeaderTimeout: 10 * time.Second, IdleTimeout: 90 * time.Second, MaxHeaderBytes: 16384,
			ConnContext: func(ctx context.Context, c net.Conn) context.Context {
				if ip := funnelClientAddr(c); ip != "" {
					return context.WithValue(ctx, clientAddrKey{}, ip)
				}
				return ctx
			}}
		fmt.Printf("MCP_URL=https://%s\n", host)
		os.Stdout.Sync()
		err = server.Serve(ln)
		ln.Close()
		fmt.Println("MCP_ERROR=MCP Funnel stopped; retrying")
		time.Sleep(30 * time.Second)
	}
}
