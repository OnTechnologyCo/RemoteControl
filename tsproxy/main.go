// tsproxy - FileSharer 의 고속 외부망 터널.
//
// tsnet 으로 Tailscale 노드를 이 프로세스 안에 띄우고, Funnel(공개 HTTPS)로 들어온
// 요청을 로컬 FileSharer 서버로 넘긴다.
//
//   - TUN 드라이버도, 관리자 권한도, 시스템 서비스도 필요 없다 (유저스페이스 WireGuard).
//   - 상태 폴더를 고정해 늘 같은 노드/같은 이름으로 붙는다 (DNS 이름이 안 바뀐다).
//   - TLS 는 이 프로세스에서 끝난다. Tailscale 은 SNI 만 보고 라우팅하므로 내용을 못 본다.
//
// 표준출력 규약 (부모 프로세스인 FileSharer.exe 가 읽는다):
//
//	FUNNEL_URL=https://...   성공
//	TSPROXY_ERROR=<사람이 읽을 수 있는 한국어 사유>
package main

import (
	"context"
	"crypto/tls"
	"fmt"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"time"

	"tailscale.com/ipn"
	"tailscale.com/tsnet"
)

// 요청을 보낸 진짜 클라이언트 주소를 요청 문맥에 실어 나르기 위한 키.
type clientAddrKey struct{}

func fail(format string, a ...any) {
	fmt.Printf("TSPROXY_ERROR=%s\n", fmt.Sprintf(format, a...))
	os.Stdout.Sync()
	os.Exit(1)
}

// tsnet 이 돌려주는 영어 오류를 사용자가 바로 조치할 수 있는 안내로 바꾼다.
func explain(err error) string {
	s := err.Error()

	switch {
	case strings.Contains(s, "HTTPS must be enabled"):
		return "Tailscale 관리 콘솔에서 HTTPS 인증서가 꺼져 있습니다.\n" +
			"https://login.tailscale.com/admin/dns 로 가서 맨 아래 " +
			"[HTTPS Certificates] 를 Enable 해 주세요."

	case strings.Contains(s, "funnel") && strings.Contains(s, "attribute"):
		return "Tailscale 정책에 Funnel 권한이 없습니다.\n" +
			"https://login.tailscale.com/admin/acls 에서 우측 [Funnel] 패널의 " +
			"[Add Funnel to policy] 를 눌러 주세요."

	case strings.Contains(s, "advertise-tags") || strings.Contains(s, "requested tags"):
		return "Trust credential 에 태그가 지정되지 않았거나, 정책에 태그 소유자가 없습니다.\n" +
			"https://login.tailscale.com/admin/acls 의 tagOwners 에 tag:filesharer 가 있는지,\n" +
			"그리고 Trust credential 생성 시 Tags 에 tag:filesharer 를 넣었는지 확인해 주세요."

	case strings.Contains(s, "invalid key") || strings.Contains(s, "unauthorized") ||
		strings.Contains(s, "expired") || strings.Contains(s, "not found"):
		return "인증 키가 유효하지 않거나 만료되었습니다.\n" +
			"https://login.tailscale.com/admin/settings/keys 에서 새로 발급해 주세요.\n" +
			"(Reusable 과 Ephemeral 을 켜 두면 편합니다)"
	}

	return s
}

// 접속자의 진짜 주소를 커넥션에서 꺼낸다.
//
// ListenFunnel 은 마지막에 tls.NewListener 로 한 번 더 감싸기 때문에,
// 여기 들어오는 것은 *tls.Conn 이고 그 안쪽에 *ipn.FunnelConn 이 있다.
// 한 겹 벗기지 않으면 타입 검사가 실패해서, 결국 Tailscale 중계 노드의
// 내부 주소(fd7a:115c:a1e0:...)만 기록에 남게 된다.
func funnelClientAddr(c net.Conn) string {
	for i := 0; i < 4 && c != nil; i++ {
		if fc, ok := c.(*ipn.FunnelConn); ok {
			if fc.Src.IsValid() {
				return fc.Src.Addr().String()
			}
			return ""
		}

		tc, ok := c.(*tls.Conn)
		if !ok {
			return ""
		}
		c = tc.NetConn()
	}
	return ""
}

