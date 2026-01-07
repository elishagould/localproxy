# PowerShell script to install the proxy as a Windows Service

$ServiceName = "SimpleProxyService"
$DisplayName = "Simple Forward Proxy"
$Description = "Forward proxy with NTLM authentication support"
$ExePath = Join-Path $PSScriptRoot "localproxy.exe"

# Check if running as administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Error "This script must be run as Administrator"
    Read-Host "Press Enter to exit"
    exit 1
}

Write-Host "Installing $ServiceName..." -ForegroundColor Cyan

# Check if service already exists
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($existingService) {
    Write-Host "Service already exists. Stopping and removing..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    sc.exe delete $ServiceName
    Start-Sleep -Seconds 2
}

# Create the service
$result = sc.exe create $ServiceName binPath= "`"$ExePath`"" DisplayName= "$DisplayName" start= auto

if ($LASTEXITCODE -eq 0) {
    sc.exe description $ServiceName "$Description"
    
    Write-Host "`nService installed successfully!" -ForegroundColor Green
    Write-Host "`nUseful commands:" -ForegroundColor Cyan
    Write-Host "  Start service:   " -NoNewline; Write-Host "Start-Service $ServiceName" -ForegroundColor Yellow
    Write-Host "  Stop service:    " -NoNewline; Write-Host "Stop-Service $ServiceName" -ForegroundColor Yellow
    Write-Host "  Service status:  " -NoNewline; Write-Host "Get-Service $ServiceName" -ForegroundColor Yellow
    Write-Host "  Service logs:    " -NoNewline; Write-Host "Get-EventLog -LogName Application -Source $ServiceName -Newest 50" -ForegroundColor Yellow
    
    $startNow = Read-Host "`nDo you want to start the service now? (Y/N)"
    if ($startNow -eq 'Y' -or $startNow -eq 'y') {
        Start-Service $ServiceName
        Write-Host "Service started!" -ForegroundColor Green
        Get-Service $ServiceName
    }
} else {
    Write-Error "Failed to install service. Error code: $LASTEXITCODE"
}

Read-Host "`nPress Enter to exit"
