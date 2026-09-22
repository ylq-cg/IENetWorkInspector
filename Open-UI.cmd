@echo off
setlocal
call "%~dp0Build-Unified.cmd"
if errorlevel 1 (
	echo Build failed. Close the previous IE Network Inspector window after saving your capture, then try again.
	pause
	exit /b 1
)
start "" "%~dp0bin\IENetworkInspector-Unified\IENetworkInspector.exe" --ui