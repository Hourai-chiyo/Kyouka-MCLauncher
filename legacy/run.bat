@echo off
REM ============================================================
REM  mcroute launcher (menu mode)
REM
REM  NOTE: keep this file ASCII-only. cmd.exe reads .bat files in
REM        the OEM codepage (GBK on zh-CN), so UTF-8 Chinese
REM        comments get mis-parsed as commands.
REM
REM  Two ways to use:
REM    1) double-click this file  -> interactive menu
REM    2) run.bat manifest        -> direct, args passed through
REM ============================================================

REM always run from the folder that contains this file
cd /d "%~dp0"

REM UTF-8 so Python's Chinese output is not garbled
chcp 65001 >nul

REM ---- arguments given: run directly, no menu ----
if not "%~1"=="" goto :direct

REM guard: how many times in a row the menu got empty input
set "emptyCount=0"


:menu
cls
echo ==========================================================
echo    mcroute   MC download-source router / verifier
echo ==========================================================
echo.
echo    [1] manifest    list all MC versions (Mojang first)
echo    [2] resolve     show files a version needs to download
echo    [3] sources     test every download source, show speed
echo    [4] build       build launch command from a local instance
echo    [0] exit
echo.
set "choice="
set /p "choice=Choose 1/2/3/4/0 : "

REM if stdin is closed or exhausted, set /p leaves choice undefined
REM -> count it, and bail out after 3 tries instead of looping forever
if not defined choice goto :empty

if "%choice%"=="1" goto :do_manifest
if "%choice%"=="2" goto :do_resolve
if "%choice%"=="3" goto :do_sources
if "%choice%"=="4" goto :do_build
if "%choice%"=="0" goto :eof
goto :menu


:empty
set /a emptyCount+=1
if %emptyCount% geq 3 goto :eof
goto :menu


:do_manifest
echo.
python mcroute.py manifest
goto :end


:do_resolve
echo.
set "ver="
set /p "ver=Version (e.g. 1.21.1) : "
if not defined ver goto :menu
echo.
python mcroute.py resolve %ver%
goto :end


:do_sources
echo.
python mcroute.py sources
goto :end


:do_build
echo.
echo    Available versions:
for /d %%D in ("%APPDATA%\ModrinthApp\meta\versions\*") do echo      %%~nxD
echo.
set "vname="
set /p "vname=Version folder name : "
if not defined vname goto :menu
echo.
python mcroute.py build %vname%
goto :end


:direct
python mcroute.py %*
goto :end


:end
echo.
echo ----------------------------------------------------------
pause
goto :menu
