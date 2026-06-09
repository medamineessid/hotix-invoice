param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot

function Write-Ok  { param([string]$msg) Write-Host "  [OK] $msg"   -ForegroundColor Green }
function Write-Fail { param([string]$msg) Write-Host "  [FAIL] $msg" -ForegroundColor Red }
function Write-Info { param([string]$msg) Write-Host "  $msg"        -ForegroundColor Cyan }

Write-Host ""
Write-Host "HOTIX — Setup" -ForegroundColor White
Write-Host "=============" -ForegroundColor White
Write-Host ""

Set-Location $projectRoot

# ── Python ───────────────────────────────────────────────────────────────────
Write-Info "Checking Python..."
try {
    $pyVersion = python --version 2>&1
    if ($pyVersion -match "3\.(1[2-9]|[2-9]\d)") {
        Write-Ok "Python found: $pyVersion"
    } else {
        Write-Fail "Python 3.12+ required. Found: $pyVersion"
        Write-Host "  Download from: https://www.python.org/downloads/" -ForegroundColor Yellow
        exit 1
    }
} catch {
    Write-Fail "Python not found on PATH."
    Write-Host "  Download from: https://www.python.org/downloads/" -ForegroundColor Yellow
    exit 1
}

# ── Poppler ───────────────────────────────────────────────────────────────────
Write-Info "Checking Poppler..."
if (Get-Command pdfinfo -ErrorAction SilentlyContinue) {
    Write-Ok "Poppler found."
} else {
    Write-Fail "Poppler not found on PATH."
    Write-Host "  Download from: https://github.com/oschwartz10612/poppler-windows/releases/latest" -ForegroundColor Yellow
    Write-Host "  Extract to C:\poppler and add C:\poppler\Library\bin to your system PATH." -ForegroundColor Yellow
    exit 1
}

# ── .NET ─────────────────────────────────────────────────────────────────────
Write-Info "Checking .NET 8..."
try {
    $dotnetRuntimes = dotnet --list-runtimes 2>&1
    if ($dotnetRuntimes -match "Microsoft\.WindowsDesktop\.App 8\.") {
        Write-Ok ".NET 8 Desktop Runtime found."
    } else {
        Write-Fail ".NET 8 Desktop Runtime not found."
        Write-Host "  Download from: https://dotnet.microsoft.com/en-us/download/dotnet/8.0" -ForegroundColor Yellow
        exit 1
    }
} catch {
    Write-Fail ".NET not found on PATH."
    Write-Host "  Download from: https://dotnet.microsoft.com/en-us/download/dotnet/8.0" -ForegroundColor Yellow
    exit 1
}

# ── Virtual environment ───────────────────────────────────────────────────────
Write-Info "Creating Python virtual environment..."
$venvPath = Join-Path $projectRoot "venv"
if (-not (Test-Path (Join-Path $venvPath "Scripts\python.exe"))) {
    python -m venv $venvPath
    Write-Ok "Virtual environment created."
} else {
    Write-Ok "Virtual environment already exists."
}

# ── Activate + install packages ───────────────────────────────────────────────
Write-Info "Installing Python packages (this may take several minutes)..."
$pip = Join-Path $venvPath "Scripts\pip.exe"
& $pip install --upgrade pip -q
& $pip install -r (Join-Path $projectRoot "requirements.txt")
Write-Ok "Python packages installed."

# ── Verify OCR ────────────────────────────────────────────────────────────────
Write-Info "Verifying OCR environment (downloads models on first run)..."
$python = Join-Path $venvPath "Scripts\python.exe"
& $python (Join-Path $projectRoot "server\verify_system.py")
if ($LASTEXITCODE -ne 0) {
    Write-Fail "Environment verification failed. Check the output above."
    exit 1
}
Write-Ok "OCR environment verified."

# ── Build C# client ───────────────────────────────────────────────────────────
Write-Info "Building HOTIX client..."
$clientPath = Join-Path $projectRoot "client"
dotnet publish $clientPath -c Release -o (Join-Path $projectRoot "client\publish") --self-contained false -q
if ($LASTEXITCODE -ne 0) {
    Write-Fail "Client build failed."
    exit 1
}
Write-Ok "Client built."

Write-Host ""
Write-Host "Setup complete." -ForegroundColor Green
Write-Host "Run 'scripts\start.bat' or double-click the HOTIX shortcut to launch." -ForegroundColor White
Write-Host ""