// 갓 만들어진 공개 이름이 실제로 조회 가능해질 때까지 기다린다.
//
// 왜 필요한가:
//
//	Funnel 을 열면 그 순간 공개 DNS 이름이 만들어지지만, 권한 서버에 반영되고
//	퍼지기까지 시간이 걸린다. 그 전에 누군가 그 이름을 조회하면 통신사/회사 DNS 가
//	"그런 이름 없음(NXDOMAIN)" 을 캐시해 버리고, 그 DNS 를 쓰는 사람 전부가
//	한동안 접속하지 못한다. 실제로 KT DNS 에서 이 현상을 겪었다.
//
//	그런데 그 이른 조회를 한 것이 바로 우리 자신이었다. 링크를 만들자마자
//	"열리는지" 확인하느라 이름을 조회했고, 그것이 캐시를 오염시켰다.
//
//	그래서 주소를 알려주기 전에 권한 네임서버에 직접 물어 레코드가 올라온 것을
//	확인한다. 권한 서버 조회는 중간 캐시를 거치지 않으므로 오염을 만들지 않는다.
func waitPublicDNS(ctx context.Context, fqdn string) {
	servers := authoritativeServers(ctx, fqdn)
	if len(servers) == 0 {
		return // 확인할 방법이 없으면 그냥 진행한다
	}

	// 실측으로 권한 서버 반영은 몇 초면 끝난다. 그보다 오래 걸리면 기다려도
	// 나아지지 않으므로 그냥 진행한다 (개통이 하염없이 길어지지 않게).
	deadline := time.Now().Add(30 * time.Second)
	for time.Now().Before(deadline) {
		for _, ns := range servers {
			if lookupAt(ctx, ns, fqdn) {
				// 권한 서버에 올라왔다. 이제 누가 조회해도 정상 응답을 받는다.
				return
			}
		}
		time.Sleep(2 * time.Second)
	}
}

// 이 이름을 관장하는 권한 네임서버 주소들. (예: ts.net 의 NS)
// 이 조회는 이미 널리 알려진 이름이라 캐시를 오염시키지 않는다.
func authoritativeServers(ctx context.Context, fqdn string) []string {
	labels := strings.Split(strings.TrimSuffix(fqdn, "."), ".")
	if len(labels) < 2 {
		return nil
	}
	zone := labels[len(labels)-2] + "." + labels[len(labels)-1]

	var out []string
	for _, pub := range []string{"8.8.8.8:53", "1.1.1.1:53"} {
		c, cancel := context.WithTimeout(ctx, 6*time.Second)
		nss, err := resolverAt(pub).LookupNS(c, zone)
		cancel()
		if err != nil || len(nss) == 0 {
			continue
		}

		for _, ns := range nss {
			c2, cancel2 := context.WithTimeout(ctx, 6*time.Second)
			ips, err := resolverAt(pub).LookupHost(c2, strings.TrimSuffix(ns.Host, "."))
			cancel2()
			if err != nil {
				continue
			}
			for _, ip := range ips {
				out = append(out, net.JoinHostPort(ip, "53"))
			}
		}

		if len(out) > 0 {
			return out
		}
	}
	return out
}

// 지정한 DNS 서버에 직접 물어 주소가 있는지 본다.
func lookupAt(ctx context.Context, server, fqdn string) bool {
	c, cancel := context.WithTimeout(ctx, 5*time.Second)
	defer cancel()

	addrs, err := resolverAt(server).LookupHost(c, fqdn)
	return err == nil && len(addrs) > 0
}

// 공개 주소가 실제로 HTTPS 응답을 줄 때까지 기다린다 (인증서 발급 포함).
//
// 중요한 점: 이 확인은 이 PC 의 DNS 설정을 쓰지 않는다.
// 사용자의 PC 가 통신사 DNS(예: KT)를 함께 쓰고 그 서버가 옛 정보를 물고 있으면,
// 링크는 멀쩡한데 확인만 계속 실패한다. 실제로 이 PC 에서 10번 중 9번 실패했다.
// 그래서 공개 DNS 로 직접 주소를 얻어 그 IP 로 붙는다.
func waitHTTPSReady(ctx context.Context, fqdn string) {
	tr := &http.Transport{
		DisableKeepAlives: true,
		DialContext: func(c context.Context, network, addr string) (net.Conn, error) {
			host, port, err := net.SplitHostPort(addr)
			if err != nil {
				return nil, err
			}

			ips, err := resolverAt("8.8.8.8:53").LookupHost(c, host)
			if err != nil || len(ips) == 0 {
				// 공개 DNS 도 안 되면 평소 방식으로 시도한다
				d := net.Dialer{Timeout: 8 * time.Second}
				return d.DialContext(c, network, addr)
			}

			var lastErr error
			for _, ip := range ips {
				d := net.Dialer{Timeout: 8 * time.Second}
				conn, err := d.DialContext(c, network, net.JoinHostPort(ip, port))
				if err == nil {
					return conn, nil
				}
				lastErr = err
			}
			return nil, lastErr
		},
	}

	// 아직 준비 전이면 어차피 실패한다. 오래 물고 있지 말고 짧게 끊고 자주 확인해야
	// 준비되는 순간을 빨리 잡는다 (타임아웃이 길면 그만큼 개통이 늦어 보인다).
	client := &http.Client{Transport: tr, Timeout: 5 * time.Second,
		CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}

	deadline := time.Now().Add(75 * time.Second)
	for time.Now().Before(deadline) {
		req, err := http.NewRequest("GET", "https://"+fqdn+"/api/network-info", nil)
		if err != nil {
			return
		}

		resp, err := client.Do(req.WithContext(ctx))
		if err == nil {
			resp.Body.Close()
			// 응답이 왔다는 것 자체가 DNS + TLS + 인증서가 모두 준비됐다는 뜻이다.
			return
		}

		time.Sleep(700 * time.Millisecond)
	}
}

