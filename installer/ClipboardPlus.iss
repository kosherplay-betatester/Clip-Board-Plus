#ifndef AppVersion
  #define AppVersion "1.1.1"
#endif
#ifndef PayloadDir
  #error PayloadDir must point to the self-contained publish folder
#endif
#ifdef InstallerTest
  #define ProductName "Clipboard Plus Installer Test"
  #define ProductId "{{570517AD-D3FC-49F0-B772-495E88AE9E19}"
  #define StartupValue "ClipboardPlusInstallerTest"
#else
  #define ProductName "Clipboard Plus"
  #define ProductId "{{A77E8B48-7453-4A35-81E2-70C06ECB9652}"
  #define StartupValue "ClipboardPlus"
#endif

[Setup]
AppId={#ProductId}
AppName={#ProductName}
AppVersion={#AppVersion}
AppPublisher=kosherplay-betatester
AppPublisherURL=https://github.com/kosherplay-betatester/Clip-Board-Plus
AppSupportURL=https://github.com/kosherplay-betatester/Clip-Board-Plus/issues
AppUpdatesURL=https://github.com/kosherplay-betatester/Clip-Board-Plus/releases
DefaultDirName={localappdata}\Programs\{#ProductName}
DefaultGroupName={#ProductName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
WizardStyle=modern
WizardSizePercent=110
WizardImageFile=assets\wizard.png
WizardSmallImageFile=assets\header.png
SetupIconFile=..\ClipboardPlus\Assets\ClipboardPlus.ico
UninstallDisplayIcon={app}\ClipboardPlus.exe
UninstallDisplayName={#ProductName}
DisableWelcomePage=no
DisableReadyPage=no
Compression=lzma2
SolidCompression=yes
OutputBaseFilename=ClipboardPlus-Setup-{#AppVersion}-win-x64
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=Clipboard Plus Setup
CloseApplications=yes
CloseApplicationsFilter=ClipboardPlus.exe
RestartApplications=no

[Messages]
SetupAppRunningError=%1 is still running in the system tray.%n%nRight-click its tray icon and choose Exit, then click OK to continue setup. Closing the history panel alone keeps the app running.
UninstallAppRunningError=%1 is still running in the system tray.%n%nRight-click its tray icon and choose Exit, then click OK to continue uninstalling. Your saved history will be kept.
WelcomeLabel1=Welcome to Clipboard Plus
WelcomeLabel2=Your clipboard, with room for more.%n%nSetup will install Clipboard Plus for your Windows account. No administrator access or separate .NET installation is needed.%n%nYour saved clipboard history stays in place when upgrading. If the app is running, setup will ask before closing it. Save any unfinished snippet edits first.
FinishedHeadingLabel=Clipboard Plus is ready
FinishedLabel=Copy something, then press Ctrl+Shift+V to find it.%n%nYou can record your own shortcut, choose Win+V, and adjust history limits in Settings. Closing the panel keeps the app in your system tray.

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Easy access:"; Flags: unchecked
Name: "startup"; Description: "Start quietly when I sign in to Windows"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#ProductName}"; Filename: "{app}\ClipboardPlus.exe"; Comment: "Your clipboard history, within reach"
Name: "{autodesktop}\{#ProductName}"; Filename: "{app}\ClipboardPlus.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#StartupValue}"; ValueData: """{app}\ClipboardPlus.exe"" --background"; Tasks: startup

[Run]
Filename: "{app}\ClipboardPlus.exe"; Description: "Open Clipboard Plus"; Flags: nowait postinstall skipifsilent

[Code]
function OpenEvent(Access: LongWord; Inherit: Boolean; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SignalEvent(Event: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Event: THandle; Attempt: Integer;
begin
  Result := '';
#ifndef InstallerTest
  if not CheckForMutexes(ExpandConstant('Local\ClipboardPlus-{username}')) then Exit;
  if WizardSilent then begin
    Result := 'Clipboard Plus is running. Close it first, or run setup interactively to approve closing it.';
    Exit;
  end;
  Event := OpenEvent(2, False, ExpandConstant('Local\ClipboardPlus-InstallerExit-{username}'));
  if Event = 0 then Exit; { Older installed versions use the Restart Manager consent page. }
  try
    if MsgBox('Clipboard Plus is running. Close it now to install this version?' + #13#10#13#10 +
      'Your saved history and settings will be kept. Finish or save any open snippet edits first.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then begin
      Result := 'The app was left running. Close it when ready, then retry setup.';
      Exit;
    end;
    if not SignalEvent(Event) then begin Result := 'Could not ask the app to close. Exit it from the tray and retry.'; Exit; end;
    for Attempt := 1 to 100 do begin
      if not CheckForMutexes(ExpandConstant('Local\ClipboardPlus-{username}')) then Exit;
      Sleep(100);
    end;
    Result := 'The app is still finishing an operation. Exit it from the tray, then retry setup.';
  finally
    CloseHandle(Event);
  end;
#endif
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
#ifndef InstallerTest
  if CheckForMutexes(ExpandConstant('Local\ClipboardPlus-{username}')) then begin
    if not UninstallSilent then MsgBox('Exit Clipboard Plus from its tray menu before uninstalling. Your saved history will be kept.', mbInformation, MB_OK);
    Result := False;
  end;
#endif
end;

procedure InitializeWizard;
var Existing, Selected: String;
begin
  if WizardSilent then Exit;
  Selected := '';
  if FileExists(ExpandConstant('{autodesktop}\{#ProductName}.lnk')) then Selected := 'desktopicon';
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#StartupValue}', Existing) then
    if Existing <> '' then begin
      if Selected <> '' then Selected := Selected + ',';
      Selected := Selected + 'startup';
    end;
  WizardSelectTasks(Selected);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not WizardIsTaskSelected('startup')) then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#StartupValue}');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Existing: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#StartupValue}', Existing) then
      if Pos(Lowercase(ExpandConstant('{app}\ClipboardPlus.exe')), Lowercase(Existing)) > 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#StartupValue}');
  { The separate LOCALAPPDATA\ClipboardPlus history directory is never removed. }
end;
