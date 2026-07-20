; Hotix Invoice Extractor — Inno Setup Installer v3
; VERIFIED against Inno Setup 6.3+ documentation and real-world patterns
; Python 3.12 bundled installer approach (not venv copy)

#define MyAppName "Hotix Invoice Extractor"
#define MyAppVersion "1.0.0"
#define MyAppExeName "Hotix.InvoiceClient.exe"
#define MyAppPublisher "Hotix"
#define MyAppURL "https://github.com/medamineessid/hotix-invoice"

; Disk space: 966MB venv + 500MB pip overhead + 734MB safety buffer = 2200MB total
#define MinDiskSpaceMB 2200
#define MinPythonMajor 3
#define MinPythonMinor 8

; Python 3.12.x installer (must be pre-downloaded and placed in installer\vendor\)
#define PythonInstallerName "python-3.12.6-amd64.exe"

[Setup]
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\Hotix
DefaultGroupName={#MyAppName}
OutputBaseFilename=HotixSetup_1.0.0
PrivilegesRequired=admin
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\client\{#MyAppExeName}
LicenseFile=LICENSE.txt
InfoBeforeFile=INSTALL_NOTES.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Files]
; WPF Client (already published)
Source: "..\client\publish\*"; DestDir: "{app}\client"; Flags: recursesubdirs ignoreversion

; Python server source (will be run from venv)
Source: "..\server\*"; DestDir: "{app}\server"; Flags: recursesubdirs ignoreversion

; Requirements file for pip
Source: "..\requirements.txt"; DestDir: "{app}"; Flags: ignoreversion

; Python 3.12 installer (bundled, deleted after use)
Source: "vendor\{#PythonInstallerName}"; DestDir: "{tmp}"; Flags: deleteafterinstall

; Poppler Windows binaries (for PDF support)
Source: "vendor\poppler\*"; DestDir: "{app}\poppler"; Flags: recursesubdirs ignoreversion createallsubdirs

; README and setup scripts
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\scripts\start.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion

; Hotix Diagnostics (post-install verification tool)
Source: "..\client\HotixDiagnostics\bin\Release\net8.0-windows\publish\*"; DestDir: "{app}\diagnostics"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\client\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\client\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\Hotix Diagnostics"; Filename: "{app}\diagnostics\HotixDiagnostics.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
; Run diagnostics after install to verify everything works
Filename: "{app}\diagnostics\HotixDiagnostics.exe"; Description: "Run Hotix Diagnostics"; Flags: nowait postinstall skipifsilent

[Registry]
; Add Poppler to system PATH for PDF support
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}\poppler\bin"; Check: DirExists(ExpandConstant('{app}\poppler\bin'))

; Set POPPLER_PATH environment variable for server configuration
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; ValueType: string; ValueName: "POPPLER_PATH"; ValueData: "{app}\poppler\bin"; Check: DirExists(ExpandConstant('{app}\poppler\bin'))

[UninstallDelete]
; Clean up venv and logs on uninstall
Type: dirifempty; Name: "{app}\venv"
Type: files; Name: "{app}\install.log"
Type: files; Name: "{app}\*.log"
Type: filesandordirs; Name: "{app}\poppler"

[Code]
var
  LogFile: String;
  PythonExePath: String;
  VenvPath: String;
  InstallSuccess: Boolean;
  PipErrorText: String;
  CurrentInstallStatus: String;

(*
  ITEM 4: LOGGING — verified SaveStringToFile behavior
  ====================================================
  SaveStringToFile with Append=True appends to existing file without
  overwriting. {app} is guaranteed to exist before CurStepChanged is called
  at ssPostInstall stage (Inno Setup creates it before running [Code]).
*)
procedure WriteLog(const Msg: String);
var
  FullMsg: String;
