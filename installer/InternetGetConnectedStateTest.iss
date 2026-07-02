[Setup]
AppName=InternetGetConnectedStateTest
AppVersion=1.0
DefaultDirName={tmp}\InternetGetConnectedStateTest
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir={tmp}
Uninstallable=no

[Code]
function InternetGetConnectedState(var Flags: DWORD): BOOL;
  external 'InternetGetConnectedState@wininet.dll stdcall';

function InitializeSetup(): Boolean;
var
  Flags: DWORD;
  Connected: Boolean;
begin
  Connected := InternetGetConnectedState(Flags);
  MsgBox(Format('InternetGetConnectedState returned %s. Flags=%d', [BoolToStr(Connected, True), Flags]), mbInformation, MB_OK);
  Result := False;
end;
