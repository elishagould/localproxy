namespace localproxy;

public class ProxyConfiguration
{
    public ProxySettings Proxy { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public AuthenticationSettings Authentication { get; set; } = new();
}

public class ProxySettings
{
    public int Port { get; set; } = 3128;
    public int BufferSize { get; set; } = 8192;

    // Default/base configuration
    public ProxyProfile DefaultProfile { get; set; } = new ProxyProfile
    {
        Name = "Default",
        EnableUpstreamProxy = true,
        NoProxy = new List<string>()
    };

    // List of selectable profiles (Default always first)
    private List<ProxyProfile> _profiles = new List<ProxyProfile>();
    public List<ProxyProfile> Profiles
    {
        get
        {
            // Always return DefaultProfile as the first entry
            var result = new List<ProxyProfile> { DefaultProfile };
            result.AddRange(_profiles.Where(p => p.Name != DefaultProfile.Name));
            return result;
        }
        set
        {
            // Remove any duplicates of DefaultProfile
            _profiles = value?.Where(p => p.Name != DefaultProfile.Name).ToList() ?? new List<ProxyProfile>();
        }
    }

    public string ActiveProfileName { get; set; } = "Default";

    // Helper to get the active profile
    public ProxyProfile ActiveProfile => Profiles.FirstOrDefault(p => p.Name == ActiveProfileName) ?? DefaultProfile;
}

public class ProxyProfile
{
    public string Name { get; set; } = string.Empty;
    public bool EnableUpstreamProxy { get; set; } = true;
    public List<string> NoProxy { get; set; } = new();
}

public class LoggingSettings
{
    public Dictionary<string, string> LogLevel { get; set; } = new();
    public ConsoleLoggingSettings Console { get; set; } = new();
    public FileLoggingSettings File { get; set; } = new();
}

public class ConsoleLoggingSettings
{
    public bool Enabled { get; set; } = true;
}

public class FileLoggingSettings
{
    public bool Enabled { get; set; } = true;
    public string Path { get; set; } = "logs/proxy-.log";
    public string RollingInterval { get; set; } = "Day";
    public int RetainedFileCountLimit { get; set; } = 7;
}

public class AuthenticationSettings
{
    public bool EnableCaching { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 30;
}
