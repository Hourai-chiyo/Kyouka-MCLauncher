@echo off
chcp 65001 >nul
cd /d "%~dp0"
set MAKEAPPX=C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe
set SIGNTOOL=C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe
if not exist "%MAKEAPPX%" set MAKEAPPX=C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\makeappx.exe
if not exist "%SIGNTOOL%" set SIGNTOOL=C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe
if not exist "%MAKEAPPX%" ( echo makeappx.exe not found - install Windows SDK & pause & exit /b 1 )

set LAYOUT=dist\msix_layout
echo [1/4] preparing layout ...
if not exist "%LAYOUT%" mkdir "%LAYOUT%"
if not exist "%LAYOUT%\Assets" mkdir "%LAYOUT%\Assets"
if not exist dist\mcroute\mcroute.exe ( echo run build_exe.bat first & pause & exit /b 1 )
xcopy /s /y /q dist\mcroute\* "%LAYOUT%\" >nul
if exist "%LAYOUT%\uninstall.bat" del /q "%LAYOUT%\uninstall.bat"
copy /y installer\AppxManifest.xml "%LAYOUT%\" >nul

echo [2/4] packing msix ...
if exist dist\mcroute.msix del /q dist\mcroute.msix
"%MAKEAPPX%" pack /d "%LAYOUT%" /p "dist\mcroute.msix" /o
if errorlevel 1 ( echo pack failed & pause & exit /b 1 )

echo [3/4] ensuring self-signed cert ...
if not exist dist\mcroute-selfsigned.pfx (
  powershell -NoProfile -Command "$c = New-SelfSignedCertificate -Type Custom -Subject 'CN=Hourai-chiyo' -KeyUsage DigitalSignature -FriendlyName 'mcroute self-signed' -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}'); $p = ConvertTo-SecureString -String 'mcroute' -Force -AsPlainText; Export-PfxCertificate -Cert $c -FilePath 'dist\mcroute-selfsigned.pfx' -Password $p | Out-Null; Export-Certificate -Cert $c -FilePath 'dist\mcroute-selfsigned.cer' | Out-Null"
)

echo [4/4] signing ...
"%SIGNTOOL%" sign /fd SHA256 /a /f "dist\mcroute-selfsigned.pfx" /p "mcroute" "dist\mcroute.msix"
if errorlevel 1 ( echo sign failed & pause & exit /b 1 )

echo.
echo Done: dist\mcroute.msix
echo To install, users must trust dist\mcroute-selfsigned.cer first:
echo   double-click the .cer -^> Install Certificate -^> Current User -^> Trusted Root Certification Authorities
echo Or for development: Add-AppxPackage -Register "%LAYOUT%\AppxManifest.xml"
pause