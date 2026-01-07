# Simple Forward Proxy with NTLM Authentication

A configurable forward proxy server with support for Windows NTLM/Negotiate authentication through corporate proxies. Can run as a Windows Service, Console application, or System Tray application.

## Features

- ? HTTP and HTTPS (CONNECT) tunneling
- ? Windows SSPI (NTLM/Negotiate) authentication
- ? Configuration file support
- ? Structured logging with Serilog
- ? Authentication scheme caching
- ? Connection pooling
- ? Configurable buffer sizes
- ? **Proxy exclusion list (NO_PROXY format)**
- ? **System tray application with icon**
- ? **Run as Windows Service or Console application**
- ? **Graceful shutdown support**

## Running the Application

### System Tray Mode (Recommended for Daily Use) ?

Run with a taskbar icon - no console window:

**Double-click:**
```
localproxy.exe
```

**Or use the launcher:**
```powershell
.\run-tray.ps1
```

**Features:**
- ?? Green icon appears in system tray (near clock)
- ?? Right-click menu:
  - **Show Logs** - Opens logs folder in Explorer
  - **Open Configuration** - Opens appsettings.json in Notepad
  - **Exit** - Stops the proxy
- ?? Double-click icon for status notification
- ?? Balloon notification when started

**Screenshot:**
```
System Tray ? [??] ? Right-click
??? Proxy running on port 3128
??? ???????????????????????
??? Show Logs
??? Open Configuration
??? ???????????????????????
??? Exit
```

### Console Mode (Development/Testing)

Run with visible console window:

```bash
dotnet run --console
```

Or:

```bash
.\localproxy.exe --console
```

Press `Ctrl+C` to stop.

### Windows Service Mode (Production Servers)

#### Install as Service (PowerShell - Recommended)

Run PowerShell as Administrator:

```powershell
.\install-service.ps1
```

This will:
- Install the service as "SimpleProxyService"
- Set it to start automatically
- Optionally start it immediately

#### Install as Service (Command Prompt)

Run Command Prompt as Administrator:

```cmd
install-service.bat
```

Then start the service:

```cmd
sc start SimpleProxyService
```

#### Service Management Commands

**PowerShell:**
```powershell
# Start service
Start-Service SimpleProxyService

# Stop service
Stop-Service SimpleProxyService

# Check status
Get-Service SimpleProxyService

# View logs (Windows Event Log)
Get-EventLog -LogName Application -Source SimpleProxyService -Newest 50
```

**Command Prompt:**
```cmd
# Start service
sc start SimpleProxyService

# Stop service
sc stop SimpleProxyService

# Check status
sc query SimpleProxyService

# Uninstall service
sc delete SimpleProxyService
```

#### Uninstall Service

**PowerShell:**
```powershell
.\uninstall-service.ps1
```

**Command Prompt:**
```cmd
uninstall-service.bat
```

## Run Mode Comparison

| Feature | System Tray | Console | Service |
|---------|-------------|---------|---------|
| **Best For** | Daily use | Development | Production servers |
| **UI** | Taskbar icon | Console window | None |
| **Auto-start** | Manual | Manual | On boot |
| **Easy access** | ? Right-click menu | ? | ? |
| **Quick exit** | ? Right-click ? Exit | ? Ctrl+C | ? Service manager |
| **View logs** | ? Right-click menu | ? Real-time | ? Log files |
| **Edit config** | ? Right-click menu | ? Manual | ? Manual |
| **Notifications** | ? Balloon tips | ? | ? |

**Recommendation:**
- ?? **Personal use**: System Tray Mode
- ?? **Development**: Console Mode
- ??? **Server deployment**: Service Mode

## Configuration

Edit `appsettings.json` to customize the proxy settings:

```json
{
  "Proxy": {
    "Port": 3128,                    // Port to listen on
    "EnableUpstreamProxy": true,     // Use system proxy settings
    "BufferSize": 8192,              // Stream buffer size in bytes
    "NoProxy": [                     // Proxy exclusion list (NO_PROXY format)
      "localhost",                   // Exact hostname
      "127.0.0.1",                   // IP address
      "*.local",                     // Wildcard domain
      ".internal.com",               // Domain suffix
      "192.168.*",                   // IP wildcard
      "10.*",                        // Class A network
      "example.com:8080"             // Port-specific
    ]
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",      // Logging levels: Trace, Debug, Information, Warning, Error, Critical
      "Microsoft": "Warning",
      "System": "Warning"
    },
    "Console": {
      "Enabled": true                // Enable console logging (disabled in tray mode)
    },
    "File": {
      "Enabled": true,               // Enable file logging
      "Path": "logs/proxy-.log",     // Log file path (date will be inserted)
      "RollingInterval": "Day",      // Day, Hour, Month, Year
      "RetainedFileCountLimit": 7    // Keep last 7 log files
    }
  },
  "Authentication": {
    "EnableCaching": true,           // Cache auth scheme preference
    "TimeoutSeconds": 30             // Connection timeout
  }
}
```

