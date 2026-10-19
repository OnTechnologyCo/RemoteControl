using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace RemoteControlSetup
{
    internal static class LanConnectionTest
    {
        private const string Primary="192.168.195.141", Secondary="10.20.0.5", Third="172.16.4.22";
        private const string CaHash="EC5534BB694AEBA638C7744317EC9B99E516A82F2A707C20698D0574A7F9B08F";
        // 공개 인증서만 fixture에 둔다. 개인 키·Windows 인증서 저장소는 사용하지 않는다.
        private const string PublicPem="-----BEGIN CERTIFICATE-----\n"+
            "MIIBeTCCAR+gAwIBAgIEATUolDAKBggqhkjOPQQDAjA6MTgwNgYDVQQDDC9SZW1v\n"+
            "dGVDb250cm9sIExBTiBUZXN0IENBIC0gcHVibGljIGZpeHR1cmUgb25seTAeFw0y\n"+
            "MDAxMDEwMDAwMDBaFw00MDAxMDEwMDAwMDBaMDoxODA2BgNVBAMML1JlbW90ZUNv\n"+
            "bnRyb2wgTEFOIFRlc3QgQ0EgLSBwdWJsaWMgZml4dHVyZSBvbmx5MFkwEwYHKoZI\n"+
            "zj0CAQYIKoZIzj0DAQcDQgAEVyFMwjrlipA0IPiq/hcQ3/aQCqgxaePHo+WwjP83\n"+
            "3/Pcor7/XcMybC3lsHhuD+VFMf1xbDN4lWcafEPXi0NGu6MTMBEwDwYDVR0TAQH/\n"+
            "BAUwAwEB/zAKBggqhkjOPQQDAgNIADBFAiA0W6hoQHSoXH28dhfUP7bhO5ntAA1E\n"+
            "8K1i2UCV9e7KYwIhAIMgytrBqgGeCMSS08hqMbaE/587GxDydbLlGuoTGj2/\n"+
            "-----END CERTIFICATE-----\n";
        private static int checks;
        private static void Require(bool condition,string message)
        { if(!condition) throw new Exception(message);checks++; }
        private static void Reject(Action action,string message)
        {
            bool rejected=false;
            try{action();}catch(ArgumentException){rejected=true;}catch(IOException){rejected=true;}
            Require(rejected,message);
        }
        private static Options Fixture()
        { return new Options{LanWebMcp=true,LanAddress=Primary,LanAdditionalAddresses=Secondary+","+Third,LanNetworks="192.168.195.0/24,10.20.0.0/24,172.16.4.0/24"}; }
        private static LanInterfaceAddress[] FixtureInterfaces()
        {
            return new[]{new LanInterfaceAddress(Primary,"Ethernet A","192.168.195.0/24"),
                new LanInterfaceAddress(Secondary,"Ethernet B","10.20.0.0/24"),new LanInterfaceAddress(Third,"Wi-Fi","172.16.4.0/24")};
        }
        private static Control Find(Control root,string name)
        {
            if(root.Name==name)return root;
            foreach(Control child in root.Controls){Control found=Find(child,name);if(found!=null)return found;}
            return null;
        }
        private static object Field(object target,string name)
        { return target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(target); }
        private static void Field(object target,string name,object value)
        { target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(target,value); }
        private static string Snippet(Options options,string address,LanSnippetKind kind)
        { return LanConnectionInstructions.Build(options,address,LanConnectionInstructions.DefaultServerName,CaHash,kind); }

        private static void TestAddresses()
        {
            Require(string.Join(",",LanWebMcp.Addresses(Primary,Secondary+",\r\n"+Third+","+Primary+","+Secondary))==Primary+","+Secondary+","+Third,"Server IP normalization or de-duplication failed");
            Require(string.Join(",",LanWebMcp.PromoteAddress(Primary,Secondary,Secondary))==Secondary+","+Primary,"Changing primary dropped the old primary");
            Require(string.Join(",",LanWebMcp.PromoteAddress("",Secondary,Primary))==Primary+","+Secondary,"Selecting the first primary required existing text");
            foreach(string bad in new[]{"", "0.0.0.0", "0.1.2.3", "127.0.0.1", "127.3.4.5", "169.254.8.9", "224.0.0.1", "255.255.255.255", "::1", "192.168.195.014", "1234", "0xC0A8C38D", "https://192.168.195.141", "192.168.195.141\";Write-Host injected"}) {
                string value=bad; Reject(delegate{LanWebMcp.Addresses(value,"");},"Invalid primary accepted: "+bad);
                if(bad.Length>0) Reject(delegate{LanWebMcp.Addresses(Primary,value);},"Invalid additional IP accepted: "+bad);
            }
            var many=new List<string>();for(int i=1;i<=32;i++)many.Add("10.1.0."+i);
            Require(LanWebMcp.Addresses(many[0],string.Join(",",many.ToArray())).Length==32,"32 addresses were rejected");
            Reject(delegate{LanWebMcp.Addresses(many[0],string.Join(",",many.ToArray())+",10.1.0.33");},"More than 32 addresses accepted");
            Require(LanWebMcp.PromoteAddress(many[0],string.Join(",",many.ToArray()),many[31]).Length==32,"Promoting within 32 addresses changed the count");
            Reject(delegate{LanWebMcp.PromoteAddress(many[0],string.Join(",",many.ToArray()),"10.1.0.33");},"Primary selection exceeded 32 addresses");
            string saved=Primary+","+Secondary+",198.51.100.254";
            Require(LanWebMcp.AdditionalAddresses(Primary,saved)==Secondary+",198.51.100.254","Saved offline address was lost");
            string[] addresses=LanWebMcp.Addresses(Primary,Secondary+",198.51.100.254");
            Require(string.Join(",",LanWebMcp.MissingAddresses(addresses,FixtureInterfaces()))=="198.51.100.254","Offline IP was not reported");
            Require(string.Join(",",addresses)==saved,"Checking NICs changed saved addresses");
            Require(LanWebMcp.Subnet(IPAddress.Parse(Primary),IPAddress.Parse("255.255.254.0"))=="192.168.194.0/23","NIC subnet calculation failed");
            Require(LanWebMcp.Subnet(IPAddress.Parse(Primary),IPAddress.Parse("255.0.255.0"))=="","Noncontiguous mask accepted");
            Require(LanWebMcp.SuggestedNetworks(new[]{Primary,Secondary},FixtureInterfaces())=="192.168.195.0/24,10.20.0.0/24","Subnet suggestions included wrong interfaces");
        }

        private static void TestSnippets(string directory)
        {
            Options options=Fixture();string name=LanConnectionInstructions.DefaultServerName;
            string safe=Snippet(options,Primary,LanSnippetKind.ClaudeSafe); Require(safe.Contains("PositionalBinding=$false") && safe.Contains("$registrationArgs+=@($Name,$Url)"),"Safe registration template missing");
            File.WriteAllText(Path.Combine(directory,"claude-safe.ps1"),safe,new UTF8Encoding(true));
            foreach(int port in new[]{443,59443}) {
                options.LanHttpsPort=port;
                foreach(string address in LanWebMcp.Addresses(options.LanAddress,options.LanAdditionalAddresses)) {
                    string origin="https://"+address+(port==443?"":":"+port),url=origin+"/mcp";
                    Require(Snippet(options,address,LanSnippetKind.ClaudeCode)=="claude mcp add --transport http --scope local \""+name+"\" \""+url+"\"","Claude command was not ready to run");
                    Require(Snippet(options,address,LanSnippetKind.CodexCommand)=="codex mcp add "+name+" --url "+url+"\r\ncodex mcp login "+name,"Codex add/login commands are wrong");
                    Require(Snippet(options,address,LanSnippetKind.CodexToml)=="[mcp_servers."+name+"]\r\nurl = \""+url+"\"","Codex TOML is wrong");
                    foreach(LanSnippetKind kind in new[]{LanSnippetKind.VsCode,LanSnippetKind.Cursor}) {
                        string json=Snippet(options,address,kind),wrapper=kind==LanSnippetKind.VsCode?"servers":"mcpServers";
                        var root=(Dictionary<string,object>)new JavaScriptSerializer().DeserializeObject(json);
                        Require(root.Count==1&&root.ContainsKey(wrapper),"Client JSON wrapper is wrong");
                        var servers=(Dictionary<string,object>)root[wrapper];
                        var server=(Dictionary<string,object>)servers[name];
                        Require((string)server["url"]==url,"Client JSON URL is wrong");
                        Require(kind!=LanSnippetKind.VsCode||(string)server["type"]=="http","VS Code transport is missing");
                        Require(!json.Contains("공개 CA")&&!json.StartsWith("//"),"Pure JSON copy contains documentation");
                    }
                    Require(Snippet(options,address,LanSnippetKind.Certificate)=="- 공개 CA: "+origin+"/ca/RemoteControl-LAN-CA.crt\r\n- 공개 CA SHA256: "+CaHash,"CA URL/hash copy is wrong");
                    string guide=Snippet(options,address,LanSnippetKind.Guide);
                    foreach(string registered in LanWebMcp.Addresses(options.LanAddress,options.LanAdditionalAddresses)) {
                        string registeredOrigin="https://"+registered+(port==443?"":":"+port);
                        Require(guide.Contains(registeredOrigin+"/mcp")&&guide.Contains(registeredOrigin+"/authorize")&&guide.Contains(registeredOrigin+"/ca/RemoteControl-LAN-CA.crt"),"Complete guide omitted an IP's endpoints");
                    }
                    Require(guide.Contains("- 공개 CA SHA256: "+CaHash)&&guide.Contains("-ExpectedSha256 \""+CaHash+"\""),"CA hash not included in guide and import command");
                    Require(guide.Contains("$env:CODEX_CA_CERTIFICATE = 'C:\\MCP\\RemoteControl-LAN-CA.crt'"),"Codex CA environment hint missing");
                    Require(!guide.Contains("{CA_")&&!guide.Contains("NODE_TLS_REJECT_UNAUTHORIZED")&&!guide.Contains("BEGIN PRIVATE KEY"),"Unsafe or unresolved guide data");
                    if(port==443&&address==Primary) File.WriteAllText(Path.Combine(directory,"connection-guide.txt"),guide,new UTF8Encoding(false));
                }
            }
            Reject(delegate{Snippet(options,"198.51.100.254",LanSnippetKind.Guide);},"Unapplied IP was accepted for copy");
            foreach(string bad in new[]{"", "43951", "123456", "-all", "name;whoami", "name\r\nwhoami", "name\"", "../name"}) {
                string value=bad;Reject(delegate{LanConnectionInstructions.Build(options,Primary,value,CaHash,LanSnippetKind.ClaudeCode);},"Command injection accepted");
            }
            Reject(delegate{LanConnectionInstructions.Build(options,Primary,name,"",LanSnippetKind.Guide);},"Missing CA hash accepted");
            options.LanWebMcp=false;Reject(delegate{Snippet(options,Primary,LanSnippetKind.ClaudeCode);},"Disabled connection produced a command");
        }

        private static void TestCa(string directory)
        {
            string path=Path.Combine(directory,"public-test-ca.crt");
            File.WriteAllText(path,PublicPem,new UTF8Encoding(false));
            Require(LanWebMcp.ReadPublicCaHash(path)==CaHash,"CA SHA256 is not the exact PEM file byte hash");
            string crlf=Path.Combine(directory,"public-test-ca-crlf.crt");File.WriteAllText(crlf,PublicPem.Replace("\n","\r\n"),new UTF8Encoding(false));
            Require(LanWebMcp.ReadPublicCaHash(crlf)!=CaHash,"PEM was normalized before hashing");
            Reject(delegate{LanWebMcp.ReadPublicCaHash(Path.Combine(directory,"missing.crt"));},"Missing certificate was accepted");
            string invalid=Path.Combine(directory,"invalid.crt");File.WriteAllText(invalid,"not a CA");
            Reject(delegate{LanWebMcp.ReadPublicCaHash(invalid);},"Invalid certificate was accepted");
        }

        private static void TestStateAndUi()
        {
            using(var form=new LanSettingsForm()) {
                form.ShowInTaskbar=false;form.Opacity=0;form.Show();Application.DoEvents();
                var connection=(LanConnectionControls)Field(form,"connection");
                var enabled=(CheckBox)Find(form,"LanWebMcpEnabled");var address=(TextBox)Find(form,"LanWebMcpAddress");
                var additional=(TextBox)Find(form,"LanWebMcpAdditionalAddresses");var networks=(TextBox)Find(form,"LanWebMcpNetworks");
                var port=(NumericUpDown)Find(form,"LanWebMcpHttpsPort");var gateway=(NumericUpDown)Find(form,"LanWebMcpGateway");
                var scripts=(Button)Find(form,"LanWebMcpScripts");Options fixture=Fixture();
                enabled.Checked=true;address.Text=fixture.LanAddress;additional.Text=fixture.LanAdditionalAddresses;networks.Text=fixture.LanNetworks;
                port.Value=fixture.LanHttpsPort;gateway.Value=fixture.LanGatewayPort;
                // 적용 결과만 fixture로 공급하고, 사용자의 수정·실패·busy 이벤트는 실제 UI를 통과시킨다.
                Field(connection,"applied",LanWebMcp.CopyConnection(fixture));Field(connection,"available",FixtureInterfaces());Field(connection,"caSha256",CaHash);
                connection.SetBusy(false);Require(scripts.Enabled,"Previously applied settings could not reopen copy UI");
                var interfaces=(ComboBox)Find(form,"LanWebMcpInterfaces");interfaces.Items.Clear();interfaces.Items.AddRange(FixtureInterfaces());interfaces.SelectedIndex=1;
                ((Button)Find(form,"LanWebMcpUsePrimary")).PerformClick();
                Require(address.Text==Secondary&&additional.Text==Primary+","+Third,"Primary selection UI discarded an existing IP");
                address.Text=fixture.LanAddress;additional.Text=fixture.LanAdditionalAddresses;
                additional.Text=Secondary;Require(!scripts.Enabled,"Unapplied IP removal was copyable");additional.Text=fixture.LanAdditionalAddresses;
                Require(scripts.Enabled,"Restoring applied IPs did not restore copy UI");
                address.Text=Secondary;Require(!scripts.Enabled,"Unapplied primary change was copyable");address.Text=Primary;
                port.Value=59443;Require(!scripts.Enabled,"Unapplied port was copyable");port.Value=443;
                gateway.Value=59444;Require(!scripts.Enabled,"Unapplied gateway was copyable");gateway.Value=fixture.LanGatewayPort;
                networks.Text=Primary+"/32";Require(!scripts.Enabled,"Unapplied allowed networks were copyable");
                additional.Text=Secondary;Require(networks.Text==Primary+"/32","Manual allowed network was overwritten by address edit");
                additional.Text=fixture.LanAdditionalAddresses;networks.Text=fixture.LanNetworks;
                connection.SetBusy(true);Require(!scripts.Enabled&&!address.Enabled,"Copy or editing enabled during apply");connection.SetBusy(false);
                enabled.Checked=false;Require(!scripts.Enabled,"Disabled connection was copyable");enabled.Checked=true;
                Field(connection,"caSha256","");connection.SetBusy(false);Require(!scripts.Enabled,"Missing CA was copyable");
                Field(connection,"caSha256",CaHash);connection.SetBusy(false);Require(scripts.Enabled,"Valid CA did not restore copy UI");
                connection.ApplyFailed();Require(!scripts.Enabled,"Failed apply was copyable");connection.SetBusy(false);Require(!scripts.Enabled,"Busy reset cleared failed apply state");
                connection.Applied(new Options{LanWebMcp=false});Require(!scripts.Enabled,"Successfully disabled connection was copyable");
                string savedPrimary=address.Text,savedAdditional=additional.Text;
                ((Button)Find(form,"LanWebMcpRefreshInterfaces")).PerformClick();
                Require(address.Text==savedPrimary&&additional.Text==savedAdditional,"NIC refresh silently replaced saved IPs");
            }
            using(var form=new LanScriptForm(Fixture(),CaHash)) {
                form.ShowInTaskbar=false;form.Opacity=0;form.Show();Application.DoEvents();
                var address=(ComboBox)Find(form,"LanScriptAddress");var format=(ComboBox)Find(form,"LanScriptFormat");
                var name=(TextBox)Find(form,"LanScriptServerName");var preview=(TextBox)Find(form,"LanScriptPreview");var copy=(Button)Find(form,"LanScriptCopy");
                address.Text=Secondary;format.SelectedIndex=(int)LanSnippetKind.ClaudeCode;
                Require(preview.Text==Snippet(Fixture(),Secondary,LanSnippetKind.ClaudeCode),"Selecting a second NIC did not update the command");
                Require(Find(form,"LanScriptAuthorizeUrl").Text=="https://"+Secondary+"/authorize","Authorization URL did not follow selected IP");
                Require(Find(form,"LanScriptCaUrl").Text=="https://"+Secondary+"/ca/RemoteControl-LAN-CA.crt","CA URL did not follow selected IP");
                format.SelectedIndex=(int)LanSnippetKind.VsCode;new JavaScriptSerializer().DeserializeObject(preview.Text);Require(copy.Enabled,"Valid JSON disabled copy");
                address.Text="198.51.100.254";Require(!copy.Enabled&&preview.Text.Length==0,"Typing unapplied IP exposed a script");
                address.Text=Primary;name.Text="name;whoami";Require(!copy.Enabled&&preview.Text.Length==0,"Invalid server name exposed a script");
                name.Text="43951";Require(!copy.Enabled && preview.Text.Length==0,"Numeric port accepted as server name");
                name.Text=LanConnectionInstructions.DefaultServerName;Require(copy.Enabled,"Valid server name did not recover preview");
                format.SelectedIndex=(int)LanSnippetKind.Certificate;Require(preview.Text==Snippet(Fixture(),Primary,LanSnippetKind.Certificate),"CA-only copy format differs");
            }
        }

        private static void TestPowerShellArguments(string directory)
        {
            string script=Path.Combine(directory,"check-arguments.ps1");
            File.WriteAllText(script,"param([string]$Address,[string]$AdditionalAddresses='DEFAULT',[int]$HttpsPort,[int]$GatewayPort,[int]$LanPort,[string]$AllowedNetworks)\r\n"+
                "if($Address -ne '"+Primary+"' -or $HttpsPort -ne 443 -or $GatewayPort -ne 59338){throw 'Argument positions changed'}\r\n"+
                "Write-Output ('additional=['+$AdditionalAddresses+']')\r\n",new UTF8Encoding(false));
            foreach(string additional in new[]{"",Secondary+","+Third}) {
                Options options=Fixture();options.LanAdditionalAddresses=additional;
                string arguments=(string)typeof(LanWebMcp).GetMethod("Arguments",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{options});
                var output=new List<string>();WebMcp.RunScript(script,arguments,delegate(string line){output.Add(line);});
                Require(output.Contains("additional=["+additional+"]"),"PowerShell native -File lost the additional-address argument");
            }
        }

        [STAThread] private static int Main(string[] args)
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            try {
                Directory.CreateDirectory(args[0]);TestAddresses();TestSnippets(args[0]);TestCa(args[0]);TestStateAndUi();TestPowerShellArguments(args[0]);
                string result="PASS "+checks+" LAN connection checks. No service, network, certificate store, settings file, or clipboard was changed.";
                File.WriteAllText(Path.Combine(args[0],"result.txt"),result);Console.WriteLine(result);return 0;
            } catch(Exception error){Console.Error.WriteLine(error.ToString());return 1;}
        }
    }
}
