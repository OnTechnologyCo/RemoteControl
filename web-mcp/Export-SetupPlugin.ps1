# EXE의 설치 진입점은 실행하지 않는다. Setup과 동일한 ZIP 생성 코드로 dist 미리보기 파일을 만든다.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Setup,[Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference='Stop'
$assembly=[Reflection.Assembly]::LoadFile([IO.Path]::GetFullPath($Setup))
$type=$assembly.GetType('RemoteControlSetup.WebMcp',$true)
$flags=[Reflection.BindingFlags]'Static,NonPublic'
$url=$type.GetField('DefaultUrl',$flags).GetRawConstantValue()
$type.GetMethod('WritePlugin',$flags).Invoke($null,[string[]]@([IO.Path]::GetFullPath($Destination),$url)) | Out-Null
"웹 ChatGPT 업로드 ZIP: $Destination"