**Note:** When running in system tray mode, right-click the icon and select "Open Configuration" to edit settings directly.

### Proxy Exclusion List (NoProxy)

The proxy supports a flexible exclusion list compatible with `curl`'s `NO_PROXY` environment variable format:

#### Supported Patterns

| Pattern | Description | Example | Matches |
|---------|-------------|---------|---------|
| `hostname` | Exact hostname | `localhost` | `localhost` only |
| `domain.com` | Exact domain | `example.com` | `example.com` only |
| `.domain.com` | Domain suffix | `.example.com` | `example.com`, `sub.example.com`, `deep.sub.example.com` |
| `*.domain.com` | Wildcard domain | `*.local` | `test.local`, `dev.local` |
| `192.168.*` | IP wildcard | `192.168.*` | `192.168.0.1`, `192.168.255.255` |
| `10.*` | Network wildcard | `10.*` | Any IP starting with `10.` |
| `host:port` | Port-specific | `example.com:8080` | `example.com` on port 8080 only |

#### Common Configurations

**Corporate Environment:**
```json
"NoProxy": [
  "localhost",
  "127.0.0.1",
  "*.local",
  "*.corp.company.com",
  "192.168.*",
  "10.*",
  "172.16.*"
]
```

**Development Environment:**
```json
"NoProxy": [
  "localhost",
  "127.0.0.1",
  "*.local",
  "*.test",
  "*.dev"
]
```

**Kubernetes/Docker:**
```json
"NoProxy": [
  "localhost",
  "127.0.0.1",
  "*.svc.cluster.local",
  "10.*",
  "172.17.*"
]
```

#### How It Works

When a request comes in:

1. The proxy checks if the target host matches any pattern in `NoProxy`
2. If matched ? Direct connection (bypasses upstream proxy)
3. If not matched ? Routes through upstream proxy with NTLM authentication

**Example Log Output:**
```
[09:45:35 INF] Proxy exclusion list configured with 5 patterns: localhost, 127.0.0.1, *.local, 192.168.*, 10.*
[09:45:36 INF] Host intranet.local:443 matches exclusion list - using direct connection
[09:45:37 INF] Tunnel established to intranet.local:443 (direct)
```

## System Tray Features

### Icon States
- ?? **Green Circle**: Proxy is running normally
- The icon appears in the system tray (notification area)

### Right-Click Menu Options

#### Show Logs
- Opens the `logs` folder in Windows Explorer
- View daily rotating log files
- Quick access to troubleshooting information

#### Open Configuration
- Opens `appsettings.json` in Notepad
- Edit proxy settings on-the-fly
- **Note**: Restart proxy for changes to take effect

#### Exit
- Gracefully stops the proxy
- Closes all connections
- Removes tray icon

### Double-Click Behavior
- Shows a balloon notification with proxy status
- Displays the current port number
- Confirms proxy is running

### Balloon Notifications
- **On startup**: "Proxy Started - Simple Proxy is running on port 3128"
- **On double-click**: "Proxy Status - Proxy is running and accepting connections"
- **Warnings**: Directory not found, file not found
- **Errors**: Failed operations

## Logging

### System Tray Mode Logging

When running in system tray mode:
- Console logging is suppressed (no visible window)
- File logging is primary
- Access logs via right-click menu ? "Show Logs"

### Service Mode Logging

When running as a Windows Service:

1. **File Logs** (Primary):
   - Location: `logs/proxy-YYYYMMDD.log` (relative to executable)
   - Auto-rotates daily
   - Keeps last 7 days by default

2. **Windows Event Log**:
   - Application log
   - Source: `SimpleProxyService`
   - View in Event Viewer or PowerShell:
     ```powershell
     Get-EventLog -LogName Application -Source SimpleProxyService -Newest 50
     ```

### Console Mode Logging

When running in console mode:
- Real-time output to console
- File logs also written simultaneously

### Log Levels

- **Trace**: Very detailed diagnostic information
- **Debug**: Detailed flow and debugging information
- **Information**: Normal operation messages (default)
- **Warning**: Unexpected events that don't prevent operation
- **Error**: Errors and exceptions
- **Critical**: Fatal errors requiring immediate attention

## Architecture

### File Structure

