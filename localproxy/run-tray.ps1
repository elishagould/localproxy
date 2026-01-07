# PowerShell script to run the proxy in system tray mode

$ExePath = Join-Path $PSScriptRoot "localproxy.exe"

if (-not (Test-Path $ExePath)) {
    Write-Error "localproxy.exe not found in $PSScriptRoot"
    Read-Host "Press Enter to exit"
    exit 1
}

Write-Host "Starting Simple Proxy in system tray mode..." -ForegroundColor Cyan
Write-Host ""
Write-Host "Features:" -ForegroundColor Yellow
Write-Host "  - Look for the green icon in your system tray (near the clock)"
Write-Host "  - Right-click the icon for options:"
Write-Host "    * Show Logs - Opens the logs folder"
Write-Host "    * Open Configuration - Edit appsettings.json"
Write-Host "    * Exit - Stop the proxy"
Write-Host "  - Double-click the icon to see status"
Write-Host ""

Start-Process -FilePath $ExePath

Start-Sleep -Seconds 2

Write-Host "Proxy should now be running in the system tray." -ForegroundColor Green
Write-Host ""
