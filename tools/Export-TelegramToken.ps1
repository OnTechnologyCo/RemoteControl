# Private, user-requested transfer file. Never include its output in a release.
[CmdletBinding()]
param([string]$Destination = (Join-Path ([Environment]::GetFolderPath('Desktop')) '원격제어_텔레그램_토큰등록.ps1'), [switch]$ReplaceExisting)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$source = [IO.File]::ReadAllText((Join-Path $root 'src\TelegramCredentials.cs'))
Add-Type -TypeDefinition ($source + @'
public static class RemoteControlTokenExport {
 public static string Read() { return RemoteControlSecrets.TelegramCredentials.Read(); }
 public static string Validate(string token) { return RemoteControlSecrets.TelegramCredentials.ValidateToken(token); }
}
'@) -ReferencedAssemblies System.Security
$token = [RemoteControlTokenExport]::Read()
if ([string]::IsNullOrWhiteSpace($token)) {
    $previous = Join-Path $env:ProgramFiles 'RemoteControl\RemoteControl.exe'
    if (-not (Test-Path -LiteralPath $previous)) { throw '기존 봇 토큰을 찾을 수 없습니다.' }
    $assembly = [Reflection.Assembly]::ReflectionOnlyLoadFrom($previous)
    $type = $assembly.GetType('RemoteControl.Notify', $false)
    $field = if ($type) { $type.GetField('BotToken', [Reflection.BindingFlags]'Static,NonPublic,Public') }
    if (-not $field -or -not $field.IsLiteral -or $field.FieldType -ne [string]) { throw '구버전 토큰 메타데이터가 없습니다.' }
    $token = [string]$field.GetRawConstantValue()
}
$token = [RemoteControlTokenExport]::Validate($token)
$template = @'
# 개인 전달용: 실제 텔레그램 봇 토큰이 포함되어 있습니다. 공개 업로드하지 마세요.
# Windows PowerShell에서 실행하면 UAC 승인 후 이 PC의 암호화 저장소에 등록합니다.
# 기존의 다른 토큰, chat_id, 서비스, 포트는 변경하지 않습니다.
[CmdletBinding()]
param([switch]$Elevated, [switch]$ValidateOnly)
$ErrorActionPreference = 'Stop'
# 현재 PC에서 가져온 실제 토큰. 다른 PC에서 값을 입력할 필요가 없습니다.
$telegramToken = '__TOKEN__'
$resultLog = Join-Path $env:TEMP 'RemoteControl-Telegram-Import.log'
function Report([string]$message) {
    if ($telegramToken) { $message = $message.Replace($telegramToken, '[토큰]') }
    Write-Host $message
    if (-not $ValidateOnly) { try { Add-Content -LiteralPath $resultLog -Value ((Get-Date -Format s) + ' ' + $message) -Encoding UTF8 } catch {} }
}
function Wait-Close {
    if (-not $ValidateOnly) { [void](Read-Host '내용을 확인한 뒤 Enter를 누르면 닫힙니다') }
}
try {
    if (-not $ValidateOnly) {
        Report '원격제어 텔레그램 토큰 등록을 시작합니다.'
        $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            if ($Elevated) { throw '관리자 권한으로 실행해 주세요.' }
            $engine = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
            Report '관리자 권한 승인 창에서 예를 선택하세요. 결과는 새 창에 표시됩니다.'
            # 사용자가 결과/시작 오류를 직접 확인하는 대화형 창이다.
            $child = Start-Process -FilePath $engine -ArgumentList @('-NoProfile', '-NoExit', '-ExecutionPolicy', 'RemoteSigned', '-File', ('"' + $PSCommandPath + '"'), '-Elevated') -Verb RunAs -WindowStyle Normal -Wait -PassThru
            if ($child.ExitCode -ne 0) { Report ('실행이 완료되지 않았습니다. 종료 코드: ' + $child.ExitCode); Report ('기록: ' + $resultLog); Wait-Close }
            exit $child.ExitCode
        }
    }
    $credentialSource = @"
__CREDENTIAL_SOURCE__
"@
    Add-Type -TypeDefinition ($credentialSource + @"
public static class RemoteControlTokenImport {
 public static void Save(string token) {
  string dir = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData), "RemoteControl", "credentials");
  RemoteControlSecrets.TelegramCredentials.SaveTo(dir, token, false);
 }
 public static void Validate(string token) {
  RemoteControlSecrets.TelegramCredentials.ValidateToken(token);
  byte[] plain = System.Text.Encoding.UTF8.GetBytes(token);
  byte[] encrypted = System.Security.Cryptography.ProtectedData.Protect(plain, null, System.Security.Cryptography.DataProtectionScope.LocalMachine);
  byte[] decoded = System.Security.Cryptography.ProtectedData.Unprotect(encrypted, null, System.Security.Cryptography.DataProtectionScope.LocalMachine);
  try { if (System.Text.Encoding.UTF8.GetString(decoded) != token) throw new System.Exception("DPAPI verification failed"); }
  finally { System.Array.Clear(plain, 0, plain.Length); System.Array.Clear(decoded, 0, decoded.Length); }
 }
}
"@) -ReferencedAssemblies System.Security
    if ($ValidateOnly) {
        [RemoteControlTokenImport]::Validate($telegramToken)
        Write-Output 'PASS: 토큰 형식과 DPAPI 암호화 왕복 확인. 설정 변경·메시지 전송 없음.'
    } else {
        [RemoteControlTokenImport]::Save($telegramToken)
        Report "완료: 토큰을 이 PC에 암호화하여 등록했습니다.`r`n원격제어의 알림 설정을 다시 열어 chat_id를 확인하세요."
        Report ('기록: ' + $resultLog)
        Wait-Close
    }
    $telegramToken = $null
    exit 0
} catch {
    # 예외의 스크립트 줄/호출 인자를 출력하면 토큰이 노출될 수 있어 메시지만 표시한다.
    $message = $_.Exception.GetBaseException().Message
    Report ('등록하지 못했습니다: ' + $message)
    if (-not $ValidateOnly) { Report ('기록: ' + $resultLog); Wait-Close }
    exit 1
}
'@
# Expandable here-string에 들어갈 C#의 PowerShell 보간 문자를 escape한다.
$escapedSource = $source.Replace('`','``').Replace('$','`$')
$content = $template.Replace('__CREDENTIAL_SOURCE__', $escapedSource).Replace('__TOKEN__', $token)
$parseTokens = $null; $parseErrors = $null
[void][Management.Automation.Language.Parser]::ParseInput($content, [ref]$parseTokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw '생성 스크립트 구문 검사 실패.' }
$data = [Text.UTF8Encoding]::new($true).GetPreamble() + [Text.Encoding]::UTF8.GetBytes($content)
$mode = if ($ReplaceExisting) { [IO.FileMode]::Create } else { [IO.FileMode]::CreateNew }
$stream = [IO.File]::Open($Destination, $mode, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $stream.Write($data, 0, $data.Length) } finally { $stream.Dispose(); $token = $null; $content = $null }
$launcher=Join-Path (Split-Path -Parent $Destination) 'Apply-RemoteControl-Telegram.cmd'
$launchLine='"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0'+[IO.Path]::GetFileName($Destination)+'" %*'
$lines=@('@echo off','chcp 65001 >nul','setlocal','title RemoteControl Telegram Token Import',$launchLine,'set "RC_RESULT=%ERRORLEVEL%"','echo.','echo Exit code: %RC_RESULT%','echo Review the result above. Press any key to close.','pause >nul','exit /b %RC_RESULT%')
[IO.File]::WriteAllText($launcher,($lines -join "`r`n")+"`r`n",[Text.UTF8Encoding]::new($false))
[pscustomobject]@{Path=$Destination;Length=(Get-Item -LiteralPath $Destination).Length;ContainsPrivateToken=$true}
