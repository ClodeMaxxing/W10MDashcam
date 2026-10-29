@echo off
rem Deletes the custom IP-over-USB entries added during LumiaCam development (LumiaCamVideo,
rem LumiaCamLog, LumiaCamTest*). They never worked and are unused; port 8020 (VSTestFramework) is kept.
rem Must be run as administrator.
net session >nul 2>&1
if errorlevel 1 (
  echo Please right-click this file and choose "Run as administrator".
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$p='HKLM:\SOFTWARE\Microsoft\Windows Phone\IpOverUsbSdk';" ^
  "if (-not (Test-Path $p)) { 'IpOverUsbSdk key not found - nothing to do.'; exit }" ^
  "$k = Get-ChildItem $p | Where-Object { $_.PSChildName -like 'LumiaCam*' };" ^
  "if (-not $k) { 'No LumiaCam* entries found - nothing to do.'; exit }" ^
  "$k | ForEach-Object { 'Removing ' + $_.PSChildName; Remove-Item $_.PSPath -Recurse }"
echo.
echo Restart the IP over USB service (or reboot) for the change to take effect.
pause
