@echo off
rem Unattended runner - version 1.8.0: build and install only.
set T=%~dp0..
del "%T%\autotest-done.txt" 2>nul
call "%T%\Build-And-Deploy.cmd"
echo done > "%T%\autotest-done.txt"
