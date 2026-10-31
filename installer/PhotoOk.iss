; PhotoOK 설치 파일 (Inno Setup 6). build.ps1이 게시 폴더와 버전을 넘겨 준다.
; 관리자 권한 없이 지금 사용자에게만 설치한다: %LOCALAPPDATA%\Programs\PhotoOk
; 실행 중인 앱을 강제로 끄지 않는다. 실행 중이면 안내하고 멈춘다 (설치와 제거 모두).
; 제거는 프로그램 파일만 지운다. 설정·작업 기록·프리셋·제외 단어·사진은 건드리지 않는다.

#ifndef AppVersion
  #define AppVersion "0.3.0"
#endif
#ifndef PublishDir
  #define PublishDir "publish"
#endif

#define AppName "PhotoOK"
#define AppExe "PhotoOk.exe"
; 한 번 만들어 고정한 값이다. 바꾸면 이전 설치와 이어지지 않는다.
#define AppGuid "653C325C-A221-480F-ADEC-E3696174E990"

[Setup]
AppId={{{#AppGuid}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=LeeHanryang
AppPublisherURL=https://github.com/LeeHanryang
AppCopyright=© 2026 LeeHanryang
VersionInfoVersion={#AppVersion}
PrivilegesRequired=lowest
DefaultDirName={userpf}\PhotoOk
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=output
OutputBaseFilename=PhotoOK-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupMutex=PhotoOkSetupMutex
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
; 앱을 끄거나 다시 시작하는 일은 하지 않는다. 실행 중인지는 아래 [Code]에서 직접 확인한다.
CloseApplications=no
RestartApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕 화면에 바로 가기 만들기"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestName: "LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[UninstallDelete]
; 프로그램 폴더가 비었을 때만 지운다. 사용자 데이터는 여기에 두지 않으므로 지우는 항목이 없다.
Type: dirifempty; Name: "{app}"

[Run]
Filename: "{app}\{#AppExe}"; Description: "PhotoOK 실행"; Flags: nowait postinstall skipifsilent

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{#AppGuid}}_is1';
  ProcessImage = '{#AppExe}';

{ 설치 중에는 UninstallSilent를, 제거 중에는 WizardSilent를 부를 수 없다 }
function IsSilent(): Boolean;
begin
  if IsUninstaller() then
    Result := UninstallSilent()
  else
    Result := WizardSilent();
end;

{ tasklist로 같은 이름의 프로세스가 있는지 본다. 이미지 이름은 따옴표 안의 영어 그대로 나오므로 Windows 표시 언어와 무관하다. }
function TasklistFindsApp(var Found: Boolean): Boolean;
var
  OutFile, Params: String;
  Code: Integer;
  Text: AnsiString;
begin
  Result := False;
  Found := False;
  OutFile := ExpandConstant('{tmp}\photook-tasklist.txt');
  DeleteFile(OutFile);
  Params := '/C ""' + ExpandConstant('{sys}\tasklist.exe') + '" /FI "IMAGENAME eq ' + ProcessImage +
    '" /NH /FO CSV > "' + OutFile + '""';
  if not Exec(ExpandConstant('{cmd}'), Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then Exit;
  if (Code <> 0) or not LoadStringFromFile(OutFile, Text) then Exit;
  Result := True;
  Found := Pos('"' + Uppercase(ProcessImage) + '"', Uppercase(String(Text))) > 0;
  DeleteFile(OutFile);
end;

{ tasklist를 못 쓸 때의 두 번째 방법: WMI }
function WmiFindsApp(var Found: Boolean): Boolean;
var
  Locator, Service, Items: Variant;
begin
  Result := False;
  Found := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Items := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''' + ProcessImage + '''');
    Found := Items.Count > 0;
    Result := True;
  except
    Log('WMI 확인 실패: ' + GetExceptionMessage);
  end;
end;

{ 어느 폴더의 PhotoOk.exe든 실행 중이면 True. 확인 방법이 모두 막혔을 때만 False(그때는 사용 중인 파일을 Setup이 직접 막는다). }
function IsAppRunning(): Boolean;
var
  Found: Boolean;
begin
  Result := False;
  if TasklistFindsApp(Found) then
    Result := Found
  else if WmiFindsApp(Found) then
    Result := Found
  else
    Log('실행 중인지 확인하지 못했습니다.');
end;

function InitializeSetup(): Boolean;
var
  Previous: String;
  PreviousVersion, NewVersion: Int64;
begin
  Result := True;

  if IsAppRunning() then
  begin
    Log('PhotoOk.exe가 실행 중이라 설치를 멈춥니다.');
    if not IsSilent() then
      MsgBox('PhotoOK를 닫은 뒤 다시 설치해 주세요.', mbInformation, MB_OK);
    Result := False;
    Exit;
  end;

  { 낮은 버전으로 덮어 설치하지 않는다 (같은 버전은 다시 설치할 수 있다) }
  Previous := '';
  if not RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', Previous) then
    RegQueryStringValue(HKLM, UninstallKey, 'DisplayVersion', Previous);
  if (Previous <> '') and StrToVersion(Previous, PreviousVersion) and StrToVersion('{#AppVersion}', NewVersion) then
    if ComparePackedVersion(PreviousVersion, NewVersion) > 0 then
    begin
      Log('설치된 ' + Previous + '보다 낮은 {#AppVersion}으로는 설치하지 않습니다.');
      if not IsSilent() then
        MsgBox('이미 더 높은 버전(' + Previous + ')이 설치되어 있어 낮은 버전({#AppVersion})으로는 설치할 수 없습니다.',
          mbInformation, MB_OK);
      Result := False;
    end;
end;

{ 마법사 도중에 앱이 켜졌을 수 있으니 파일을 옮기기 전에 한 번 더 본다 }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if IsAppRunning() then
    Result := 'PhotoOK를 닫은 뒤 다시 설치해 주세요.';
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if IsAppRunning() then
  begin
    Log('PhotoOk.exe가 실행 중이라 제거를 멈춥니다.');
    if not IsSilent() then
      MsgBox('PhotoOK를 닫은 뒤 다시 제거해 주세요.', mbInformation, MB_OK);
    Result := False;
  end;
end;
