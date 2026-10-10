using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Web.Script.Serialization;

namespace RemoteControlPackages
{
    internal static class WebPlugin
    {
        internal static void Write(string destination,string origin,string version)
        {
            var json=new JavaScriptSerializer();
            var manifest=new Dictionary<string,object>{{"$schema","https://agent-plugins.org/schemas/1.0.0/plugin.schema.json"},{"name","remotecontrol-web"},{"version",version},{"description","Control an authorized RemoteControl PC through HTTPS and OAuth."},{"author",new{name="ontech"}}};
            var mcp=new Dictionary<string,object>{{"$schema","https://agent-plugins.org/schemas/1.0.0/mcp.schema.json"},{"mcpServers",new Dictionary<string,object>{{"remotecontrol-web",new{type="streamable-http",url=origin+"/mcp"}}}}};
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string temp=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                using(var output=File.Create(temp))using(var zip=new ZipArchive(output,ZipArchiveMode.Create)) {
                    Entry(zip,"plugin.json",json.Serialize(manifest));Entry(zip,"mcp.json",json.Serialize(mcp));
                    Entry(zip,"skills/remotecontrol-web/SKILL.md","---\nname: remotecontrol-web\ndescription: Inspect and manage the connected RemoteControl PC over HTTPS MCP.\n---\nCall pc_get_status first and verify the target hostname before changes. Paths and commands refer to that remote PC. Never retry an execution after an uncertain transport failure. Authentication persists across restarts; reconnect only after explicit revocation. Use pc_open_file_transfer for browser file/folder uploads and streamed downloads. Direct binary transfers use pc_begin_upload, pc_write_upload_chunk, pc_finish_upload, pc_prepare_download and pc_read_download_chunk with SHA-256. Local and external clients use the same HTTPS /mcp address without a local connector install.\n");
                    Entry(zip,"README.md","# RemoteControl Web\n\nMCP URL: "+origin+"/mcp\n\nConnect with OAuth and the RemoteControl ID/password. Claude: register the same MCP URL as a remote connector. URL availability does not mean that your ChatGPT or Claude account is connected.\n");
                }
                if(File.Exists(destination))File.Replace(temp,destination,null);else File.Move(temp,destination);
            } finally {if(File.Exists(temp))File.Delete(temp);}
        }
        private static void Entry(ZipArchive zip,string name,string text)
        {using(var output=zip.CreateEntry("remotecontrol-web/"+name,CompressionLevel.Optimal).Open()){byte[] bytes=Encoding.UTF8.GetBytes(text);output.Write(bytes,0,bytes.Length);}}
    }
}
