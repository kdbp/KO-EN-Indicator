@echo off
setlocal
cd /d "%~dp0"
set DOTNET_CLI_HOME=%~dp0.build\dotnet-home
set NUGET_PACKAGES=%~dp0.build\packages
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
set DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false
set DOTNET_GENERATE_ASPNET_CERTIFICATE=false
set DOTNET_BUNDLE_EXTRACT_BASE_DIR=%~dp0.build\bundle
dotnet restore KorEng.csproj --configfile NuGet.Config -p:NuGetAudit=false --verbosity minimal
if errorlevel 1 exit /b 1
dotnet build KorEng.csproj -c Release --no-restore --verbosity minimal
if errorlevel 1 exit /b 1
bin\Release\net10.0-windows\win-x64\KorEng.exe --write-icon KorEng.ico
if errorlevel 1 exit /b 1
dotnet publish KorEng.csproj -c Release --no-restore -o dist --verbosity minimal
if errorlevel 1 exit /b 1
if "%~1"=="--build-only" exit /b 0
dist\KorEng.exe --self-test
exit /b %errorlevel%
