@echo off
chcp 65001 >nul
set TARGET=%LOCALAPPDATA%\mcroute
echo Removing mcroute ...
del "%APPDATA%\Microsoft\Windows\Start Menu\mcroute.lnk" >nul 2>&1
del "%APPDATA%\Microsoft\Windows\Start Menu\Programs\mcroute.lnk" >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\mcroute" /f >nul 2>&1
powershell -NoProfile -Command "Remove-Item (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'mcroute.lnk') -Force -ErrorAction SilentlyContinue" >nul 2>&1
cd /d "%TEMP%"
ping -n 2 127.0.0.1 >nul
rd /s /q "%TARGET%"
echo Done.
timeout /t 2 >nul