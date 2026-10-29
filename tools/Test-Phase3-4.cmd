@echo off
rem Builds and installs the current version, then tests TCP, UDP and snapshots.
rem Needs the phone on USB (settings are changed over USB) and the app open on the phone.
rem Results: tools\rtsp-test-p3*-log.txt, rtsp-test-p4video-log.txt, snapshot-check.txt, snapshot-*.jpg
set T=%~dp0
set PS=powershell -NoProfile -ExecutionPolicy Bypass -File
call "%T%Build-And-Deploy.cmd"
timeout /t 25 /nobreak >nul
%PS% "%T%test-rtsp.ps1" -Target wifi -Seconds 15 -Tag -p3tcp
%PS% "%T%set-camera-config.ps1" -Body "udp=on"
%PS% "%T%test-rtsp.ps1" -Target wifi -Seconds 60 -Udp -Tag -p3udp
%PS% "%T%test-rtsp.ps1" -Target wifi -Seconds 15 -NoSave -Tag -p3tcp2
%PS% "%T%set-camera-config.ps1" -Body "snapshot=on"
start "snapviewer" /min %PS% "%T%test-rtsp.ps1" -Target wifi -Seconds 50 -NoSave -Tag -p4video
timeout /t 5 /nobreak >nul
%PS% "%T%check-snapshot.ps1" -Count 10 -EverySeconds 3
timeout /t 20 /nobreak >nul
%PS% "%T%set-camera-config.ps1" -Body "snapshot=off&udp=off"
echo Finished. Look for PASS in the rtsp-test-p3* and p4video logs, and open snapshot-1.jpg.
pause
