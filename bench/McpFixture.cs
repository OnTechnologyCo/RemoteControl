using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace RemoteControl
{
    internal static class McpFixture
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if(args.Length>0&&args[0]=="--mcp-job")return McpJobRunner.Run(args);
            if(args.Length>0&&args[0]=="--mcp-client")return McpClient.Run(args);
            if(args.Length==2&&(args[0]=="--session-save"||args[0]=="--session-check"))return McpSessionsTest.Run(args[0],args[1]);
            if(args.Length==2&&args[0]=="--probe") {File.WriteAllText(args[1],"remote-probe-v1");Console.WriteLine("deployed binary executed");return 23;}
            if(args.Length==2&&args[0]=="--ui-proof") {
                System.Windows.Forms.Application.EnableVisualStyles();
                using(var form=new McpForm(delegate{return "MCP 대기 중 · 포트 5901";}))Draw(form,Path.Combine(args[1],"mcp-settings.png"));
                using(var form=new McpClientForm())Draw(form,Path.Combine(args[1],"mcp-client.png"));
                return 0;
            }
            if(args.Length!=1)return 64;
            string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
            var reserve=new TcpListener(IPAddress.Loopback,0);reserve.Start();int port=((IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();
            var options=new McpOptions { Enabled=true, Port=port, Workspaces=new[]{root}, AllowCommands=true, AllowScreen=false };
            Func<McpHost> create=delegate{return new McpHost(delegate{return options;},delegate(string user,string password){return user=="fixture"&&password=="fixture-only";},delegate(string message){Console.Error.WriteLine(message);},Path.Combine(root,"mcp-sessions.bin"));};
            McpHost host=create();
            try
            {
                host.Start();if(host.Port!=port)throw new Exception(host.Note);Console.WriteLine(port);Console.Out.Flush();
                string command;while(!string.IsNullOrEmpty(command=Console.ReadLine())) {
                    if(command=="screen")options.AllowScreen=true;
                    if(command=="commands-off")options.AllowCommands=false;
                    if(command=="commands-on")options.AllowCommands=true;
                    if(command=="password-change")options.CredentialStamp="changed";
                    if(command=="restart") {host.Dispose();Auth.DropAll();host=create();host.Start();}
                    host.Sync();Console.WriteLine("synced");Console.Out.Flush();
                }
            }
            finally {host.Dispose();}
            return 0;
        }
        private static void Draw(System.Windows.Forms.Form form,string path)
        {
            using(var bitmap=new System.Drawing.Bitmap(form.ClientSize.Width,form.ClientSize.Height)) {
                using(var g=System.Drawing.Graphics.FromImage(bitmap))g.Clear(form.BackColor);
                foreach(System.Windows.Forms.Control control in form.Controls) {IntPtr handle=control.Handle;control.DrawToBitmap(bitmap,control.Bounds);}
                bitmap.Save(path);
            }
        }
    }
}
