@echo off
setlocal
set "OUTPUT=%~dp0bin\IENetworkInspector-Unified"
dotnet publish "%~dp0IeNetworkDemo.csproj" -c Release -r win-x64 --self-contained true -p:PlatformTarget=x64 -p:UseAppHost=true -p:PublishTrimmed=false -p:PublishSingleFile=false -p:CopyRetryCount=0 -o "%OUTPUT%" --nologo
if errorlevel 1 exit /b 1
dotnet publish "%~dp0IeNetworkDemo.csproj" -c Release -r win-x86 --self-contained true -p:PlatformTarget=x86 -p:UseAppHost=true -p:PublishTrimmed=false -p:PublishSingleFile=false -p:CopyRetryCount=0 -o "%OUTPUT%\workers\x86" --nologo
if errorlevel 1 exit /b 1
echo Unified package ready: %OUTPUT%\IENetworkInspector.exe
exit /b 0