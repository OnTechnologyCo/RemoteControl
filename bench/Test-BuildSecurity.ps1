$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'build\Verify-Inputs.ps1')
$work=Join-Path $root ('artifacts\build-security-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
$pins=Get-Content (Join-Path $root 'build\dependencies.json') -Raw | ConvertFrom-Json
$script:checks=0
function Check([bool]$Value,[string]$Message){if(-not $Value){throw $Message};$script:checks++}
function Reject([scriptblock]$Action,[string]$Message){$rejected=$false;try{& $Action}catch{$rejected=$true};Check $rejected $Message}
Assert-VddPayload (Join-Path $root 'vdd') $pins.vdd
$checks++
$fake=Join-Path $work 'driver'
[void][IO.Directory]::CreateDirectory($fake)
[IO.File]::WriteAllText((Join-Path $fake 'unexpected.exe'),'not executable')
Reject {Assert-VddPayload $fake $pins.vdd} 'Unexpected VDD executable was accepted'
$unsigned=Join-Path $work 'unsigned'
[void][IO.Directory]::CreateDirectory($unsigned)
$bad=Join-Path $unsigned 'MttVDD.dll'
[IO.File]::WriteAllText($bad,'unsigned bytes')
$ownPins=[pscustomobject]@{'MttVDD.dll'=(Get-FileHash $bad).Hash}
Reject {Assert-VddPayload $unsigned $ownPins} 'Unsigned DLL was accepted'
$hash=(Get-FileHash $bad).Hash
Assert-FileHash $bad $hash
$checks++
[IO.File]::AppendAllText($bad,'tampered')
Reject {Assert-FileHash $bad $hash} 'Tampered input was accepted'

$csc="$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$old=Join-Path $work 'old.exe'
$oldSource=Join-Path $work 'old.cs'
# 실제 토큰 대신 고정된 가짜 값을 사용하며 네트워크로 보내지 않는다.
[IO.File]::WriteAllText($oldSource,'namespace RemoteControl { internal static class Notify { internal const string BotToken="123456789:abcdefghijklmnopqrstuvwxyz_12345"; } } class P {static void Main(){throw new System.Exception("must never run");}}')
& $csc /nologo /target:exe "/out:$old" $oldSource
if($LASTEXITCODE-ne 0){throw 'Legacy fixture compile failed'}
$testSource=Join-Path $work 'test.cs'
@'
using System;using System.IO;using System.Text;using RemoteControlSecrets;
class Test {
static int Main(string[] args){
 string directory=Path.Combine(args[0],"credentials");
 TelegramCredentials.PreserveFrom(args[1],directory);
 string token=TelegramCredentials.ReadFrom(directory);
 if(token!="123456789:abcdefghijklmnopqrstuvwxyz_12345")throw new Exception("Token not preserved");
 string file=Path.Combine(directory,"telegram-token.dpapi");byte[] before=File.ReadAllBytes(file);
 if(Encoding.UTF8.GetString(before).Contains(token))throw new Exception("Plaintext token persisted");
 TelegramCredentials.PreserveFrom(args[1],directory);
 if(Convert.ToBase64String(before)!=Convert.ToBase64String(File.ReadAllBytes(file)))throw new Exception("Existing token overwritten");
 File.WriteAllText(file,"corrupt");bool rejected=false;try{TelegramCredentials.PreserveFrom(args[1],directory);}catch{rejected=true;}
 if(!rejected)throw new Exception("Corrupt credential overwritten");
 Console.WriteLine("Credential migration: 4 checks passed; old executable was never run");return 0;
}}
'@ | Set-Content $testSource -Encoding UTF8
$test=Join-Path $work 'test.exe'
& $csc /nologo /target:exe /r:System.Security.dll "/out:$test" $testSource (Join-Path $root 'src\TelegramCredentials.cs') (Join-Path $root 'src\ConfigProtection.cs')
if($LASTEXITCODE-ne 0){throw 'Credential test compile failed'}
& $test $work $old
if($LASTEXITCODE-ne 0){throw 'Credential preservation tests failed'}
"Build input validation: $checks checks passed"
$driverSource=Join-Path $work 'driver-policy.cs'
@'
using System;using System.IO;using RemoteControlSetup;
class DriverPolicyTest {static int Main(string[] args){
 string dir=Path.Combine(args[0],"runtime-driver");Directory.CreateDirectory(dir);
 foreach(string name in new[]{"MttVDD.inf","MttVDD.dll","mttvdd.cat","vdd_settings.xml"})File.WriteAllText(Path.Combine(dir,name),"fixture only");
 if(Vdd.PayloadInf(dir)!=Path.Combine(dir,"MttVDD.inf"))throw new Exception("Incorrect INF selection");
 File.WriteAllText(Path.Combine(dir,"install.bat"),"never execute");bool rejected=false;try{Vdd.PayloadInf(dir);}catch(IOException){rejected=true;}
 if(!rejected)throw new Exception("Leftover script accepted");
 Console.WriteLine("Driver directory policy: 2 checks passed; no driver or script was executed");return 0;
}}
'@ | Set-Content $driverSource -Encoding UTF8
$driverTest=Join-Path $work 'driver-policy.exe'
& $csc /nologo /target:exe "/out:$driverTest" $driverSource (Join-Path $root 'SetupVdd.cs')
if($LASTEXITCODE-ne 0){throw 'Driver policy test compile failed'}
& $driverTest $work
if($LASTEXITCODE-ne 0){throw 'Driver policy tests failed'}
