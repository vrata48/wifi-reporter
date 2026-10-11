@echo off
rem Runs install.ps1 regardless of the PowerShell execution policy (asks for admin rights itself).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
