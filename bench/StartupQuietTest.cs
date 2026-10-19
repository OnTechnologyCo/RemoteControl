// 실제 서버/트레이를 실행하지 않고 빌드한 EXE의 IL을 검사한다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

internal static class StartupQuietTest
{
    private static int count;
    private static readonly Dictionary<ushort,OpCode> opcodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public|BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null))
        .ToDictionary(op => unchecked((ushort)op.Value));
    private static void Check(bool ok,string label)
    { if(!ok)throw new Exception(label);count++;Console.WriteLine("PASS "+label); }
    private static IEnumerable<MethodBase> Calls(MethodBase method)
    {
        var body=method.GetMethodBody();if(body==null)yield break;
        var bytes=body.GetILAsByteArray();int p=0;
        while(p<bytes.Length)
        {
            ushort code=bytes[p++];if(code==0xfe)code=(ushort)(0xfe00|bytes[p++]);
            OpCode op;if(!opcodes.TryGetValue(code,out op))throw new Exception("Unknown IL opcode");
            if(op.OperandType==OperandType.InlineMethod)
                yield return method.Module.ResolveMethod(BitConverter.ToInt32(bytes,p));
            switch(op.OperandType)
            {
                case OperandType.InlineNone:break;
                case OperandType.ShortInlineBrTarget:case OperandType.ShortInlineI:case OperandType.ShortInlineVar:p++;break;
                case OperandType.InlineVar:p+=2;break;
                case OperandType.InlineI8:case OperandType.InlineR:p+=8;break;
                case OperandType.InlineSwitch:p+=4+4*BitConverter.ToInt32(bytes,p);break;
                default:p+=4;break;
            }
            if(p>bytes.Length)throw new Exception("Invalid IL length");
        }
    }
    public static int Main(string[] args)
    {
        try
        {
            if(args.Length!=3)throw new Exception("Usage: StartupQuietTest app.exe Program.cs expected-version");
            var assembly=Assembly.LoadFile(Path.GetFullPath(args[0]));
            var type=assembly.GetType("RemoteControl.MainForm",true);
            const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly;
            Check(type.GetField("announce",flags)==null,"startup notification flag absent from built binary");
            Check(type.GetMethod("AnnounceTray",flags)==null,"startup balloon method absent from built binary");
            var visibility=type.GetMethod("SetVisibleCore",flags);
            Check(visibility!=null&&!Calls(visibility).Any(m=>m.Name=="ShowBalloonTip"),"initial hidden startup has no balloon call");
            var methods=type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags));
            var balloons=methods.SelectMany(m=>Calls(m).Where(c=>c.DeclaringType!=null&&c.DeclaringType.FullName=="System.Windows.Forms.NotifyIcon"&&c.Name=="ShowBalloonTip").Select(c=>m.Name)).ToArray();
            Check(balloons.Length==2,"only two non-startup balloon call sites remain");
            Check(balloons.Any(n=>n=="Copy"),"user-requested copy confirmation retained");
            Check(type.GetMethod("BuildTray",flags)!=null&&type.GetMethod("StartWakeWatcher",flags)!=null,"tray and existing-window wake handlers retained");
            var source=File.ReadAllText(args[1],Encoding.UTF8);
            Check(!source.Contains("AnnounceTray")&&!source.Contains("announce = true"),"startup announcement also removed from source");
            Check(source.Contains("tick.Tick += delegate { UpdateStatus(); };"),"periodic status update retained without announcement");
            Check(source.Contains("startHidden = toTray && Config.HasPassword;"),"automatic tray start and first-use setup behavior retained");
            Check(source.Contains("Application.Run(new MainForm(mode == \"--tray\"));"),"manual launch remains visible");
            Check(source.Contains("트레이에서 계속 실행 중입니다.")&&source.Contains("주소 복사됨"),"unrelated manual notification messages retained");
            var version=(AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(assembly,typeof(AssemblyFileVersionAttribute));
            Check(version!=null&&version.Version==args[2]+".0","built file version matches source release");
            Console.WriteLine("ALL "+count+" CHECKS PASSED; production services, settings and desktop were not changed.");
            return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
