@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0test-rtsp.ps1" -Target wifi
start "" notepad "%~dp0rtsp-test-log.txt"
