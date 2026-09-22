@echo off
setlocal
dotnet build "%~dp0IeNetworkDemo.csproj" -c Release -o "%~dp0bin\IENetworkInspector" --nologo -p:CopyRetryCount=0
if errorlevel 1 (
	echo Build failed. Close the previous IE Network Inspector window after saving your capture, then try again.
	pause
	exit /b 1
)
start "" "%~dp0bin\IENetworkInspector\IENetworkInspector.exe" --ui