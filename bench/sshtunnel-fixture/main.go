// 실제 SSHTunnel 서버를 일회용 계정과 루프백 포트로만 실행한다.
package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"net"
	"net/http"
	"os"
	"path/filepath"

	"ontech/sshtunnel/internal/server"
	"ontech/sshtunnel/pkg/tunnel"
)

func freeAddress() string {
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		panic(err)
	}
	defer l.Close()
	return l.Addr().String()
}
func main() {
	if err := run(os.Args[1]); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
func run(dir string) error {
	store, err := server.NewStore(filepath.Join(dir, "relay-data"))
	if err != nil {
		return err
	}
	defer store.Close()
	accessKey, err := tunnel.NewKey()
	if err != nil {
		return err
	}
	hash := sha256.Sum256([]byte(accessKey))
	cfg := store.Config()
	cfg.Listen, cfg.AdminListen = freeAddress(), freeAddress()
	cfg.Broker.Enabled = true
	cfg.Web = server.WebConfig{Enabled: true, Listen: freeAddress(), BaseDomain: "tunnel.example.test", Scheme: "https", MaxBodyMB: 128}
	cfg.Users = []server.User{{Name: "test", Enabled: true, AllowTunnel: true, AccessKeyHash: hex.EncodeToString(hash[:]), MaxConnections: 8}}
	if err = store.Save(cfg); err != nil {
		return err
	}
	relay := server.New(store)
	defer relay.Close()
	if err = relay.Start(); err != nil {
		return err
	}
	control, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return err
	}
	defer control.Close()
	_, sshPort, _ := net.SplitHostPort(relay.Addr())
	_, gatewayPort, _ := net.SplitHostPort(relay.WebAddr())
	_, controlPort, _ := net.SplitHostPort(control.Addr().String())
	info, _ := json.Marshal(map[string]string{"sshPort": sshPort, "gatewayPort": gatewayPort, "controlPort": controlPort, "accessKey": accessKey, "fingerprint": relay.Fingerprint()})
	if err = os.WriteFile(filepath.Join(dir, "info.json"), info, 0600); err != nil {
		return err
	}
	mux := http.NewServeMux()
	mux.HandleFunc("POST /disconnect", func(w http.ResponseWriter, r *http.Request) { relay.DisconnectAll(); w.WriteHeader(204) })
	mux.HandleFunc("GET /tunnels", func(w http.ResponseWriter, r *http.Request) { _ = json.NewEncoder(w).Encode(relay.TunnelViews()) })
	return http.Serve(control, mux)
}
