@echo off
setlocal
cd /d "%~dp0.."
set DOTNET_CLI_HOME=%CD%\.build\dotnet-home
set NUGET_PACKAGES=%CD%\.build\packages
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
set DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false
set DOTNET_GENERATE_ASPNET_CERTIFICATE=false
set DOTNET_BUNDLE_EXTRACT_BASE_DIR=%CD%\.build\bundle
dotnet restore src\KorEng.csproj --configfile src\NuGet.Config -p:NuGetAudit=false --verbosity minimal
if errorlevel 1 exit /b 1
dotnet build src\KorEng.csproj -c Release --no-restore --verbosity minimal
if errorlevel 1 exit /b 1
src\bin\Release\net10.0-windows\win-x64\KorEng.exe --write-icon src\KorEng.ico
if errorlevel 1 exit /b 1
dotnet publish src\KorEng.csproj -c Release --no-restore -o dist --verbosity minimal
if errorlevel 1 exit /b 1
if "%~1"=="--build-only" exit /b 0
dist\KorEng.exe --self-test
exit /b %errorlevel%