func resolverAt(server string) *net.Resolver {
	return &net.Resolver{
		PreferGo: true,
		Dial: func(ctx context.Context, network, _ string) (net.Conn, error) {
			d := net.Dialer{Timeout: 5 * time.Second}
			return d.DialContext(ctx, network, server)
		},
	}
}

var startedAt = time.Now()

// 단계별 소요 시간을 찍는다. 부모 프로세스는 FUNNEL_URL / TSPROXY_ERROR 만 읽으므로
// 이 줄들은 그냥 무시된다. 개통이 느릴 때 어디서 걸리는지 보려고 남겨 둔다.
func stage(name string) {
	fmt.Printf("TSPROXY_STAGE=%s %.1f\n", name, time.Since(startedAt).Seconds())
	os.Stdout.Sync()
}

func main() {
	authKey := os.Getenv("TS_AUTHKEY")
	if authKey == "" {
		fail("인증 키가 지정되지 않았습니다.")
	}

	backend := os.Getenv("BACKEND")
	if backend == "" {
		backend = "http://127.0.0.1:3000"
	}
	target, err := url.Parse(backend)
	if err != nil {
		fail("로컬 서버 주소가 잘못되었습니다: %v", err)
	}

	hostname := os.Getenv("TS_HOSTNAME")
	if hostname == "" {
		hostname = "filesharer"
	}

	// 상태 폴더를 고정한다.
	//
	// 예전에는 실행할 때마다 임시 폴더를 새로 만들었다. 그러면 장비 키도 매번 새로
	// 생겨서 Tailscale 은 이것을 늘 "처음 보는 장비" 로 취급했고, 이름이 filesharer-pc,
	// -1, -2 ... 로 계속 늘어났다.
	//
	// 이름이 매번 바뀐다는 것은 공개 DNS 이름도 매번 새로 만들어진다는 뜻이다.
	// 그 이름은 갓 만들어져 아직 퍼지기 전이라, 링크를 받자마자 열면 DNS 가 "그런 이름
	// 없음(NXDOMAIN)" 을 돌려주고, 회사/ISP DNS 서버가 그 답을 몇 분간 캐시해 버린다.
	// 그 뒤로는 레코드가 생겨도 캐시가 만료될 때까지 아무도 접속하지 못한다.
	// ("한참 뒤에는 열리더라" 의 정체가 이것이다.)
	//
	// 상태를 유지하면 늘 같은 장비, 같은 이름, 같은 DNS 레코드를 쓰게 되어
	// 두 번째 공유부터는 이름을 새로 알릴 일 자체가 없다.
	dir := os.Getenv("TS_STATEDIR")
	if dir == "" {
		dir = filepath.Join(os.TempDir(), "fstsnet-state")
	}
	if err := os.MkdirAll(dir, 0700); err != nil {
		fail("상태 폴더를 만들지 못했습니다: %v", err)
	}

	// 노드를 남길지. 남겨 두면 DNS 레코드가 유지되어 다음 공유가 곧바로 열린다.
	ephemeral := os.Getenv("TS_EPHEMERAL") == "1"

	s := &tsnet.Server{
		Dir:       dir,
		Hostname:  hostname,
		AuthKey:   authKey,
		Ephemeral: ephemeral,
		Logf:      func(string, ...any) {},
	}

	// Trust credential(OAuth, tskey-client-...) 로 붙을 때는 태그가 필수다.
	// tsnet 이 시크릿을 짧은 auth key 로 바꿀 때 이 태그를 함께 요청한다
	// (태그 없이 주면 "oauth authkeys require --advertise-tags" 로 거절당한다).
	// 일반 auth key 에는 태그를 붙이지 않는다 — 그 키가 태그 소유자가 아니면 오히려 실패한다.
	if strings.HasPrefix(strings.ToLower(authKey), "tskey-client-") {
		tags := os.Getenv("TS_TAGS")
		if tags == "" {
			tags = "tag:filesharer"
		}

		for _, t := range strings.Split(tags, ",") {
			if t = strings.TrimSpace(t); t != "" {
				s.AdvertiseTags = append(s.AdvertiseTags, t)
			}
		}
	}

	defer s.Close()

	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()

	stage("start")

	st, err := s.Up(ctx)
	if err != nil {
		fail("%s", explain(err))
	}
	stage("tailnet-up")

	ln, err := s.ListenFunnel("tcp", ":443")
	if err != nil {
		fail("%s", explain(err))
	}
	defer ln.Close()
	stage("funnel-open")

	name := strings.TrimSuffix(st.Self.DNSName, ".")

	proxy := httputil.NewSingleHostReverseProxy(target)

	// 뒤쪽 FileSharer 서버가 "터널 경유" 로 인식해 내용을 암호화하도록,
	// 공개 호스트명을 그대로 넘겨 준다.
	inner := proxy.Director
	proxy.Director = func(r *http.Request) {
		inner(r)
		r.Host = name
		r.Header.Set("X-Forwarded-Host", name)
		r.Header.Set("X-Forwarded-Proto", "https")

		// 접속자 IP.
		//
		// 이 프록시가 보는 r.RemoteAddr 은 Tailscale 중계 노드의 내부 주소
		// (fd7a:115c:a1e0:... 로 시작하는 tailnet 전용 IPv6) 라서, 그대로 두면
		// 접속 기록에 누가 받아 갔는지가 아니라 중계 주소만 남는다.
		// tsnet 은 Funnel 연결을 ipn.FunnelConn 으로 감싸 주고 그 Src 에
		// 진짜 접속자 주소가 들어 있다. 그것을 여기서 실어 보낸다.
		if v, ok := r.Context().Value(clientAddrKey{}).(string); ok && v != "" {
			r.Header.Set("X-Forwarded-For", v)
			r.Header.Set("X-Real-IP", v)
		}
	}

	// 대용량 전송이 중간에 잘리지 않도록 넉넉히 잡는다.
	proxy.FlushInterval = 200 * time.Millisecond
	var publicHandler http.Handler = proxy
	shared := false
	if os.Getenv("RC_MCP_BACKEND") != "" && os.Getenv("RC_MCP_PUBLIC_PORT") == "443" {
		handler, err := sharedMCP(proxy, os.Getenv("RC_MCP_BACKEND"), name, os.Getenv("RC_MCP_PROXY_KEY"))
		if err != nil {
			fmt.Println("MCP_ERROR=Invalid shared MCP proxy settings")
		} else {
			publicHandler = handler
			shared = true
		}
	}

	srv := &http.Server{
		Handler:      publicHandler,
		ReadTimeout:  0,
		WriteTimeout: 0,
		IdleTimeout:  120 * time.Second,

		// 연결이 만들어질 때 진짜 접속자 주소를 문맥에 담아 둔다.
		ConnContext: func(ctx context.Context, c net.Conn) context.Context {
			if addr := funnelClientAddr(c); addr != "" {
				return context.WithValue(ctx, clientAddrKey{}, addr)
			}
			return ctx
		},
	}

	// 서비스를 먼저 시작한다.
	//
	// 아래 준비 확인은 자기 자신에게 HTTPS 요청을 보내는 것이라,
	// 서버가 돌기 전에 확인하면 아무도 응답하지 않아 그대로 시간만 흘러간다
	// (실제로 그렇게 만들었다가 81초를 버렸다).
	serveErr := make(chan error, 1)
	go func() { serveErr <- srv.Serve(ln) }()

	if shared {
		fmt.Printf("MCP_URL=https://%s\n", name)
		os.Stdout.Sync()
	} else if os.Getenv("RC_MCP_PUBLIC_PORT") != "443" {
		// Separate ports remain available. Failure must not stop the screen listener.
		go serveMCP(s, name)
	}

	// 주소를 알려주기 전에, 그 이름이 실제로 조회 가능해질 때까지 기다린다.
	// 이 확인을 건너뛰고 먼저 조회하면 통신사/회사 DNS 에 "없는 이름" 이 캐시되어
	// 그 DNS 를 쓰는 사람들이 한동안 접속하지 못하게 된다.
	waitPublicDNS(context.Background(), name)
	stage("dns-ready")

	// 인증서까지 준비되어 실제로 HTTPS 응답이 오는 것을 확인한다.
	// 이 확인은 공개 DNS 로 직접 하므로, 이 PC 가 쓰는 통신사 DNS 가 옛 정보를
	// 물고 있어도 영향을 받지 않는다.
	waitHTTPSReady(context.Background(), name)
	stage("https-ready")

	fmt.Printf("FUNNEL_URL=https://%s\n", name)
	os.Stdout.Sync()

	if err := <-serveErr; err != nil {
		fail("전송 중 오류: %v", err)
	}
}
