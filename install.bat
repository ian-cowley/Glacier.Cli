@echo off
title Glacier CLI Installer
echo ======================================================================
echo              Glacier CLI Windows Setup Installer
echo ======================================================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
pause
