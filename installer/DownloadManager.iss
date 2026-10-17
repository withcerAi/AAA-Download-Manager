#define AppVersion "0.5.7"
#ifndef SetupAppId
#define SetupAppId "167AB087-C613-423C-9984-84448870E972"
#endif
#ifndef SetupFileName
#define SetupFileName "AAA-Download-Manager-0.5.7-Setup-x64"
#endif
[Setup]
AppId={{{#SetupAppId}}
AppName=AAA Download Manager
AppVersion={#AppVersion}
AppVerName=AAA Download Manager 0.5.7 Preview
VersionInfoVersion=0.5.7.0
VersionInfoDescription=AAA Download Manager Setup
DefaultDirName={localappdata}\Programs\DownloadManager
DefaultGroupName=AAA Download Manager
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\release\setup
OutputBaseFilename={#SetupFileName}
SetupIconFile=assets\app.ico
UninstallDisplayIcon={app}\DownloadManager.exe
WizardStyle=modern dynamic
WizardSizePercent=115
DisableWelcomePage=no
DisableProgramGroupPage=yes
#ifdef IsolatedTest
Compression=none
SolidCompression=no
#else
Compression=lzma2/normal
SolidCompression=yes
#endif
#ifdef IsolatedTest
CloseApplications=no
#else
CloseApplications=yes
#endif
CloseApplicationsFilter=DownloadManager.exe,DownloadManager.BrowserHost.exe
RestartApplications=no
SetupLogging=yes
UninstallDisplayName=AAA Download Manager 0.5.7 Preview
LicenseFile=..\legal\TERMS-OF-USE.txt
InfoBeforeFile=preview-notes.en.txt
AllowNoIcons=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "farsi"; MessagesFile: "Farsi.isl"; InfoBeforeFile: "preview-notes.fa.txt"

[CustomMessages]
english.DesktopShortcut=Create a desktop shortcut
farsi.DesktopShortcut=ساخت میان‌بر روی دسکتاپ
english.LaunchApp=Launch AAA Download Manager
farsi.LaunchApp=اجرای AAA Download Manager

english.BrowserBridge=Connect Chrome, Edge and Firefox to AAA Download Manager
farsi.BrowserBridge=اتصال Chrome و Edge و Firefox به AAA Download Manager
english.BrowserIntegration=Browser integration
farsi.BrowserIntegration=اتصال مرورگر

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

Name: "browserbridge"; Description: "{cm:BrowserBridge}"; GroupDescription: "{cm:BrowserIntegration}"

[Files]
Source: "..\release\DownloadManager-0.5.7-preview\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "preview-notes.en.txt"; DestDir: "{app}"; DestName: "Preview-Readme.txt"; Flags: ignoreversion
Source: "preview-notes.fa.txt"; DestDir: "{app}"; DestName: "Preview-Readme.fa.txt"; Flags: ignoreversion

Source: "CHANGES-0.5.7.fa.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "Chrome-Extension.fa.txt"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
Type: files; Name: "{group}\Download Manager.lnk"
Type: files; Name: "{autodesktop}\Download Manager.lnk"
; Retire only known, app-owned obsolete extension pages on upgrade.
Type: files; Name: "{app}\browser-extension\chromium\capture.html"
Type: files; Name: "{app}\browser-extension\chromium\capture.js"
Type: files; Name: "{app}\browser-extension\chromium\popup.html"
Type: files; Name: "{app}\browser-extension\chromium\popup.js"
Type: files; Name: "{app}\browser-extension\chromium\popup.css"
Type: files; Name: "{app}\browser-extension\firefox\capture.html"
Type: files; Name: "{app}\browser-extension\firefox\capture.js"
Type: files; Name: "{app}\browser-extension\firefox\popup.html"
Type: files; Name: "{app}\browser-extension\firefox\popup.js"
Type: files; Name: "{app}\browser-extension\firefox\popup.css"

[Icons]
Name: "{group}\Chrome extension guide"; Filename: "{app}\Chrome-Extension.fa.txt"

Name: "{group}\AAA Download Manager"; Filename: "{app}\DownloadManager.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\AAA Download Manager"; Filename: "{app}\DownloadManager.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{group}\{cm:UninstallProgram,AAA Download Manager}"; Filename: "{uninstallexe}"

[Run]
#ifndef IsolatedTest
Filename: "{app}\browser-host\DownloadManager.BrowserHost.exe"; Parameters: "--register chrome gibcnlmfmdabncpgkkhiilhoajbbdifa"; Tasks: browserbridge; Flags: runhidden waituntilterminated
Filename: "{app}\browser-host\DownloadManager.BrowserHost.exe"; Parameters: "--register edge gibcnlmfmdabncpgkkhiilhoajbbdifa"; Tasks: browserbridge; Flags: runhidden waituntilterminated
Filename: "{app}\browser-host\DownloadManager.BrowserHost.exe"; Parameters: "--register firefox downloadmanager@local.invalid"; Tasks: browserbridge; Flags: runhidden waituntilterminated
#endif

Filename: "{app}\DownloadManager.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent unchecked

[UninstallRun]
Filename: "{app}\browser-host\DownloadManager.BrowserHost.exe"; Parameters: "--unregister chrome gibcnlmfmdabncpgkkhiilhoajbbdifa"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "DownloadManager-unregister-chrome"
Filename: "{app}\browser-host\DownloadManager.BrowserHost.exe"; Parameters: "--unregister edge gibcnlmfmdabncpgkkhiilhoajbbdifa"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "DownloadManager-unregister-edge"
Filename: "{app}\browser-host\DownloadManager.BrowserHost.exe"; Parameters: "--unregister firefox downloadmanager@local.invalid"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "DownloadManager-unregister-firefox"

; Deliberately no wildcard uninstall deletion: preserve user data and downloads.

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
  if WizardSilent and (ExpandConstant('{param:ACCEPTTERMS|0}') <> '1') then
  begin
    Log('AAA Download Manager: unattended installation requires /ACCEPTTERMS=1 after reviewing the terms.');
    Result := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Existing, Command: String;
begin
#ifndef IsolatedTest
  if CurStep = ssPostInstall then
  begin
    Command := '"' + ExpandConstant('{app}\DownloadManager.exe') + '"';
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DownloadManager', Existing) then
      if CompareText(Existing, Command) = 0 then
        RegWriteStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DownloadManager', Command + ' --startup');
  end;
#endif
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Existing: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DownloadManager', Existing) then
      if (CompareText(Existing, '"' + ExpandConstant('{app}\DownloadManager.exe') + '"') = 0) or
         (CompareText(Existing, '"' + ExpandConstant('{app}\DownloadManager.exe') + '" --startup') = 0) then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DownloadManager');
end;
