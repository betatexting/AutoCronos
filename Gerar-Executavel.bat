@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-dist.ps1" -OpenFolder
if errorlevel 1 (
    echo.
    echo A geracao do executavel falhou.
    pause
)
