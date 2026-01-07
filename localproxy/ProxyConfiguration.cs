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
    public bool EnableUpstreamProxy { get; set; } = true;
    public int BufferSize { get; set; } = 8192;
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
