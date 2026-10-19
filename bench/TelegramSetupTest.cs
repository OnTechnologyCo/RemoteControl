using System;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using RemoteControlSecrets;

namespace RemoteControlSetup {
    internal static class TelegramSetupTest {
        const string Token="123456789:abcdefghijklmnopqrstuvwxyz_12345";
        const string Other="987654321:ABCDEFGHIJKLMNOPQRSTUVWXYZ_54321";
        static int count;
        static void Check(bool ok,string label){if(!ok)throw new Exception(label);count++;}
        static void Reject(Action act,string label){bool rejected=false;try{act();}catch{rejected=true;}Check(rejected,label);}
        static Control Find(Control parent,string name){if(parent.Name==name)return parent;foreach(Control child in parent.Controls){var result=Find(child,name);if(result!=null)return result;}return null;}
        static string Input(string token){return "Version=1\nDir=C:\\Program Files\\RemoteControl\nPort=59401\nLockScreen=1\nAutoRun=1\nFirewall=0\nDesktop=1\nVirtualDisplay=0\nTelegramToken="+token+"\nTelegramChatId=";}
        static string Decrypt(string file){byte[] plain=ProtectedData.Unprotect(File.ReadAllBytes(file),null,DataProtectionScope.LocalMachine);try{return Encoding.UTF8.GetString(plain);}finally{Array.Clear(plain,0,plain.Length);}}
        [STAThread]static int Main(string[] args){
            string env=Environment.GetEnvironmentVariable("REMOTE_CONTROL_TELEGRAM_BOT_TOKEN");
            try {
                Environment.SetEnvironmentVariable("REMOTE_CONTROL_TELEGRAM_BOT_TOKEN",null);
                Check(TelegramCredentials.ValidateToken("  "+Token+"  ")==Token,"token whitespace");
                foreach(string bad in new[]{"", "123:short",Token+"\nx=y",new string('1',300)+":abc"})Reject(delegate{TelegramCredentials.ValidateToken(bad);},"bad token accepted");
                Options blank=SetupBridge.Parse(Input(""));
                Check(blank.TelegramToken==""&&blank.Port==59401&&!blank.Firewall&&!blank.WebMcp&&!blank.LanWebMcp,"optional choices changed");
                Options supplied=SetupBridge.Parse(Input(Token)+"-123456789");
                Check(supplied.TelegramToken==Token&&supplied.TelegramChatId=="-123456789","input lost");
                foreach(string bad in new[]{Input(Token)+"\nPort=1234",Input(Token)+"\nUnknown=1",Input(Token).Replace("Port=59401","Port=65536"),Input(Token).Replace("LockScreen=1","LockScreen=yes"),Input(Token)+"abc"})Reject(delegate{SetupBridge.Parse(bad);},"bad options accepted");
                Environment.SetEnvironmentVariable("REMOTE_CONTROL_TELEGRAM_BOT_TOKEN",Other);
                Reject(delegate{SetupBridge.Parse(Input(Token));},"conflicting environment accepted");
                Environment.SetEnvironmentVariable("REMOTE_CONTROL_TELEGRAM_BOT_TOKEN",null);
                var acl=TelegramCredentials.CredentialDirectorySecurity();
                Check(acl.AreAccessRulesProtected&&((SecurityIdentifier)acl.GetOwner(typeof(SecurityIdentifier))).IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid),"credential ACL owner/inheritance");
                foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
                    if(((SecurityIdentifier)rule.IdentityReference).IsWellKnown(WellKnownSidType.BuiltinUsersSid))Check((rule.FileSystemRights&FileSystemRights.Write)==0,"users can write credentials");
                string dir=Path.Combine(args[0],"encrypted");Directory.CreateDirectory(dir);
                var write=typeof(TelegramCredentials).GetMethod("WriteEncrypted",BindingFlags.NonPublic|BindingFlags.Static);
                write.Invoke(null,new object[]{dir,Token,false});string file=Path.Combine(dir,"telegram-token.dpapi");
                Check(Decrypt(file)==Token,"saved token cannot decrypt");
                byte[] original=File.ReadAllBytes(file);
                Check(!Encoding.UTF8.GetString(original).Contains(Token)&&!Encoding.Unicode.GetString(original).Contains(Token),"plaintext stored");
                Reject(delegate{write.Invoke(null,new object[]{dir,Other,false});},"private import overwrote token");
                Check(Convert.ToBase64String(original)==Convert.ToBase64String(File.ReadAllBytes(file)),"failed import changed token");
                write.Invoke(null,new object[]{dir,Other,true});Check(Decrypt(file)==Other,"explicit replacement failed");
                Check(Directory.GetFiles(dir,"*.tmp").Length==0,"temporary token file remains");
                Reject(delegate{TelegramCredentials.ReadFrom(dir);},"unsafe owner accepted");
                Reject(delegate{TelegramCredentials.SaveTo(dir,Token,true);},"unsafe credential overwritten");
                Check(Decrypt(file)==Other,"unsafe credential modified");
                Application.EnableVisualStyles();
                using(var form=new SetupForm()){
                    var box=(TextBox)Find(form,"TelegramBotToken");
                    Check(box!=null&&box.UseSystemPasswordChar&&box.Text==""&&box.MaxLength==256,"token UI unmasked or prepopulated");
                    Check(form.ReadOptions().TelegramToken=="","empty token blocked installation");
                    box.Text=Token;Check(form.ReadOptions().TelegramToken==Token,"token UI not connected");
                    box.Text="invalid";Reject(delegate{form.ReadOptions();},"invalid UI token accepted");
                }
                File.WriteAllText(Path.Combine(args[0],"result.txt"),"PASS "+count+" checks. Fake tokens only; no services, live settings or Telegram messages changed.");
                Console.WriteLine("PASS "+count+" Telegram setup checks (isolated fake tokens; ACL policy and rejection, DPAPI roundtrip, atomic replacement, input/UI).");return 0;
            }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}finally{Environment.SetEnvironmentVariable("REMOTE_CONTROL_TELEGRAM_BOT_TOKEN",env);}
        }
    }
}
