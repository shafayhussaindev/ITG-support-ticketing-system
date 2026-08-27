@echo off
setlocal enabledelayedexpansion
title Support Desk - email setup

cd /d "%~dp0"

echo.
echo  ============================================================
echo   Support Desk - set the Gmail app password and start the API
echo  ============================================================
echo.
echo   Get a 16-character app password from:
echo     https://myaccount.google.com/apppasswords
echo.
echo   Paste ONLY the letters. No angle brackets. No quotes.
echo   Spaces between the groups are fine.
echo.

set "PW="
set /p PW=  App password:

if "!PW!"=="" (
    echo.
    echo   Nothing entered. Cancelled - no changes made.
    echo.
    pause
    exit /b 1
)

echo.
echo   Saving...
dotnet user-secrets set "Email:Password" "!PW!" --project "src\SupportTicketing.Api"
if errorlevel 1 goto failed

REM Every message currently goes to one inbox regardless of recipient. Removing this
REM makes the desk mail the real recipients. Harmless if it was never set.
dotnet user-secrets remove "Email:RedirectAllTo" --project "src\SupportTicketing.Api" >nul 2>&1

echo.
echo   Checking it stored...
dotnet user-secrets list --project "src\SupportTicketing.Api" | findstr /C:"Email:Password" >nul
if errorlevel 1 goto notstored

echo   OK - Email:Password is in the store.
echo.
echo  ------------------------------------------------------------
echo   Starting the API. Watch the first few lines for:
echo.
echo      "password present"        - good
echo      "will be rejected"        - something is still wrong
echo.
echo   Leave this window open. Ctrl-C stops the API.
echo  ------------------------------------------------------------
echo.

cd "src\SupportTicketing.Api"
dotnet run
goto end

:failed
echo.
echo   The command failed. Is the .NET SDK installed and on PATH?
echo   Try running:  dotnet --version
echo.
pause
exit /b 1

:notstored
echo.
echo   It did not store. Nothing was saved.
echo.
pause
exit /b 1

:end
echo.
echo   The API has stopped.
pause
