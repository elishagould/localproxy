# LocalProxy

LocalProxy is a lightweight proxy server that forwards HTTP/HTTPS requests using the system's default proxy settings. It automatically authenticates using the NTLM credentials of the currently logged-in user, making it ideal for environments that require integrated Windows authentication for outbound proxy connections.

## Features
- Forwards requests through the system-configured proxy.
- Uses NTLM authentication with the credentials of the logged-in user.
- Supports HTTP and HTTPS traffic.
- Allows configuration of hosts to bypass the proxy (NO_PROXY) with flexible patterns.
- Supports multiple proxy profiles for easy switching between configurations.

## Configuration

LocalProxy is configured via an `appsettings.json` file. The proxy supports multiple profiles, allowing you to quickly switch between different proxy behaviors (such as enabling or disabling the upstream proxy, or using different NO_PROXY lists).

### Sample Configuration

```json
{
  "Proxy": {
    "Port": 3128,
    "BufferSize": 8192,
    "DefaultProfile": {
      "Name": "Default",
      "EnableUpstreamProxy": true,
      "NoProxy": [
        "localhost",
        "127.0.0.1",
        "*.local",
        "192.168.*",
        "10.*"
      ]
    },
    "Profiles": [
      {
        "Name": "DisableUpstream",
        "EnableUpstreamProxy": false,
      },
      {
        "Name": "CustomProfile",
        "EnableUpstreamProxy": true,
        "NoProxy": [
          ".internal.example.com",
          "*.dev.local"
        ]
      }
    ]
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "System": "Warning"
    },
    "Console": {
      "Enabled": true
    },
    "File": {
      "Enabled": true,
      "Path": "logs/proxy-.log",
      "RollingInterval": "Day",
      "RetainedFileCountLimit": 7
    }
  },
  "Authentication": {
    "EnableCaching": true,
    "TimeoutSeconds": 30
  }
}
```

### Profile Configuration Details

- **DefaultProfile**: The main profile used unless another is selected. If `Name` is omitted, it defaults to `"Default"`.
- **Profiles**: An array of additional profiles. Each profile can have:
  - `Name`: Unique profile name.
  - `EnableUpstreamProxy`: Whether to use the system proxy or to bypass for all requests.
  - `NoProxy`: (Optional) List of hosts, domains, wildcards, or CIDR ranges to bypass the proxy.
- **ActiveProfileName**: The name of the profile to use. If omitted, `"Default"` is used.

You can define as many profiles as you need and switch between them by changing `ActiveProfileName`.

- `NoProxy` supports hostnames, domain suffixes (e.g., `.internal.example.com`), wildcards (e.g., `*.dev.local`), IPs, CIDR, and port-specific exclusions.
- `EnableUpstreamProxy` controls whether the system proxy is used (with NTLM credentials from the logged-in user).

## Usage

1. Place your `appsettings.json` in the application directory.
2. Run the LocalProxy executable.
3. Configure your applications to use `localhost:<Port>` as their proxy.

---
