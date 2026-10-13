@echo off
setlocal
cd /d "%~dp0"
set DOTNET_BUNDLE_EXTRACT_BASE_DIR=%~dp0.build\bundle
dist\KorEng.exe --self-test
exit /b %errorlevel%
