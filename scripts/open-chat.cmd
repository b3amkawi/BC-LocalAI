@echo off
setlocal

set "CHAT_SCRIPT=%~dp0chat-ollama.ps1"
set "CODEX_PWSH=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe"

where pwsh.exe >nul 2>nul
if %errorlevel% equ 0 (
    pwsh.exe -NoProfile -File "%CHAT_SCRIPT%"
    exit /b %errorlevel%
)

if exist "%CODEX_PWSH%" (
    "%CODEX_PWSH%" -NoProfile -File "%CHAT_SCRIPT%"
    exit /b %errorlevel%
)

echo PowerShell 7 was not found.
echo Install PowerShell 7, then run this file again:
echo https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows
exit /b 1