```
localproxy/
??? Program.cs                      # Entry point, hosting setup
??? ProxyConfiguration.cs           # Configuration models
??? ProxyServer.cs                  # Main server orchestration
??? ProxyServiceWorker.cs           # Background service worker
??? SystemTrayIcon.cs               # System tray icon & menu
??? ProxyExclusionMatcher.cs        # Proxy bypass logic (NO_PROXY format)
??? ClientHandler.cs                # Client request routing
??? HttpRequestHandler.cs           # HTTP request forwarding
??? HttpResponseWriter.cs           # HTTP response writing
??? ConnectTunnelHandler.cs         # CONNECT tunnel management
??? ProxyAuthenticationHandler.cs   # Proxy authentication flow
??? NtlmAuthenticator.cs            # NTLM handshake logic
??? SspiHelper.cs                   # Windows SSPI authentication
??? SspiCredentialCache.cs          # Auth scheme caching
??? AuthenticatedConnectionPool.cs  # Connection pooling
??? StreamCopier.cs                 # Bidirectional stream copying
??? appsettings.json                # Configuration file
??? run-tray.ps1                    # PowerShell tray launcher
??? run-tray.bat                    # Batch tray launcher
??? install-service.ps1             # PowerShell service installer
??? uninstall-service.ps1           # PowerShell service uninstaller
??? install-service.bat             # Batch service installer
??? uninstall-service.bat           # Batch service uninstaller
```

## Browser Configuration

Configure your browser to use the proxy:

### Firefox
1. Settings ? General ? Network Settings
2. Manual proxy configuration
3. HTTP Proxy: `localhost`, Port: `3128`
4. Check "Also use this proxy for HTTPS"

### Chrome/Edge
```bash
chrome.exe --proxy-server="http://localhost:3128"
```

### System-wide (Windows)
```powershell
netsh winhttp set proxy proxy-server="http://localhost:3128" bypass-list="<local>"
```

## Troubleshooting

### System Tray Icon Not Appearing

1. **Check if proxy is running**:
   - Open Task Manager (Ctrl+Shift+Esc)
   - Look for `localproxy.exe`

2. **Check system tray overflow**:
   - Click the up arrow (^) in the system tray
   - Look for the green proxy icon

3. **Run in console mode to see errors**:
   ```cmd
   localproxy.exe --console
   ```

### Can't Exit the Proxy

1. **Right-click the tray icon** ? Exit
2. **If icon is unresponsive**:
   - Open Task Manager
   - End `localproxy.exe` process

### Logs/Configuration Won't Open

1. **Check file paths**:
   - Configuration: Same directory as exe
   - Logs: `logs` subdirectory

2. **Permissions**:
   - Ensure user has read/write access
   - Run from a writable location

### Service Won't Start

1. **Check Event Viewer**:
   ```powershell
   Get-EventLog -LogName Application -Source SimpleProxyService -Newest 20
   ```

2. **Check File Logs**:
   - Navigate to the service installation directory
   - Check `logs/proxy-YYYYMMDD.log`

3. **Verify Configuration**:
   - Ensure `appsettings.json` is in the same directory as `localproxy.exe`
   - Check port 3128 is not already in use

4. **Run in Console Mode First**:
   ```cmd
   localproxy.exe --console
   ```
   This will show any startup errors immediately

### Enable Debug Logging

In `appsettings.json`:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug"
    }
  }
}
```

## Command-Line Arguments

| Argument | Description | Use Case |
|----------|-------------|----------|
| (none) | System tray mode | Daily use |
| `--console` | Console mode | Development, debugging |
| `--service` | Service mode | Automatic, set by Windows Service Manager |

**Examples:**
```cmd
REM System tray mode (default)
localproxy.exe

REM Console mode with visible window
localproxy.exe --console

REM Service mode (don't use manually)
localproxy.exe --service
```

## Startup Options

### Run at Windows Startup (System Tray)

1. **Create a shortcut to localproxy.exe**
2. **Press Win+R** ? type `shell:startup` ? Enter
3. **Copy the shortcut** to the Startup folder

### Run at Windows Startup (Service)

Use the service installation scripts - services start automatically on boot.

## Building from Source

```bash
dotnet build -c Release
```

The output will be in `bin/Release/net10.0-windows/`

**Note**: The project targets `net10.0-windows` for Windows Forms support.

## Development

Run in development mode:

```bash
# System tray mode
dotnet run

# Console mode
dotnet run -- --console
```

Or use Visual Studio/VS Code with debugging support.

## Testing

Run unit tests:

```bash
dotnet test
```

## Security Notes

?? **Important Security Considerations:**

1. This proxy uses your Windows credentials automatically (SSO)
2. Log files may contain sensitive information
3. The proxy does not validate SSL certificates in tunnels
4. When running as a service, ensure proper account permissions
5. Only run on trusted networks
6. Do not expose to the internet
7. Exclusion list bypasses authentication - use carefully
8. System tray mode runs under your user account (not elevated)

## License

This is a development/testing tool. Use at your own risk.
