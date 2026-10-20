@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo [1/2] building exe with PyInstaller ...
python -m PyInstaller --noconfirm --onedir --console --name mcroute --distpath dist --workpath build --specpath build mcroute_gui.py
if errorlevel 1 ( echo build failed & pause & exit /b 1 )

echo [2/2] copying launcher files ...
rem PyInstaller wipes dist\mcroute on every build, so these must be re-copied
copy /y packaging\start-mcroute.vbs dist\mcroute\ >nul
copy /y packaging\uninstall.bat dist\mcroute\ >nul

echo.
echo Done: dist\mcroute\mcroute.exe
echo Run it with: dist\mcroute\start-mcroute.vbs   (no console window)
pause