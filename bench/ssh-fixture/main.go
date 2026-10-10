// A loopback-only SSH fixture for interoperability tests. Never shipped with the application.
package main

import (
    "bytes"
    "crypto/rand"
    "crypto/rsa"
    "crypto/x509"
    "encoding/json"
    "encoding/pem"
    "fmt"
    "io"
    "net"
    "os"
    "path/filepath"
    "strconv"
    "sync"
    "golang.org/x/crypto/ssh"
)

type forward struct { Host string; Port uint32 }
type direct struct { Host string; Port uint32; Origin string; OriginPort uint32 }

func must(err error) { if err != nil { panic(err) } }

func main() {
    if len(os.Args) != 2 { panic("fixture requires isolated output directory") }
    folder := os.Args[1]
    must(os.MkdirAll(folder, 0700))
    serverKey, err := rsa.GenerateKey(rand.Reader, 3072); must(err)
    clientKey, err := rsa.GenerateKey(rand.Reader, 3072); must(err)
    signer, err := ssh.NewSignerFromKey(serverKey); must(err)
    clientSigner, err := ssh.NewSignerFromKey(clientKey); must(err)
    keyPath := filepath.Join(folder, "test-client.pem")
    must(os.WriteFile(keyPath, pem.EncodeToMemory(&pem.Block{Type:"RSA PRIVATE KEY", Bytes:x509.MarshalPKCS1PrivateKey(clientKey)}), 0600))
    config := &ssh.ServerConfig{PublicKeyCallback:func(meta ssh.ConnMetadata, key ssh.PublicKey)(*ssh.Permissions,error){
        if meta.User()=="test" && bytes.Equal(key.Marshal(),clientSigner.PublicKey().Marshal()) { return nil,nil }
        return nil,fmt.Errorf("test key required")
    }}
    config.AddHostKey(signer)
    echo, err := net.Listen("tcp4","127.0.0.1:0"); must(err)
    defer echo.Close()
    go func(){ for { c,e:=echo.Accept(); if e!=nil{return}; go func(){defer c.Close(); _,_=io.Copy(c,c)}() } }()
    listener, err := net.Listen("tcp4","127.0.0.1:0"); must(err)
    defer listener.Close()
    known := fmt.Sprintf("[127.0.0.1]:%d %s", listener.Addr().(*net.TCPAddr).Port, ssh.MarshalAuthorizedKey(signer.PublicKey()))
    must(os.WriteFile(filepath.Join(folder,"known_hosts"), []byte(known),0600))
    wrong := fmt.Sprintf("[127.0.0.1]:%d %s", listener.Addr().(*net.TCPAddr).Port, ssh.MarshalAuthorizedKey(clientSigner.PublicKey()))
    must(os.WriteFile(filepath.Join(folder,"wrong_known_hosts"), []byte(wrong),0600))
    info,_ := json.Marshal(map[string]any{"sshPort":listener.Addr().(*net.TCPAddr).Port,"echoPort":echo.Addr().(*net.TCPAddr).Port,"keyPath":keyPath,"hostKeyFingerprint":ssh.FingerprintSHA256(signer.PublicKey())})
    must(os.WriteFile(filepath.Join(folder,"info.json"),info,0600))
    for { c,e:=listener.Accept(); if e!=nil{return}; go serve(c,config) }
}

func serve(raw net.Conn, config *ssh.ServerConfig){
    conn, channels, requests, err := ssh.NewServerConn(raw,config)
    if err!=nil { raw.Close(); return }
    defer conn.Close()
    var mu sync.Mutex
    listeners:=map[uint32]net.Listener{}
    defer func(){mu.Lock();defer mu.Unlock();for _,l:=range listeners{l.Close()}}()
    go func(){ for req:=range requests {
        var f forward
        switch req.Type {
        case "tcpip-forward":
            if ssh.Unmarshal(req.Payload,&f)!=nil || f.Host!="127.0.0.1" {req.Reply(false,nil);continue}
            l,e:=net.Listen("tcp4",net.JoinHostPort(f.Host,strconv.Itoa(int(f.Port))))
            if e!=nil{req.Reply(false,nil);continue}
            bound:=uint32(l.Addr().(*net.TCPAddr).Port)
            mu.Lock();listeners[bound]=l;mu.Unlock()
            req.Reply(true,ssh.Marshal(struct{Port uint32}{bound}))
            go func(){for{c,e:=l.Accept();if e!=nil{return};go func(){
                addr:=c.RemoteAddr().(*net.TCPAddr)
                payload:=direct{f.Host,bound,addr.IP.String(),uint32(addr.Port)}
                ch,r,e:=conn.OpenChannel("forwarded-tcpip",ssh.Marshal(payload))
                if e!=nil{c.Close();return};go ssh.DiscardRequests(r);bridge(c,ch)
            }()}}()
        case "cancel-tcpip-forward":
            if ssh.Unmarshal(req.Payload,&f)!=nil{req.Reply(false,nil);continue}
            mu.Lock();l,ok:=listeners[f.Port];if ok{l.Close();delete(listeners,f.Port)};mu.Unlock()
            req.Reply(ok,nil)
        default: req.Reply(req.Type=="keepalive@openssh.com",nil)
        }
    }}()
    for incoming:=range channels {
        if incoming.ChannelType()!="direct-tcpip" {incoming.Reject(ssh.UnknownChannelType,"forwarding only");continue}
        var d direct
        if ssh.Unmarshal(incoming.ExtraData(),&d)!=nil || d.Host!="127.0.0.1" {incoming.Reject(ssh.Prohibited,"loopback only");continue}
        c,e:=net.Dial("tcp4",net.JoinHostPort(d.Host,strconv.Itoa(int(d.Port))))
        if e!=nil{incoming.Reject(ssh.ConnectionFailed,"target unavailable");continue}
        ch,r,e:=incoming.Accept();if e!=nil{c.Close();continue}
        go ssh.DiscardRequests(r);go bridge(c,ch)
    }
}

func bridge(c net.Conn,ch ssh.Channel){
    defer c.Close();defer ch.Close()
    done:=make(chan struct{})
    go func(){_,_=io.Copy(ch,c);_=ch.CloseWrite();close(done)}()
    _,_=io.Copy(c,ch)
    if tcp,ok:=c.(*net.TCPConn);ok{_=tcp.CloseWrite()}
    <-done
}
