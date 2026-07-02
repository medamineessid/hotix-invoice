@echo off
cd /d C:\hotix-invoice
if not exist venv (
    echo Environnement virtuel introuvable. Veuillez lancer setup.ps1 d'abord.
    pause
    exit /b
)

echo Demarrage du serveur OCR en arrière-plan...
start /min "HOTIX OCR Server" venv\Scripts\python.exe -m uvicorn server.main:app --host 127.0.0.1 --port 8000

echo Attente du demarrage du serveur...
timeout /t 5 /nobreak >nul

echo Lancement du client WPF...
start "" "client\publish\Hotix.InvoiceClient.exe"
