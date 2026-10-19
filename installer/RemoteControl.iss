#define AppVersion GetVersionNumbersString("..\dist\RemoteControl.exe")

[Setup]
AppId=OnTech.RemoteControl
AppName=원격제어
AppVersion={#AppVersion}
AppPublisher=OnTech
VersionInfoVersion={#AppVersion}
DefaultDirName={code:DefaultInstallDir}
DisableDirPage=no
DisableProgramGroupPage=yes
#ifdef WizardPreview
PrivilegesRequired=lowest
#else
PrivilegesRequired=admin
#endif
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
WizardResizable=yes
WizardSizePercent=115,115
SetupIconFile=..\remotecontrol.ico
OutputDir=..\dist
OutputBaseFilename=RemoteControl_Setup
Compression=lzma2/normal
SolidCompression=yes
CloseApplications=no
RestartApplications=no
CreateAppDir=no
; Existing owned engine keeps the same uninstall entry, services, settings and post-install dialogs.
; No second uninstaller or duplicate Add/Remove Programs entry is created.
Uninstallable=no
ShowLanguageDialog=no
DisableWelcomePage=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "lockscreen"; Description: "잠금 화면 제어 켜기 (Windows 서비스)"
Name: "autorun"; Description: "Windows 로그인 시 트레이에서 자동 실행"
Name: "firewall"; Description: "선택한 접속 포트를 사설·도메인 방화벽에서 허용"
Name: "desktop"; Description: "바탕화면 바로가기 만들기"
#ifdef HasVdd
Name: "vdd"; Description: "가상 모니터 설치 (모니터 없는 PC 전용)"; Flags: unchecked
#endif

[Files]
Source: "..\dist\RemoteControl_SetupEngine.exe"; Flags: dontcopy

[Run]
#ifndef WizardPreview
Filename: "{app}\RemoteControl.exe"; Description: "원격제어 실행"; Flags: postinstall nowait skipifsilent runasoriginaluser
Filename: "{app}\uninstall.exe"; Parameters: "/web-settings"; Description: "웹 ChatGPT / Claude MCP 연결 설정 열기"; Flags: postinstall unchecked nowait skipifsilent
Filename: "{app}\uninstall.exe"; Parameters: "/lan-web-settings"; Description: "LAN MCP 연결 설정 열기"; Flags: postinstall unchecked nowait skipifsilent
#endif

[Code]
var
  PortPage, TelegramPage: TInputQueryWizardPage;
  InputFolder, InputFile, EngineFile: String;

function OptionValue(Name: String): String;
begin
  Result := '0';
  if WizardIsTaskSelected(Name) then Result := '1';
end;

function DefaultInstallDir(Param: String): String;
begin
  if not RegQueryStringValue(HKLM64, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteControl', 'InstallLocation', Result) then
    Result := ExpandConstant('{autopf}\RemoteControl');
  if Result = '' then Result := ExpandConstant('{autopf}\RemoteControl');
end;

procedure InitializeWizard;
var
  Settings: String;
  Lines: TArrayOfString;
  I: Integer;
begin
  PortPage := CreateInputQueryPage(wpSelectDir, '접속 포트', '이 PC의 화면 접속 포트를 지정합니다.',
    '기존 포트를 유지하거나 사용할 번호를 입력하세요. MCP 연결은 설치 후 별도로 설정할 수 있습니다.');
  PortPage.Add('화면 접속 포트 (1~65535):', False);
  PortPage.Values[0] := '5900';
  Settings := ExpandConstant('{commonappdata}\RemoteControl\settings.txt');
  if LoadStringsFromFile(Settings, Lines) then
    for I := 0 to GetArrayLength(Lines)-1 do
      if Pos('port=', Trim(Lines[I])) = 1 then PortPage.Values[0] := Copy(Trim(Lines[I]), 6, MaxInt);
  TelegramPage := CreateInputQueryPage(PortPage.ID, '텔레그램 알림 (선택)', '사용할 봇 토큰과 받을 곳을 입력합니다.',
    '비워 두면 기존 설정을 유지합니다. 토큰은 설치 대상 PC에 암호화하여 저장됩니다. chat_id는 설치 후 알림 설정에서도 찾을 수 있습니다.');
  TelegramPage.Add('봇 토큰 (BotFather에서 받은 전체 값):', True);
  TelegramPage.Add('알림 받을 chat_id (선택):', False);
  TelegramPage.Edits[0].MaxLength := 256;
  TelegramPage.Edits[1].MaxLength := 21;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port: Integer;
  Token, Chat, Digits, Tail: String;
  Colon, I: Integer;
begin
  Result := True;
  if CurPageID = PortPage.ID then begin
    Port := StrToIntDef(Trim(PortPage.Values[0]), 0);
    Result := (Port >= 1) and (Port <= 65535);
    if not Result then MsgBox('접속 포트는 1~65535 사이의 숫자로 입력하세요.', mbError, MB_OK);
  end;
  if CurPageID = TelegramPage.ID then begin
    Token := Trim(TelegramPage.Values[0]);
    if Token <> '' then begin
      Colon := Pos(':', Token);
      Digits := Copy(Token, 1, Colon-1); Tail := Copy(Token, Colon+1, MaxInt);
      Result := (Colon >= 7) and (Length(Tail) >= 20);
      for I := 1 to Length(Digits) do if (Digits[I] < '0') or (Digits[I] > '9') then Result := False;
      for I := 1 to Length(Tail) do
        if Pos(Tail[I], 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-') = 0 then Result := False;
      if not Result then begin MsgBox('봇 토큰 전체를 입력하세요. 비워 두면 기존 값을 유지합니다.', mbError, MB_OK); Exit; end;
    end;
    Chat := Trim(TelegramPage.Values[1]);
    if Chat <> '' then begin
      if Chat[1] = '-' then Delete(Chat, 1, 1);
      Result := (Length(Chat) >= 1) and (Length(Chat) <= 20);
      for I := 1 to Length(Chat) do if (Chat[I] < '0') or (Chat[I] > '9') then Result := False;
      if not Result then MsgBox('chat_id는 숫자로 입력하세요. 비워 두어도 설치할 수 있습니다.', mbError, MB_OK);
    end;
  end;
end;

procedure CleanInput;
begin
  if InputFile <> '' then DeleteFile(InputFile);
  if InputFolder <> '' then begin
    DeleteFile(InputFolder + '\result.txt');
    RemoveDir(InputFolder);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Lines: TArrayOfString;
begin
  Result := '';
#ifdef WizardPreview
  Result := '화면 검증용입니다. 설치·설정 변경은 실행하지 않습니다.';
  Exit;
#endif
  CleanInput;
  ExtractTemporaryFile('RemoteControl_SetupEngine.exe');
  EngineFile := ExpandConstant('{tmp}\RemoteControl_SetupEngine.exe');
  InputFolder := ExpandConstant('{tmp}\rc-install-options');
  InputFile := InputFolder + '\install.options';
  if not Exec(EngineFile, '/prepare-options "' + InputFolder + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then begin
    Result := '설치 엔진을 시작하지 못했습니다.'; Exit;
  end;
  if ResultCode <> 0 then begin Result := '보호된 설치 입력 폴더를 만들지 못했습니다.'; Exit; end;
  SetArrayLength(Lines, 10);
  Lines[0] := 'Version=1';
  Lines[1] := 'Dir=' + WizardDirValue;
  Lines[2] := 'Port=' + Trim(PortPage.Values[0]);
  Lines[3] := 'LockScreen=' + OptionValue('lockscreen');
  Lines[4] := 'AutoRun=' + OptionValue('autorun');
  Lines[5] := 'Firewall=' + OptionValue('firewall');
  Lines[6] := 'Desktop=' + OptionValue('desktop');
  Lines[7] := 'VirtualDisplay=' + OptionValue('vdd');
  Lines[8] := 'TelegramToken=' + Trim(TelegramPage.Values[0]);
  Lines[9] := 'TelegramChatId=' + Trim(TelegramPage.Values[1]);
  if not SaveStringsToUTF8File(InputFile, Lines, False) then Result := '설치 입력을 저장하지 못했습니다.';
  SetArrayLength(Lines, 0);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  MessageLines: TArrayOfString;
  MessageText: String;
  I: Integer;
begin
  if CurStep = ssInstall then begin
    WizardForm.StatusLabel.Caption := '원격제어와 연결 패키지를 설치하고 있습니다...';
    try
      if not Exec(EngineFile, '/install-options "' + InputFile + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
        RaiseException('설치 엔진을 실행하지 못했습니다.');
      if ResultCode <> 0 then begin
        MessageText := '설치를 완료하지 못했습니다.';
        if LoadStringsFromFile(InputFolder + '\result.txt', MessageLines) then
          for I := 0 to GetArrayLength(MessageLines)-1 do
            if Pos('실패:', MessageLines[I]) = 1 then MessageText := MessageLines[I];
        RaiseException(MessageText);
      end;
    finally
      TelegramPage.Values[0] := '';
      CleanInput;
    end;
  end;
end;

procedure DeinitializeSetup;
begin
  CleanInput;
end;
