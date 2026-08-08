#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#ifndef MyRuntime
  #define MyRuntime "win-x64"
#endif

[Setup]
AppId={{A6B3E0F2-1C54-4C99-9CF5-3E8D4B5E0E2A}
AppName=Usage Monitor
AppVersion={#MyAppVersion}
AppPublisher=Usage Monitor contributors
DefaultDirName={localappdata}\Programs\Usage Monitor
DefaultGroupName=Usage Monitor
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=UsageMonitor-{#MyAppVersion}-{#MyRuntime}-Setup
OutputDir=.
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName=Usage Monitor
SetupLogging=yes

[Tasks]
Name: "addtopath"; Description: "Add the usage-monitor CLI to my user PATH"; GroupDescription: "Command line access:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\desktop\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\publish\cli\*"; DestDir: "{app}\cli"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Usage Monitor"; Filename: "{app}\UsageMonitor.exe"; WorkingDir: "{app}"
Name: "{group}\Usage Monitor CLI"; Filename: "{app}\cli\usage-monitor.exe"; WorkingDir: "{app}\cli"

[Run]
Filename: "{app}\UsageMonitor.exe"; Description: "Launch Usage Monitor"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{code:UpdatedUserPath}"; Tasks: addtopath

[Code]
function UpdatedUserPath(Param: String): String;
var
  Existing: String;
  CliDirectory: String;
begin
  CliDirectory := ExpandConstant('{app}\cli');
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Existing) then
    Existing := '';
  if Pos(';' + Uppercase(CliDirectory) + ';', ';' + Uppercase(Existing) + ';') = 0 then begin
    if Existing = '' then
      Result := CliDirectory
    else
      Result := Existing + ';' + CliDirectory;
  end else
    Result := Existing;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Existing: String;
  CliDirectory: String;
  Updated: String;
begin
  if CurUninstallStep <> usUninstall then
    exit;
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Existing) then
    exit;
  CliDirectory := ExpandConstant('{app}\cli');
  Updated := Existing;
  StringChangeEx(Updated, CliDirectory + ';', '', True);
  StringChangeEx(Updated, ';' + CliDirectory, '', True);
  if CompareText(Updated, CliDirectory) = 0 then
    Updated := '';
  RegWriteStringValue(HKCU, 'Environment', 'Path', Updated);
end;