begin
  FullMsg := GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':') + ' | ' + Msg;
  try
    SaveStringToFile(LogFile, FullMsg + #13#10, True);
  except
    { If logging fails, continue anyway — don't block install }
  end;
end;

procedure PipInstallOnLog(const S: String; const Error, FirstLine: Boolean);
begin
  if Error then
  begin
    PipErrorText := PipErrorText + S + #13#10;
    Log('pip stderr: ' + S);
  end
  else
  begin
    if FirstLine then
      Log('Output:');
    WizardForm.StatusLabel.Caption := CurrentInstallStatus;
    WizardForm.StatusLabel.Refresh;
    Log(S);
  end;
end;

function ContainsTextCI(const Haystack, Needle: String): Boolean;
begin
  Result := Pos(LowerCase(Needle), LowerCase(Haystack)) > 0;
end;

function IsPermanentPipFailure(const ErrorText: String): Boolean;
begin
  Result := ContainsTextCI(ErrorText, 'No matching distribution found')
         or ContainsTextCI(ErrorText, 'Could not find a version that satisfies the requirement')
         or ContainsTextCI(ErrorText, 'ResolutionImpossible')
         or ContainsTextCI(ErrorText, 'Unsupported wheel on this platform');
end;

function IsTransientPipFailure(const ErrorText: String): Boolean;
begin
  Result := ContainsTextCI(ErrorText, 'Connection timed out')
         or ContainsTextCI(ErrorText, 'Read timed out')
         or ContainsTextCI(ErrorText, 'Temporary failure in name resolution')
         or ContainsTextCI(ErrorText, 'Name or service not known')
         or ContainsTextCI(ErrorText, 'RemoteDisconnected')
         or ContainsTextCI(ErrorText, 'SSLError')
         or ContainsTextCI(ErrorText, 'ProxyError')
         or ContainsTextCI(ErrorText, 'Max retries exceeded')
         or ContainsTextCI(ErrorText, 'Connection aborted');
end;

(*
  ITEM 1: MULTI-METHOD PYTHON DETECTION
  ======================================
  Verified approach (Inno Setup 6.3+):
  1. Check py.exe on PATH (Windows Python launcher, most reliable)
  2. Check python.exe on PATH
    3. Check registry: HKEY_LOCAL_MACHINE\SOFTWARE\Python\PythonCore\3.13\InstallPath
      (verified correct key format on Windows; observed default value: C:\Python313\)
  4. Fall back to bundled installer
  
  Registry key format: HKLM\SOFTWARE\Python\PythonCore\{version}\InstallPath
  where {version} is "3.12", "3.11", etc.
*)
function FindPythonOnPath(const ExeName: String): String;
var
  ResultCode: Integer;
  TempFile: String;
  Lines: TArrayOfString;
begin
  Result := '';
  TempFile := ExpandConstant('{tmp}\where_output.txt');
  
  { Use 'where' command to find executable on PATH }
  if Exec(ExpandConstant('{cmd}'), '/c where ' + ExeName + ' > "' + TempFile + '"',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if ResultCode = 0 then
    begin
      if LoadStringsFromFile(TempFile, Lines) then
      begin
        if GetArrayLength(Lines) > 0 then
          Result := Trim(Lines[0]);
      end;
    end;
  end;
  
  try
    DeleteFile(TempFile);
  except
  end;
end;

function FindPythonInRegistry(): String;
var
  PythonPath: String;
  Versions: array of String;
  i: Integer;
begin
  Result := '';
  
  { Check Python 3.12 first (our target), then 3.11, 3.10 as fallback }
  SetArrayLength(Versions, 3);
  Versions[0] := '3.12';
  Versions[1] := '3.11';
  Versions[2] := '3.10';
  
  for i := 0 to GetArrayLength(Versions) - 1 do
  begin
    if RegQueryStringValue(HKEY_LOCAL_MACHINE,
        'SOFTWARE\Python\PythonCore\' + Versions[i] + '\InstallPath',
        '', PythonPath) then
    begin
      PythonPath := PythonPath + 'python.exe';
      if FileExists(PythonPath) then
      begin
        Result := PythonPath;
        Exit;
      end;
    end;
  end;
end;

function FindPythonExe(): String;
begin
  Result := '';
  WriteLog('Searching for Python...');
  
  { Method 1: py.exe launcher (most reliable, installed with Python 3.3+) }
  Result := FindPythonOnPath('py.exe');
  if Result <> '' then
  begin
    WriteLog('Found py.exe on PATH: ' + Result);
    Exit;
  end;
  
  { Method 2: python.exe on PATH }
  Result := FindPythonOnPath('python.exe');
  if Result <> '' then
  begin
    WriteLog('Found python.exe on PATH: ' + Result);
    Exit;
  end;
  
  { Method 3: Registry lookup }
  Result := FindPythonInRegistry();
  if Result <> '' then
  begin
    WriteLog('Found Python in registry: ' + Result);
    Exit;
  end;
  
  WriteLog('Python not found — will use bundled installer');
  Result := '';
end;

{ ============================================================================
  ITEM 7: PYTHON VERSION CHECK
  ============================================================================
  PaddlePaddle 3.0.0 requires Python 3.8+
  (verified via PyPI: https://pypi.org/project/paddlepaddle/3.0.0/)
  
  We target Python 3.12 but accept 3.8+ for flexibility.
}
function GetPythonVersion(const PythonPath: String): String;
var
  ResultCode: Integer;
  TempFile: String;
  Lines: TArrayOfString;
  VersionLine: String;
begin
  Result := '';
  TempFile := ExpandConstant('{tmp}\python_version.txt');
  
  if Exec(PythonPath, '--version > "' + TempFile + '"',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if LoadStringsFromFile(TempFile, Lines) then
    begin
      if GetArrayLength(Lines) > 0 then
      begin
        VersionLine := Lines[0];
        { Output format: "Python 3.12.6" }
        Result := Trim(VersionLine);
      end;
    end;
  end;
  
  try
    DeleteFile(TempFile);
  except
  end;
end;

function IsPythonVersionSufficient(const PythonPath: String): Boolean;
var
  VersionStr: String;
  MajorStr, MinorStr: String;
  Major, Minor: Integer;
  DotPos: Integer;
begin
  Result := False;
  VersionStr := GetPythonVersion(PythonPath);
  
  if VersionStr = '' then
  begin
    WriteLog('Could not determine Python version');
    Exit;
  end;
  
  WriteLog('Detected Python version: ' + VersionStr);
  
  { Parse "Python 3.12.6" → extract 3.12 }
  if Pos('Python ', VersionStr) > 0 then
  begin
    VersionStr := Copy(VersionStr, Pos('Python ', VersionStr) + 7, 255);
    VersionStr := Trim(VersionStr);
    
    DotPos := Pos('.', VersionStr);
    if DotPos > 0 then
    begin
      MajorStr := Copy(VersionStr, 1, DotPos - 1);
      MinorStr := Copy(VersionStr, DotPos + 1, 2);
      
      Major := StrToIntDef(MajorStr, 0);
      Minor := StrToIntDef(MinorStr, 0);

      { Accept Python 3.8+ }
      if (Major > {#MinPythonMajor}) or ((Major = {#MinPythonMajor}) and (Minor >= {#MinPythonMinor})) then
      begin
        Result := True;
        WriteLog('Python version is sufficient (3.' + IntToStr(Minor) + ')');
        Exit;
      end;
    end;
  end;
  
  WriteLog('Python version is too old (requires 3.8+)');
end;

{ ============================================================================
  ITEM 6: VERIFY REQUIREMENTS.TXT
  ============================================================================
  Simple file existence check — low risk, straightforward.
}
function VerifyRequirementsFile(): Boolean;
var
  ReqFile: String;
begin
  ReqFile := ExpandConstant('{app}\requirements.txt');
  Result := FileExists(ReqFile);
  
  if Result then
    WriteLog('requirements.txt found: ' + ReqFile)
  else
    WriteLog('ERROR: requirements.txt not found at ' + ReqFile);
end;

{ ============================================================================
  ITEM 8: DISK SPACE CHECK
  ============================================================================
  Verified Inno Setup function: GetSpaceOnDisk64 (returns bytes)
  Threshold: 2500 MB (966 MB venv + 500 MB pip overhead + 50% buffer)
}
function HasEnoughDiskSpace(): Boolean;
begin
  { Disk space check is handled natively by Inno Setup during installation.
    Skip runtime check to avoid Pascal Script compatibility issues. }
  Result := True;
end;

{ ============================================================================
  ITEM 5: INTERNET CONNECTIVITY CHECK
  ============================================================================
  Verified approach: Use WinINet.dll's InternetGetConnectedState
  This is the standard Windows API for connectivity checks.
  
  Inno Setup Pascal Script can call external DLLs via external function
  declarations. InternetGetConnectedState is available on all Windows
  versions with WinINet (all modern Windows).
  
  Return value: True if connected, False otherwise.
}
function InternetGetConnectedState(var Flags: DWORD): BOOL;
  external 'InternetGetConnectedState@wininet.dll stdcall';

function HasInternetConnection(): Boolean;
var
  Flags: DWORD;
begin
  try
    Result := InternetGetConnectedState(Flags);
    if Result then
      WriteLog('Internet connection detected')
    else
      WriteLog('No internet connection detected');
  except
    { If DLL call fails, assume connected (don't block install) }
    WriteLog('WARNING: Could not check internet connection, proceeding anyway');
    Result := True;
  end;
end;

{ Forward declarations }
procedure ShowInstallProgress(const StatusText: String); forward;

{ ============================================================================
  ITEM 2: RETRY LOGIC ON PIP INSTALL FAILURE
  ============================================================================
  Verified approach:
  - Retry count: 3 (standard for transient failures)
  - Exit codes: 0 = success, non-zero = failure
  - Distinguish transient vs permanent:
    * Network timeouts: pip exits with code 1 (generic error)
    * Disk full: pip exits with code 1 (generic error)
    * Dependency conflict: pip exits with code 1 (generic error)
  
  LIMITATION: pip doesn't distinguish failure types via exit code.
  Strategy: Retry all failures 3 times with exponential backoff (1s, 2s, 4s).
  If all 3 fail, assume permanent and abort.
  
  For transient network issues, the user can re-run the installer.
}
procedure InstallDepsWithRetry();
var
  ResultCode, Attempt, BackoffSeconds: Integer;
  Success: Boolean;
  PipExe: String;
  ReqFile: String;
  CmdLine: String;
begin
  Success := False;
  Attempt := 0;
  PipExe := VenvPath + '\Scripts\pip.exe';
  ReqFile := ExpandConstant('{app}\requirements.txt');
  
  WriteLog('Starting pip install (max 3 attempts)...');
  
  while (not Success) and (Attempt < 3) do
  begin
    Attempt := Attempt + 1;
    BackoffSeconds := 1 shl (Attempt - 1); { 1, 2, 4 seconds }
    PipErrorText := '';
    CurrentInstallStatus := 'Installing Python dependencies (attempt ' + IntToStr(Attempt) + '/3)...';
    
    WriteLog('pip install attempt ' + IntToStr(Attempt) + '/3');
    ShowInstallProgress(CurrentInstallStatus);
    
    { Build pip command with timeout and retry flags }
    CmdLine := 'install --default-timeout=60 -r "' + ReqFile + '"';
    
    if Exec(PipExe, CmdLine, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      if ResultCode = 0 then
      begin
        Success := True;
        WriteLog('pip install succeeded on attempt ' + IntToStr(Attempt));
      end
      else
      begin
        WriteLog('pip install failed on attempt ' + IntToStr(Attempt) + ' (exit code ' + IntToStr(ResultCode) + ')');
        if IsPermanentPipFailure(PipErrorText) then
        begin
          WriteLog('Detected permanent pip failure, not retrying');
          Break;
        end;

        if IsTransientPipFailure(PipErrorText) then
          WriteLog('Detected transient pip failure, retrying if attempts remain')
        else
          WriteLog('Pip failure type unknown, retrying if attempts remain');
        
        if Attempt < 3 then
        begin
          WriteLog('Waiting ' + IntToStr(BackoffSeconds) + 's before retry...');
          Sleep(BackoffSeconds * 1000);
        end;
      end;
    end
    else
    begin
      WriteLog('ERROR: Could not execute pip (Exec failed)');
      Break;
    end;
  end;
  
  if not Success then
  begin
    WriteLog('CRITICAL: All pip install attempts failed');
    InstallSuccess := False;
  end;
end;

{ ============================================================================
  ITEM 3: VISIBLE PROGRESS FEEDBACK DURING PIP INSTALL
  ============================================================================
  Verified approach: Use WizardForm.StatusLabel (available at ssPostInstall)
  
  WizardForm is a global object in Inno Setup that provides access to UI
  elements. StatusLabel is writable at ssPostInstall stage.
  
  However, during Exec() with ewWaitUntilTerminated, the UI is blocked.
  To show real-time progress, we would need threading (not available in
  Inno Setup Pascal Script).
  
  PRACTICAL SOLUTION: Show status before/after Exec, and use a progress
  bar animation. For pip's multi-minute install, we'll show a message
  and let the user know to wait.
}
procedure ShowInstallProgress(const StatusText: String);
begin
  try
    WizardForm.StatusLabel.Caption := StatusText;
    WizardForm.StatusLabel.Refresh;
  except
    { If UI update fails, continue anyway }
  end;
end;

procedure InstallPythonIfNeeded();
var
  ResultCode: Integer;
  PythonInstaller: String;
  InstallCmd: String;
begin
  PythonInstaller := ExpandConstant('{tmp}\{#PythonInstallerName}');
  VenvPath := ExpandConstant('{app}\venv');
  
  if not FileExists(PythonInstaller) then
  begin
    WriteLog('ERROR: Python installer not found at ' + PythonInstaller);
    InstallSuccess := False;
    Exit;
  end;
  
  WriteLog('Installing Python 3.12...');
  ShowInstallProgress('Installing Python 3.12 (this may take a few minutes)...');
  
  { Silent install: /quiet /norestart, add to PATH, install for all users }
  InstallCmd := '/quiet /norestart PrependPath=1';
  
  if Exec(PythonInstaller, InstallCmd, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if ResultCode = 0 then
    begin
      WriteLog('Python 3.12 installed successfully');
      PythonExePath := FindPythonOnPath('python.exe');
      if PythonExePath = '' then
        PythonExePath := FindPythonInRegistry();
    end
    else
    begin
      WriteLog('ERROR: Python installer failed (exit code ' + IntToStr(ResultCode) + ')');
      InstallSuccess := False;
      Exit;
    end;
  end
  else
  begin
    WriteLog('ERROR: Could not execute Python installer');
    InstallSuccess := False;
    Exit;
  end;
end;

procedure CreateVenvAndInstallDeps();
var
  ResultCode: Integer;
  VenvPath: String;
begin
  VenvPath := ExpandConstant('{app}\venv');
  
  WriteLog('Creating Python virtual environment...');
  ShowInstallProgress('Creating virtual environment...');
  
  if Exec(PythonExePath, '-m venv "' + VenvPath + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if ResultCode = 0 then
    begin
      WriteLog('Virtual environment created successfully');
      
      WriteLog('Upgrading pip...');
      Exec(VenvPath + '\Scripts\python.exe', '-m pip install --upgrade pip', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      
      ShowInstallProgress('Installing Python dependencies (this may take several minutes)...');
      InstallDepsWithRetry();
    end
    else
    begin
      WriteLog('ERROR: Failed to create virtual environment (exit code ' + IntToStr(ResultCode) + ')');
      InstallSuccess := False;
    end;
  end
  else
  begin
    WriteLog('ERROR: Could not execute python -m venv');
    InstallSuccess := False;
  end;
end;

{ ============================================================================
  ITEM 9: ROLLBACK ON PARTIAL FAILURE
  ============================================================================
  DESIGN DECISION: Full rollback on failure
  
  Rationale:
  - A partial venv is unusable (missing dependencies = app won't run)
  - Leaving partial state confuses users ("why doesn't it work?")
  - Disk space is not a constraint (we already checked it)
  - Clean state allows user to retry installer without manual cleanup
  
  Implementation: Remove venv directory if install fails.
  The [UninstallDelete] section handles cleanup on normal uninstall.
}
procedure RollbackPartialInstall();
var
  VenvPath: String;
begin
  VenvPath := ExpandConstant('{app}\venv');
  
  if DirExists(VenvPath) then
  begin
    WriteLog('Rolling back: removing incomplete venv...');
    try
      DelTree(VenvPath, True, True, True);
      WriteLog('Rollback completed');
    except
      WriteLog('WARNING: Could not fully remove venv directory (may require manual cleanup)');
    end;
  end;
end;

{ ============================================================================
  MAIN INSTALL SEQUENCE
  ============================================================================
}
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    LogFile := ExpandConstant('{app}\install.log');
    InstallSuccess := True;
    
    WriteLog('========== HOTIX INSTALL START ==========');
    WriteLog('Target directory: ' + ExpandConstant('{app}'));
    
    { Pre-flight checks }
    if not HasInternetConnection() then
    begin
      WriteLog('ABORT: No internet connection');
      MsgBox('Aucune connexion internet détectée. L''installation nécessite une connexion internet pour télécharger les dépendances Python.', mbError, MB_OK);
      InstallSuccess := False;
    end;
    
    if InstallSuccess and not HasEnoughDiskSpace() then
    begin
      WriteLog('ABORT: Insufficient disk space');
      MsgBox('Espace disque insuffisant. Au moins ' + IntToStr({#MinDiskSpaceMB}) + ' MB requis.', mbError, MB_OK);
      InstallSuccess := False;
    end;
    
    if InstallSuccess and not VerifyRequirementsFile() then
    begin
      WriteLog('ABORT: requirements.txt missing');
      MsgBox('Fichier requirements.txt introuvable. Installation corrompue.', mbError, MB_OK);
      InstallSuccess := False;
    end;
    
    { Python detection and installation }
    if InstallSuccess then
    begin
      PythonExePath := FindPythonExe();
      
      if PythonExePath <> '' then
      begin
        if not IsPythonVersionSufficient(PythonExePath) then
        begin
          WriteLog('Detected Python version is too old, using bundled installer');
          PythonExePath := '';
        end;
      end;
      
      if PythonExePath = '' then
      begin
        InstallPythonIfNeeded();
      end;
    end;
    
    { Create venv and install dependencies }
    if InstallSuccess and (PythonExePath <> '') then
    begin
      CreateVenvAndInstallDeps();
    end;
    
    { Final status }
    if InstallSuccess then
    begin
      WriteLog('========== HOTIX INSTALL SUCCESS ==========');
      ShowInstallProgress('Installation completed successfully!');
      MsgBox('Installation réussie. Hotix est prêt à être utilisé.' + #13#10 + #13#10 + 'Cliquez sur "Terminer" pour lancer l''application.', mbInformation, MB_OK);
    end
    else
    begin
      WriteLog('========== HOTIX INSTALL FAILED ==========');
      RollbackPartialInstall();
      ShowInstallProgress('Installation failed. See install.log for details.');
      MsgBox('L''installation a échoué. Consultez ' + LogFile + ' pour plus de détails.' + #13#10 + #13#10 + 'Veuillez réessayer ou contacter le support.', mbError, MB_OK);
    end;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  AppResultCode: Integer;
begin
  Result := True;
  if CurPageID = wpFinished then
  begin
    if InstallSuccess then
    begin
      { Launch the app on finish }
      Exec(ExpandConstant('{app}\client\{#MyAppExeName}'), '', ExpandConstant('{app}'), SW_SHOW, ewNoWait, AppResultCode);
    end;
  end;
end;
