@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0get-phone-log-usb.ps1"
start "" notepad "%~dp0phone-log.txt"
