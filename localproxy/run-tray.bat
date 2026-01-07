@echo off
REM Run the proxy in system tray mode

SET EXE_PATH=%~dp0localproxy.exe

echo Starting Simple Proxy in system tray mode...
echo Look for the icon in your system tray (near the clock)
echo Right-click the icon to access options and exit
echo.

start "" "%EXE_PATH%"

timeout /t 2 /nobreak >nul
echo.
echo Proxy should now be running in the system tray.
echo.
pause
