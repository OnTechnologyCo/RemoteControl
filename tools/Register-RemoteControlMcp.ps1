#requires -Version 5.1
# 이름/URL/콜백 포트를 분리하고, 등록 실패 뒤 다른 명령을 자동 실행하지 않습니다.
[CmdletBinding(PositionalBinding=$false, SupportsShouldProcess=$true)]
param(
    [ValidatePattern('\A[A-Za-z][A-Za-z0-9_-]{0,63}\z')]
    [string]$Name='remotecontrol-pth-web',
    [string]$Url='https://t0l382.tunnel.ontechdev.com/mcp',
    [ValidateSet('local','project','user')][string]$Scope='local',
    [ValidateRange(1024,65535)][int]$CallbackPort,
    [switch]$CheckOnly
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0
# /register의 오류 문자열이나 OAuth client_id를 명령 인자로 만들지 않습니다.
if($Name -cnotmatch '\A[A-Za-z][A-Za-z0-9_-]{0,63}\z'){throw '서버 이름은 영문으로 시작하는 1~64자입니다. 숫자만 있는 이름은 허용하지 않습니다.'}
$target=$null
if($Url -cnotmatch '\Ahttps://(?:[A-Za-z0-9.-]+|\[[0-9A-Fa-f:]+\])(?::[0-9]{1,5})?/mcp\z' -or
   -not [Uri]::TryCreate($Url,[UriKind]::Absolute,[ref]$target) -or
   $target.Scheme -cne 'https' -or $target.AbsolutePath -cne '/mcp' -or
   $target.UserInfo -or $target.Query -or $target.Fragment -or $target.Port -lt 1){
    throw 'URL은 https://호스트[:포트]/mcp 전체 주소여야 합니다. 공백·인증정보·쿼리는 허용하지 않습니다.'
}
$Url=$target.AbsoluteUri
$origin=$target.GetLeftPart([UriPartial]::Authority)
[string[]]$registrationArgs=@('mcp','add','--transport','http','--scope',$Scope)
if($PSBoundParameters.ContainsKey('CallbackPort')){$registrationArgs+=@('--callback-port',[string]$CallbackPort)}
$registrationArgs+=@($Name,$Url)
Write-Host ('등록 대상: '+$Name+' | '+$Url+' | 범위: '+$Scope)
Write-Host ('실행 인자: '+(($registrationArgs|ForEach-Object{'"'+$_+'"'}) -join ' '))
if($CheckOnly){Write-Host '입력 검증 완료. 클라이언트 설정·인증정보는 변경하지 않았습니다.';return}
if(-not $PSCmdlet.ShouldProcess($Name+' -> '+$Url,'검증 후 Claude Code MCP 등록')){return}
$command=Get-Command claude -CommandType Application,ExternalScript -ErrorAction Stop|Select-Object -First 1
function Invoke-ClaudeArguments([string[]]$Arguments){
    # PowerShell 5.1도 종료 코드를 직접 검사합니다. stderr를 다음 명령에 전달하지 않습니다.
    $ErrorActionPreference='Continue'
    $PSNativeCommandUseErrorActionPreference=$false
    $lines=@(& $command.Source @Arguments 2>&1)
    $code=$global:LASTEXITCODE
    if($null -eq $code){throw 'Claude CLI 종료 코드를 확인할 수 없어 중단했습니다.'}
    $text=($lines|ForEach-Object{[string]$_}) -join "`n"
    $text=[regex]::Replace($text,[string][char]27+'\[[0-9;]*[A-Za-z]','')
    return [pscustomobject]@{ExitCode=[int]$code;Text=$text}
}
function Matches-ExpectedServer([string]$Text){
    $type=[regex]::Match($Text,'(?im)^\s*Type:\s*(http|streamable-http)\s*$')
    $address=[regex]::Match($Text,'(?im)^\s*URL:\s*(\S+)\s*$')
    $actual=$null
    return $type.Success -and $address.Success -and
      [Uri]::TryCreate($address.Groups[1].Value,[UriKind]::Absolute,[ref]$actual) -and
      $actual.AbsoluteUri -ceq $Url
}
$help=Invoke-ClaudeArguments @('mcp','add','--help')
if($help.ExitCode -ne 0 -or $help.Text -notmatch '--transport' -or $help.Text -notmatch '--scope' -or
   ($PSBoundParameters.ContainsKey('CallbackPort') -and $help.Text -notmatch '--callback-port')){
    throw '현재 Claude CLI가 필요한 등록 옵션을 지원하는지 확인할 수 없습니다. 임의 대체 명령은 실행하지 않습니다.'
}
$existing=Invoke-ClaudeArguments @('mcp','get',$Name)
if($existing.ExitCode -eq 0){
    if(-not (Matches-ExpectedServer $existing.Text)){throw '같은 이름에 다른 URL/전송 방식이 있거나 CLI 출력 형식을 확인할 수 없습니다. 기존 항목을 삭제하거나 덮어쓰지 않았습니다.'}
    Write-Host '동일한 서버 설정이 이미 있습니다. 중복 등록하지 않았습니다. OAuth 인증 상태는 /mcp에서 확인하세요.'
    return
}
$notFound='\A\s*No MCP server found with name:\s*'+[regex]::Escape($Name)+'\s*\z'
if($existing.Text -notmatch $notFound){throw ('기존 서버 조회 실패(exit '+$existing.ExitCode+'). 없는 항목으로 간주하지 않고 중단했습니다.')}
function Read-Metadata([string]$Address){
    # TLS 검증을 끄거나 리디렉션을 따라가지 않습니다. 인증정보는 전송하지 않습니다.
    $response=Invoke-WebRequest -UseBasicParsing -Uri $Address -Method Get -TimeoutSec 10 -MaximumRedirection 0 -ErrorAction Stop
    if([int]$response.StatusCode -ne 200 -or $response.Content.Length -gt 65536){throw 'MCP 메타데이터 HTTP 상태 또는 크기가 올바르지 않습니다.'}
    return ($response.Content|ConvertFrom-Json -ErrorAction Stop)
}
$resource=Read-Metadata ($origin+'/.well-known/oauth-protected-resource')
$auth=Read-Metadata ($origin+'/.well-known/oauth-authorization-server')
if($resource.resource -cne $Url -or @($resource.authorization_servers).Count -ne 1 -or
   $resource.authorization_servers[0] -cne ($origin+'/') -or $auth.issuer -cne ($origin+'/') -or
   $auth.authorization_endpoint -cne ($origin+'/authorize') -or $auth.token_endpoint -cne ($origin+'/token') -or
   $auth.registration_endpoint -cne ($origin+'/register') -or
   @($auth.code_challenge_methods_supported) -notcontains 'S256'){
    throw 'MCP 주소와 인증 서버 설정이 일치하지 않습니다. 등록 명령을 실행하지 않았습니다.'
}
$result=Invoke-ClaudeArguments $registrationArgs
if($result.ExitCode -ne 0){throw ('MCP 등록 실패(exit '+$result.ExitCode+'). 재등록·삭제·로그인을 자동 실행하지 않았습니다. 해당 이름의 설정을 확인하세요.')}
$verified=Invoke-ClaudeArguments @('mcp','get',$Name)
if($verified.ExitCode -ne 0 -or -not (Matches-ExpectedServer $verified.Text)){
    throw '등록 후 서버 URL/전송 방식 확인에 실패했습니다. 성공 처리하지 않습니다. 자동 삭제는 하지 않으므로 해당 이름만 확인하세요.'
}
Write-Host '서버 설정 등록·검증 완료. 로그인 완료를 의미하지는 않습니다.'
Write-Host ('Claude Code의 /mcp에서 '+$Name+'을 선택해 인증하세요. 실패 시 숫자 포트나 오류 문자열을 서버 이름으로 재등록하지 마세요.')
