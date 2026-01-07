@echo off
REM Uninstall the proxy Windows Service

SET SERVICE_NAME=SimpleProxyService

echo Stopping %SERVICE_NAME%...
sc stop %SERVICE_NAME%

timeout /t 3 /nobreak >nul

echo Uninstalling %SERVICE_NAME%...
sc delete %SERVICE_NAME%

echo.
echo Service uninstalled successfully!
echo.

pause
