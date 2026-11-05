; Wave Flume installer (Inno Setup 6). Build with installer\build-release.ps1, which runs the tests, publishes the
; self-contained single-file exe, and then runs ISCC on this script.
;
; Silent install for several PCs (all optional):
;   WaveFlume_Setup_1.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /Controller=real /Protocol=OmronFins /Ip=192.168.250.1 /Port=9600 /DataDir="D:\FlumeData"
; Controller is "simulator" (default) or "real"; Protocol is OmronFins, ModbusTcp, OpcUa, EtherNetIp or S7.
; Add /Fresh=1 to remove settings, probe calibration and logs left by earlier use (recipes and results are kept).

#define AppName "Wave Flume"
#define AppVersion "1.0.0"
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef OutDir
  #define OutDir "..\artifacts\installer"
#endif

[Setup]
AppId={{6E8B3C52-9A7D-4C1E-B0E4-2F6A5D3C7B91}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir={#OutDir}
OutputBaseFilename=WaveFlume_Setup_{#AppVersion}
SetupIconFile=..\src\WaveFlume.App\Assets\WaveFlume.ico
UninstallDisplayIcon={app}\WaveFlume.exe
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardImageFile=wizard-large.bmp
WizardSmallImageFile=wizard-small.bmp
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline
CloseApplications=yes
RestartApplications=no
SetupMutex=WaveFlumeSetupMutex
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} setup

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nWave Flume controls the paddles of the wave flume, checks every wave for safety, and records the results.%n%nSetup also installs the Microsoft Visual C++ runtime if your PC does not already have it, and asks how Wave Flume should connect to the flume.%n%nIt is recommended that you close all other applications before continuing.

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
; Only offered when Wave Flume has been used on this PC before. Recipes and test results (Documents) are never touched.
Name: "freshstart"; Description: "Start fresh: remove the settings, probe calibration and logs left by earlier use of Wave Flume on this PC"; GroupDescription: "Earlier use of Wave Flume was found:"; Flags: unchecked; Check: PreviousUseExists

[Files]
; Self-contained single-file app (.NET runtime, WPF, SkiaSharp and QuestPDF natives embedded), so no .NET install is needed.
Source: "{#PublishDir}\WaveFlume.exe"; DestDir: "{app}"; Flags: ignoreversion
; libplctag native library (EtherNet/IP). Shipped so the app never has to write into Program Files.
Source: "{#PublishDir}\plctag.dll"; DestDir: "{app}"; Flags: ignoreversion
; QuestPDF fallback font.
Source: "{#PublishDir}\LatoFont\*"; DestDir: "{app}\LatoFont"; Flags: ignoreversion recursesubdirs
; Default flume config and TagMap profiles (copied to %LOCALAPPDATA%\WaveFlume on first run).
Source: "{#PublishDir}\config\*"; DestDir: "{app}\config"; Flags: ignoreversion recursesubdirs
Source: "..\docs\Wave_Flume_User_Manual.pdf"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\docs\PLC_IO_MAP.md"; DestDir: "{app}\docs"; Flags: ignoreversion
; Microsoft Visual C++ 2015-2022 runtime (x64), needed by the PLC and graphics libraries. Only run when missing.
Source: "prereq\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: VCRuntimeMissing

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\WaveFlume.exe"
Name: "{group}\User Manual (PDF)"; Filename: "{app}\docs\Wave_Flume_User_Manual.pdf"
Name: "{group}\PLC address map"; Filename: "{app}\docs\PLC_IO_MAP.md"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\WaveFlume.exe"; Tasks: desktopicon

[Run]
; 1638 (a newer runtime is already there) and 3010 (restart advised, not needed to run) are not failures.
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing the Microsoft Visual C++ runtime..."; Flags: waituntilterminated; Check: VCRuntimeMissing
Filename: "{app}\WaveFlume.exe"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Written by setup (the choices made in the wizard), so setup removes it again.
Type: files; Name: "{app}\config\install_defaults.json"

; Not done by the installer on purpose:
; - Windows Firewall rules for the PLC port (502 / 9600 / 4840 / 44818 / 102): site IT decision.
; - A static IP on the flume network card: set it on the flume PC (Settings shows the subnet hint).
; User data (settings, calibration, recipes, run data, logs) is kept when Wave Flume is uninstalled.

[Code]
var
  ControllerPage: TWizardPage;
  RbSimulator, RbReal: TNewRadioButton;
  CbProtocol: TNewComboBox;
  EdIp, EdPort: TNewEdit;
  LblProtocol, LblIp, LblPort, LblHint: TNewStaticText;
  DataDirPage: TInputDirWizardPage;
  DefaultDataDir: String;

{ The folder with the program's own files for this Windows user (settings, calibration, logs). The FreshDir
  switch exists so the clean-up can be tested without touching real data; it must still end in "WaveFlume". }
function PreviousUseDir: String;
begin
  Result := ExpandConstant('{param:FreshDir|}');
  if Result = '' then
    Result := ExpandConstant('{localappdata}\WaveFlume');
  Result := RemoveBackslashUnlessRoot(Result);
end;

function PreviousUseExists: Boolean;
begin
  Result := (CompareText(ExtractFileName(PreviousUseDir), 'WaveFlume') = 0) and DirExists(PreviousUseDir);
end;

function FreshStartWanted: Boolean;
begin
  Result := WizardIsTaskSelected('freshstart') or (ExpandConstant('{param:Fresh|0}') = '1');
end;

function ProtocolKey(Index: Integer): String;
begin
  case Index of
    0: Result := 'OmronFins';
    1: Result := 'ModbusTcp';
    2: Result := 'OpcUa';
    3: Result := 'EtherNetIp';
  else
    Result := 'S7';
  end;
end;

{ True when S is four numbers from 0 to 255 separated by dots. }
function IsValidIPv4(const S: String): Boolean;
var
  I, Part, Digits, Dots: Integer;
  C: Char;
begin
  Result := False;
  Part := 0;
  Digits := 0;
  Dots := 0;
  for I := 1 to Length(S) do
  begin
    C := S[I];
    if (C >= '0') and (C <= '9') then
    begin
      Part := Part * 10 + (Ord(C) - Ord('0'));
      Inc(Digits);
      if (Digits > 3) or (Part > 255) then
        Exit;
    end
    else if C = '.' then
    begin
      if Digits = 0 then
        Exit;
      Inc(Dots);
      Part := 0;
      Digits := 0;
    end
    else
      Exit;
  end;
  Result := (Dots = 3) and (Digits > 0);
end;

{ True when the Microsoft Visual C++ 2015-2022 x64 runtime (14.30 or newer) is not installed. }
function VCRuntimeMissing: Boolean;
var
  Installed, Major, Minor: Cardinal;
  Key: String;
begin
  Result := True;
  Key := 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64';
  if RegQueryDWordValue(HKLM, Key, 'Installed', Installed) and (Installed = 1) and
     RegQueryDWordValue(HKLM, Key, 'Major', Major) and RegQueryDWordValue(HKLM, Key, 'Minor', Minor) then
    Result := (Major < 14) or ((Major = 14) and (Minor < 30));
end;

procedure ControllerChoiceClick(Sender: TObject);
var
  Real: Boolean;
begin
  Real := RbReal.Checked;
  LblProtocol.Enabled := Real;
  CbProtocol.Enabled := Real;
  LblIp.Enabled := Real;
  EdIp.Enabled := Real;
  LblPort.Enabled := Real;
  EdPort.Enabled := Real;
end;

procedure InitializeWizard;
var
  Param: String;
  I: Integer;
begin
  { ---- controller page ---- }
  ControllerPage := CreateCustomPage(wpSelectDir, 'Controller', 'How will Wave Flume connect to the wave flume?');

  RbSimulator := TNewRadioButton.Create(ControllerPage);
  RbSimulator.Parent := ControllerPage.Surface;
  RbSimulator.Left := 0;
  RbSimulator.Top := 0;
  RbSimulator.Width := ControllerPage.SurfaceWidth;
  RbSimulator.Height := ScaleY(20);
  RbSimulator.Caption := 'Use the built-in Simulator (no hardware needed - for training and tests)';
  RbSimulator.OnClick := @ControllerChoiceClick;

  RbReal := TNewRadioButton.Create(ControllerPage);
  RbReal.Parent := ControllerPage.Surface;
  RbReal.Left := 0;
  RbReal.Top := ScaleY(28);
  RbReal.Width := ControllerPage.SurfaceWidth;
  RbReal.Height := ScaleY(20);
  RbReal.Caption := 'Connect to the real flume controller (PLC)';
  RbReal.OnClick := @ControllerChoiceClick;

  LblProtocol := TNewStaticText.Create(ControllerPage);
  LblProtocol.Parent := ControllerPage.Surface;
  LblProtocol.Left := ScaleX(20);
  LblProtocol.Top := ScaleY(66);
  LblProtocol.Caption := 'Controller type:';

  CbProtocol := TNewComboBox.Create(ControllerPage);
  CbProtocol.Parent := ControllerPage.Surface;
  CbProtocol.Left := ScaleX(130);
  CbProtocol.Top := ScaleY(62);
  CbProtocol.Width := ScaleX(260);
  CbProtocol.Style := csDropDownList;
  CbProtocol.Items.Add('Omron FINS (Omron CJ / CS controllers)');
  CbProtocol.Items.Add('Modbus TCP');
  CbProtocol.Items.Add('OPC UA');
  CbProtocol.Items.Add('EtherNet/IP (Omron NX / NJ controllers)');
  CbProtocol.Items.Add('Siemens S7');
  CbProtocol.ItemIndex := 0;

  LblIp := TNewStaticText.Create(ControllerPage);
  LblIp.Parent := ControllerPage.Surface;
  LblIp.Left := ScaleX(20);
  LblIp.Top := ScaleY(100);
  LblIp.Caption := 'Controller address:';

  EdIp := TNewEdit.Create(ControllerPage);
  EdIp.Parent := ControllerPage.Surface;
  EdIp.Left := ScaleX(130);
  EdIp.Top := ScaleY(96);
  EdIp.Width := ScaleX(140);
  EdIp.Text := ExpandConstant('{param:Ip|192.168.250.1}');

  LblPort := TNewStaticText.Create(ControllerPage);
  LblPort.Parent := ControllerPage.Surface;
  LblPort.Left := ScaleX(20);
  LblPort.Top := ScaleY(134);
  LblPort.Caption := 'Port (optional):';

  EdPort := TNewEdit.Create(ControllerPage);
  EdPort.Parent := ControllerPage.Surface;
  EdPort.Left := ScaleX(130);
  EdPort.Top := ScaleY(130);
  EdPort.Width := ScaleX(70);
  EdPort.Text := ExpandConstant('{param:Port|}');

  LblHint := TNewStaticText.Create(ControllerPage);
  LblHint.Parent := ControllerPage.Surface;
  LblHint.Left := 0;
  LblHint.Top := ScaleY(176);
  LblHint.Width := ControllerPage.SurfaceWidth;
  LblHint.AutoSize := False;
  LblHint.WordWrap := True;
  LblHint.Height := ScaleY(60);
  LblHint.Caption := 'Leave the port empty to use the standard port for the controller type. ' +
    'Everything here can be changed later in the program: Settings tab, Controller Connection.';

  Param := ExpandConstant('{param:Protocol|}');
  for I := 0 to 4 do
    if CompareText(Param, ProtocolKey(I)) = 0 then
      CbProtocol.ItemIndex := I;

  if CompareText(ExpandConstant('{param:Controller|simulator}'), 'real') = 0 then
    RbReal.Checked := True
  else
    RbSimulator.Checked := True;
  ControllerChoiceClick(nil);

  { ---- data folder page ---- }
  DefaultDataDir := ExpandConstant('{userdocs}\WaveFlume');
  DataDirPage := CreateInputDirPage(ControllerPage.ID, 'Data folder', 'Where should recipes and results be kept?',
    'Recipes, test data (CSV) and reports (PDF) are saved in this folder. Select a folder, then click Next.', False, '');
  DataDirPage.Add('');
  Param := ExpandConstant('{param:DataDir|}');
  if Param = '' then
    Param := DefaultDataDir;
  DataDirPage.Values[0] := Param;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port: Integer;
begin
  Result := True;

  if (CurPageID = ControllerPage.ID) and RbReal.Checked then
  begin
    if not IsValidIPv4(Trim(EdIp.Text)) then
    begin
      MsgBox('Please enter the controller address as four numbers separated by dots, for example 192.168.250.1.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if Trim(EdPort.Text) <> '' then
    begin
      Port := StrToIntDef(Trim(EdPort.Text), -1);
      if (Port < 1) or (Port > 65535) then
      begin
        MsgBox('The port must be a number from 1 to 65535, or empty to use the standard port.', mbError, MB_OK);
        Result := False;
        Exit;
      end;
    end;
  end;

  if CurPageID = DataDirPage.ID then
  begin
    if ExtractFileDrive(Trim(DataDirPage.Values[0])) = '' then
    begin
      MsgBox('Please select a full folder path, for example D:\FlumeData.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function JsonEscape(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

var
  JsonLines: TArrayOfString;

procedure AddJsonLine(const Line: String);
var
  N: Integer;
begin
  N := GetArrayLength(JsonLines);
  SetArrayLength(JsonLines, N + 1);
  JsonLines[N] := Line;
end;

{ Writes the wizard's choices where the program finds them the first time it starts. }
procedure WriteInstallDefaults;
var
  Port: Integer;
  Dir: String;
begin
  SetArrayLength(JsonLines, 0);
  AddJsonLine('{');
  if RbReal.Checked and IsValidIPv4(Trim(EdIp.Text)) then
  begin
    AddJsonLine('  "Protocol": "' + ProtocolKey(CbProtocol.ItemIndex) + '",');
    AddJsonLine('  "IpAddress": "' + Trim(EdIp.Text) + '",');
    Port := StrToIntDef(Trim(EdPort.Text), 0);
    if (Port >= 1) and (Port <= 65535) then
      AddJsonLine('  "Port": ' + IntToStr(Port) + ',');
  end
  else
    AddJsonLine('  "Protocol": "Simulator",');

  { Only a folder the operator changed is written; otherwise each Windows user keeps their own Documents\WaveFlume. }
  Dir := RemoveBackslashUnlessRoot(Trim(DataDirPage.Values[0]));
  if (Dir <> '') and (CompareText(Dir, DefaultDataDir) <> 0) then
    AddJsonLine('  "DataDirectory": "' + JsonEscape(Dir) + '",');

  AddJsonLine('  "WrittenBy": "setup"');
  AddJsonLine('}');
  SaveStringsToUTF8File(ExpandConstant('{app}\config\install_defaults.json'), JsonLines, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssInstall) and PreviousUseExists and FreshStartWanted then
  begin
    { Settings, calibration, TagMap edits and logs only. Recipes and results live in the data folder and stay. }
    if DelTree(PreviousUseDir, True, True, True) then
      Log('Start fresh: removed ' + PreviousUseDir)
    else
      Log('Start fresh: could not remove everything in ' + PreviousUseDir);
  end;
  if CurStep = ssPostInstall then
    WriteInstallDefaults;
end;
