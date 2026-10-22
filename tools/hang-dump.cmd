@echo off
setlocal
title Hang Dump
echo.
echo ==============================================
echo   Minecraft 1.12.2 shutdown-hang thread dump
echo   Run this WHILE the game is stuck at "Shutting
echo   down internal server" (do NOT close the game).
echo ==============================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0hang-dump.ps1"
echo.
echo Dump saved under %TEMP%\kyouka-hang-dumps
pause
