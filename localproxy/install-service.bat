@echo off
REM Install the proxy as a Windows Service

SET SERVICE_NAME=SimpleProxyService
SET DISPLAY_NAME=Simple Forward Proxy
SET DESCRIPTION=Forward proxy with NTLM authentication support
SET EXE_PATH=%~dp0localproxy.exe

echo Installing %SERVICE_NAME%...

sc create %SERVICE_NAME% binPath= "%EXE_PATH%" DisplayName= "%DISPLAY_NAME%" start= auto
sc description %SERVICE_NAME% "%DESCRIPTION%"

echo.
echo Service installed successfully!
echo.
echo To start the service, run:
echo   sc start %SERVICE_NAME%
echo.
echo To check service status:
echo   sc query %SERVICE_NAME%
echo.
echo To stop the service:
echo   sc stop %SERVICE_NAME%
echo.
echo To uninstall the service:
echo   sc delete %SERVICE_NAME%
echo.

pause
