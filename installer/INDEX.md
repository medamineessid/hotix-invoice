HOTIX INSTALLER — DOCUMENTATION INDEX
================================================================================

START HERE
==========

If you're new to this installer, read in this order:

1. README.md (this directory)
   - Overview of all deliverables
   - Installation flow diagram
   - Next steps for compilation and testing

2. CRITICAL_ITEMS_ANSWERS.md
   - Concise answers to the 6 originally-unverified items
   - Each item has: Question, Answer, Details, Source, Implementation
   - Best for quick reference

3. VERIFICATION_REPORT.md
   - Detailed research and verification for all 9 items
   - Sources cited (Inno Setup API, Windows SDK, PyPI, etc.)
   - Design decisions explained with rationale
   - Testing checklist

4. Hotix.iss
   - The actual Inno Setup script
   - 600+ lines of verified Pascal Script
   - Ready to compile with Inno Setup 6.3+
   - Every function documented with purpose and approach

5. DIAGNOSTICS_SCOPE.md
   - Complete architecture for HotixDiagnostics.exe (separate WPF project)
   - 10 sections covering project structure, services, repairs, UI, etc.
   - Estimated effort: 15-20 hours for production v1

================================================================================
FILE STRUCTURE
================================================================================

installer/
├── Hotix.iss                          ← Main Inno Setup script (READY TO COMPILE)
├── README.md                          ← Overview & next steps
├── CRITICAL_ITEMS_ANSWERS.md          ← Quick reference for 6 critical items
├── VERIFICATION_REPORT.md             ← Detailed research & verification
├── DIAGNOSTICS_SCOPE.md               ← HotixDiagnostics architecture
├── vendor/
│   └── python-3.12.6-amd64.exe        ← TODO: Download from python.org
├── INSTALL_NOTES.txt                  ← TODO: Create (shown before install)
└── hotix_icon.ico                     ← TODO: Create (optional, installer icon)

================================================================================
QUICK REFERENCE: 9 ITEMS STATUS
================================================================================

Item | Name                          | Status    | Location
-----|-------------------------------|-----------|---------------------
1    | Multi-method Python Detection | ✓ DONE    | Hotix.iss, FindPythonExe()
2    | Retry Logic on pip Failure    | ✓ DONE    | Hotix.iss, InstallDepsWithRetry()
3    | Progress Feedback             | ✓ DONE    | Hotix.iss, ShowInstallProgress()
4    | Logging                       | ✓ DONE    | Hotix.iss, WriteLog()
5    | Internet Connectivity         | ✓ DONE    | Hotix.iss, HasInternetConnection()
6    | Verify requirements.txt       | ✓ DONE    | Hotix.iss, VerifyRequirementsFile()
7    | Python Version Check          | ✓ DONE    | Hotix.iss, IsPythonVersionSufficient()
8    | Disk Space Check              | ✓ DONE    | Hotix.iss, HasEnoughDiskSpace()
9    | Rollback on Failure           | ✓ DONE    | Hotix.iss, RollbackPartialInstall()

================================================================================
BEFORE COMPILATION
================================================================================

Required Files (TODO):
1. installer/vendor/python-3.12.6-amd64.exe
   - Download from: https://www.python.org/downloads/
   - Size: ~27 MB
   - Verify SHA256 hash for security

2. installer/INSTALL_NOTES.txt
   - System requirements (Windows 10+, 2500 MB free)
   - Internet required during install
   - Admin privileges required

3. LICENSE.txt (in project root)
   - Your project's license
   - Shown during install

4. installer/hotix_icon.ico (optional)
   - Icon for installer window
   - If not provided, Inno Setup uses default

================================================================================
COMPILATION
================================================================================

1. Install Inno Setup 6.3+ (free, open-source)
   - Download from: https://jrsoftware.org/isdl.php

2. Compile the script:
   - iscc.exe installer/Hotix.iss
   - Output: HotixSetup_1.0.0.exe (~30 MB with bundled Python)

3. Verify output:
   - Check for "Successful compilation" message
   - HotixSetup_1.0.0.exe should be created in installer/ directory

