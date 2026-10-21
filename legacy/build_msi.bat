@echo off
chcp 65001 >nul
cd /d "%~dp0"
if not exist dist\mcroute\mcroute.exe (
  echo Run build_exe.bat first. & pause & exit /b 1
)
where wix >nul 2>&1
if errorlevel 1 (
  echo.
  echo WiX not found. Install version 5 (v7+ requires accepting the OSMF fee):
  echo   dotnet tool install --global wix --version 5.0.2
  echo.
  pause & exit /b 1
)
echo Building MSI ...
if exist dist\mcroute.msi del /q dist\mcroute.msi
wix build installer\product.wxs -o dist\mcroute.msi
if errorlevel 1 ( echo build failed & pause & exit /b 1 )
echo.
echo Done: dist\mcroute.msi
echo Silent install : msiexec /i dist\mcroute.msi /qn
echo Silent uninstall: msiexec /x dist\mcroute.msi /qn
pause