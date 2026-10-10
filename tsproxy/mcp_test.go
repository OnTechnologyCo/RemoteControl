package main

import (
	"context"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestMCPProxyBoundary(t *testing.T) {
	key := strings.Repeat("a", 43)
	upstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Host != "pc.test.ts.net:8443" || r.Header.Get("X-RC-Proxy-Key") != key || r.Header.Get("X-RC-Client-IP") != "198.51.100.4" || r.Header.Get("X-Forwarded-Proto") != "https" {
			t.Error("trusted proxy fields missing")
		}
		if r.Header.Get("Forwarded") != "" || r.Header.Get("X-Forwarded-For") != "" {
			t.Error("spoofed client metadata passed")
		}
		if r.Header.Get("Authorization") != "Bearer fixture" || r.Header.Get("Origin") != "https://pc.test.ts.net:8443" {
			t.Error("OAuth headers changed")
		}
		if r.URL.RequestURI() != "/mcp?fixture=1" {
			t.Error("MCP path changed")
		}
		w.Header().Set("WWW-Authenticate", "Bearer fixture")
		w.WriteHeader(401)
		io.WriteString(w, "fixture")
	}))
	defer upstream.Close()
	handler, err := mcpProxy(upstream.URL, "pc.test.ts.net:8443", key)
	if err != nil {
		t.Fatal(err)
	}
	r := httptest.NewRequest("POST", "https://pc.test.ts.net:8443/mcp?fixture=1", strings.NewReader("{}"))
	r = r.WithContext(context.WithValue(r.Context(), clientAddrKey{}, "198.51.100.4"))
	for k, v := range map[string]string{"Forwarded": "evil", "X-Forwarded-For": "evil", "X-RC-Proxy-Key": "evil", "X-RC-Client-IP": "evil", "X-Forwarded-Proto": "http", "Authorization": "Bearer fixture", "Origin": "https://pc.test.ts.net:8443"} {
		r.Header.Set(k, v)
	}
	w := httptest.NewRecorder()
	handler.ServeHTTP(w, r)
	if w.Code != 401 || w.Header().Get("WWW-Authenticate") != "Bearer fixture" {
		t.Fatal("auth response changed")
	}
	r = httptest.NewRequest("GET", "https://wrong.test/mcp", nil)
	w = httptest.NewRecorder()
	handler.ServeHTTP(w, r)
	if w.Code != 400 {
		t.Fatal("wrong public host accepted")
	}
	for _, bad := range []string{"http://192.168.1.1:5901", "http://127.0.0.1:80", "https://127.0.0.1:59335", "http://user:pw@127.0.0.1:59335", "http://127.0.0.1:59335/private"} {
		if _, err := mcpProxy(bad, "pc.test.ts.net:8443", key); err == nil {
			t.Fatalf("unsafe backend allowed: %s", bad)
		}
	}
}

func TestSharedDefaultHTTPS(t *testing.T) {
	key := strings.Repeat("b", 43)
	upstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Host != "pc.test.ts.net" || r.Header.Get("X-RC-Proxy-Key") != key || r.Header.Get("X-Forwarded-Proto") != "https" {
			t.Error("canonical default-port proxy headers missing")
		}
		w.WriteHeader(401)
	}))
	defer upstream.Close()
	screen := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("X-RC-Proxy-Key") == key {
			t.Error("MCP proxy key leaked into screen request")
		}
		w.Header().Set("Screen-Path", r.URL.Path)
		w.WriteHeader(202)
	})
	handler, err := sharedMCP(screen, upstream.URL, "pc.test.ts.net", key)
	if err != nil {
		t.Fatal(err)
	}
	for _, path := range []string{"/mcp", "/authorize", "/connect", "/token", "/register", "/revoke", "/.well-known/oauth-authorization-server", "/.well-known/oauth-protected-resource", "/.well-known/oauth-protected-resource/mcp"} {
		for _, authority := range []string{"pc.test.ts.net", "pc.test.ts.net:443"} {
			r := httptest.NewRequest("POST", "https://"+authority+path+"?fixture=1", strings.NewReader("{}"))
			w := httptest.NewRecorder()
			handler.ServeHTTP(w, r)
			if w.Code != 401 {
				t.Errorf("MCP route escaped authentication: %s %s", authority, path)
			}
		}
	}
	for _, path := range []string{"/", "/ping", "/login", "/logout", "/ws", "/fs/list", "/viewer.js", "/mcp-other"} {
		r := httptest.NewRequest("GET", "https://pc.test.ts.net"+path, nil)
		w := httptest.NewRecorder()
		handler.ServeHTTP(w, r)
		if w.Code != 202 || w.Header().Get("Screen-Path") != path {
			t.Errorf("Screen route changed: %s", path)
		}
	}
	r := httptest.NewRequest("GET", "https://attacker.test/mcp", nil)
	w := httptest.NewRecorder()
	handler.ServeHTTP(w, r)
	if w.Code != 400 {
		t.Fatal("Shared listener accepted foreign MCP host")
	}
}