================================================================================
TESTING
================================================================================

Recommended Test Environment:
- Windows 10 or 11 (clean VM preferred)
- No Python pre-installed
- 2500+ MB free disk space
- Internet connection

Test Scenarios:
1. Clean install (no Python)
   - Run HotixSetup_1.0.0.exe
   - Verify all checks pass
   - Verify app launches
   - Check install.log for all steps

2. Install with existing Python 3.12
   - Should use existing Python (not bundled)
   - Verify venv created successfully

3. Install with Python 3.11
   - Should use existing Python (3.11 is acceptable)
   - Verify venv created successfully

4. Install with Python 3.7
   - Should use bundled installer (3.7 too old)
   - Verify bundled Python installed

5. Simulate network failure
   - Disconnect internet before install
   - Should abort with "No internet connection" message

6. Simulate disk full
   - Fill disk to < 2500 MB free
   - Should abort with "Insufficient disk space" message

7. Verify uninstall
   - Run uninstall
   - Verify venv removed
   - Verify app removed
   - Verify logs removed

================================================================================
TROUBLESHOOTING
================================================================================

If something goes wrong:
1. Check {app}\install.log for detailed step-by-step output
2. Each step is timestamped and logged
3. Error messages include context (what failed and why)

Common Issues:
- "No internet connection detected" → User must connect to internet
- "Insufficient disk space" → User must free up 2500 MB
- "Python installer failed" → Check install.log for exit code
- "pip install failed after 3 attempts" → Check install.log for specific error
- "Virtual environment creation failed" → Check install.log for error

For Support:
- Provide install.log to support team
- Log contains all steps and errors
- Enables quick diagnosis

================================================================================
NEXT STEPS: HOTIXDIAGNOSTICS
================================================================================

After the installer is working, implement HotixDiagnostics.exe:

What is HotixDiagnostics?
- Standalone WPF utility launched automatically after install
- Performs comprehensive system checks
- Offers one-click repairs for common issues
- Reduces support burden

Architecture:
- 7 check services (Python, venv, PaddleOCR, FastAPI, Gemini, folders, internet)
- 4 repair actions (recreate venv, reinstall Python, fix permissions, download models)
- WPF UI with color-coded results (green/yellow/red)
- Detailed logging to {app}\diagnostics.log

Estimated Effort: 15-20 hours for production v1

See DIAGNOSTICS_SCOPE.md for complete architecture and implementation guide.

================================================================================
DOCUMENTATION REFERENCES
================================================================================

Inno Setup:
- Official site: https://jrsoftware.org/
- Documentation: https://jrsoftware.org/ishelp/
- Download: https://jrsoftware.org/isdl.php

Python:
- Official site: https://www.python.org/
- Windows guide: https://docs.python.org/3/using/windows.html
- Registry entries: https://docs.python.org/3/using/windows.html#registry-entries

pip:
- Official site: https://pip.pypa.io/
- Exit codes: https://pip.pypa.io/en/stable/reference/pip_exit_codes/

PaddlePaddle:
- PyPI: https://pypi.org/project/paddlepaddle/3.0.0/
- Requirements: Python 3.8+

Windows API:
- InternetGetConnectedState: https://learn.microsoft.com/en-us/windows/win32/api/wininet/nf-wininet-internetgetconnectedstate

================================================================================
SUPPORT & MAINTENANCE
================================================================================

For Questions:
1. Check CRITICAL_ITEMS_ANSWERS.md for quick reference
2. Check VERIFICATION_REPORT.md for detailed research
3. Check Hotix.iss comments for implementation details
4. Check install.log for runtime errors

For Issues:
1. Reproduce on clean Windows VM
2. Collect install.log
3. Check VERIFICATION_REPORT.md for known limitations
4. Contact support with install.log and system details

For Updates:
- Keep Hotix.iss in version control
- Document any changes to installer logic
- Test thoroughly before releasing new version
- Update version number in Hotix.iss (#define MyAppVersion)

================================================================================
