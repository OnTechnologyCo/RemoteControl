# 안전 등록 스크립트를 임시 fake CLI/메타데이터와 검사합니다. 실제 Claude 설정은 읽거나 쓰지 않습니다.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$scriptFile=Join-Path $root 'tools\Register-RemoteControlMcp.ps1'
$testRoot=Join-Path (Join-Path $root 'artifacts') ('safe-registration-test-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
$source=@'
using System;using System.IO;using System.Linq;using System.Web.Script.Serialization;
class FakeClaude {
 static int Main(string[] args){
  string root=Environment.GetEnvironmentVariable("RC_SAFE_ROOT"),mode=Environment.GetEnvironmentVariable("RC_SAFE_MODE");
  File.AppendAllText(Path.Combine(root,"calls.jsonl"),new JavaScriptSerializer().Serialize(args)+"\n");
  if(args.SequenceEqual(new[]{"mcp","add","--help"})){Console.WriteLine("--transport --scope --callback-port");return 0;}
  if(args.Length==3&&args[0]=="mcp"&&args[1]=="get"){
   if(mode=="get-error"){Console.Error.WriteLine("Configuration unreadable");return 5;}
   if(File.Exists(Path.Combine(root,"state"))||mode=="existing"||mode=="conflict"){
    Console.WriteLine(args[2]+":\n  Scope: Local\n  Status: Needs authentication\n  Type: http\n  URL: "+((mode=="conflict"||mode=="wrong-post")?"https://wrong.example.test/mcp":Environment.GetEnvironmentVariable("RC_SAFE_URL")));return 0;
   }
   Console.Error.WriteLine("No MCP server found with name: "+args[2]);return 1;
  }
  if(args.Length>=8&&args[0]=="mcp"&&args[1]=="add"){
   if(mode=="add-error"){Console.Error.WriteLine("43951\ninvalid_client: Supported callback required");return 17;}
   File.WriteAllText(Path.Combine(root,"state"),"saved");Console.WriteLine("Added HTTP server");return 0;
  }
  Console.Error.WriteLine("Unexpected command");return 99;
 }
}
'@
$fakeSource=Join-Path $testRoot 'FakeClaude.cs';[IO.File]::WriteAllText($fakeSource,$source)
$exe=Join-Path $testRoot 'claude.exe'
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:x64 /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll "/out:$exe" $fakeSource
if($LASTEXITCODE -ne 0){throw 'Fake CLI compilation failed'}
$previousPath=$env:PATH;$env:PATH=$testRoot+';'+$env:PATH
$previousRoot=$env:RC_SAFE_ROOT;$previousMode=$env:RC_SAFE_MODE;$previousUrl=$env:RC_SAFE_URL
$count=0
function Invoke-WebRequest {
 param([switch]$UseBasicParsing,[string]$Uri,[string]$Method,[int]$TimeoutSec,[int]$MaximumRedirection,[string]$ErrorAction)
 if($env:RC_SAFE_MODE -eq 'metadata-http-error'){throw 'Simulated HTTP/TLS failure'}
 if($env:RC_SAFE_MODE -eq 'metadata-json-error'){return [pscustomobject]@{StatusCode=200;Content='43951 not-json'}}
 $origin=([Uri]$env:RC_SAFE_URL).GetLeftPart([UriPartial]::Authority)
 if($Uri -like '*/oauth-protected-resource'){
  $content=@{resource=$env:RC_SAFE_URL;authorization_servers=@($origin+'/')}|ConvertTo-Json -Compress
 }else{
  $issuer=$origin+'/';if($env:RC_SAFE_MODE -eq 'metadata-issuer-error'){$issuer='https://attacker.example/'}
  $content=@{issuer=$issuer;authorization_endpoint=$origin+'/authorize';token_endpoint=$origin+'/token';registration_endpoint=$origin+'/register';code_challenge_methods_supported=@('S256')}|ConvertTo-Json -Compress
 }
 return [pscustomobject]@{StatusCode=200;Content=$content}
}
function Check([bool]$ok,[string]$text){if(-not $ok){throw $text};$script:count++;Write-Host ('PASS '+$text)}
function Case([string]$mode,[hashtable]$values,[bool]$shouldSucceed,[int]$expectedAdds){
 $dir=Join-Path $testRoot ([guid]::NewGuid().ToString('N'));[void][IO.Directory]::CreateDirectory($dir)
 $env:RC_SAFE_ROOT=$dir;$env:RC_SAFE_MODE=$mode;$env:RC_SAFE_URL='https://remote.example.test/mcp'
 $succeeded=$true;$reason=''
 try{& $scriptFile @values}catch{$succeeded=$false;$reason=$_.Exception.Message}
 Check ($succeeded -eq $shouldSucceed) ($mode+' outcome; '+$reason)
 $calls=@();if(Test-Path -LiteralPath (Join-Path $dir 'calls.jsonl')){$calls=@(Get-Content -LiteralPath (Join-Path $dir 'calls.jsonl')|ForEach-Object{,$(ConvertFrom-Json $_)})}
 $adds=@($calls|Where-Object{$_[1] -eq 'add' -and $_ -notcontains '--help'})
 Check ($adds.Count -eq $expectedAdds) ($mode+' expected add count '+$expectedAdds)
 Check (@($calls|Where-Object{$_[1] -in @('remove','login','logout')}).Count -eq 0) ($mode+' no automatic deletion/login')
 foreach($a in $adds){Check ($a[-2] -ceq 'remotecontrol-pth-web' -and $a[-1] -ceq $env:RC_SAFE_URL) ($mode+' fixed name/URL positions')}
 if($mode -eq 'callback'){$a=$adds[0];Check ($a[6] -ceq '--callback-port' -and $a[7] -ceq '43951') 'callback port is an option, never the name'}
 if($mode -eq 'add-error'){Check ($calls[-1][1] -eq 'add') 'nonzero add exit stops all later calls'}
}
try{
 $base=@{Name='remotecontrol-pth-web';Url='https://remote.example.test/mcp'}
 Case 'numeric' @{Name='43951';Url=$base.Url} $false 0
 Case 'empty-name' @{Name='';Url=$base.Url} $false 0
 Case 'injected-name' @{Name='safe;whoami';Url=$base.Url} $false 0
 Case 'whitespace-url' @{Name=$base.Name;Url=($base.Url+' ')} $false 0
 Case 'url-port-only' @{Name=$base.Name;Url='43951'} $false 0
 Case 'url-http' @{Name=$base.Name;Url='http://remote.example.test/mcp'} $false 0
 Case 'url-userinfo' @{Name=$base.Name;Url='https://user@remote.example.test/mcp'} $false 0
 Case 'callback-invalid' @{Name=$base.Name;Url=$base.Url;CallbackPort=0} $false 0
 Case 'check-only' @{Name=$base.Name;Url=$base.Url;CheckOnly=$true} $true 0
 Case 'whatif' @{Name=$base.Name;Url=$base.Url;WhatIf=$true} $true 0
 Case 'get-error' $base $false 0
 Case 'metadata-http-error' $base $false 0
 Case 'metadata-json-error' $base $false 0
 Case 'metadata-issuer-error' $base $false 0
 Case 'add-error' $base $false 1
 Case 'wrong-post' $base $false 1
 Case 'conflict' $base $false 0
 Case 'existing' $base $true 0
 Case 'success' $base $true 1
 Case 'callback' @{Name=$base.Name;Url=$base.Url;CallbackPort=43951} $true 1
 Check ($count -gt 60) 'all scenario assertions executed'
 [ordered]@{passed=$count;scenarios=20;failed=0;actualUserConfigurationChanged=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'safe-registration-result.json') -Encoding UTF8
 Write-Host ('ALL '+$count+' CHECKS PASSED')
}finally{
 $env:PATH=$previousPath;$env:RC_SAFE_ROOT=$previousRoot;$env:RC_SAFE_MODE=$previousMode;$env:RC_SAFE_URL=$previousUrl
 Remove-Item function:Invoke-WebRequest -ErrorAction SilentlyContinue
}
