using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace RemoteControlSetup
{
    internal sealed class LanInterfaceAddress
    {
        internal readonly string Address, InterfaceName, Network;
        internal LanInterfaceAddress(string address, string name, string network)
        { Address=address; InterfaceName=name; Network=network; }
        public override string ToString()
        { return InterfaceName+" · "+Address+(Network.Length==0?"":" ("+Network+")"); }
    }

    internal static class LanWebMcp
    {
        internal const int DefaultPort=443, DefaultGateway=59338, MaxAddresses=32;
        internal const string CaFileName="RemoteControl-LAN-CA.crt";
        internal static string PublicCaPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "RemoteControlLanWebMcp", "certificates", CaFileName); }
        }

        internal static bool IsLanAddress(string value)
        {
            IPAddress ip;
            if(!IPAddress.TryParse(value, out ip) || ip.AddressFamily!=AddressFamily.InterNetwork || ip.ToString()!=value) return false;
            byte[] bytes=ip.GetAddressBytes();
            return bytes[0]!=0 && bytes[0]!=127 && bytes[0]<224 && !(bytes[0]==169 && bytes[1]==254);
        }

        internal static string Subnet(IPAddress address, IPAddress mask)
        {
            if(address==null || mask==null || address.AddressFamily!=AddressFamily.InterNetwork || mask.AddressFamily!=AddressFamily.InterNetwork) return "";
            byte[] a=address.GetAddressBytes(), m=mask.GetAddressBytes(); int prefix=0; bool zero=false;
            for(int i=0;i<4;i++) {
                a[i]=(byte)(a[i]&m[i]);
                for(int bit=7;bit>=0;bit--) {
                    if((m[i]&(1<<bit))==0) zero=true;
                    else { if(zero) return ""; prefix++; }
                }
            }
            return prefix>=8 ? new IPAddress(a).ToString()+"/"+prefix : "";
        }

        internal static LanInterfaceAddress[] Interfaces()
        {
            var found=new List<LanInterfaceAddress>();
            try {
                foreach(NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces()) {
                    if(nic.OperationalStatus!=OperationalStatus.Up || nic.NetworkInterfaceType==NetworkInterfaceType.Loopback) continue;
                    try {
                        foreach(UnicastIPAddressInformation ip in nic.GetIPProperties().UnicastAddresses) {
                            string address=ip.Address.ToString(); if(!IsLanAddress(address)) continue;
                            string network=""; try { network=Subnet(ip.Address, ip.IPv4Mask); } catch(NetworkInformationException) { }
                            found.Add(new LanInterfaceAddress(address, nic.Name.Replace('\r',' ').Replace('\n',' '), network));
                        }
                    } catch(NetworkInformationException) { }
                }
            } catch(NetworkInformationException) { }
            found.Sort(delegate(LanInterfaceAddress a, LanInterfaceAddress b) {
                int name=string.Compare(a.InterfaceName,b.InterfaceName,StringComparison.CurrentCulture);
                return name!=0 ? name : string.CompareOrdinal(a.Address,b.Address);
            });
            return found.ToArray();
        }

        internal static string DefaultAddress()
        {
            // 네트워크가 잠시 끊긴 상태에서 첫 NIC로 바꾸면 저장한 인증 주소까지 바뀐다.
            string saved=WebMcp.ReadSetting("mcp_lan_address", ""); if(saved.Length>0) return saved;
            LanInterfaceAddress[] interfaces=Interfaces(); return interfaces.Length>0 ? interfaces[0].Address : "";
        }

        internal static string AdditionalAddresses(string primary, string allAddresses)
        {
            var additional=new List<string>();
            foreach(string part in (allAddresses??"").Split(new[]{',','\r','\n'},StringSplitOptions.RemoveEmptyEntries)) {
                string address=part.Trim();
                if(address.Length>0 && address!=primary && !additional.Contains(address)) additional.Add(address);
            }
            return string.Join(",",additional.ToArray());
        }

        internal static string DefaultAdditionalAddresses(string primary)
        { return AdditionalAddresses(primary,WebMcp.ReadSetting("mcp_lan_addresses", "")); }

        internal static string[] PromoteAddress(string primary,string additional,string selected)
        {
            // 기본 IP 선택은 다른 랜카드를 해제하는 동작이 아니므로 선택한 전체 주소를 보존한다.
            if(string.IsNullOrWhiteSpace(primary)) return Addresses(selected,additional);
            string[] existing=Addresses(primary,additional);
            return Addresses(selected,string.Join(",",existing));
        }

        internal static string[] Addresses(string primary, string additional)
        {
            var values=new List<string>();
            if(!IsLanAddress(primary)) throw new ArgumentException("이 PC의 LAN IPv4 주소를 입력하세요. 루프백·자동 할당·멀티캐스트 주소는 사용할 수 없습니다.");
            values.Add(primary);
            if((additional??"").Length>2048) throw new ArgumentException("추가 서버 IP는 기본 IP를 포함해 최대 32개입니다.");
            foreach(string part in (additional??"").Split(new[]{',','\r','\n'},StringSplitOptions.RemoveEmptyEntries)) {
                string value=part.Trim();
                if(!IsLanAddress(value)) throw new ArgumentException("추가 서버 IP를 IPv4 형식으로 입력하세요: "+value);
                if(!values.Contains(value)) values.Add(value);
                if(values.Count>MaxAddresses) throw new ArgumentException("서버 IP는 기본 IP를 포함해 최대 32개입니다.");
            }
            return values.ToArray();
        }

        internal static string[] MissingAddresses(string[] addresses, LanInterfaceAddress[] interfaces)
        {
            var missing=new List<string>(); var local=new HashSet<string>(StringComparer.Ordinal);
            foreach(LanInterfaceAddress item in interfaces) local.Add(item.Address);
            foreach(string address in addresses) if(!local.Contains(address)) missing.Add(address);
            return missing.ToArray();
        }

        internal static string SuggestedNetworks(string[] addresses, LanInterfaceAddress[] interfaces)
        {
            var networks=new List<string>();
            foreach(string address in addresses) foreach(LanInterfaceAddress item in interfaces)
                if(item.Address==address && item.Network.Length>0 && !networks.Contains(item.Network)) networks.Add(item.Network);
            return string.Join(",",networks.ToArray());
        }

        internal static string DefaultNetworks(string address)
        {
            string saved=WebMcp.ReadSetting("mcp_lan_networks", ""); if(saved.Length>0) return saved;
            try { return SuggestedNetworks(Addresses(address,DefaultAdditionalAddresses(address)),Interfaces()); }
            catch(ArgumentException) { return ""; }
        }

        internal static string NormalizeNetworks(string value)
        {
            string[] entries=(value??"").Split(',');
            for(int i=0;i<entries.Length;i++) entries[i]=entries[i].Trim();
            return string.Join(",",entries);
        }

        internal static int ReadPort(string key,int fallback)
        { int value; return int.TryParse(WebMcp.ReadSetting(key,""),out value) && (value==443 || value>=1024) && value<=65535 ? value : fallback; }
        internal static string Origin(string address,int port)
        { return "https://"+address+(port==443?"":":"+port); }
        internal static string Origin(Options o) { return Origin(o.LanAddress,o.LanHttpsPort); }
        internal static string CaUrl(string origin) { return origin+"/ca/"+CaFileName; }

        internal static Options CopyConnection(Options o)
        {
            return new Options {LanWebMcp=o.LanWebMcp,LanAddress=o.LanAddress,LanAdditionalAddresses=o.LanAdditionalAddresses,
                LanHttpsPort=o.LanHttpsPort,LanGatewayPort=o.LanGatewayPort,LanNetworks=o.LanNetworks};
        }

        internal static bool SameConnection(Options a, Options b)
        {
            if(a==null || b==null || !a.LanWebMcp || !b.LanWebMcp) return false;
            try {
                return a.LanHttpsPort==b.LanHttpsPort && a.LanGatewayPort==b.LanGatewayPort &&
                    NormalizeNetworks(a.LanNetworks)==NormalizeNetworks(b.LanNetworks) &&
                    string.Join(",",Addresses(a.LanAddress,a.LanAdditionalAddresses))==string.Join(",",Addresses(b.LanAddress,b.LanAdditionalAddresses));
            } catch(ArgumentException) { return false; }
        }

        internal static Options ReadAppliedConnection()
        {
            if(WebMcp.ReadSetting("mcp_lan_enabled","0")!="1") return null;
            var o=new Options {LanWebMcp=true,LanAddress=WebMcp.ReadSetting("mcp_lan_address",""),
                LanHttpsPort=ReadPort("mcp_lan_https_port",DefaultPort),LanGatewayPort=ReadPort("mcp_lan_gateway",DefaultGateway),
                LanNetworks=WebMcp.ReadSetting("mcp_lan_networks","")};
            o.LanAdditionalAddresses=DefaultAdditionalAddresses(o.LanAddress);
            try { Addresses(o.LanAddress,o.LanAdditionalAddresses); } catch(ArgumentException) { return null; }
            return WebMcp.ReadSetting("mcp_lan_url","")==Origin(o) ? o : null;
        }

        internal static string ReadPublicCaHash() { return ReadPublicCaHash(PublicCaPath); }
        internal static string ReadPublicCaHash(string path)
        {
            var file=new FileInfo(path);
            if(!file.Exists || file.Length==0 || file.Length>65536) throw new IOException("공개 CA 인증서가 없습니다. LAN 설정을 적용한 후 다시 여세요.");
            byte[] bytes=File.ReadAllBytes(path);
            Match pem=Regex.Match(Encoding.ASCII.GetString(bytes).Trim(),@"\A-----BEGIN CERTIFICATE-----\s*([A-Za-z0-9+/=\r\n\t ]+)\s*-----END CERTIFICATE-----\z");
            if(!pem.Success) throw new IOException("공개 CA 인증서를 읽을 수 없습니다. LAN 설정을 다시 적용하세요.");
            try {
                using(var certificate=new X509Certificate2(Convert.FromBase64String(pem.Groups[1].Value))) {
                    bool isCa=false;
                    foreach(X509Extension extension in certificate.Extensions) {
                        if(extension.Oid.Value!="2.5.29.19") continue;
                        var constraint=new X509BasicConstraintsExtension(); constraint.CopyFrom(extension); isCa=constraint.CertificateAuthority;
                    }
                    if(!isCa || DateTime.Now<certificate.NotBefore || DateTime.Now>certificate.NotAfter)
                        throw new IOException("공개 CA가 유효한 CA 인증서인지 확인한 후 LAN 설정을 다시 적용하세요.");
                }
            } catch(CryptographicException error) { throw new IOException("공개 CA 인증서를 읽을 수 없습니다.",error); }
            catch(FormatException error) { throw new IOException("공개 CA 인증서 형식이 올바르지 않습니다.",error); }
            // 클라이언트의 Get-FileHash 및 기존 SHA256.txt와 같은 PEM 파일 바이트를 해시한다.
            using(SHA256 sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");
        }

        internal static void Validate(Options o)
        {
            if(!o.LanWebMcp) return;
            string[] addresses=Addresses(o.LanAddress,o.LanAdditionalAddresses);
            string[] missing=MissingAddresses(addresses,Interfaces());
            if(missing.Length>0) throw new ArgumentException("현재 연결된 랜카드에 없는 서버 IP입니다: "+string.Join(", ",missing)+"\n주소를 유지했으므로 해당 랜카드를 연결하거나 IP를 직접 수정한 뒤 적용하세요.");
            o.LanAdditionalAddresses=AdditionalAddresses(o.LanAddress,string.Join(",",addresses));
            if((o.LanHttpsPort!=443&&o.LanHttpsPort<1024)||o.LanHttpsPort>65535||o.LanGatewayPort<1024||o.LanGatewayPort>65535||o.LanHttpsPort==o.LanGatewayPort||o.LanHttpsPort==WebMcp.LanPort()||o.LanGatewayPort==WebMcp.LanPort()||o.LanHttpsPort==o.Port||o.LanGatewayPort==o.Port) throw new ArgumentException("LAN HTTPS·중계·MCP·화면 포트를 각각 다르게 지정하세요.");
            if(!o.LockScreen) throw new ArgumentException("LAN WebMCP에는 잠금 화면 제어 서비스가 필요합니다.");
            if(o.WebMcp&&(o.LanHttpsPort==o.WebGatewayPort||o.LanGatewayPort==o.WebPublicPort||o.LanGatewayPort==o.WebGatewayPort)) throw new ArgumentException("LAN 포트와 외부 WebMCP 포트를 다르게 지정하세요.");
            if(string.IsNullOrWhiteSpace(o.LanNetworks)||o.LanNetworks.Length>2048) throw new ArgumentException("LAN 접속 허용 IPv4 대역을 입력하세요.");
            o.LanNetworks=NormalizeNetworks(o.LanNetworks);
            string[] rows=o.LanNetworks.Split(','); if(rows.Length>32) throw new ArgumentException("LAN 허용 대역은 최대 32개입니다.");
            foreach(string row in rows) {
                string[] parts=row.Split('/'); int prefix=32; IPAddress ip;
                if(parts.Length>2||!IPAddress.TryParse(parts[0],out ip)||ip.AddressFamily!=AddressFamily.InterNetwork||ip.ToString()!=parts[0]||(parts.Length==2&&(!Regex.IsMatch(parts[1],@"\A\d{1,2}\z")||!int.TryParse(parts[1],out prefix)))||prefix<8||prefix>32) throw new ArgumentException("허용 대역은 IPv4 또는 IPv4/8~32 형식입니다.");
            }
        }

        private static string Arguments(Options o)
        { return " -Address \""+o.LanAddress+"\" -AdditionalAddresses \""+(o.LanAdditionalAddresses??"")+"\" -HttpsPort "+o.LanHttpsPort+" -GatewayPort "+o.LanGatewayPort+" -LanPort "+WebMcp.LanPort()+" -AllowedNetworks \""+o.LanNetworks+"\""; }
        internal static void Preflight(Options o,Installer.Say say)
        { if(o.LanWebMcp) WebMcp.RunScript(Path.Combine(o.Dir,"web-mcp","Install-LanWebMcp.ps1"),Arguments(o)+" -CheckOnly",say); }
        internal static void Activate(Options o,Installer.Say say)
        {
            WebMcp.RunScript(Path.Combine(o.Dir,"web-mcp","Install-LanWebMcp.ps1"),Arguments(o),say);
            string[] addresses=Addresses(o.LanAddress,o.LanAdditionalAddresses), origins=new string[addresses.Length];
            for(int i=0;i<addresses.Length;i++) origins[i]=Origin(addresses[i],o.LanHttpsPort);
            WebMcp.SaveSettings(Installer.SettingsPath,new Dictionary<string,string>{{"mcp_lan_enabled","1"},{"mcp_lan_address",o.LanAddress},
                {"mcp_lan_addresses",string.Join(",",addresses)},{"mcp_lan_https_port",o.LanHttpsPort.ToString()},{"mcp_lan_gateway",o.LanGatewayPort.ToString()},
                {"mcp_lan_networks",o.LanNetworks},{"mcp_lan_url",Origin(o)},{"mcp_lan_urls",string.Join(",",origins)}});
        }
        internal static void ConfigureExisting(Options o,Installer.Say say)
        {
            Validate(o);
            if(!o.LanWebMcp) { Disable(say); WebMcp.SaveSettings(Installer.SettingsPath,new Dictionary<string,string>{{"mcp_lan_enabled","0"},{"mcp_lan_url",""},{"mcp_lan_urls",""}}); return; }
            if(!Installer.ServiceRunning()||WebMcp.ReadSetting("mcp_enabled","0")!="1") throw new ArgumentException("잠금 화면 제어 서비스와 [LLM / MCP 설정]의 MCP 사용을 먼저 켜고 저장하세요.");
            Preflight(o,say); Activate(o,say);
        }
        internal static void Disable(Installer.Say say)
        { string script=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RemoteControlLanWebMcp","Disable-LanWebMcp.ps1"); if(File.Exists(script)) WebMcp.RunScript(script,"",say); }
    }

    internal enum LanSnippetKind { Guide, ClaudeCode, VsCode, Cursor, CodexCommand, CodexToml, Certificate, ClaudeSafe }

    internal static class SafeMcpRegistration
    {
        internal static string Build(string name,string url)
        {
            if(!Regex.IsMatch(name??"",@"\A[A-Za-z][A-Za-z0-9_-]{0,63}\z")) throw new ArgumentException("서버 이름은 영문으로 시작하는 1~64자입니다. 숫자만 있는 이름은 허용하지 않습니다.");
            Uri parsed;
            if(!Uri.TryCreate(url,UriKind.Absolute,out parsed)||parsed.Scheme!="https"||parsed.AbsolutePath!="/mcp"||!string.IsNullOrEmpty(parsed.UserInfo)||!string.IsNullOrEmpty(parsed.Query)||!string.IsNullOrEmpty(parsed.Fragment)||url.IndexOf('\'')>=0||url.IndexOf('\r')>=0||url.IndexOf('\n')>=0) throw new ArgumentException("올바른 HTTPS MCP 주소를 지정하세요.");
            return "& {\r\n"+Template.Replace("__RC_NAME__",name).Replace("__RC_URL__",url)+"\r\n}";
        }
        private const string Template=@"#requires -Version 5.1
# 이름/URL/콜백 포트를 분리하고, 등록 실패 뒤 다른 명령을 자동 실행하지 않습니다.
[CmdletBinding(PositionalBinding=$false, SupportsShouldProcess=$true)]
param(
    [ValidatePattern('\A[A-Za-z][A-Za-z0-9_-]{0,63}\z')]
    [string]$Name='__RC_NAME__',
    [string]$Url='__RC_URL__',
    [ValidateSet('local','project','user')][string]$Scope='local',
    [ValidateRange(1024,65535)][int]$CallbackPort,
    [switch]$CheckOnly
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0
# /register의 오류 문자열이나 OAuth client_id를 명령 인자로 만들지 않습니다.
if($Name -cnotmatch '\A[A-Za-z][A-Za-z0-9_-]{0,63}\z'){throw '서버 이름은 영문으로 시작하는 1~64자입니다. 숫자만 있는 이름은 허용하지 않습니다.'}
$target=$null
if($Url -cnotmatch '\Ahttps://(?:[A-Za-z0-9.-]+|\[[0-9A-Fa-f:]+\])(?::[0-9]{1,5})?/mcp\z' -or
   -not [Uri]::TryCreate($Url,[UriKind]::Absolute,[ref]$target) -or
   $target.Scheme -cne 'https' -or $target.AbsolutePath -cne '/mcp' -or
   $target.UserInfo -or $target.Query -or $target.Fragment -or $target.Port -lt 1){
    throw 'URL은 https://호스트[:포트]/mcp 전체 주소여야 합니다. 공백·인증정보·쿼리는 허용하지 않습니다.'
}
$Url=$target.AbsoluteUri
$origin=$target.GetLeftPart([UriPartial]::Authority)
[string[]]$registrationArgs=@('mcp','add','--transport','http','--scope',$Scope)
if($PSBoundParameters.ContainsKey('CallbackPort')){$registrationArgs+=@('--callback-port',[string]$CallbackPort)}
$registrationArgs+=@($Name,$Url)
Write-Host ('등록 대상: '+$Name+' | '+$Url+' | 범위: '+$Scope)
Write-Host ('실행 인자: '+(($registrationArgs|ForEach-Object{'""'+$_+'""'}) -join ' '))
if($CheckOnly){Write-Host '입력 검증 완료. 클라이언트 설정·인증정보는 변경하지 않았습니다.';return}
if(-not $PSCmdlet.ShouldProcess($Name+' -> '+$Url,'검증 후 Claude Code MCP 등록')){return}
$command=Get-Command claude -CommandType Application,ExternalScript -ErrorAction Stop|Select-Object -First 1
function Invoke-ClaudeArguments([string[]]$Arguments){
    # PowerShell 5.1도 종료 코드를 직접 검사합니다. stderr를 다음 명령에 전달하지 않습니다.
    $ErrorActionPreference='Continue'
    $PSNativeCommandUseErrorActionPreference=$false
    $lines=@(& $command.Source @Arguments 2>&1)
    $code=$global:LASTEXITCODE
    if($null -eq $code){throw 'Claude CLI 종료 코드를 확인할 수 없어 중단했습니다.'}
    $text=($lines|ForEach-Object{[string]$_}) -join ""`n""
    $text=[regex]::Replace($text,[string][char]27+'\[[0-9;]*[A-Za-z]','')
    return [pscustomobject]@{ExitCode=[int]$code;Text=$text}
}
function Matches-ExpectedServer([string]$Text){
    $type=[regex]::Match($Text,'(?im)^\s*Type:\s*(http|streamable-http)\s*$')
    $address=[regex]::Match($Text,'(?im)^\s*URL:\s*(\S+)\s*$')
    $actual=$null
    return $type.Success -and $address.Success -and
      [Uri]::TryCreate($address.Groups[1].Value,[UriKind]::Absolute,[ref]$actual) -and
      $actual.AbsoluteUri -ceq $Url
}
$help=Invoke-ClaudeArguments @('mcp','add','--help')
if($help.ExitCode -ne 0 -or $help.Text -notmatch '--transport' -or $help.Text -notmatch '--scope' -or
   ($PSBoundParameters.ContainsKey('CallbackPort') -and $help.Text -notmatch '--callback-port')){
    throw '현재 Claude CLI가 필요한 등록 옵션을 지원하는지 확인할 수 없습니다. 임의 대체 명령은 실행하지 않습니다.'
}
$existing=Invoke-ClaudeArguments @('mcp','get',$Name)
if($existing.ExitCode -eq 0){
    if(-not (Matches-ExpectedServer $existing.Text)){throw '같은 이름에 다른 URL/전송 방식이 있거나 CLI 출력 형식을 확인할 수 없습니다. 기존 항목을 삭제하거나 덮어쓰지 않았습니다.'}
    Write-Host '동일한 서버 설정이 이미 있습니다. 중복 등록하지 않았습니다. OAuth 인증 상태는 /mcp에서 확인하세요.'
    return
}
$notFound='\A\s*No MCP server found with name:\s*'+[regex]::Escape($Name)+'\s*\z'
if($existing.Text -notmatch $notFound){throw ('기존 서버 조회 실패(exit '+$existing.ExitCode+'). 없는 항목으로 간주하지 않고 중단했습니다.')}
function Read-Metadata([string]$Address){
    # TLS 검증을 끄거나 리디렉션을 따라가지 않습니다. 인증정보는 전송하지 않습니다.
    $response=Invoke-WebRequest -UseBasicParsing -Uri $Address -Method Get -TimeoutSec 10 -MaximumRedirection 0 -ErrorAction Stop
    if([int]$response.StatusCode -ne 200 -or $response.Content.Length -gt 65536){throw 'MCP 메타데이터 HTTP 상태 또는 크기가 올바르지 않습니다.'}
    return ($response.Content|ConvertFrom-Json -ErrorAction Stop)
}
$resource=Read-Metadata ($origin+'/.well-known/oauth-protected-resource')
$auth=Read-Metadata ($origin+'/.well-known/oauth-authorization-server')
if($resource.resource -cne $Url -or @($resource.authorization_servers).Count -ne 1 -or
   $resource.authorization_servers[0] -cne ($origin+'/') -or $auth.issuer -cne ($origin+'/') -or
   $auth.authorization_endpoint -cne ($origin+'/authorize') -or $auth.token_endpoint -cne ($origin+'/token') -or
   $auth.registration_endpoint -cne ($origin+'/register') -or
   @($auth.code_challenge_methods_supported) -notcontains 'S256'){
    throw 'MCP 주소와 인증 서버 설정이 일치하지 않습니다. 등록 명령을 실행하지 않았습니다.'
}
$result=Invoke-ClaudeArguments $registrationArgs
if($result.ExitCode -ne 0){throw ('MCP 등록 실패(exit '+$result.ExitCode+'). 재등록·삭제·로그인을 자동 실행하지 않았습니다. 해당 이름의 설정을 확인하세요.')}
$verified=Invoke-ClaudeArguments @('mcp','get',$Name)
if($verified.ExitCode -ne 0 -or -not (Matches-ExpectedServer $verified.Text)){
    throw '등록 후 서버 URL/전송 방식 확인에 실패했습니다. 성공 처리하지 않습니다. 자동 삭제는 하지 않으므로 해당 이름만 확인하세요.'
}
Write-Host '서버 설정 등록·검증 완료. 로그인 완료를 의미하지는 않습니다.'
Write-Host ('Claude Code의 /mcp에서 '+$Name+'을 선택해 인증하세요. 실패 시 숫자 포트나 오류 문자열을 서버 이름으로 재등록하지 마세요.')
";
    }
    internal static class LanConnectionInstructions
    {
        internal const string DefaultServerName="remotecontrol-pth-web";
        internal static string Build(Options applied, string address, string name, string caSha256, LanSnippetKind kind)
        {
            if(applied==null || !applied.LanWebMcp) throw new ArgumentException("LAN 설정을 먼저 적용하세요.");
            string[] addresses=LanWebMcp.Addresses(applied.LanAddress,applied.LanAdditionalAddresses);
            if(Array.IndexOf(addresses,address)<0) throw new ArgumentException("이 IP는 적용된 서버 주소가 아닙니다. LAN 설정에 추가한 뒤 적용하세요.");
            if(!Regex.IsMatch(name??"", @"\A[A-Za-z][A-Za-z0-9_-]{0,63}\z")) throw new ArgumentException("서버 이름은 영문으로 시작하는 영문·숫자·밑줄·하이픈 1~64자입니다. 숫자만 있는 이름은 사용할 수 없습니다.");
            if(!WebMcp.IsHttpsPort(applied.LanHttpsPort)) throw new ArgumentException("적용된 HTTPS 포트를 확인하세요.");
            if(!Regex.IsMatch(caSha256??"", @"\A[A-Fa-f0-9]{64}\z")) throw new ArgumentException("공개 CA SHA256을 확인한 후 복사하세요.");
            string origin=LanWebMcp.Origin(address,applied.LanHttpsPort), url=origin+"/mcp";
            string caInfo="- 공개 CA: "+LanWebMcp.CaUrl(origin)+"\r\n- 공개 CA SHA256: "+caSha256.ToUpperInvariant();
            if(kind==LanSnippetKind.ClaudeSafe) return SafeMcpRegistration.Build(name,url);
            if(kind==LanSnippetKind.ClaudeCode) return "claude mcp add --transport http --scope local \""+name+"\" \""+url+"\"";
            if(kind==LanSnippetKind.VsCode) return "{\r\n  \"servers\": {\r\n    \""+name+"\": {\r\n      \"type\": \"http\",\r\n      \"url\": \""+url+"\"\r\n    }\r\n  }\r\n}";
            if(kind==LanSnippetKind.Cursor) return "{\r\n  \"mcpServers\": {\r\n    \""+name+"\": {\r\n      \"url\": \""+url+"\"\r\n    }\r\n  }\r\n}";
            if(kind==LanSnippetKind.CodexCommand) return "codex mcp add "+name+" --url "+url+"\r\ncodex mcp login "+name;
            if(kind==LanSnippetKind.CodexToml) return "[mcp_servers."+name+"]\r\nurl = \""+url+"\"";
            if(kind==LanSnippetKind.Certificate) return caInfo;
            var text=new StringBuilder();
            text.AppendLine("LAN WebMCP 연결 안내").AppendLine();
            text.AppendLine("- 서버 이름: "+name).AppendLine("- 선택한 MCP 주소: "+url).AppendLine("- 인증 주소: "+origin+"/authorize").AppendLine(caInfo).AppendLine();
            text.AppendLine("적용된 서버 IP별 주소");
            foreach(string item in addresses) {
                string itemOrigin=LanWebMcp.Origin(item,applied.LanHttpsPort);
                text.AppendLine("- "+item).AppendLine("  MCP: "+itemOrigin+"/mcp").AppendLine("  인증: "+itemOrigin+"/authorize").AppendLine("  공개 CA: "+LanWebMcp.CaUrl(itemOrigin));
            }
            text.AppendLine().AppendLine("클라이언트 PC에서 최초 1회 공개 CA 등록");
            text.AppendLine("대상 PC의 다음 폴더에서 공개 CA 파일과 Import-LanWebMcpCertificate.ps1을 클라이언트 PC로 복사하세요.");
            text.AppendLine(Path.GetDirectoryName(LanWebMcp.PublicCaPath));
            text.AppendLine("공개 CA URL도 HTTPS이므로 처음에는 인증서 오류가 날 수 있습니다. 최초 등록은 대상 PC에서 파일을 전달받고 위 SHA256을 대조하세요.");
            text.AppendLine("복사한 폴더에서 PowerShell을 열고 다음 명령을 실행하세요. 현재 사용자 인증서 저장소에 등록됩니다.");
            text.AppendLine(".\\Import-LanWebMcpCertificate.ps1 -ExpectedSha256 \""+caSha256.ToUpperInvariant()+"\"");
            text.AppendLine("Node 기반 클라이언트는 시작 전에 공개 CA 파일의 실제 절대 경로를 NODE_EXTRA_CA_CERTS에 지정하세요.");
            text.AppendLine("예: $env:NODE_EXTRA_CA_CERTS = 'C:\\MCP\\RemoteControl-LAN-CA.crt'").AppendLine();
            text.AppendLine("# Claude Code — 안전 등록 (PowerShell 권장)");
            text.AppendLine(Build(applied,address,name,caSha256,LanSnippetKind.ClaudeSafe)).AppendLine();
            text.AppendLine("안전 등록은 입력·서버 정보·CLI 결과를 검증하며 실패 시 멈춥니다. 클라이언트 ID와 콜백 포트를 직접 조합하지 마세요.").AppendLine();
            text.AppendLine("# Claude Code — 명령 한 줄 (local: 현재 프로젝트의 개인 설정)");
            text.AppendLine(Build(applied,address,name,caSha256,LanSnippetKind.ClaudeCode));
            text.AppendLine("전역 사용자 범위를 원하면 명령에 --scope user를 추가하세요.").AppendLine();
            text.AppendLine("# VS Code — .vscode/mcp.json");
            text.AppendLine(Build(applied,address,name,caSha256,LanSnippetKind.VsCode)).AppendLine();
            text.AppendLine("# Cursor — .cursor/mcp.json (전역: ~/.cursor/mcp.json)");
            text.AppendLine(Build(applied,address,name,caSha256,LanSnippetKind.Cursor)).AppendLine();
            text.AppendLine("# Codex — 명령");
            text.AppendLine("Codex는 시작 전에 공개 CA 파일의 실제 절대 경로를 CODEX_CA_CERTIFICATE에 지정하세요.");
            text.AppendLine("예: $env:CODEX_CA_CERTIFICATE = 'C:\\MCP\\RemoteControl-LAN-CA.crt'");
            text.AppendLine(Build(applied,address,name,caSha256,LanSnippetKind.CodexCommand)).AppendLine();
            text.AppendLine("# Codex — ~/.codex/config.toml 직접 설정");
            text.AppendLine(Build(applied,address,name,caSha256,LanSnippetKind.CodexToml)).AppendLine();
            text.AppendLine("기존 JSON/TOML 파일이 있으면 기존 서버 항목을 유지하면서 위 서버 항목을 추가하세요.");
            text.AppendLine("등록 후 첫 연결에서 클라이언트가 표시하는 로그인 링크를 열어 기존 원격제어 ID·비밀번호로 로그인하세요.");
            text.AppendLine("/authorize는 인증 엔드포인트입니다. 실제 로그인 링크의 인증 매개변수는 MCP 클라이언트가 생성합니다.");
            text.AppendLine("클라이언트 PC에서 도달할 수 있는 IP 하나를 선택하세요. IP별 인증 주소가 다르므로 주소를 변경하면 해당 IP로 다시 로그인하세요.");
            return text.ToString().Replace("\r\n","\n").Replace("\n","\r\n").TrimEnd();
        }
    }
}
