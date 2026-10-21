@echo off
REM ============================================================
REM  Launch the mcroute GUI (double-click this file)
REM
REM  NOTE: keep this file ASCII-only. cmd.exe reads .bat files in
REM        the OEM codepage, so UTF-8 Chinese comments break it.
REM ============================================================

cd /d "%~dp0"
chcp 65001 >nul

REM use pythonw so no black console window stays behind the GUI
where pythonw >nul 2>nul
if errorlevel 1 (
    echo pythonw not found, falling back to python...
    python mcroute_gui.py
    pause
) else (
    start "" pythonw mcroute_gui.py
)
