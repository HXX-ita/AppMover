@echo off
setlocal
cd /d "%~dp0"
echo.
echo  ============================================
echo    Publish AppMover to GitHub
echo  ============================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\publish-github.ps1"
echo.
echo  --------------------------------------------
pause
