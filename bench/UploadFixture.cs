// 격리된 임시 폴더만 허용하고 제품의 실제 HTTP 파일 처리 메서드를 호출한다.
// 서비스, 운영 설정, 화면 캡처, SendInput은 실행하지 않는다.
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

internal static class UploadFixture
{
    static readonly BindingFlags Hidden = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static string root;
    static Type serverType, sessionType;
    public static void Main(string[] args)
    {
        root = Path.GetFullPath(args[1]);
        var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        serverType = assembly.GetType("RemoteControl.Server");
        sessionType = assembly.GetType("RemoteControl.Session");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Console.WriteLine(((IPEndPoint)listener.LocalEndpoint).Port);
        while (true)
        {
            var client = listener.AcceptTcpClient();
            ThreadPool.QueueUserWorkItem(delegate { Handle(client); });
        }
    }
    static void Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream(); stream.ReadTimeout = 5000;
                object[] input = { stream, null };
                string header = (string)serverType.GetMethod("ReadHead", Hidden).Invoke(null, input);
                string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
                string[] request = lines[0].Split(' ');
                string address = request[1]; int at = address.IndexOf('?');
                string path = at < 0 ? address : address.Substring(0, at);
                string query = at < 0 ? "" : address.Substring(at + 1);
                string folder = (string)serverType.GetMethod("QStr", Hidden).Invoke(null, new object[] { query, "p" });
                string full = Path.GetFullPath(folder);
                if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("fixture path rejected");
                if (path != "/fs/list" && path != "/fs/mkdir" && path != "/fs/put") throw new Exception("fixture route rejected");
                object session = Activator.CreateInstance(sessionType, true);
                sessionType.GetField("User", Hidden).SetValue(session, "fixture");
                serverType.GetMethod("DoFs", Hidden).Invoke(Activator.CreateInstance(serverType, true),
                    new object[] { stream, lines, session, "127.0.0.1", path, query, request[0], input[1] });
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.GetBaseException().Message); }
        }
    }
}
