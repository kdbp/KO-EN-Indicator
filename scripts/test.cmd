@echo off
setlocal
cd /d "%~dp0.."
set DOTNET_BUNDLE_EXTRACT_BASE_DIR=%CD%\.build\bundle
dist\KorEng.exe --self-test
exit /b %errorlevel%
