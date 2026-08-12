#ifndef MyAppVersion
  #define MyAppVersion "0.0.1"
#endif
#ifndef MyRuntime
  #define MyRuntime "win-x64"
#endif

[Setup]
AppId={{A6B3E0F2-1C54-4C99-9CF5-3E8D4B5E0E2A}
AppName=TokenBurn
AppVersion={#MyAppVersion}
AppPublisher=TokenBurn contributors
DefaultDirName={localappdata}\Programs\TokenBurn
DefaultGroupName=TokenBurn
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=TokenBurn-{#MyAppVersion}-{#MyRuntime}-Setup
OutputDir=.
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName=TokenBurn
SetupLogging=yes

[Tasks]
Name: "addtopath"; Description: "Add the usage-monitor CLI to my user PATH"; GroupDescription: "Command line access:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\desktop\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\publish\cli\*"; DestDir: "{app}\cli"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\TokenBurn"; Filename: "{app}\TokenBurn.exe"; WorkingDir: "{app}"
Name: "{group}\TokenBurn CLI"; Filename: "{app}\cli\usage-monitor.exe"; WorkingDir: "{app}\cli"

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command ""$existing = Get-AppxPackage -Name MicrosoftCorporationII.WinAppRuntime.Singleton -ErrorAction SilentlyContinue; if ($null -eq $existing -or [version]$existing.Version -lt [version]'8002.3.0.0') {{ Add-AppxPackage -Path '{app}\WindowsAppRuntime\Microsoft.WindowsAppRuntime.Singleton.2.msix' -ErrorAction Stop }"""; Description: "Install TokenBurn notification support"; Flags: runhidden waituntilterminated
Filename: "{app}\TokenBurn.exe"; Description: "Launch TokenBurn"; Flags: nowait postinstall skipifsilent

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
