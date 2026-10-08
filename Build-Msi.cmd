@echo off
setlocal

call "%~dp0Build-Unified.cmd"
if errorlevel 1 exit /b 1

for /f "usebackq delims=" %%V in (`powershell.exe -NoProfile -Command "$project = [xml](Get-Content -LiteralPath '%~dp0IeNetworkDemo.csproj'); $project.Project.PropertyGroup.Version | Select-Object -First 1"`) do set "PRODUCT_VERSION=%%V"
if not defined PRODUCT_VERSION (
  echo Could not read the product version from IeNetworkDemo.csproj.
  exit /b 1
)

dotnet build "%~dp0installer\IENetworkInspector.Installer.wixproj" -c Release -p:ProductVersion=%PRODUCT_VERSION% --nologo
if errorlevel 1 exit /b 1

echo MSI package ready: %~dp0bin\Installer\IENetworkInspector-%PRODUCT_VERSION%-win-x64.msi
exit /b 0