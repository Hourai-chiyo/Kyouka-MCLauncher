@echo off
chcp 65001 >nul
cd /d "%~dp0"
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" ( echo csc.exe not found - need .NET Framework 4 & pause & exit /b 1 )
set STAGE=%TEMP%\mcroute_installer_build

echo [1/4] preparing stage ...
if exist "%STAGE%" rmdir /s /q "%STAGE%"
mkdir "%STAGE%"

echo [2/4] packing payload ...
if exist dist\installer_payload rmdir /s /q dist\installer_payload
mkdir dist\installer_payload
powershell -NoProfile -Command "Compress-Archive -Path 'dist\mcroute\*' -DestinationPath 'dist\installer_payload\payload.zip' -Force"
if errorlevel 1 ( echo pack failed & pause & exit /b 1 )

echo [3/4] compiling ...
copy /y installer\McrouteSetup.cs "%STAGE%" >nul
copy /y dist\installer_payload\payload.zip "%STAGE%" >nul
pushd "%STAGE%"
"%CSC%" /nologo /codepage:65001 /target:winexe /out:mcroute-setup.exe /r:System.Windows.Forms.dll /resource:payload.zip,McroutePayload.zip McrouteSetup.cs
if errorlevel 1 ( popd & echo compile failed & pause & exit /b 1 )
popd

echo [4/4] done ...
copy /y "%STAGE%\mcroute-setup.exe" dist\mcroute-setup.exe >nul
echo.
echo Output: dist\mcroute-setup.exe
echo Silent install: mcroute-setup.exe /S
pause