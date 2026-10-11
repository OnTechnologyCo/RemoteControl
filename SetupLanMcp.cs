using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteControlSetup
{
    internal static class LanWebMcp
    {
        internal const int DefaultPort=443, DefaultGateway=59338;
        internal static string DefaultAddress()
        {
            string saved=WebMcp.ReadSetting("mcp_lan_address","");
            foreach(NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces()){
                if(nic.OperationalStatus!=OperationalStatus.Up || nic.NetworkInterfaceType==NetworkInterfaceType.Loopback)continue;
                foreach(UnicastIPAddressInformation ip in nic.GetIPProperties().UnicastAddresses)
                    if(ip.Address.AddressFamily==AddressFamily.InterNetwork && ip.Address.ToString()==saved)return saved;
            }
            foreach(NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces()){
                if(nic.OperationalStatus!=OperationalStatus.Up || nic.NetworkInterfaceType==NetworkInterfaceType.Loopback)continue;
                foreach(UnicastIPAddressInformation ip in nic.GetIPProperties().UnicastAddresses)
                    if(ip.Address.AddressFamily==AddressFamily.InterNetwork && !ip.Address.ToString().StartsWith("169.254."))return ip.Address.ToString();
            }
            return "";
        }
        internal static string DefaultNetworks(string address)
        {
            string saved=WebMcp.ReadSetting("mcp_lan_networks","");if(saved.Length>0)return saved;
            foreach(NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())foreach(UnicastIPAddressInformation ip in nic.GetIPProperties().UnicastAddresses){
                if(ip.Address.ToString()!=address || ip.Address.AddressFamily!=AddressFamily.InterNetwork || ip.IPv4Mask==null)continue;
                byte[] a=ip.Address.GetAddressBytes(),mask=ip.IPv4Mask.GetAddressBytes();int prefix=0;
                for(int i=0;i<4;i++){a[i]=(byte)(a[i]&mask[i]);for(int bit=7;bit>=0;bit--)if((mask[i]&(1<<bit))!=0)prefix++;}
                if(prefix>=8)return new IPAddress(a).ToString()+"/"+prefix;
            }
            return "192.168.0.0/16,10.0.0.0/8,172.16.0.0/12";
        }
        internal static int ReadPort(string key,int fallback){int value;return int.TryParse(WebMcp.ReadSetting(key,""),out value)&&(value==443||value>=1024)&&value<=65535?value:fallback;}
        internal static string Origin(Options o){return "https://"+o.LanAddress+(o.LanHttpsPort==443?"":":"+o.LanHttpsPort);}
        internal static void Validate(Options o)
        {
            if(!o.LanWebMcp)return;
            IPAddress ip;
            if(!IPAddress.TryParse(o.LanAddress,out ip)||ip.AddressFamily!=AddressFamily.InterNetwork||ip.ToString()!=o.LanAddress||o.LanAddress=="0.0.0.0")throw new ArgumentException("이 PC의 LAN IPv4 주소를 입력하세요.");
            if((o.LanHttpsPort!=443&&o.LanHttpsPort<1024)||o.LanHttpsPort>65535||o.LanGatewayPort<1024||o.LanGatewayPort>65535||o.LanHttpsPort==o.LanGatewayPort||o.LanHttpsPort==WebMcp.LanPort()||o.LanGatewayPort==WebMcp.LanPort()||o.LanHttpsPort==o.Port||o.LanGatewayPort==o.Port)throw new ArgumentException("LAN HTTPS·중계·MCP·화면 포트를 각각 다르게 지정하세요.");
            if(!o.LockScreen)throw new ArgumentException("LAN WebMCP에는 잠금 화면 제어 서비스가 필요합니다.");
            if(o.WebMcp&&(o.LanHttpsPort==o.WebGatewayPort||o.LanGatewayPort==o.WebPublicPort||o.LanGatewayPort==o.WebGatewayPort))throw new ArgumentException("LAN 포트와 외부 WebMCP 포트를 다르게 지정하세요.");
            if(string.IsNullOrWhiteSpace(o.LanNetworks)||o.LanNetworks.Length>2048)throw new ArgumentException("LAN 접속 허용 IPv4 대역을 입력하세요.");
            string[] rows=o.LanNetworks.Split(',');if(rows.Length>32)throw new ArgumentException("LAN 허용 대역은 최대 32개입니다.");
            foreach(string row in rows){string[] parts=row.Trim().Split('/');int prefix=32;
                if(parts.Length>2||!IPAddress.TryParse(parts[0],out ip)||ip.AddressFamily!=AddressFamily.InterNetwork||ip.ToString()!=parts[0]||(parts.Length==2&&(!System.Text.RegularExpressions.Regex.IsMatch(parts[1],@"\A\d{1,2}\z")||!int.TryParse(parts[1],out prefix)))||prefix<8||prefix>32)throw new ArgumentException("허용 대역은 IPv4 또는 IPv4/8~32 형식입니다.");
            }
        }
        private static string Arguments(Options o){return " -Address \""+o.LanAddress+"\" -HttpsPort "+o.LanHttpsPort+" -GatewayPort "+o.LanGatewayPort+" -LanPort "+WebMcp.LanPort()+" -AllowedNetworks \""+o.LanNetworks+"\"";}
        internal static void Preflight(Options o,Installer.Say say){if(o.LanWebMcp)WebMcp.RunScript(Path.Combine(o.Dir,"web-mcp","Install-LanWebMcp.ps1"),Arguments(o)+" -CheckOnly",say);}
        internal static void Activate(Options o,Installer.Say say)
        {
            WebMcp.RunScript(Path.Combine(o.Dir,"web-mcp","Install-LanWebMcp.ps1"),Arguments(o),say);
            WebMcp.SaveSettings(Installer.SettingsPath,new Dictionary<string,string>{{"mcp_lan_enabled","1"},{"mcp_lan_address",o.LanAddress},{"mcp_lan_https_port",o.LanHttpsPort.ToString()},{"mcp_lan_gateway",o.LanGatewayPort.ToString()},{"mcp_lan_networks",o.LanNetworks},{"mcp_lan_url",Origin(o)}});
        }
        internal static void ConfigureExisting(Options o,Installer.Say say)
        {
            Validate(o);
            if(!o.LanWebMcp){Disable(say);WebMcp.SaveSettings(Installer.SettingsPath,new Dictionary<string,string>{{"mcp_lan_enabled","0"},{"mcp_lan_url",""}});return;}
            if(!Installer.ServiceRunning()||WebMcp.ReadSetting("mcp_enabled","0")!="1")throw new ArgumentException("잠금 화면 제어 서비스와 [LLM / MCP 설정]의 MCP 사용을 먼저 켜고 저장하세요.");
            Preflight(o,say);Activate(o,say);
        }
        internal static void Disable(Installer.Say say){string script=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RemoteControlLanWebMcp","Disable-LanWebMcp.ps1");if(File.Exists(script))WebMcp.RunScript(script,"",say);}
    }
}
