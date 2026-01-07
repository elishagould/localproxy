# PowerShell script to uninstall the proxy Windows Service

$ServiceName = "SimpleProxyService"

# Check if running as administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Error "This script must be run as Administrator"
    Read-Host "Press Enter to exit"
    exit 1
}

Write-Host "Uninstalling $ServiceName..." -ForegroundColor Cyan

# Check if service exists
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if (-not $existingService) {
    Write-Host "Service does not exist." -ForegroundColor Yellow
    Read-Host "Press Enter to exit"
    exit 0
}

# Stop the service
Write-Host "Stopping service..." -ForegroundColor Yellow
Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3

# Delete the service
Write-Host "Removing service..." -ForegroundColor Yellow
$result = sc.exe delete $ServiceName

if ($LASTEXITCODE -eq 0) {
    Write-Host "`nService uninstalled successfully!" -ForegroundColor Green
} else {
    Write-Error "Failed to uninstall service. Error code: $LASTEXITCODE"
}

Read-Host "`nPress Enter to exit"
