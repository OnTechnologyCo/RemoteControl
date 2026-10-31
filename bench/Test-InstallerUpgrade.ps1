param([switch]$BuiltSetup)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$dest=Join-Path $root ('artifacts\installer-upgrade-test\'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($dest)
$csc="$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$fixtures=@{
 NoToken='internal static readonly string BotToken = "";'
 EmptyToken='internal const string BotToken = "";'
 LegacyToken='internal const string BotToken = "123456789:abcdefghijklmnopqrstuvwxyz_12345";'
 GetterToken='internal static string BotToken { get { throw new System.Exception("getter executed"); } }'
 InvalidToken='internal const string BotToken = "invalid";'
 ExistingToken='internal const string BotToken = "123456789:abcdefghijklmnopqrstuvwxyz_12345";'
}
foreach($name in $fixtures.Keys){
 $source=Join-Path $dest ($name+'.cs')
 $body='namespace RemoteControl { internal static class Notify { static Notify(){throw new System.Exception("static constructor executed");} '+$fixtures[$name]+' } } internal static class Entry { static void Main(){throw new System.Exception("entry executed");} }'
 [IO.File]::WriteAllText($source,$body)
 & $csc /nologo /target:exe /platform:x64 "/out:$(Join-Path $dest ($name+'.exe'))" $source
 if($LASTEXITCODE-ne 0){throw "Fixture compile failed: $name"}
}
$sources=@('src\ConfigProtection.cs','src\TelegramCredentials.cs','bench\InstallerUpgradeTest.cs') | ForEach-Object {Join-Path $root $_}
$test=Join-Path $dest 'InstallerUpgradeTest.exe'
& $csc /nologo /target:exe /platform:x64 /langversion:5 "/out:$test" /r:System.dll /r:System.Core.dll /r:System.Security.dll @sources
if($LASTEXITCODE-ne 0){throw 'Upgrade test compile failed'}
if($BuiltSetup) { & $test $dest (Join-Path $root 'dist\RemoteControl_SetupEngine.exe') }
else { & $test $dest }
if($LASTEXITCODE-ne 0){throw "Installer upgrade regression failed; isolated fixtures: $dest"}
"Fixtures: $dest"
