@echo off
rem Starter vinduet "Windows Opsaetning". Dobbeltklik paa denne fil.
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0WinSetup.ps1"
